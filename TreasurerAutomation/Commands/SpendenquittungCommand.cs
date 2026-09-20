using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Spectre.Console;
using Spectre.Console.Cli;
using TreasurerAutomation.Services;

namespace TreasurerAutomation.Commands
{
    /// <summary>
    /// Einstellungen für den Spendenquittungs-Wizard. Bewusst KEIN GlobalSettings,
    /// damit keine SumUp-/easyVerein-Tokens verlangt werden.
    /// </summary>
    public class SpendenquittungSettings : CommandSettings
    {
        [CommandOption("--vorlagen-dir <PFAD>")]
        [Description("Ordner mit vorlage.typ. Default: automatische Suche ab Arbeitsverzeichnis.")]
        public string? VorlagenDir { get; set; }

        [CommandOption("--output-dir <PFAD>")]
        [Description("Zielordner für .typ/.pdf. Default: Ordner von vorlage.typ.")]
        public string? OutputDir { get; set; }

        [CommandOption("--skip-compile")]
        [Description("Nur .typ erzeugen, kein 'typst compile' ausführen.")]
        public bool SkipCompile { get; set; }
    }

    /// <summary>
    /// Interaktiver Wizard für Zuwendungsbestätigungen (Einzelbestätigung Geld/Mitgliedsbeitrag).
    /// Fragt alle Pflichtangaben ab, validiert sie, erzeugt eine spende-*.typ Datei
    /// neben vorlage.typ und kompiliert sie per typst zu PDF.
    /// </summary>
    public class SpendenquittungCommand : AsyncCommand<SpendenquittungSettings>
    {
        // Freistellungsbescheid FA Steinfurt vom 12.08.2026 für 2024, taggenau 5 Jahre gültig.
        private static readonly DateTime Freistellungsdatum = new(2026, 8, 12);

        private static readonly CultureInfo De = new("de-DE");

        protected override async Task<int> ExecuteAsync(CommandContext context, SpendenquittungSettings settings,
            CancellationToken cancellationToken)
        {
            AnsiConsole.Write(new Rule("[yellow]Zuwendungsbestätigung – Wizard[/]").RuleStyle("grey").LeftJustified());
            Console.WriteLine();

            var vorlagenDir = FindeVorlagenDir(settings.VorlagenDir);
            if (vorlagenDir == null)
            {
                AnsiConsole.MarkupLine("[red]✘ Fehler:[/] vorlage.typ nicht gefunden. Entweder aus dem Repo-Root starten oder --vorlagen-dir angeben.");
                return 1;
            }

            var outputDir = string.IsNullOrWhiteSpace(settings.OutputDir)
                ? vorlagenDir
                : Path.GetFullPath(settings.OutputDir);
            Directory.CreateDirectory(outputDir);

            // 1. Spender
            var spenderName = FrageText("Name des Zuwendenden", null, Pflicht: true);
            var spenderStrasse = FrageText("Straße + Hausnummer", null, Pflicht: true);
            var spenderPlzOrt = FrageAdresse();

            // 2. Art
            var art = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("Art der Zuwendung")
                    .AddChoices("Geldzuwendung", "Mitgliedsbeitrag"));
            var istMitgliedsbeitrag = art == "Mitgliedsbeitrag";

            // 3. Betrag (mit Zahlwort-Bestätigung)
            var betrag = FrageBetrag();
            var betragZiffern = $"{betrag.ToString("N2", De)} EUR";
            var betragBuchstaben = GermanNumberToWords.BetragInWorten(betrag);
            if (betrag <= 300)
                AnsiConsole.MarkupLine("[blue]ℹ[/] Bis 300 € würde der vereinfachte Nachweis (Kontoauszug) reichen – eine formelle Bestätigung darf trotzdem ausgestellt werden.");

            // 4. Tag der Zuwendung
            var tagZuwendung = FrageDatum("Tag der Zuwendung", DateTime.Today);
            if (tagZuwendung > DateTime.Today)
            {
                AnsiConsole.MarkupLine("[yellow]⚠[/] Das Zuwendungsdatum liegt in der Zukunft.");
                if (!Bestaetigen("Trotzdem fortfahren?", false))
                    return 1;
            }

            // 5. Verzicht (Aufwandsspende)
            var verzicht = AnsiConsole.Prompt(new ConfirmationPrompt("Verzicht auf Erstattung von Aufwendungen? (Aufwandsspende)") { DefaultValue = false });
            if (verzicht)
                AnsiConsole.MarkupLine("[yellow]⚠[/] Aufwandsspende: vorheriger wirksamer Anspruch (Vertrag/Beschluss) + schriftliche Verzichtserklärung müssen vorliegen und mit abgelegt werden.");

