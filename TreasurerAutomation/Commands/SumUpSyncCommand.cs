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

                            // Post to easyVerein
                            await CreateEasyVereinBookingAsync(
                                easyVereinToken: settings.ResolvedEasyVereinToken,
                                amount: parsedAmount,
                                date: transactionDate,
                                billingAccountId: settings.ResolvedEasyVereinBillingAccountId,
                                description: description,
                                receiver: "Getränkeverkauf",
                                reference: referenceCode,
                                counterpartName: "Kartenkunde (via SumUp)"
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
                                $" [red]✘[/] Fehler bei Transaktion [yellow]{txCode}[/]: {ex.Message}");
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

        private static async Task CreateEasyVereinBookingAsync(
            string easyVereinToken,
            decimal amount,
            DateTime date,
            int billingAccountId,
            string description,
            string receiver,
            string reference,
            string counterpartName = "Kartenkunde (via SumUp)",
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
                sphere = 0
            };
            var json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await httpClient.PostAsync("v2.0/booking", content, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var respText = await response.Content.ReadAsStringAsync();
                throw new Exception($"easyVerein API returned {response.StatusCode}: {respText}");
            }
        }
    }
}