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

        public string ResolvedEasyVereinToken =>
            EasyVereinToken ?? Environment.GetEnvironmentVariable("EASYVEREIN_TOKEN") ?? string.Empty;

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
                    });

                Console.WriteLine();
                AnsiConsole.MarkupLine(
                    "[green]✔ Erfolg:[/] Test erfolgreich abgeschlossen! Beleg mit PNG-Anhang wurde in easyVerein angelegt (ohne Buchungsverknüpfung).");
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                AnsiConsole.MarkupLine($"[red]✘ Fehler:[/] Test fehlgeschlagen: {Markup.Escape(ex.Message)}");
                return 1;
            }

            return 0;
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

            using var content = new MultipartFormDataContent();
            var fileContent = new ByteArrayContent(fileBytes);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            content.Add(fileContent, "path", filename);

            var response = await httpClient.PatchAsync($"v2.0/invoice/{invoiceId}", content, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var respText = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new Exception($"easyVerein Invoice file upload returned {response.StatusCode}: {respText}");
            }
        }
    }
}