            // 6. Optionaler Projektvermerk (kommt NICHT auf den Beleg)
            var projekt = AnsiConsole.Prompt(new TextPrompt<string>("Projektvermerk / Zweckbindung [gray](optional, nur für Kommentar + Ablage)[/]").AllowEmpty());
            if (!string.IsNullOrWhiteSpace(projekt))
                AnsiConsole.MarkupLine("[blue]ℹ[/] Der Vermerk steht nicht auf der Bestätigung (kein Muster-Feld) – bitte zusätzlich ins Begleitschreiben + Ablage.");

            // 7. Ausstellung
            var ort = FrageText("Ausstellungsort", "Rheine", Pflicht: true);
            var ausstellungsdatum = FrageDatum("Ausstellungsdatum", DateTime.Today);
            if (ausstellungsdatum < tagZuwendung)
            {
                AnsiConsole.MarkupLine("[red]✘ Fehler:[/] Ausstellungsdatum liegt vor dem Tag der Zuwendung.");
                return 1;
            }
            if (ausstellungsdatum >= Freistellungsdatum.AddYears(5))
            {
                AnsiConsole.MarkupLine($"[red]✘ Fehler:[/] Freistellungsbescheid vom {Freistellungsdatum:dd.MM.yyyy} ist am {ausstellungsdatum:dd.MM.yyyy} älter als 5 Jahre (§ 63 Abs. 5 AO) – keine gültige Bestätigung möglich.");
                return 1;
            }

            // 8. Unterschriften (Gesamtvertretung § 9 Abs. 2 Satzung)
            AnsiConsole.MarkupLine("[grey]Es sind zwei Unterschriften nötig (Gesamtvertretung).[/]");
            var u1Name = FrageText("1. Unterschrift – Name", "Luca Schöneberg", Pflicht: true);
            var u1Funktion = FrageText("1. Unterschrift – Funktion", "Kassenwart", Pflicht: true);
            var u2Name = FrageText("2. Unterschrift – Name", "Jascha Wallmeier", Pflicht: true);
            var u2Funktion = FrageText("2. Unterschrift – Funktion", "Vorstandsvorsitzender", Pflicht: true);

            // 9. Beleg-Nr.
            var vorschlag = NaechsteBelegNr(outputDir, ausstellungsdatum.Year);
            var belegNr = FrageText("Beleg-Nr.", vorschlag, Pflicht: true);
            while (!Regex.IsMatch(belegNr, @"^\d{4}-\d+$"))
            {
                AnsiConsole.MarkupLine("[red]✘[/] Format: JJJJ-NNN (z.B. 2026-002).");
                belegNr = FrageText("Beleg-Nr.", vorschlag, Pflicht: true);
            }

            var dateiname = $"spende-{belegNr}-{SpendenquittungFileBuilder.Slug(spenderName)}";
            var typPfad = Path.Combine(outputDir, dateiname + ".typ");
            var pdfPfad = Path.Combine(outputDir, dateiname + ".pdf");
            if ((File.Exists(typPfad) || File.Exists(pdfPfad)) && !Bestaetigen($"[yellow]'{dateiname}' existiert bereits. Überschreiben?[/]", false))
                return 1;

            // 10. Zusammenfassung + Freigabe
            ZeigeZusammenfassung(spenderName, spenderStrasse, spenderPlzOrt, art, betragZiffern,
                betragBuchstaben, tagZuwendung, verzicht, projekt, ort, ausstellungsdatum,
                u1Name, u1Funktion, u2Name, u2Funktion, belegNr);
            if (!Bestaetigen("Bestätigung erzeugen?", true))
            {
                AnsiConsole.MarkupLine("[yellow]Abgebrochen – nichts geschrieben.[/]");
                return 1;
            }

            var daten = new SpendenquittungDaten(
                spenderName, spenderStrasse, spenderPlzOrt,
                betragZiffern, betragBuchstaben, tagZuwendung.ToString("dd.MM.yyyy"),
                verzicht, istMitgliedsbeitrag,
                ort, ausstellungsdatum.ToString("dd.MM.yyyy"),
                u1Name, u1Funktion, u2Name, u2Funktion,
                belegNr, string.IsNullOrWhiteSpace(projekt) ? null : projekt.Trim());

            var importPfad = Path.GetRelativePath(outputDir, Path.Combine(vorlagenDir, "vorlage.typ"))
                .Replace(Path.DirectorySeparatorChar, '/');
            await File.WriteAllTextAsync(typPfad, SpendenquittungFileBuilder.Build(daten, importPfad), cancellationToken);
            AnsiConsole.MarkupLine($"[green]✔[/] Vorlage geschrieben: {Markup.Escape(typPfad)}");

