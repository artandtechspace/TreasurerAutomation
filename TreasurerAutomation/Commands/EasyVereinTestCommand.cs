using System;
using System.ComponentModel;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Spectre.Console;
using Spectre.Console.Cli;

namespace TreasurerAutomation.Commands
{
    public class EasyVereinTestSettings : CommandSettings
    {
        [CommandOption("--easyverein-token <VALUE>")]
        [Description("Sets the easyVerein Token. Overrides EASYVEREIN_TOKEN env var.")]
        public string? EasyVereinToken { get; set; }

        [CommandOption("--billing-account <VALUE>")]
        [Description("Sets the easyVerein Billing Account ID. Overrides EASYVEREIN_BILLING_ACCOUNT_ID env var.")]
        public int? EasyVereinBillingAccountId { get; set; }

        public string ResolvedEasyVereinToken =>
            EasyVereinToken ?? Environment.GetEnvironmentVariable("EASYVEREIN_TOKEN") ?? string.Empty;

        public int ResolvedEasyVereinBillingAccountId
        {
            get
            {
                if (EasyVereinBillingAccountId.HasValue) return EasyVereinBillingAccountId.Value;
                var envVal = Environment.GetEnvironmentVariable("EASYVEREIN_BILLING_ACCOUNT_ID");
                return int.TryParse(envVal, out var val) ? val : 0;
            }
        }

        public override ValidationResult Validate()
        {
            if (string.IsNullOrEmpty(ResolvedEasyVereinToken))
            {
                return ValidationResult.Error(
                    "Missing configuration: --easyverein-token or EASYVEREIN_TOKEN env var is required.");
            }

            return ValidationResult.Success();
        }
    }

    public class EasyVereinTestCommand : AsyncCommand<EasyVereinTestSettings>
    {
        protected override async Task<int> ExecuteAsync(CommandContext context, EasyVereinTestSettings settings,
            CancellationToken cancellationToken)
        {
            AnsiConsole.Write(new Rule("[yellow]easyVerein API Connection & Upload Test[/]").RuleStyle("grey")
                .LeftJustified());
            Console.WriteLine();

            var token = settings.ResolvedEasyVereinToken;
            var billingAccountId = settings.ResolvedEasyVereinBillingAccountId;

            try
            {
                int invoiceId = 0;
                var amount = 12.34m;
                var date = DateTime.UtcNow;
                var description = "Test Beleg-Upload ohne SumUp";
                var referenceCode = "TEST_" + Guid.NewGuid().ToString("N").Substring(0, 8).ToUpper();
                var receiver = "Kartenkunde (Test)";

                // 1x1 Pixel transparente PNG-Datei
                byte[] dummyPng = new byte[]
                {
                    0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D,
                    0x49, 0x48, 0x44, 0x52, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
                    0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4, 0x89, 0x00, 0x00, 0x00,
                    0x0D, 0x49, 0x44, 0x41, 0x54, 0x78, 0xDA, 0x63, 0x00, 0x01, 0x00, 0x00,
                    0x05, 0x00, 0x01, 0x0D, 0x0A, 0x2D, 0xB4, 0x00, 0x00, 0x00, 0x00, 0x49,
                    0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82
                };

                await AnsiConsole.Status()
                    .Spinner(Spinner.Known.Dots)
                    .SpinnerStyle(Style.Parse("blue bold"))
                    .StartAsync("Führe easyVerein API-Test durch...", async ctx =>
                    {
                        // 0. Zahlungskonto erstellen, falls nicht übergeben
                        if (billingAccountId <= 0)
                        {
                            var random = new Random();
                            var accountName = "Test-Zahlungskonto " +
                                              Guid.NewGuid().ToString("N").Substring(0, 6).ToUpper();
                            var accountNumber = random.Next(1000, 9999);

                            AnsiConsole.MarkupLine(
                                $" [blue]ℹ[/] Erstelle Zahlungskonto '{accountName}' (Nr. {accountNumber})...");
                            billingAccountId = await CreateEasyVereinBillingAccountAsync(token, accountName,
                                accountNumber, cancellationToken);
                            AnsiConsole.MarkupLine($"   [green]✔[/] Zahlungskonto ID: {billingAccountId}");
                        }
                        else
                        {
                            AnsiConsole.MarkupLine(
                                $" [blue]ℹ[/] Verwende existierendes Zahlungskonto ID: {billingAccountId}");
                        }

                        // 1. Beleg erstellen
                        AnsiConsole.MarkupLine($" [blue]ℹ[/] Erstelle Beleg mit Code '{referenceCode}'...");
                        invoiceId = await CreateEasyVereinInvoiceAsync(token, amount, date, description, receiver,
                            referenceCode, cancellationToken);
                        AnsiConsole.MarkupLine($"   [green]✔[/] Beleg ID: {invoiceId}");

                        // 2. PNG-Datei hochladen
                        AnsiConsole.MarkupLine($" [blue]ℹ[/] Lade 1x1 Dummy-PNG hoch...");
                        await UploadEasyVereinInvoiceFileAsync(token, invoiceId, dummyPng,
                            $"test_receipt_{referenceCode}.png", cancellationToken);
                        AnsiConsole.MarkupLine("   [green]✔[/] Datei erfolgreich hochgeladen.");

                        // 3. Buchung erstellen und verknüpfen
                        AnsiConsole.MarkupLine($" [blue]ℹ[/] Erstelle Buchung und verknüpfe Beleg...");
                        await CreateEasyVereinBookingAsync(token, amount, date, billingAccountId, description,
                            "Getränkeverkauf", referenceCode, receiver, new[] { invoiceId }, cancellationToken);
                        AnsiConsole.MarkupLine("   [green]✔[/] Buchung erfolgreich verknüpft.");
                    });

                Console.WriteLine();
                AnsiConsole.MarkupLine(
                    "[green]✔ Erfolg:[/] Test erfolgreich abgeschlossen! Zahlungskonto, Beleg mit PNG-Anhang und Buchung wurden in easyVerein angelegt.");
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                AnsiConsole.MarkupLine($"[red]✘ Fehler:[/] Test fehlgeschlagen: {Markup.Escape(ex.Message)}");
                return 1;
            }

            return 0;
        }

