using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;

namespace TreasurerAutomation.Services
{
    /// <summary>
    /// A reusable client for interacting with the easyVerein API.
    /// Handles invoice creation, items mapping, document upload, and finalization.
    /// Alle GETs laufen über einen TokenBucket (90/min, Burst 20), damit parallele
    /// Aufrufe das API-Limit (100/min) nicht reißen und keine 429-Retry-Stürme entstehen.
    /// Der Limiter ist thread-safe – der Client darf concurrent benutzt werden.
    /// </summary>
    public class EasyVereinClient : IDisposable
    {
        /// <summary>Ziel-Durchsatz pro Minute (Puffer unter dem 100/min-Limit).</summary>
        public const int RateLimitProMinute = 90;
        /// <summary>Max. sofortige Requests (Burst). Muss über TokensProFenster liegen,
        /// sonst deckelt der volle Bucket den Dauer-Durchsatz (Falle: 20/min statt 90/min).</summary>
        public const int RateLimitBurst = 20;
        /// <summary>
        /// Refill-Fenster: klein und häufig statt 60s-Schüben (vermeidet Minuten-Stalls
        /// und Bursts, die serverseitig 429er auslösen).
        /// </summary>
        private static readonly TimeSpan RateLimitFenster = TimeSpan.FromSeconds(2);
        /// <summary>Tokens je Fenster, exakt auf RateLimitProMinute abgestimmt (3/2s = 90/min).</summary>
        private static int RateLimitTokensProFenster => RateLimitProMinute / 30;
        private const string DefaultBaseUrl = "https://easyverein.com/api/";

        private readonly HttpClient _httpClient;
        private readonly bool _ownsHttpClient;
        private readonly TokenBucketRateLimiter _rateLimiter;
        private bool _disposed;
        private int _requestCount;
        private int _rateLimitHits;

        public string ApiToken { get; private set; }

        /// <summary>Gesendete HTTP-Requests (inkl. Retries). Für Timing-/Diagnosezeilen.</summary>
        public int RequestCount => _requestCount;
        /// <summary>429/503-Antworten (sollten dank Limiter bei 0 bleiben).</summary>
        public int RateLimitHits => _rateLimitHits;

        /// <summary>
        /// Wird von der API über den Response-Header "tokenRefreshNeeded" gesetzt
        /// (ab v2.0, ca. 15 Tage nach Erstellung/Refresh fällig, 30 Tage gültig).
        /// </summary>
        public bool TokenRefreshNeeded { get; private set; }