            if (!settings.SkipCompile)
            {
                var (gefunden, ok, ausgabe) = Kompiliere(typPfad, pdfPfad);
                if (!gefunden)
                {
                    AnsiConsole.MarkupLine("[yellow]⚠[/] 'typst' nicht im PATH gefunden. Nur .typ erzeugt – manuell kompilieren mit:");
                    AnsiConsole.MarkupLine($"[grey]typst compile \"{Markup.Escape(typPfad)}\" \"{Markup.Escape(pdfPfad)}\"[/]");
                }
                else if (!ok)
                {
                    AnsiConsole.MarkupLine($"[red]✘ typst compile fehlgeschlagen:[/] {Markup.Escape(ausgabe.Trim())}");
                    return 1;
                }
                else
                {
                    AnsiConsole.MarkupLine($"[green]✔[/] PDF erzeugt: {Markup.Escape(pdfPfad)}");
                }
            }

            Console.WriteLine();
            AnsiConsole.MarkupLine("[grey]Nächste Schritte: 2x unterschreiben lassen, Doppel + Kontoauszug 10 Jahre ablegen.[/]");
            return 0;
        }

        private static string FrageText(string label, string? defaultWert, bool Pflicht)
        {
            while (true)
            {
                var prompt = new TextPrompt<string>(label);
                if (defaultWert != null)
                    prompt.DefaultValue(defaultWert);
                var wert = AnsiConsole.Prompt(prompt).Trim();
                if (!Pflicht || !string.IsNullOrWhiteSpace(wert))
                    return wert;
                AnsiConsole.MarkupLine("[red]✘[/] Pflichtangabe – bitte ausfüllen.");
            }
        }

        private static string FrageAdresse()
        {
            while (true)
            {
                var wert = FrageText("PLZ + Ort (z.B. 48431 Rheine)", null, Pflicht: true);
                if (Regex.IsMatch(wert, @"^\d{5}\s+\S"))
                    return wert;
                AnsiConsole.MarkupLine("[yellow]⚠[/] Sieht nicht wie 'PLZ Ort' aus.");
                if (Bestaetigen("Trotzdem übernehmen?", true))
                    return wert;
            }
        }

        private static decimal FrageBetrag()
        {
            while (true)
            {
                var roh = AnsiConsole.Prompt(new TextPrompt<string>("Betrag in EUR (z.B. 1.000,00)").Validate(_ => ValidationResult.Success()));
                var betrag = ParseBetrag(roh);
                if (betrag == null)
                {
                    AnsiConsole.MarkupLine("[red]✘[/] Betrag nicht erkannt – Format z.B. 150,00 oder 1.000,00.");
                    continue;
                }
                if (betrag.Value <= 0)
                {
                    AnsiConsole.MarkupLine("[red]✘[/] Betrag muss größer als 0 sein.");
                    continue;
                }
                string worte;
                try
                {
                    worte = GermanNumberToWords.BetragInWorten(betrag.Value);
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[red]✘[/] {Markup.Escape(ex.Message)}");
                    continue;
                }
                AnsiConsole.MarkupLine($"[blue]ℹ[/] {Markup.Escape($"{betrag.Value.ToString("N2", De)} EUR")} → {Markup.Escape(worte)}");
                if (Bestaetigen("Betrag übernehmen?", true))
                    return betrag.Value;
            }
        }

        public static decimal? ParseBetrag(string roh)
        {
            var s = roh.Trim()
                .Replace("€", "", StringComparison.Ordinal)
                .Replace("EUR", "", StringComparison.OrdinalIgnoreCase)
                .Trim();
            // Eindeutig deutsch: Komma vorhanden (',' = Dezimal, '.' = Tausender).
            if (s.Contains(','))
                return TryParseDe(s);
            // Nur Punkt: nur bei strikter Tausendergruppierung (z.B. "1.000") deutsch,
            // sonst (z.B. "1000.50" aus Bank-/SumUp-Export) als Dezimalpunkt lesen.
            if (s.Contains('.'))
            {
                if (Regex.IsMatch(s, @"^\d{1,3}(\.\d{3})+$"))
                    return TryParseDe(s);
                return TryParseInv(s);
            }
            return TryParseDe(s) ?? TryParseInv(s);
        }

        private static decimal? TryParseDe(string s) =>
            decimal.TryParse(s, NumberStyles.Number, De, out var v) && decimal.Round(v, 2) == v ? v : null;