        private static async Task<int> CreateEasyVereinBillingAccountAsync(
            string easyVereinToken,
            string name,
            int number,
            CancellationToken cancellationToken)
        {
            var baseUri = new Uri("https://easyverein.com/api/");
            using var httpClient = new HttpClient { BaseAddress = baseUri };
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", easyVereinToken);

            var payload = new
            {
                name = name,
                number = number
            };

            var json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await httpClient.PostAsync("v2.0/billing-account", content, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var respText = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new Exception($"easyVerein Billing Account creation returned {response.StatusCode}: {respText}");
            }

            var respJson = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(respJson);
            if (!doc.RootElement.TryGetProperty("id", out var idProp))
            {
                throw new Exception("easyVerein Billing Account response did not contain an 'id' property.");
            }

            return idProp.GetInt32();
        }

        private static async Task<int> CreateEasyVereinInvoiceAsync(
            string easyVereinToken,
            decimal amount,
            DateTime date,
            string description,
            string receiver,
            string referenceCode,
            CancellationToken cancellationToken)
        {
            var baseUri = new Uri("https://easyverein.com/api/");
            using var httpClient = new HttpClient { BaseAddress = baseUri };
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", easyVereinToken);

            var absAmount = Math.Abs(amount);
            var payload = new
            {
                invNumber = referenceCode,
                totalPrice = absAmount,
                receiver = receiver,
                date = date.ToString("yyyy-MM-dd"),
                description = description,
                isReceipt = true,
                isDraft = true,
                kind = amount >= 0 ? "revenue" : "expense"
            };

            var json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await httpClient.PostAsync("v2.0/invoice", content, cancellationToken);
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

        private static async Task UploadEasyVereinInvoiceFileAsync(
            string easyVereinToken,
            int invoiceId,
            byte[] fileBytes,
            string filename,
            CancellationToken cancellationToken)
        {
            var baseUri = new Uri("https://easyverein.com/api/");
            using var httpClient = new HttpClient { BaseAddress = baseUri };
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", easyVereinToken);

            using var content = new ByteArrayContent(fileBytes);
            content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment")
            {
                Name = "\"path\"",
                FileName = $"\"{filename}\""
            };

            var response = await httpClient.PatchAsync($"v2.0/invoice/{invoiceId}", content, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var respText = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new Exception($"easyVerein Invoice file upload returned {response.StatusCode}: {respText}");
            }
        }

        private static async Task CreateEasyVereinBookingAsync(
            string easyVereinToken,
            decimal amount,
            DateTime date,
            int billingAccountId,
            string description,
            string receiver,
            string reference,
            string counterpartName,
            int[] relatedInvoiceIds,
            CancellationToken cancellationToken)
        {
            var baseUri = new Uri("https://easyverein.com/api/");
            using var httpClient = new HttpClient { BaseAddress = baseUri };
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", easyVereinToken);

            var payload = new
            {
                amount = amount,
                billingAccount = billingAccountId,
                description = description,
                date = date.ToString("yyyy-MM-ddTHH:mm:ss"),
                receiver = receiver,
                billingId = reference,
                paymentDifference = 0,
                counterpartName = counterpartName,
                counterpartIban = string.Empty,
                counterpartBic = string.Empty,
                twingoDonation = false,
                sphere = 0,
                relatedInvoice = relatedInvoiceIds
            };
            var json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await httpClient.PostAsync("v2.0/booking", content, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var respText = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new Exception($"easyVerein API returned {response.StatusCode}: {respText}");
            }
        }
    }
}