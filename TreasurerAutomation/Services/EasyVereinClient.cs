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
        private bool _disposed;

        public EasyVereinClient(string apiToken)
        {
            if (string.IsNullOrWhiteSpace(apiToken))
            {
                throw new ArgumentException("easyVerein API token must be specified.", nameof(apiToken));
            }

            _httpClient = new HttpClient
            {
                BaseAddress = new Uri("https://easyverein.com/api/")
            };
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiToken);
        }

        /// <summary>
        /// Checks if an invoice with the specified invoice number (invNumber) already exists in easyVerein.
        /// </summary>
        public async Task<bool> InvoiceExistsAsync(string invNumber, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(invNumber)) return false;

            var url = $"v2.0/invoice?search={Uri.EscapeDataString(invNumber)}";
            var response = await _httpClient.GetAsync(url, cancellationToken);
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
                    _httpClient.Dispose();
                }
                _disposed = true;
            }
        }
    }
}