        private static decimal? TryParseInv(string s) =>
            decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) && decimal.Round(v, 2) == v ? v : null;

        private static DateTime FrageDatum(string label, DateTime defaultWert)
        {
            while (true)
            {
                var prompt = new TextPrompt<string>($"{label} [gray](TT.MM.JJJJ)[/]")
                    .DefaultValue(defaultWert.ToString("dd.MM.yyyy"));
                var wert = AnsiConsole.Prompt(prompt).Trim();
                if (DateTime.TryParseExact(wert, "dd.MM.yyyy", De, DateTimeStyles.None, out var datum))
                    return datum;
                AnsiConsole.MarkupLine("[red]✘[/] Format TT.MM.JJJJ erwartet (z.B. 14.09.2026).");
            }
        }

        private static bool Bestaetigen(string frage, bool defaultWert) =>
            AnsiConsole.Prompt(new ConfirmationPrompt(frage) { DefaultValue = defaultWert });

        private static string NaechsteBelegNr(string outputDir, int jahr)
        {
            var max = 0;
            if (Directory.Exists(outputDir))
            {
                foreach (var datei in Directory.GetFiles(outputDir, $"spende-{jahr}-*"))
                {
                    var m = Regex.Match(Path.GetFileName(datei), @"^spende-(\d{4})-(\d+)-");
                    if (m.Success && int.TryParse(m.Groups[2].Value, out var nr) && nr > max)
                        max = nr;
                }
            }
            return $"{jahr}-{(max + 1):000}";
        }

        private static string? FindeVorlagenDir(string? option)
        {
            if (!string.IsNullOrWhiteSpace(option))
                return Directory.Exists(option) ? Path.GetFullPath(option) : null;
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null)
            {
                var kandidat = Path.Combine(dir.FullName, "vorlagen", "zuwendungsbestaetigung", "vorlage.typ");
                if (File.Exists(kandidat))
                    return Path.GetDirectoryName(kandidat);
                if (File.Exists(Path.Combine(dir.FullName, "vorlage.typ")))
                    return dir.FullName;
                dir = dir.Parent;
            }
            return null;
        }

        private static void ZeigeZusammenfassung(
            string name, string strasse, string plzOrt, string art,
            string ziffern, string buchstaben, DateTime tag, bool verzicht,
            string projekt, string ort, DateTime ausstellung,
            string u1n, string u1f, string u2n, string u2f, string belegNr)
        {
            Console.WriteLine();
            var tabelle = new Table().Border(TableBorder.Rounded);
            tabelle.AddColumn("[grey]Feld[/]");
            tabelle.AddColumn("[bold]Wert[/]");
            tabelle.AddRow("Zuwendender", $"{Markup.Escape(name)}\n{Markup.Escape(strasse)}\n{Markup.Escape(plzOrt)}");
            tabelle.AddRow("Art", Markup.Escape(art));
            tabelle.AddRow("Betrag", $"{Markup.Escape(ziffern)} ({Markup.Escape(buchstaben)})");
            tabelle.AddRow("Tag der Zuwendung", tag.ToString("dd.MM.yyyy"));
            tabelle.AddRow("Verzicht", verzicht ? "Ja (Aufwandsspende)" : "Nein");
            if (!string.IsNullOrWhiteSpace(projekt))
                tabelle.AddRow("Projektvermerk", Markup.Escape(projekt.Trim()) + " [grey](nur Ablage)[/]");
            tabelle.AddRow("Ausstellung", $"{Markup.Escape(ort)}, {ausstellung:dd.MM.yyyy}");
            tabelle.AddRow("Unterschrift 1", $"{Markup.Escape(u1n)}, {Markup.Escape(u1f)}");
            tabelle.AddRow("Unterschrift 2", $"{Markup.Escape(u2n)}, {Markup.Escape(u2f)}");
            tabelle.AddRow("Beleg-Nr.", Markup.Escape(belegNr));
            AnsiConsole.Write(tabelle);
            Console.WriteLine();
        }

        private static (bool Gefunden, bool Ok, string Ausgabe) Kompiliere(string typPfad, string pdfPfad)
        {
            var psi = new ProcessStartInfo("typst", $"compile \"{typPfad}\" \"{pdfPfad}\"")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            try
            {
                using var prozess = Process.Start(psi);
                if (prozess == null)
                    return (true, false, "Prozess konnte nicht gestartet werden.");
                prozess.WaitForExit();
                var ausgabe = prozess.StandardOutput.ReadToEnd() + prozess.StandardError.ReadToEnd();
                return (true, prozess.ExitCode == 0, ausgabe);
            }
            catch (Win32Exception)
            {
                return (false, false, string.Empty);
            }
        }
    }
}