        public EasyVereinClient(string apiToken, HttpClient? httpClient = null,
            int? rateLimitProMinute = null, int? rateLimitBurst = null)
        {
            if (string.IsNullOrWhiteSpace(apiToken))
            {
                throw new ArgumentException("easyVerein API token must be specified.", nameof(apiToken));
            }

            ApiToken = apiToken.Trim();
            _ownsHttpClient = httpClient == null;
            _httpClient = httpClient ?? new HttpClient
            {
                BaseAddress = new Uri(DefaultBaseUrl)
            };
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiToken);
            _rateLimiter = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
            {
                TokenLimit = rateLimitBurst ?? RateLimitBurst,
                ReplenishmentPeriod = RateLimitFenster,
                TokensPerPeriod = rateLimitProMinute.HasValue
                    ? Math.Max(1, rateLimitProMinute.Value / 30)
                    : RateLimitTokensProFenster,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 10_000,
                AutoReplenishment = true,
            });
        }

        public void SetToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("Token darf nicht leer sein.", nameof(token));
            ApiToken = token.Trim();
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiToken);
        }

        private void ObserveRefreshHeader(System.Net.Http.HttpResponseMessage response)
        {
            try
            {
                foreach (var h in response.Headers)
                {
                    if (string.Equals(h.Key, "tokenRefreshNeeded", StringComparison.OrdinalIgnoreCase))
                    {
                        var v = string.Join(",", h.Value).Trim().ToLowerInvariant();
                        if (v is "true" or "1" or "yes") TokenRefreshNeeded = true;
                    }
                }
            }
            catch { /* best effort */ }
        }

        /// <summary>
        /// POST v2.0/get-token ohne Auth. Username in Form $orgShort_$emailOrUsername,
        /// z.B. ats_luca.schoeneberg@artandtech.space. 2FA nur mitschicken wenn vorhanden.
        /// Wirft mit verständlicher Meldung bei 400/401 (falsche Logindaten / 2FA nötig).
        /// </summary>
        public static async Task<EasyVereinTokenResponse> GetTokenAsync(
            string username,
            string password,
            string? twoFA = null,
            HttpClient? httpClient = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(username)) throw new ArgumentException("Username fehlt.", nameof(username));
            if (password is null) throw new ArgumentException("Passwort fehlt.", nameof(password));

            var client = httpClient ?? new HttpClient { BaseAddress = new Uri(DefaultBaseUrl) };
            // Nur selbst erzeugte Clients verwerfen (übergebene gehören dem Aufrufer/den Tests).
            using var _ = httpClient == null ? client : null;
            var payload = new Dictionary<string, string>
            {
                { "username", username.Trim() },
                { "password", password },
            };
            if (!string.IsNullOrWhiteSpace(twoFA)) payload["2FA"] = twoFA.Trim();

            using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(BuildUrl("get-token"), content, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                if (status is 400 or 401 or 403)
                    throw new EasyVereinApiException(status, $"Login fehlgeschlagen ({status}): Logindaten/2FA prüfen. Details: {Truncate(body)}");
                throw new EasyVereinApiException(status, $"get-token returned {status}: {Truncate(body)}");
            }
            using var doc = JsonDocument.Parse(body);
            return EasyVereinTokenResponse.Parse(doc.RootElement);
        }

        /// <summary>
        /// GET v2.0/refresh-token mit aktuellem Bearer. Nur sinnvoll wenn TokenRefreshNeeded,
        /// sonst liefert die API das aktuelle Token unverändert zurück. Ersetzt das Token sofort.
        /// </summary>
        public async Task<EasyVereinTokenResponse> RefreshTokenAsync(CancellationToken cancellationToken = default)
        {
            using var response = await _httpClient.GetAsync(BuildUrl("refresh-token"), cancellationToken);
            ObserveRefreshHeader(response);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new Exception($"refresh-token returned {(int)response.StatusCode}: {Truncate(body)}");
            using var doc = JsonDocument.Parse(body);
            var el = doc.RootElement;
            // Antwortform A: volles Token-Objekt {token,...}; Form B: {token: "..."} pur
            EasyVereinTokenResponse resp;
            if (el.TryGetProperty("token", out _) && el.ValueKind == JsonValueKind.Object)
            {
                try { resp = EasyVereinTokenResponse.Parse(el); }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
                {
                    // Parse kennt nur Form A – bei abweichenden Typen als reines Token lesen.
                    var t = el.GetProperty("token").GetString() ?? "";
                    resp = new EasyVereinTokenResponse(0, "", false, 0, t);
                }
            }
            else if (el.ValueKind == JsonValueKind.String)
            {
                resp = new EasyVereinTokenResponse(0, "", false, 0, el.GetString() ?? "");
            }
            else throw new Exception("refresh-token Antwort ohne 'token'.");
            if (!string.IsNullOrWhiteSpace(resp.Token))
            {
                SetToken(resp.Token);
                TokenRefreshNeeded = false;
            }
            return resp;
        }

        /// <summary>Baut "v2.0/pfad?query" (Pfad wird normiert).</summary>
        private static string BuildUrl(string path, string? query = null) =>
            $"v2.0/{path.Trim('/')}" + (string.IsNullOrWhiteSpace(query) ? "" : "?" + query.TrimStart('?'));

        /// <summary>Wirft mit gekürztem Body bei Fehlerstatus (Status immer numerisch für Tests/Logs).</summary>
        private static async Task EnsureSuccessAsync(HttpResponseMessage response, string vorgang, CancellationToken ct)
        {
            if (response.IsSuccessStatusCode) return;
            var text = await response.Content.ReadAsStringAsync(ct);
            throw new Exception($"{vorgang} returned {(int)response.StatusCode}: {Truncate(text)}");
        }

        private static string Truncate(string s, int max = 300) =>
            string.IsNullOrWhiteSpace(s) ? "" : (s.Length <= max ? s : s.Substring(0, max) + "…");

        /// <summary>
        /// Checks if an invoice with the specified invoice number (invNumber) already exists in easyVerein.
        /// </summary>
        public async Task<bool> InvoiceExistsAsync(string invNumber, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(invNumber)) return false;

            var url = BuildUrl("invoice", $"search={Uri.EscapeDataString(invNumber)}");
            using var response = await GetWithRetryAsync(url, cancellationToken);
            ObserveRefreshHeader(response);
            if (!response.IsSuccessStatusCode)
            {
                var respText = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new Exception($"easyVerein Invoice search returned {(int)response.StatusCode}: {Truncate(respText)}");
            }

            var respJson = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(respJson);
            if (doc.RootElement.TryGetProperty("results", out var resultsProp) && resultsProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in resultsProp.EnumerateArray())
                {
                    if (item.TryGetProperty("invNumber", out var invNumProp) && 
                        invNumProp.ValueKind == JsonValueKind.String &&
                        string.Equals(invNumProp.GetString(), invNumber, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Creates a new draft invoice/receipt in easyVerein.
        /// </summary>
        public async Task<int> CreateInvoiceAsync(
            decimal amount,
            DateTime date,
            string description,
            string receiver,
            string referenceCode,
            string paymentInformation,
            int? bankAccount,
            string kind,
            CancellationToken cancellationToken = default)
        {
            var absAmount = Math.Abs(amount);

            // Construct payload dynamically to optionally omit null bankAccount
            var payload = new Dictionary<string, object>
            {
                { "invNumber", referenceCode },
                { "totalPrice", absAmount },
                { "receiver", receiver },
                { "date", date.ToString("yyyy-MM-dd") },
                // Leistungsdatum (exakte API-Schreibweise inkl. Typo – nicht "korrigieren")
                { "dateItHappend", date.ToString("yyyy-MM-dd") },
                { "description", description },
                { "isReceipt", true },
                { "isDraft", true },
                { "paymentInformation", paymentInformation },
                { "kind", kind }
            };

            if (bankAccount.HasValue)
            {
                payload.Add("bankAccount", bankAccount.Value);
            }

            var json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(BuildUrl("invoice"), content, cancellationToken);
            await EnsureSuccessAsync(response, "easyVerein Invoice creation", cancellationToken);

            var respJson = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(respJson);
            if (!doc.RootElement.TryGetProperty("id", out var idProp))
            {
                throw new Exception("easyVerein Invoice response did not contain an 'id' property.");
            }

            return idProp.GetInt32();
        }

        /// <summary>
        /// Creates a new line item (InvoiceItem) linked to the specified invoice.
        /// </summary>
        public async Task CreateInvoiceItemAsync(
            int invoiceId,
            decimal amount,
            string title = "SumUp Zahlung",
            CancellationToken cancellationToken = default)
        {
            var payload = new
            {
                relatedInvoice = invoiceId,
                title = title,
                quantity = 1,
                unitPrice = Math.Abs(amount)
            };

            var json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(BuildUrl("invoice-item"), content, cancellationToken);
            await EnsureSuccessAsync(response, "easyVerein Invoice Item creation", cancellationToken);
        }

        /// <summary>
        /// Uploads a file attachment and associates it with the invoice.
        /// </summary>
        public async Task UploadInvoiceFileAsync(
            int invoiceId,
            byte[] fileBytes,
            string filename,
            CancellationToken cancellationToken = default)
        {
            using var content = new MultipartFormDataContent();
            var fileContent = new ByteArrayContent(fileBytes);

            string contentType = Path.GetExtension(filename).ToLowerInvariant() switch
            {
                ".pdf" => "application/pdf",
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                _ => "application/octet-stream",
            };

            fileContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            content.Add(fileContent, "path", filename);

            var response = await _httpClient.PatchAsync(BuildUrl($"invoice/{invoiceId}"), content, cancellationToken);
            await EnsureSuccessAsync(response, "easyVerein Invoice file upload", cancellationToken);
        }

        /// <summary>
        /// Finalizes the invoice, taking it out of draft status.
        /// </summary>
        public async Task FinalizeInvoiceAsync(int invoiceId, CancellationToken cancellationToken = default)
        {
            var payload = new { isDraft = false };
            var json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PatchAsync(BuildUrl($"invoice/{invoiceId}"), content, cancellationToken);
            await EnsureSuccessAsync(response, "easyVerein Invoice finalization", cancellationToken);
        }

        /// <summary>
        /// Generic paged read for any v2.0 endpoint (read-only, only GET requests).
        /// Follows "next" links and collects "results" items (DRF-Format).
        /// Basis für Kassenprüfungs-Auswertungen (booking, billing-account, invoice, member).
        /// </summary>
        public async Task<IReadOnlyList<JsonElement>> ListRawAsync(
            string endpoint,
            string? query = null,
            int maxPages = 20,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(endpoint))
                throw new ArgumentException("Endpoint must be specified.", nameof(endpoint));
            if (maxPages < 1)
                throw new ArgumentOutOfRangeException(nameof(maxPages), "maxPages muss >= 1 sein.");

            var result = new List<JsonElement>();
            string? nextUrl = BuildUrl(endpoint, query);
            var pages = 0;

            while (nextUrl != null && pages < maxPages)
            {
                using var response = await GetWithRetryAsync(nextUrl, cancellationToken);
                ObserveRefreshHeader(response);
                await EnsureSuccessAsync(response, $"easyVerein GET {nextUrl}", cancellationToken);

                var respJson = await response.Content.ReadAsStringAsync(cancellationToken);
                using var doc = JsonDocument.Parse(respJson);
                if (doc.RootElement.TryGetProperty("results", out var results) &&
                    results.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in results.EnumerateArray())
                        result.Add(item.Clone());
                }
                else if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in doc.RootElement.EnumerateArray())
                        result.Add(item.Clone());
                }

                nextUrl = doc.RootElement.TryGetProperty("next", out var nextProp) &&
                    nextProp.ValueKind == JsonValueKind.String
                    ? nextProp.GetString()
                    : null;
                pages++;
            }

            return result;
        }

        /// <summary>
        /// Liest ein einzelnes Objekt per GET (z.B. contact-details/123 oder member/123).
        /// Gibt null zurück, wenn 404. Nur lesend, keine Seiteneffekte.
        /// </summary>
        public async Task<JsonElement?> GetSingleRawAsync(
            string path,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Path must be specified.", nameof(path));

            var url = BuildUrl(path);
            using var response = await GetWithRetryAsync(url, cancellationToken);
            ObserveRefreshHeader(response);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;
            await EnsureSuccessAsync(response, $"easyVerein GET {url}", cancellationToken);

            var respJson = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(respJson);
            return doc.RootElement.Clone();
        }

        /// <summary>
        /// GET mit globalem Rate-Limit (TokenBucket) + Retry bei 429/503.
        /// Durch den Limiter sind 429er die Ausnahme; Retry-After bzw.
        /// exponentiell (1s, 2s, 4s, …), max. 5 Versuche.
        /// </summary>
        private async Task<System.Net.Http.HttpResponseMessage> GetWithRetryAsync(
            string url,
            CancellationToken cancellationToken,
            int maxVersuche = 5)
        {
            var delay = TimeSpan.FromSeconds(1);
            for (var versuch = 1; ; versuch++)
            {
                using var lease = await _rateLimiter.AcquireAsync(permitCount: 1, cancellationToken);
                Interlocked.Increment(ref _requestCount);
                var response = await _httpClient.GetAsync(url, cancellationToken);
                if ((int)response.StatusCode != 429 && (int)response.StatusCode != 503)
                    return response;
                Interlocked.Increment(ref _rateLimitHits);
                if (versuch >= maxVersuche)
                    return response;
                var warten = delay;
                try
                {
                    if (response.Headers.RetryAfter?.Delta is { } d && d > TimeSpan.Zero && d < TimeSpan.FromMinutes(2))
                        warten = d;
                }
                catch { /* Fallback delay */ }
                response.Dispose();
                await Task.Delay(warten, cancellationToken);
                delay = delay * 2;
            }
        }

        /// <summary>
        /// PATCH mit JSON-Body auf einen v2.0-Pfad (z.B. contact-details/123).
        /// Für member-fix Auto-Korrekturen (dry-run default, --apply schreibt).
        /// </summary>
        public async Task PatchRawAsync(
            string path,
            IReadOnlyDictionary<string, object?> felder,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Path must be specified.", nameof(path));
            var url = BuildUrl(path);
            var json = JsonSerializer.Serialize(felder);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await _httpClient.PatchAsync(url, content, cancellationToken);
            ObserveRefreshHeader(response);
            await EnsureSuccessAsync(response, $"easyVerein PATCH {url}", cancellationToken);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _rateLimiter.Dispose();
                    if (_ownsHttpClient)
                        _httpClient.Dispose();
                }
                _disposed = true;
            }
        }
    }
}
