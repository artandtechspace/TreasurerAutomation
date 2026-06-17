using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
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
    /// <summary>
    /// Base settings containing global configuration options.
    /// These can be passed via command line flags and fallback to environment variables.
    /// </summary>
    public class GlobalSettings : CommandSettings
    {
        [CommandOption("--sumup-token <VALUE>")]
        [Description("Sets the SumUp Access Token. Overrides SUMUP_ACCESS_TOKEN env var.")]
        public string? SumupToken { get; set; }

        [CommandOption("--merchant-code <VALUE>")]
        [Description("Sets the SumUp Merchant Code. Overrides SUMUP_MERCHANT_CODE env var.")]
        public string? MerchantCode { get; set; }

        [CommandOption("--easyverein-token <VALUE>")]
        [Description("Sets the easyVerein Token. Overrides EASYVEREIN_TOKEN env var.")]
        public string? EasyVereinToken { get; set; }

        [CommandOption("--billing-account <VALUE>")]
        [Description("Sets the easyVerein Billing Account ID. Overrides EASYVEREIN_BILLING_ACCOUNT_ID env var.")]
        public int? EasyVereinBillingAccountId { get; set; }

        public string ResolvedSumupToken =>
            SumupToken ?? Environment.GetEnvironmentVariable("SUMUP_ACCESS_TOKEN") ?? string.Empty;

        public string ResolvedMerchantCode =>
            MerchantCode ?? Environment.GetEnvironmentVariable("SUMUP_MERCHANT_CODE") ?? string.Empty;

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
            var missing = new List<string>();

            if (string.IsNullOrEmpty(ResolvedSumupToken))
                missing.Add("SUMUP_ACCESS_TOKEN / --sumup-token");

            if (string.IsNullOrEmpty(ResolvedMerchantCode))
                missing.Add("SUMUP_MERCHANT_CODE / --merchant-code");

            if (string.IsNullOrEmpty(ResolvedEasyVereinToken))
                missing.Add("EASYVEREIN_TOKEN / --easyverein-token");

            if (ResolvedEasyVereinBillingAccountId <= 0)
                missing.Add("EASYVEREIN_BILLING_ACCOUNT_ID / --billing-account (must be a positive integer)");

            return missing.Count > 0
                ? ValidationResult.Error("Missing or invalid configuration values: " + string.Join(", ", missing))
                : ValidationResult.Success();
        }
    }

    /// <summary>
    /// Settings specific to the sumup-sync command.
    /// </summary>
    public class SumUpSyncSettings : GlobalSettings
    {
        [CommandOption("--days <VALUE>")]
        [Description("Number of days in the past to fetch transactions from. Default is 1.")]
        [DefaultValue(1)]
        public int Days { get; set; }

        [CommandOption("--limit <VALUE>")]
        [Description("Maximum number of transactions to fetch from SumUp. Default is 100.")]
        [DefaultValue(100)]
        public int Limit { get; set; }
    }

    /// <summary>
    /// CLI Command to synchronize SumUp transactions to easyVerein.
    /// </summary>
    public class SumUpSyncCommand : AsyncCommand<SumUpSyncSettings>
    {
        protected override async Task<int> ExecuteAsync(CommandContext context, SumUpSyncSettings settings,
            CancellationToken cancellationToken)
        {
            AnsiConsole.Write(new Rule("[yellow]SumUp to easyVerein Sync[/]").RuleStyle("grey").LeftJustified());
            Console.WriteLine();

            var sumUpOptions = new SumUp.SumUpClientOptions { AccessToken = settings.ResolvedSumupToken };
            var sumUpClient = new SumUp.SumUpClient(sumUpOptions);

            var since = DateTime.UtcNow.AddDays(-settings.Days);
            var listOptions = new SumUp.TransactionsListOptions
            {
                ChangesSince = since,
                Limit = settings.Limit
            };

            SumUp.TransactionsListResponse? listResponse = null;

            // 1. Fetch transactions list with spinner
            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .SpinnerStyle(Style.Parse("yellow bold"))
                .StartAsync("Abrufen der Transaktionsliste von SumUp...", async ctx =>
                {
                    var listApiResponse =
                        await sumUpClient.Transactions.ListAsync(settings.ResolvedMerchantCode, listOptions);
                    if (listApiResponse != null && listApiResponse.IsSuccess)
                    {
                        listResponse = listApiResponse.Data;
                    }
                    else
                    {
                        throw new Exception($"SumUp API returned status: {listApiResponse?.StatusCode}");
                    }
                });

            if (listResponse?.Items == null || !listResponse.Items.Any())
            {
                AnsiConsole.MarkupLine("[yellow]Info:[/] Keine neuen Transaktionen im gewählten Zeitraum gefunden.");
                return 0;
            }

            var items = listResponse.Items.ToArray();
            AnsiConsole.MarkupLine(
                $"[green]Erfolg:[/] {items.Length} Transaktion(en) gefunden. Starte Import...[reset]");
            Console.WriteLine();

            // Structure to hold summary of executions
            var summaryList = new List<(string TxId, string Code, string Status, string Detail, string Amount)>();

            // 2. Process each transaction
            foreach (var tx in items)
            {
                if (string.IsNullOrEmpty(tx.Id))
                {
                    summaryList.Add(("n/a", "n/a", "[red]Fehler[/]", "Transaktions-ID fehlt", "0,00 €"));
                    continue;
                }

                if (tx.Status != SumUp.TransactionStatus.Successful)
                {
                    summaryList.Add((tx.Id, tx.TransactionCode ?? "n/a", "[yellow]Übersprungen[/]",
                        $"Status ist {tx.Status}", $"{tx.Amount:N2} €"));
                    continue;
                }

                var txId = tx.Id;
                var txCode = tx.TransactionCode ?? "n/a";
                var txAmountFormatted = $"{tx.Amount:N2} €";

                await AnsiConsole.Status()
                    .Spinner(Spinner.Known.Dots)
                    .SpinnerStyle(Style.Parse("blue bold"))
                    .StartAsync($"Verarbeite Transaktion {txCode}...", async ctx =>
                    {
                        try
                        {
                            // Fetch Receipt details
                            var receiptApiResponse = await sumUpClient.Receipts.GetAsync(txId,
                                new SumUp.ReceiptsGetOptions { Mid = settings.ResolvedMerchantCode });
                            if (!receiptApiResponse.IsSuccess || receiptApiResponse.Data == null ||
                                receiptApiResponse.Data.TransactionData == null)
                            {
                                throw new Exception("Belegdetails konnten nicht geladen werden.");
                            }

                            var receipt = receiptApiResponse.Data;
                            var description = BuildDescription(receipt, tx.User);

                            if (!decimal.TryParse(receipt.TransactionData.Amount, CultureInfo.InvariantCulture,
                                    out var parsedAmount))
                            {
                                throw new Exception($"Betrag '{receipt.TransactionData.Amount}' konnte nicht parsen.");
                            }

                            var transactionDate = receipt.TransactionData.Timestamp?.DateTime ?? DateTime.UtcNow;
                            var referenceCode = receipt.TransactionData.TransactionCode ??
                                                tx.TransactionCode ?? receipt.TransactionData.TransactionId ?? txId;

                            // Fetch Transaction Details (TransactionFull) to find the receipt PNG URL
                            int? easyVereinInvoiceId = null;
                            try
                            {
                                var txFullApiResponse = await sumUpClient.Transactions.GetAsync(
                                    settings.ResolvedMerchantCode,
                                    new SumUp.TransactionsGetOptions { Id = txId },
                                    cancellationToken: cancellationToken);

                                if (txFullApiResponse.IsSuccess && txFullApiResponse.Data != null)
                                {
                                    var txFull = txFullApiResponse.Data;
                                    var pngLink = txFull.Links?.FirstOrDefault(l =>
                                        l.Type != null && l.Type.Contains("png", StringComparison.OrdinalIgnoreCase));

                                    if (pngLink == null && txFull.Links != null)
                                    {
                                        pngLink = txFull.Links.FirstOrDefault(l =>
                                            l.Href != null && l.Href.EndsWith(".png", StringComparison.OrdinalIgnoreCase));
                                    }

                                    if (pngLink != null && !string.IsNullOrEmpty(pngLink.Href))
                                    {
                                        AnsiConsole.MarkupLine($"   [blue]ℹ[/] Lade PNG-Beleg von SumUp herunter...");
                                        var fileBytes = await DownloadSumUpReceiptAsync(pngLink.Href, settings.ResolvedSumupToken, cancellationToken);

                                        AnsiConsole.MarkupLine($"   [blue]ℹ[/] Erstelle Beleg in easyVerein...");
                                        var invoiceId = await CreateEasyVereinInvoiceAsync(
                                            easyVereinToken: settings.ResolvedEasyVereinToken,
                                            amount: parsedAmount,
                                            date: transactionDate,
                                            description: description,
                                            receiver: "Kartenkunde (via SumUp)",
                                            referenceCode: referenceCode,
                                            cancellationToken: cancellationToken
                                        );

                                        AnsiConsole.MarkupLine($"   [blue]ℹ[/] Hochladen des Belegs ({fileBytes.Length} Bytes)...");
                                        await UploadEasyVereinInvoiceFileAsync(
                                            easyVereinToken: settings.ResolvedEasyVereinToken,
                                            invoiceId: invoiceId,
                                            fileBytes: fileBytes,
                                            filename: $"receipt_{referenceCode}.png",
                                            cancellationToken: cancellationToken
                                        );

                                        easyVereinInvoiceId = invoiceId;
                                    }
                                    else
                                    {
                                        AnsiConsole.MarkupLine($"   [yellow]⚠[/] Kein PNG-Beleglink für Transaktion {txCode} gefunden.");
                                    }
                                }
                                else
                                {
                                    AnsiConsole.MarkupLine($"   [yellow]⚠[/] Transaktionsdetails konnten nicht geladen werden.");
                                }
                            }
                            catch (Exception ex)
                            {
                                AnsiConsole.MarkupLine($"   [yellow]⚠[/] Beleg-Download/Upload übersprungen wegen Fehler: {Markup.Escape(ex.Message)}");
                            }

                            // Post to easyVerein
                            var relatedInvoiceIds = easyVereinInvoiceId.HasValue ? new[] { easyVereinInvoiceId.Value } : null;
                            await CreateEasyVereinBookingAsync(
                                easyVereinToken: settings.ResolvedEasyVereinToken,
                                amount: parsedAmount,
                                date: transactionDate,
                                billingAccountId: settings.ResolvedEasyVereinBillingAccountId,
                                description: description,
                                receiver: "Getränkeverkauf",
                                reference: referenceCode,
                                counterpartName: "Kartenkunde (via SumUp)",
                                relatedInvoiceIds: relatedInvoiceIds,
                                cancellationToken: cancellationToken
                            );

                            summaryList.Add((txId, txCode, "[green]Importiert[/]",
                                "Erfolgreich nach easyVerein gebucht", txAmountFormatted));
                            AnsiConsole.MarkupLine(
                                $" [green]✔[/] Transaktion [yellow]{txCode}[/] ({txAmountFormatted}) erfolgreich importiert.");
                        }
                        catch (Exception ex)
                        {
                            summaryList.Add((txId, txCode, "[red]Fehler[/]", ex.Message, txAmountFormatted));
                            AnsiConsole.MarkupLine(
                                $" [red]✘[/] Fehler bei Transaktion [yellow]{txCode}[/]: {Markup.Escape(ex.Message)}");
                        }
                    });
            }

            Console.WriteLine();
            AnsiConsole.Write(new Rule("[yellow]Zusammenfassung[/]").RuleStyle("grey").LeftJustified());
            Console.WriteLine();

            // 3. Render summary table
            var table = new Table().Border(TableBorder.Rounded);
            table.AddColumn("[grey]Transaktions-ID[/]");
            table.AddColumn("[bold]Code[/]");
            table.AddColumn("[bold]Status[/]");
            table.AddColumn("[bold]Menge/Betrag[/]");
            table.AddColumn("[bold]Detail[/]");

            foreach (var log in summaryList)
            {
                table.AddRow(log.TxId, log.Code, log.Status, log.Amount, log.Detail);
            }

            AnsiConsole.Write(table);
            Console.WriteLine();

            return 0;
        }

        private static string BuildDescription(SumUp.Receipt receipt, string? cashierEmail)
        {
            var sb = new StringBuilder();

            if (receipt.TransactionData?.Products != null && receipt.TransactionData.Products.Any())
            {
                sb.AppendLine("[Produkte]");
                foreach (var product in receipt.TransactionData.Products)
                {
                    var name = product.Name ?? "Unbenannt";
                    var qty = product.Quantity ?? 1.0;
                    var price = product.Price ?? "0.00";
                    var totalPrice = product.TotalPrice ?? "0.00";

                    sb.AppendLine($"{qty:G} × {name} (à {price} € = {totalPrice} €)");
                }

                sb.AppendLine();
            }

            sb.AppendLine("[Zahlung]");
            if (receipt.TransactionData?.Card != null)
            {
                var cardType = receipt.TransactionData.Card.Type ?? "Karte";
                var last4Digits = receipt.TransactionData.Card.Last4Digits ?? "****";
                sb.AppendLine($"Methode: {cardType} (****{last4Digits})");
            }
            else if (!string.IsNullOrEmpty(receipt.TransactionData?.PaymentType))
            {
                sb.AppendLine($"Methode: {receipt.TransactionData.PaymentType}");
            }

            if (!string.IsNullOrEmpty(receipt.TransactionData?.TipAmount) &&
                decimal.TryParse(receipt.TransactionData.TipAmount, CultureInfo.InvariantCulture, out var tip) &&
                tip > 0)
            {
                sb.AppendLine($"Trinkgeld: {tip:N2} €");
            }

            var txCode = receipt.TransactionData?.TransactionCode ?? "unbekannt";
            var txId = receipt.TransactionData?.TransactionId ?? "unbekannt";
            sb.AppendLine($"SumUp-Code: {txCode} (ID: {txId})");

            if (!string.IsNullOrEmpty(cashierEmail))
            {
                sb.AppendLine($"Kassierer: {cashierEmail}");
            }

            sb.AppendLine();

            if (receipt.TransactionData?.VatRates != null && receipt.TransactionData.VatRates.Any())
            {
                sb.AppendLine("[Umsatzsteuer]");
                foreach (var rateItem in receipt.TransactionData.VatRates)
                {
                    var rate = rateItem.Rate ?? 0.0;
                    var net = rateItem.Net ?? 0.0;
                    var vat = rateItem.Vat ?? 0.0;
                    var gross = rateItem.Gross ?? 0.0;

                    sb.AppendLine($"Satz {rate:G}%: Netto {net:N2} € | MwSt {vat:N2} € | Brutto {gross:N2} €");
                }
            }

            var result = sb.ToString().TrimEnd();
            return string.IsNullOrEmpty(result) ? $"SumUp Transaction {txCode}" : result;
        }

        private static async Task<byte[]> DownloadSumUpReceiptAsync(
            string url,
            string token,
            CancellationToken cancellationToken)
        {
            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/115.0.0.0 Safari/537.36");
            httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", token);

            var response = await httpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var errorText = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new Exception($"SumUp Receipt download failed with {response.StatusCode}: {errorText}");
            }

            return await response.Content.ReadAsByteArrayAsync(cancellationToken);
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
            using var httpClient = new HttpClient
            {
                BaseAddress = baseUri
            };
            httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", easyVereinToken);

            var absAmount = Math.Abs(amount);
            var payload = new
            {
                invNumber = referenceCode,
                totalPrice = absAmount,
                receiver = receiver,
                date = date.ToString("yyyy-MM-dd"),
                description = description,
                isReceipt = true,
                kind = amount >= 0 ? "revenue" : "expense",
                invoiceItems = new[]
                {
                    new
                    {
                        title = "SumUp Zahlung",
                        quantity = 1,
                        unitPrice = absAmount
                    }
                }
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
            using var httpClient = new HttpClient
            {
                BaseAddress = baseUri
            };
            httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", easyVereinToken);

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
            string counterpartName = "Kartenkunde (via SumUp)",
            int[]? relatedInvoiceIds = null,
            CancellationToken cancellationToken = default)
        {
            var baseUri = new Uri("https://easyverein.com/api/");
            using var httpClient = new HttpClient
            {
                BaseAddress = baseUri
            };
            httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", easyVereinToken);

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