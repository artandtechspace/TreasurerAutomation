using System;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Spectre.Console;
using Spectre.Console.Cli;

namespace TreasurerAutomation.Commands
{
    /// <summary>
    /// Einstellungen für die easyVerein Lese-Probe. Bewusst KEIN GlobalSettings,
    /// damit keine SumUp-Tokens verlangt werden. Führt ausschließlich GET-Requests aus.
    /// </summary>
    public class EasyVereinProbeSettings : CommandSettings
    {
        [CommandOption("--easyverein-token <VALUE>")]
        [Description("easyVerein API-Token. Überschreibt EASYVEREIN_TOKEN. Wird nur lesend verwendet.")]
        public string? EasyVereinToken { get; set; }

        [CommandOption("--endpoint <VALUE>")]
        [Description("Endpunkt ohne Version, z.B. booking, billing-account, invoice, member.")]
        [DefaultValue("booking")]
        public string Endpoint { get; set; } = "booking";

        [CommandOption("--query <VALUE>")]
        [Description("Rohe Query, z.B. 'limit=5'.")]
        [DefaultValue("limit=5")]
        public string Query { get; set; } = "limit=5";

        [CommandOption("--max-pages <VALUE>")]
        [Description("Maximale Seitenzahl beim Blättern.")]
        [DefaultValue(1)]
        public int MaxPages { get; set; }

        public string ResolvedToken =>
            Services.EasyVereinTokenResolver.Resolve(EasyVereinToken);

        public override ValidationResult Validate()
        {
            if (string.IsNullOrWhiteSpace(ResolvedToken))
                return ValidationResult.Error("Kein Token: erst 'dotnet run -- login' oder EASYVEREIN_TOKEN / --easyverein-token setzen.");
            if (string.IsNullOrWhiteSpace(Endpoint))
                return ValidationResult.Error("--endpoint darf nicht leer sein.");
            if (MaxPages < 1)
                return ValidationResult.Error("--max-pages muss >= 1 sein.");
            return ValidationResult.Success();
        }
    }

    /// <summary>
    /// Reine Lese-Probe gegen die easyVerein API: listet einen Endpunkt auf und zeigt,
    /// welche Felder die Instanz tatsächlich liefert (Vorbereitung Kassenprüfung).
    /// </summary>
    public class EasyVereinProbeCommand : AsyncCommand<EasyVereinProbeSettings>
    {
        protected override async Task<int> ExecuteAsync(CommandContext context, EasyVereinProbeSettings settings,
            CancellationToken cancellationToken)
        {
            AnsiConsole.Write(new Rule("[yellow]easyVerein Lese-Probe (nur GET)[/]").RuleStyle("grey").LeftJustified());
            Console.WriteLine();

            try
            {
                using var client = new Services.EasyVereinClient(settings.ResolvedToken);
                var items = await client.ListRawAsync(settings.Endpoint, settings.Query, settings.MaxPages, cancellationToken);

                AnsiConsole.MarkupLine($"[grey]Endpunkt:[/] {Markup.Escape(settings.Endpoint)}  [grey]Query:[/] {Markup.Escape(settings.Query)}");
                AnsiConsole.MarkupLine($"[green]Erfolg:[/] {items.Count} Datensätze geladen.");
                Console.WriteLine();

                if (items.Count == 0)
                {
                    AnsiConsole.MarkupLine("[yellow]Info:[/] Keine Datensätze – Query anpassen (z.B. Filter entfernen).");
                    return 0;
                }

                var erste = items[0];
                var schluessel = erste.ValueKind == JsonValueKind.Object
                    ? erste.EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToList()
                    : new System.Collections.Generic.List<string>();

                var tabelle = new Table().Border(TableBorder.Rounded);
                tabelle.AddColumn("[bold]Feld[/]");
                tabelle.AddColumn("[bold]Typ (1. Satz)[/]");
                foreach (var key in schluessel)
                    tabelle.AddRow(Markup.Escape(key), Markup.Escape(erste.GetProperty(key).ValueKind.ToString()));
                AnsiConsole.Write(tabelle);
                Console.WriteLine();

                var json = JsonSerializer.Serialize(erste, new JsonSerializerOptions { WriteIndented = true });
                if (json.Length > 1500)
                    json = json.Substring(0, 1500) + "\n… (gekürzt)";
                AnsiConsole.MarkupLine("[grey]Erster Datensatz (roh):[/]");
                AnsiConsole.WriteLine(json);
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                AnsiConsole.MarkupLine($"[red]✘ Fehler:[/] {Markup.Escape(ex.Message)}");
                return 1;
            }

            return 0;
        }
    }
}
