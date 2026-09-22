using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace TreasurerAutomation.Services
{
    /// <summary>
    /// A reusable client for interacting with the easyVerein API.
    /// Handles invoice creation, items mapping, document upload, and finalization.
    /// </summary>
    public class EasyVereinClient : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly bool _ownsHttpClient;
        private bool _disposed;

        public string ApiToken { get; private set; }

        /// <summary>
        /// Wird von der API über den Response-Header "tokenRefreshNeeded" gesetzt
        /// (ab v2.0, ca. 15 Tage nach Erstellung/Refresh fällig, 30 Tage gültig).
        /// </summary>
        public bool TokenRefreshNeeded { get; private set; }

        public EasyVereinClient(string apiToken, HttpClient? httpClient = null)
        {
            if (string.IsNullOrWhiteSpace(apiToken))
            {
                throw new ArgumentException("easyVerein API token must be specified.", nameof(apiToken));
            }

            ApiToken = apiToken.Trim();
            _ownsHttpClient = httpClient == null;
            _httpClient = httpClient ?? new HttpClient
            {
                BaseAddress = new Uri("https://easyverein.com/api/")
            };
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiToken);
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

            var client = httpClient ?? new HttpClient { BaseAddress = new Uri("https://easyverein.com/api/") };
            var payload = new Dictionary<string, string>
            {
                { "username", username.Trim() },
                { "password", password },
            };
            if (!string.IsNullOrWhiteSpace(twoFA)) payload["2FA"] = twoFA.Trim();

            using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using var response = await client.PostAsync("v2.0/get-token", content, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                if ((int)response.StatusCode is 400 or 401 or 403)
                    throw new Exception($"Login fehlgeschlagen ({(int)response.StatusCode}): Logindaten/2FA prüfen. Details: {Kuerze(body)}");
                throw new Exception($"get-token returned {(int)response.StatusCode}: {Kuerze(body)}");
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
            using var response = await _httpClient.GetAsync("v2.0/refresh-token", cancellationToken);
            ObserveRefreshHeader(response);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new Exception($"refresh-token returned {(int)response.StatusCode}: {Kuerze(body)}");
            using var doc = JsonDocument.Parse(body);
            var el = doc.RootElement;
            // Antwortform A: volles Token-Objekt {token,...}; Form B: {token: "..."} pur
            EasyVereinTokenResponse resp;
            if (el.TryGetProperty("token", out _) && el.ValueKind == JsonValueKind.Object)
            {
                try { resp = EasyVereinTokenResponse.Parse(el); }
                catch
                {
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

        private static string Kuerze(string s, int max = 300) =>
            string.IsNullOrWhiteSpace(s) ? "" : (s.Length <= max ? s : s.Substring(0, max) + "…");

        /// <summary>
        /// Checks if an invoice with the specified invoice number (invNumber) already exists in easyVerein.
        /// </summary>
        public async Task<bool> InvoiceExistsAsync(string invNumber, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(invNumber)) return false;

            var url = $"v2.0/invoice?search={Uri.EscapeDataString(invNumber)}";
            var response = await _httpClient.GetAsync(url, cancellationToken);
            ObserveRefreshHeader(response);
            if (!response.IsSuccessStatusCode)
            {
                var respText = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new Exception($"easyVerein Invoice search returned {response.StatusCode}: {respText}");
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
            var payload = new System.Collections.Generic.Dictionary<string, object>
            {
                { "invNumber", referenceCode },
                { "totalPrice", absAmount },
                { "receiver", receiver },
                { "date", date.ToString("yyyy-MM-dd") },
                { "dateItHappend", date.ToString("yyyy-MM-dd") }, // Leistungsdatum
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

            var response = await _httpClient.PostAsync("v2.0/invoice", content, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var respText = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new Exception($"easyVerein Invoice creation returned {response.StatusCode}: {respText}");
            }

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

            var response = await _httpClient.PostAsync("v2.0/invoice-item", content, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var respText = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new Exception($"easyVerein Invoice Item creation returned {response.StatusCode}: {respText}");
            }
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
            
            string contentType = filename.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
                ? "application/pdf"
                : "image/png";
            
            fileContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            content.Add(fileContent, "path", filename);

            var response = await _httpClient.PatchAsync($"v2.0/invoice/{invoiceId}", content, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var respText = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new Exception($"easyVerein Invoice file upload returned {response.StatusCode}: {respText}");
            }
        }

        /// <summary>
        /// Finalizes the invoice, taking it out of draft status.
        /// </summary>
        public async Task FinalizeInvoiceAsync(int invoiceId, CancellationToken cancellationToken = default)
        {
            var payload = new { isDraft = false };
            var json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PatchAsync($"v2.0/invoice/{invoiceId}", content, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var respText = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new Exception($"easyVerein Invoice finalization returned {response.StatusCode}: {respText}");
            }
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
            var start = $"v2.0/{endpoint.Trim('/')}" +
                (string.IsNullOrWhiteSpace(query) ? "" : "?" + query.TrimStart('?'));
            string? nextUrl = start;
            var pages = 0;

            while (nextUrl != null && pages < maxPages)
            {
                using var response = await GetWithRetryAsync(nextUrl, cancellationToken);
                ObserveRefreshHeader(response);
                if (!response.IsSuccessStatusCode)
                {
                    var respText = await response.Content.ReadAsStringAsync(cancellationToken);
                    throw new Exception($"easyVerein GET {nextUrl} returned {(int)response.StatusCode}: {respText}");
                }

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

            var url = $"v2.0/{path.Trim('/')}";
            using var response = await GetWithRetryAsync(url, cancellationToken);
            ObserveRefreshHeader(response);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return null;
            if (!response.IsSuccessStatusCode)
            {
                var respText = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new Exception($"easyVerein GET {url} returned {(int)response.StatusCode}: {respText}");
            }

            var respJson = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(respJson);
            return doc.RootElement.Clone();
        }

        /// <summary>
        /// GET mit Retry bei Rate-Limit (429) / Service-Unavailable (503).
        /// easyVerein limitiert auf 100/min – der Audit lädt ~3 Requests pro Mitglied.
        /// Wartet Retry-After bzw. exponentiell (1s, 2s, 4s, …), max. 5 Versuche.
        /// </summary>
        private async Task<System.Net.Http.HttpResponseMessage> GetWithRetryAsync(
            string url,
            CancellationToken cancellationToken,
            int maxVersuche = 5)
        {
            var delay = TimeSpan.FromSeconds(1);
            for (var versuch = 1; ; versuch++)
            {
                var response = await _httpClient.GetAsync(url, cancellationToken);
                if ((int)response.StatusCode != 429 && (int)response.StatusCode != 503)
                    return response;
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
            var url = $"v2.0/{path.Trim('/')}";
            var json = JsonSerializer.Serialize(felder);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await _httpClient.PatchAsync(url, content, cancellationToken);
            ObserveRefreshHeader(response);
            if (!response.IsSuccessStatusCode)
            {
                var respText = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new Exception($"easyVerein PATCH {url} returned {(int)response.StatusCode}: {Kuerze(respText)}");
            }
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
                if (disposing && _ownsHttpClient)
                {
                    _httpClient.Dispose();
                }
                _disposed = true;
            }
        }
    }
}
