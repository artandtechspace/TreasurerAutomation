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
    public class EasyVereinTestSettings : GlobalSettings
    {
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
            using var easyVereinClient = new Services.EasyVereinClient(token);

            try
            {
                int invoiceId = 0;
                var amount = 12.34m;
                var date = DateTime.UtcNow;
                var description = "Test Beleg-Upload ohne SumUp";
                var referenceCode = "TEST_" + Guid.NewGuid().ToString("N").Substring(0, 8).ToUpper();
                var receiver = "Kartenkunde (Test)";

                byte[] fileBytes;
                string filename;

                string pdfPath = System.IO.Path.Combine(AppContext.BaseDirectory, "test_receipt.pdf");
                if (System.IO.File.Exists(pdfPath))
                {
                    AnsiConsole.MarkupLine($" [blue]ℹ[/] Lese PDF-Belegdatei '{pdfPath}'...");
                    fileBytes = await System.IO.File.ReadAllBytesAsync(pdfPath, cancellationToken);
                    filename = $"test_receipt_{referenceCode}.pdf";
                }
                else
                {
                    AnsiConsole.MarkupLine($" [yellow]⚠[/] PDF-Belegdatei '{pdfPath}' nicht gefunden. Verwende 1x1 Dummy-PNG.");
                    // 1x1 Pixel transparente PNG-Datei
                    fileBytes = new byte[]
                    {
                        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D,
                        0x49, 0x48, 0x44, 0x52, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
                        0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4, 0x89, 0x00, 0x00, 0x00,
                        0x0D, 0x49, 0x44, 0x41, 0x54, 0x78, 0xDA, 0x63, 0x00, 0x01, 0x00, 0x00,
                        0x05, 0x00, 0x01, 0x0D, 0x0A, 0x2D, 0xB4, 0x00, 0x00, 0x00, 0x00, 0x49,
                        0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82
                    };
                    filename = $"test_receipt_{referenceCode}.png";
                }

                await AnsiConsole.Status()
                    .Spinner(Spinner.Known.Dots)
                    .SpinnerStyle(Style.Parse("blue bold"))
                    .StartAsync("Führe easyVerein API-Test durch...", async ctx =>
                    {
                        // 1. Beleg erstellen
                        AnsiConsole.MarkupLine($" [blue]ℹ[/] Erstelle Beleg (Entwurf) mit Code '{referenceCode}'...");
                        invoiceId = await easyVereinClient.CreateInvoiceAsync(
                            amount: amount,
                            date: date,
                            description: description,
                            receiver: receiver,
                            referenceCode: referenceCode,
                            paymentInformation: settings.ResolvedPaymentInfo,
                            bankAccount: settings.ResolvedBankAccount,
                            kind: "revenue",
                            cancellationToken: cancellationToken
                        );
                        AnsiConsole.MarkupLine($"   [green]✔[/] Beleg ID: {invoiceId}");

                        // 2. Position erstellen
                        AnsiConsole.MarkupLine($" [blue]ℹ[/] Erstelle Position (InvoiceItem) in easyVerein...");
                        await easyVereinClient.CreateInvoiceItemAsync(invoiceId, amount, cancellationToken: cancellationToken);
                        AnsiConsole.MarkupLine("   [green]✔[/] Position erfolgreich erstellt.");

                        // 3. Datei hochladen
                        AnsiConsole.MarkupLine($" [blue]ℹ[/] Lade Belegdatei hoch...");
                        await easyVereinClient.UploadInvoiceFileAsync(invoiceId, fileBytes, filename, cancellationToken);
                        AnsiConsole.MarkupLine("   [green]✔[/] Datei erfolgreich hochgeladen.");

                        // 4. Finalisieren
                        AnsiConsole.MarkupLine($" [blue]ℹ[/] Finalisiere Beleg...");
                        await easyVereinClient.FinalizeInvoiceAsync(invoiceId, cancellationToken);
                        AnsiConsole.MarkupLine("   [green]✔[/] Beleg erfolgreich finalisiert.");
                    });

                Console.WriteLine();
                AnsiConsole.MarkupLine(
                    $"[green]✔ Erfolg:[/] Test erfolgreich abgeschlossen! Beleg mit Anhang '{filename}' wurde in easyVerein angelegt (Finalisiert, ohne Buchungsverknüpfung).");
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                AnsiConsole.MarkupLine($"[red]✘ Fehler:[/] Test fehlgeschlagen: {Markup.Escape(ex.Message)}");
                return 1;
            }

            return 0;
        }
    }
}