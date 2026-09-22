using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Spectre.Console;
using Spectre.Console.Cli;
using TreasurerAutomation.Services;
using TreasurerAutomation.Services.MemberAudit;

namespace TreasurerAutomation.Commands
{
    public sealed class MemberFixSettings : CommandSettings
    {
        [CommandOption("--easyverein-token <VALUE>")]
        [Description("API-Token (statt EASYVEREIN_TOKEN/login).")]
        public string? EasyVereinToken { get; set; }

        [CommandOption("--beitrag-jahr <JAHR>")]
        [Description("Beitragsjahr (sonst aktuelles Jahr).")]
        public int? BeitragJahr { get; set; }

        [CommandOption("--max-pages <N>")]
        [Description("Max. API-Seiten.")]
        [DefaultValue(20)]
        public int MaxPages { get; set; } = 20;

        [CommandOption("--limit <N>")]
        [Description("Seitengröße pro Request.")]
        [DefaultValue(100)]
        public int Limit { get; set; } = 100;

        [CommandOption("--search <TEXT>")]
        [Description("Filter Name/E-Mail (?search=).")]
        public string? Search { get; set; }

        [CommandOption("--member <ID>")]
        [Description("Nur ein Mitglied (ID/Nummer).")]
        public string? Member { get; set; }

        [CommandOption("--nur-probleme")]
        [Description("Nur Mitglieder mit Blocker/Warnung bearbeiten.")]
        public bool NurProbleme { get; set; }

        [CommandOption("--apply")]
        [Description("Schreibt wirklich in easyVerein (PATCH). Ohne --apply: Dry-Run, nichts wird geschrieben.")]
        public bool Apply { get; set; }

        [CommandOption("--auto-only")]
        [Description("Nur sichere Auto-Fixes (keine Rückfragen, kein SEPA-Mandat).")]
        public bool AutoOnly { get; set; }

        [CommandOption("--yes")]
        [Description("Alle Vorschläge ohne Rückfrage übernehmen (nur mit --apply schreibend).")]
        public bool Yes { get; set; }

        [CommandOption("--sepa-mandat")]
        [Description("SEPA-Mandate (.typ/.pdf) für Mitglieder mit SEPA-Blocker erzeugen.")]
        public bool SepaMandat { get; set; }

        [CommandOption("--vorlagen-dir <PFAD>")]
        [Description("Ordner mit sepa vorlage.typ. Default: automatische Suche.")]
        public string? VorlagenDir { get; set; }

        [CommandOption("--output-dir <PFAD>")]
        [Description("Zielordner für Mandate + Anfragen-CSV. Default: Ordner von vorlage.typ.")]
        public string? OutputDir { get; set; }

        [CommandOption("--skip-compile")]
        [Description("Nur .typ erzeugen, kein 'typst compile'.")]
        public bool SkipCompile { get; set; }

        [CommandOption("--anfragen <PFAD>")]
        [Description("CSV-Ziel für geparkte Anfragen (Default: fix-anfragen-JJJJ.csv im Output-Ordner).")]
        public string? Anfragen { get; set; }

        [CommandOption("--glaeubiger-id <ID>")]
        [Description("Gläubiger-ID des Vereins für SEPA-Mandate.")]
        public string? GlaeubigerId { get; set; }

        public string ResolvedToken =>
            Services.EasyVereinTokenResolver.Resolve(EasyVereinToken);

        public override ValidationResult Validate()
        {
            if (string.IsNullOrWhiteSpace(ResolvedToken))
                return ValidationResult.Error("Kein Token: erst 'dotnet run -- login' oder EASYVEREIN_TOKEN / --easyverein-token setzen.");
            if (MaxPages < 1) return ValidationResult.Error("--max-pages muss >= 1 sein.");
            if (Limit is < 1 or > 1000) return ValidationResult.Error("--limit muss 1..1000 sein.");
            var jahr = BeitragJahr ?? DateTime.Today.Year;
            if (jahr is < 2020 or > 2100) return ValidationResult.Error("--beitrag-jahr unplausibel.");
            return ValidationResult.Success();
        }
    }

    /// <summary>
    /// Interaktiver Fix-Wizard auf Basis von member-audit + FixPlanner.
    /// Dry-Run ist Standard: ohne --apply wird NICHTS in easyVerein geschrieben
    /// (nur angezeigt + lokale .typ/.csv-Dateien nach Bestätigung).
    /// Safe Auto-Fixes (PATCH contact-details): EMAIL_NORM, MANDATSREF_NEU,
    /// ZAHLART_LASTSCHRIFT. Alles andere (IBAN/Mandat/Beitragsklasse) wird
    /// entweder nachgefragt oder als Anfrage geparkt + ggf. SEPA-Mandat erzeugt.
    /// </summary>
    public sealed class MemberFixCommand : AsyncCommand<MemberFixSettings>
    {
        private const int MaxParallelMembers = 10;
        private const string DefaultVereinName = "ARTandTECH.space e.V.";
        private const string DefaultVereinAdresse = "Rheine";
        private const string DefaultGlaeubigerId = "DE00ZZZ00000000000";

        private sealed record PatchAuftrag(
            FixVorschlag Plan,
            string ContactId,
            Dictionary<string, object?> Felder);

        private sealed record Anfrage(
            string Mitgliedsnummer,
            string Name,
            string Email,
            string Aktion,
            string Notiz);

        protected override async Task<int> ExecuteAsync(CommandContext context, MemberFixSettings settings,
            CancellationToken cancellationToken)
        {
            var jahr = settings.BeitragJahr ?? DateTime.Today.Year;
            var heute = DateTime.Today;
            var interaktiv = AnsiConsole.Profile.Capabilities.Interactive && !settings.AutoOnly && !settings.Yes;

            ConsoleHelper.PrintHeader($"Mitglieder-Fix {jahr} (dry-run ohne --apply)");

            try
            {
                using var client = new EasyVereinClient(settings.ResolvedToken);

                var (resultsAlle, kontaktIds) = await LadeAuditAsync(client, settings, jahr, heute, cancellationToken);
                var results = settings.NurProbleme
                    ? resultsAlle.Where(r => r.Findings.Any(f => f.Severity != FindingSeverity.Info)).ToList()
                    : resultsAlle;
                if (results.Count == 0)
                {
                    AnsiConsole.MarkupLine("[yellow]Keine Mitglieder gefunden.[/]");
                    return 0;
                }

                var plaene = FixPlanner.Plane(results, jahr);
                var autos = plaene.Where(p => p.Auto).ToList();
                var manuell = plaene.Where(p => !p.Auto).ToList();
                ZeigePlaene(autos, manuell);

                var nachId = results.ToDictionary(r => r.Mitglied.Id);
                var warteschlange = new List<PatchAuftrag>();
                var anfragen = new List<Anfrage>();

                // 1. Safe Auto-Fixes triagieren (EMAIL_NORM, MANDATSREF_NEU, ZAHLART_LASTSCHRIFT)
                foreach (var plan in autos)
                {
                    var uebernehmen = settings.Yes || !interaktiv || Bestaetigen(
                        $"[grey]{Markup.Escape(plan.DisplayName)}:[/] {Markup.Escape(plan.Aktion)} {Markup.Escape(plan.Alt)} → {Markup.Escape(plan.Neu)} übernehmen?", true);
                    if (!uebernehmen) continue;
                    if (!kontaktIds.TryGetValue(plan.MemberId, out var cid) || string.IsNullOrWhiteSpace(cid))
                    {
                        anfragen.Add(new(plan.MembershipNumber ?? plan.MemberId.ToString(), plan.DisplayName,
                            EmailVon(nachId, plan.MemberId), plan.Aktion, "keine contactDetails-ID gefunden – manuell in easyVerein prüfen"));
                        continue;
                    }
                    warteschlange.Add(new(plan, cid!, FelderFuer(plan)));
                }

                // 2. IBAN/Mandatsdatum nachfragen (nur interaktiv, nie erfinden)
                if (interaktiv && !settings.AutoOnly)
                    SammleWerteAbfrage(nachId, kontaktIds, warteschlange, anfragen);

                // 3. Restliche manuelle Todos parken (Beitragsklasse, Nachweise, Einwilligung)
                foreach (var plan in manuell)
                {
                    if (settings.Yes) { Parke(plan, nachId, anfragen, ""); continue; }
                    if (!interaktiv) { Parke(plan, nachId, anfragen, ""); continue; }
                    var notiz = AnsiConsole.Prompt(new TextPrompt<string>(
                        $"[grey]{Markup.Escape(plan.DisplayName)}:[/] {Markup.Escape(plan.Aktion)} – {Markup.Escape(plan.Begruendung)} [grey](Notiz leer = parken, 's' = skip)[/]")
                        .AllowEmpty());
                    if (notiz.Trim().Equals("s", StringComparison.OrdinalIgnoreCase)) continue;
                    Parke(plan, nachId, anfragen, notiz.Trim());
                }

                // 4. SEPA-Mandate erzeugen (lokale Dateien, kein API-Schreiben)
                var mandatKandidaten = MandatKandidaten(results);
                if (settings.SepaMandat || (interaktiv && !settings.AutoOnly && mandatKandidaten.Count > 0))
                {
                    var erzeugen = settings.SepaMandat || Bestaetigen(
                        $"{mandatKandidaten.Count} Mitglied(er) mit SEPA-Blocker – Mandat-Formulare (.typ/.pdf) erzeugen?", true);
                    if (erzeugen)
                        await ErzeugeMandateAsync(mandatKandidaten, settings, jahr, heute, cancellationToken);
                }

                // 5. Anfragen-CSV schreiben (lokal, ungefährlich)
                if (anfragen.Count > 0)
                    SchreibeAnfragen(anfragen, settings, jahr);

                // 6. Dry-Run vs. Apply (API-Schreiben nur mit --apply)
                if (warteschlange.Count == 0)
                {
                    AnsiConsole.MarkupLine("[grey]Keine API-Änderungen in der Warteschlange.[/]");
                    DruckeNaechsteSchritte(anfragen.Count > 0);
                    return 0;
                }

                ZeigeWarteschlange(warteschlange);
                if (!settings.Apply)
                {
                    AnsiConsole.MarkupLine("[yellow]Dry-Run – nichts geschrieben.[/] Mit [bold]--apply[/] (ggf. + --yes) erneut aufrufen, um zu schreiben.");
                    return 0;
                }

                if (!settings.Yes && interaktiv &&
                    !Bestaetigen($"Wirklich {warteschlange.Count} PATCH(es) nach easyVerein schreiben?", false))
                {
                    AnsiConsole.MarkupLine("[yellow]Abgebrochen – nichts geschrieben.[/]");
                    return 1;
                }

                var (ok, fehler) = await SchreibePatchesAsync(client, warteschlange, cancellationToken);
                AnsiConsole.MarkupLine($"[green]✔ {ok} geschrieben[/]" + (fehler > 0 ? $", [red]✘ {fehler} fehlgeschlagen[/]" : ""));
                if (client.TokenRefreshNeeded)
                    AnsiConsole.MarkupLine("[grey]Hinweis: Token-Refresh fällig – 'dotnet run -- auth-status --refresh'.[/]");
                DruckeNaechsteSchritte(anfragen.Count > 0);
                return fehler > 0 ? 1 : 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                AnsiConsole.MarkupLine($"[red]✘ Fehler:[/] {Markup.Escape(ex.Message)}");
                return 1;
            }
        }

        // ---------- Audit laden (nur GET, wie member-audit) ----------

        private static async Task<(List<MemberAuditResult> Results, Dictionary<int, string?> KontaktIds)> LadeAuditAsync(
            EasyVereinClient client, MemberFixSettings settings, int jahr, DateTime heute, CancellationToken ct)
        {
            var gruppenLookup = await MemberAuditCommand.LadeLookup(client, "member-group", ct);
            var customDefLookup = await MemberAuditCommand.LadeLookup(client, "custom-field", ct);

            List<JsonElement> members;
            if (!string.IsNullOrWhiteSpace(settings.Member))
            {
                var single = await LadeEinzelJson(client, settings.Member.Trim(), ct);
                members = single.HasValue ? new() { single.Value } : new();
            }
            else
            {
                var query = $"limit={settings.Limit}";
                if (!string.IsNullOrWhiteSpace(settings.Search))
                    query += $"&search={Uri.EscapeDataString(settings.Search.Trim())}";
                members = (await client.ListRawAsync("member", query, settings.MaxPages, ct)).ToList();
            }

            var records = new MemberAuditResult[members.Count];
            var kontaktIds = new Dictionary<int, string?>();
            await AnsiConsole.Status().Spinner(Spinner.Known.Dots).SpinnerStyle(Style.Parse("yellow bold"))
                .StartAsync("Lade Details (Kontakt, Gruppen, Nachweise)…", async ctx =>
                {
                    var fertig = 0;
                    var sperre = new object();
                    await Parallel.ForEachAsync(
                        Enumerable.Range(0, members.Count),
                        new ParallelOptions { MaxDegreeOfParallelism = MaxParallelMembers, CancellationToken = ct },
                        async (i, c) =>
                        {
                            var m = members[i];
                            var (cd, kuerzel, namen, customs) = await MemberAuditCommand.Anreichern(client, m, gruppenLookup, customDefLookup, c);
                            var rec = EasyVereinMemberParser.Parse(m, cd, kuerzel, namen, customs);
                            records[i] = MemberAuditService.Audit(rec, jahr, heute);
                            lock (sperre)
                            {
                                kontaktIds[rec.Id] = KontaktIdAus(m, cd);
                                ctx.Status($"Lade Details ({Interlocked.Increment(ref fertig)}/{members.Count})…");
                            }
                        });
                });

            var list = new List<MemberAuditResult>();
            var paare = new List<(JsonElement Json, MemberRecord Record)>();
            for (var i = 0; i < members.Count; i++)
            {
                if (records[i] is null) continue;
                list.Add(records[i]);
                paare.Add((members[i], records[i].Mitglied));
            }
            var technischUnvollstaendig = MemberAuditCommand.ErgänzeTechnikFindings(paare, list);
            MemberAuditCommand.ErgänzeDuplikatFindings(list);
            AnsiConsole.MarkupLine($"[grey]Mitglieder:[/] {list.Count}  [red]mit Blocker:[/] {list.Count(r => r.BlockerCount > 0)}");
            if (technischUnvollstaendig > 0)
                AnsiConsole.MarkupLine($"[yellow]⚠ Bei {technischUnvollstaendig} Mitglied(ern) konnten Kontakt-/Gruppendaten nicht geladen werden (Rate-Limit). " +
                    $"Befunde dort ggf. unvollständig – Lauf wiederholen.[/]");
            return (list, kontaktIds);
        }

        private static async Task<JsonElement?> LadeEinzelJson(EasyVereinClient client, string eingabe, CancellationToken ct)
        {
            if (int.TryParse(eingabe, out _))
            {
                try
                {
                    var direkt = await client.GetSingleRawAsync($"member/{eingabe}", ct);
                    if (direkt.HasValue) return direkt;
                }
                catch { /* Fallback Suche */ }
            }
            try
            {
                var treffer = await client.ListRawAsync("member", $"limit=10&search={Uri.EscapeDataString(eingabe)}", maxPages: 1, ct);
                if (treffer.Count == 0) return null;
                var perNummer = treffer.FirstOrDefault(t =>
                    string.Equals(EasyVereinMemberParser.GetString(t, "membershipNumber")?.Trim(), eingabe.Trim(), StringComparison.OrdinalIgnoreCase));
                if (perNummer.ValueKind != JsonValueKind.Undefined) return perNummer;
                return treffer[0];
            }
            catch { return null; }
        }

        /// <summary>contactDetails-PK für PATCH contact-details/{id} (URL-Ref, int oder eingebettete id).</summary>
        internal static string? KontaktIdAus(JsonElement member, JsonElement? kontakt)
        {
            foreach (var prop in member.EnumerateObject())
            {
                if (!string.Equals(prop.Name, "contactDetails", StringComparison.OrdinalIgnoreCase)) continue;
                var pk = MemberAuditCommand.ExtractPk(prop.Value);
                if (pk != null) return pk;
            }
            if (kontakt.HasValue)
            {
                var id = EasyVereinMemberParser.GetInt(kontakt.Value, "id")?.ToString();
                if (id != null) return id;
            }
            return null;
        }

        // ---------- Anzeige ----------

        private static void ZeigePlaene(List<FixVorschlag> autos, List<FixVorschlag> manuell)
        {
            Console.WriteLine();
            var tabelle = new Table().Border(TableBorder.Rounded);
            tabelle.AddColumn("[bold]Mitglied[/]");
            tabelle.AddColumn("[bold]Aktion[/]");
            tabelle.AddColumn("[bold]Neu[/]");
            tabelle.AddColumn("[bold]Art[/]");
            foreach (var p in autos)
                tabelle.AddRow(Markup.Escape(p.DisplayName), Markup.Escape(p.Aktion), Markup.Escape(p.Neu), "[green]auto[/]");
            foreach (var p in manuell)
                tabelle.AddRow(Markup.Escape(p.DisplayName), Markup.Escape(p.Aktion), Markup.Escape(p.Neu), "[yellow]manuell[/]");
            if (autos.Count + manuell.Count == 0)
                AnsiConsole.MarkupLine("[green]✔ Keine Fix-Vorschläge – alles sauber.[/]");
            else
                AnsiConsole.Write(tabelle);
        }

        private static void ZeigeWarteschlange(List<PatchAuftrag> warteschlange)
        {
            Console.WriteLine();
            AnsiConsole.Write(new Rule("[yellow]PATCH-Warteschlange[/]").RuleStyle("grey").LeftJustified());
            foreach (var a in warteschlange)
            {
                var json = JsonSerializer.Serialize(a.Felder);
                AnsiConsole.MarkupLine($"[grey]PATCH[/] contact-details/{Markup.Escape(a.ContactId)} {Markup.Escape(json)} [grey]({Markup.Escape(a.Plan.DisplayName)}: {Markup.Escape(a.Plan.Aktion)})[/]");
            }
        }

        // ---------- Interaktive Werte-Abfrage (IBAN, Mandatsdatum) ----------

        private static void SammleWerteAbfrage(
            Dictionary<int, MemberAuditResult> nachId,
            Dictionary<int, string?> kontaktIds,
            List<PatchAuftrag> warteschlange,
            List<Anfrage> anfragen)
        {
            foreach (var r in nachId.Values
                         .Where(r => r.BlockerCount > 0 && r.Mitglied.Zahlungsart == 1)
                         .OrderByDescending(r => r.BlockerCount))
            {
                var m = r.Mitglied;
                // IBAN fehlt/ungültig -> jetzt eingeben oder parken
                if (r.Findings.Any(f => f.Code == "SEPA_IBAN_FEHLT"))
                {
                    var eingabe = AnsiConsole.Prompt(new TextPrompt<string>(
                        $"IBAN für [grey]{Markup.Escape(m.DisplayName)}[/] [grey](leer = parken)[/]").AllowEmpty()).Trim();
                    if (string.IsNullOrWhiteSpace(eingabe))
                    {
                        anfragen.Add(new(m.MembershipNumber ?? m.Id.ToString(), m.DisplayName, m.PrimaereEmail ?? "",
                            "IBAN_ANFORDERN", "IBAN beim Mitglied anfordern (SEPA §7 Abs. 3)"));
                    }
                    else if (!FieldValidators.IstGueltigeIban(eingabe))
                    {
                        AnsiConsole.MarkupLine("[red]✘[/] IBAN ungültig (Mod97) – geparkt statt geschrieben.");
                        anfragen.Add(new(m.MembershipNumber ?? m.Id.ToString(), m.DisplayName, m.PrimaereEmail ?? "",
                            "IBAN_ANFORDERN", $"Eingegebene IBAN ungültig: {eingabe}"));
                    }
                    else if (!kontaktIds.TryGetValue(m.Id, out var cid) || string.IsNullOrWhiteSpace(cid))
                    {
                        anfragen.Add(new(m.MembershipNumber ?? m.Id.ToString(), m.DisplayName, m.PrimaereEmail ?? "",
                            "IBAN_ANFORDERN", "keine contactDetails-ID gefunden"));
                    }
                    else
                    {
                        var felder = new Dictionary<string, object?> { ["iban"] = FieldValidators.NormalisiereIban(eingabe) };
                        if (FieldValidators.IstAuslandsIban(eingabe) && string.IsNullOrWhiteSpace(m.Bic))
                        {
                            var bic = AnsiConsole.Prompt(new TextPrompt<string>("BIC (Auslands-IBAN, Pflicht)").AllowEmpty()).Trim().ToUpperInvariant();
                            if (!FieldValidators.IstGueltigeBic(bic))
                            {
                                AnsiConsole.MarkupLine("[red]✘[/] BIC ungültig – IBAN wird ohne BIC geparkt.");
                                anfragen.Add(new(m.MembershipNumber ?? m.Id.ToString(), m.DisplayName, m.PrimaereEmail ?? "",
                                    "BIC_ANFORDERN", $"IBAN {FieldValidators.NormalisiereIban(eingabe)} ok, BIC fehlt/ungültig"));
                                continue;
                            }
                            felder["bic"] = bic;
                        }
                        warteschlange.Add(new(new(m.Id, m.MembershipNumber, m.DisplayName, "IBAN_NEU",
                            "iban", m.Iban ?? "– fehlt", felder["iban"]?.ToString() ?? "", true, "Interaktiv erfasst + Mod97-geprüft."),
                            cid!, felder));
                    }
                }

                // Mandatsdatum fehlt -> heute vorschlagen oder parken
                if (r.Findings.Any(f => f.Code == "SEPA_MANDATSDATUM_FEHLT")
                    && warteschlange.All(a => !(a.Plan.MemberId == m.Id && a.Plan.Aktion == "IBAN_NEU"))
                    && !string.IsNullOrWhiteSpace(m.Iban) && m.SepaEinverstaendnis == true)
                {
                    var datumRoh = AnsiConsole.Prompt(new TextPrompt<string>(
                        $"Mandatsdatum für [grey]{Markup.Escape(m.DisplayName)}[/] [grey](TT.MM.JJJJ, default heute, leer = parken)[/]")
                        .DefaultValue(DateTime.Today.ToString("dd.MM.yyyy"))
                        .AllowEmpty()).Trim();
                    if (string.IsNullOrWhiteSpace(datumRoh))
                    {
                        anfragen.Add(new(m.MembershipNumber ?? m.Id.ToString(), m.DisplayName, m.PrimaereEmail ?? "",
                            "MANDATSDATUM_ANFORDERN", "Unterschriftsdatum beim Mitglied erfragen"));
                    }
                    else if (!DateTime.TryParseExact(datumRoh, "dd.MM.yyyy", new System.Globalization.CultureInfo("de-DE"),
                                 System.Globalization.DateTimeStyles.None, out var datum) || datum.Date > DateTime.Today)
                    {
                        AnsiConsole.MarkupLine("[red]✘[/] Datum ungültig oder in der Zukunft – geparkt.");
                        anfragen.Add(new(m.MembershipNumber ?? m.Id.ToString(), m.DisplayName, m.PrimaereEmail ?? "",
                            "MANDATSDATUM_ANFORDERN", $"Eingabe ungültig: {datumRoh}"));
                    }
                    else if (!kontaktIds.TryGetValue(m.Id, out var cid2) || string.IsNullOrWhiteSpace(cid2))
                    {
                        anfragen.Add(new(m.MembershipNumber ?? m.Id.ToString(), m.DisplayName, m.PrimaereEmail ?? "",
                            "MANDATSDATUM_ANFORDERN", "keine contactDetails-ID gefunden"));
                    }
                    else
                    {
                        warteschlange.Add(new(new(m.Id, m.MembershipNumber, m.DisplayName, "MANDATSDATUM_NEU",
                            "sepaDate", "– fehlt", datum.ToString("dd.MM.yyyy"), true, "Interaktiv erfasst."),
                            cid2!, new Dictionary<string, object?> { ["sepaDate"] = datum.ToString("yyyy-MM-dd") }));
                    }
                }
            }
        }

        private static void Parke(FixVorschlag plan, Dictionary<int, MemberAuditResult> nachId, List<Anfrage> anfragen, string notiz)
        {
            var email = nachId.TryGetValue(plan.MemberId, out var r) ? r.Mitglied.PrimaereEmail ?? "" : "";
            anfragen.Add(new(plan.MembershipNumber ?? plan.MemberId.ToString(), plan.DisplayName, email, plan.Aktion,
                string.IsNullOrWhiteSpace(notiz) ? plan.Begruendung : notiz));
        }

        private static string EmailVon(Dictionary<int, MemberAuditResult> nachId, int memberId) =>
            nachId.TryGetValue(memberId, out var r) ? r.Mitglied.PrimaereEmail ?? "" : "";

        private static Dictionary<string, object?> FelderFuer(FixVorschlag plan) => plan.Aktion switch
        {
            "ZAHLART_LASTSCHRIFT" => new() { ["methodOfPayment"] = 1 },
            "MANDATSREF_NEU" => new() { ["sepaMandate"] = plan.Neu },
            "EMAIL_NORM" => new() { [plan.Feld] = plan.Neu },
            _ => new() { [plan.Feld] = plan.Neu },
        };

        // ---------- SEPA-Mandate (lokale Dateien) ----------

        internal static List<MemberAuditResult> MandatKandidaten(List<MemberAuditResult> results) =>
            results.Where(r => !r.Mitglied.Ehrenmitglied
                    && r.Mitglied.Austrittsdatum == null
                    && r.Findings.Any(f => f.Code is "SEPA_MANDATSREF_FEHLT" or "SEPA_MANDATSDATUM_FEHLT" or "SEPA_EINWILLIGUNG_FEHLT"))
                .OrderByDescending(r => r.BlockerCount)
                .ThenBy(r => r.Mitglied.DisplayName)
                .ToList();

        private static async Task ErzeugeMandateAsync(
            List<MemberAuditResult> kandidaten, MemberFixSettings settings,
            int jahr, DateTime heute, CancellationToken ct)
        {
            var vorlagenDir = FindeSepaVorlagenDir(settings.VorlagenDir);
            if (vorlagenDir == null)
            {
                AnsiConsole.MarkupLine("[red]✘ Fehler:[/] vorlagen/sepa-mandat/vorlage.typ nicht gefunden (oder --vorlagen-dir angeben).");
                return;
            }
            var outputDir = string.IsNullOrWhiteSpace(settings.OutputDir)
                ? vorlagenDir
                : Path.GetFullPath(settings.OutputDir);
            Directory.CreateDirectory(outputDir);
            var glaeubigerId = string.IsNullOrWhiteSpace(settings.GlaeubigerId) ? DefaultGlaeubigerId : settings.GlaeubigerId.Trim();
            if (glaeubigerId == DefaultGlaeubigerId)
                AnsiConsole.MarkupLine("[yellow]⚠[/] Keine --glaeubiger-id angegeben – Platzhalter wird eingesetzt, bitte vor Versand ersetzen.");

            var erzeugt = 0;
            foreach (var r in kandidaten)
            {
                var m = r.Mitglied;
                var mandatRef = string.IsNullOrWhiteSpace(m.Mandatsreferenz) ? $"EV-{m.Id}-{jahr}" : m.Mandatsreferenz.Trim();
                var daten = new SepaMandatDaten(
                    DefaultVereinName, DefaultVereinAdresse, glaeubigerId, mandatRef,
                    m.DisplayName, m.Strasse ?? "", $"{(m.Plz ?? "").Trim()} {(m.Stadt ?? "").Trim()}".Trim(),
                    string.IsNullOrWhiteSpace(m.Iban) ? "___ wird nachgereicht ___" : FieldValidators.NormalisiereIban(m.Iban),
                    m.Bic ?? "", $"Rheine, {heute:dd.MM.yyyy}",
                    m.MembershipNumber != null ? $"Mitgliedsnummer {m.MembershipNumber}" : null);
                var basis = SepaMandatFileBuilder.DateiName(heute, m.DisplayName, m.MembershipNumber);
                var typPfad = EindeutigerPfad(outputDir, basis, ".typ");
                var pdfPfad = Path.ChangeExtension(typPfad, ".pdf");
                var importPfad = Path.GetRelativePath(outputDir, Path.Combine(vorlagenDir, "vorlage.typ")).Replace(Path.DirectorySeparatorChar, '/');
                await File.WriteAllTextAsync(typPfad, SepaMandatFileBuilder.Build(daten, importPfad), ct);
                AnsiConsole.MarkupLine($"[green]✔[/] Mandat: {Markup.Escape(typPfad)}");
                erzeugt++;
                if (!settings.SkipCompile)
                {
                    var (gefunden, ok, ausgabe) = await KompiliereAsync(typPfad, pdfPfad, ct);
                    if (!gefunden)
                        AnsiConsole.MarkupLine($"[grey]typst nicht im PATH – nur .typ (manuell: typst compile \"{Markup.Escape(typPfad)}\")[/]");
                    else if (!ok)
                        AnsiConsole.MarkupLine($"[red]✘ typst:[/] {Markup.Escape(ausgabe.Trim())}");
                    else
                        AnsiConsole.MarkupLine($"[green]✔[/] PDF: {Markup.Escape(pdfPfad)}");
                }
            }
            AnsiConsole.MarkupLine($"[grey]{erzeugt} Mandat(e) in {Markup.Escape(outputDir)}. Ablauf: unterschreiben lassen → Mandatsreferenz + Mandatsdatum in easyVerein pflegen.[/]");
        }

        private static string EindeutigerPfad(string dir, string basis, string endung)
        {
            var pfad = Path.Combine(dir, basis + endung);
            var i = 2;
            while (File.Exists(pfad) || File.Exists(Path.ChangeExtension(pfad, ".pdf")))
                pfad = Path.Combine(dir, $"{basis}-{i++}{endung}");
            return pfad;
        }

        private static string? FindeSepaVorlagenDir(string? option)
        {
            if (!string.IsNullOrWhiteSpace(option))
                return Directory.Exists(option) ? Path.GetFullPath(option) : null;
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null)
            {
                var kandidat = Path.Combine(dir.FullName, "vorlagen", "sepa-mandat", "vorlage.typ");
                if (File.Exists(kandidat))
                    return Path.GetDirectoryName(kandidat);
                dir = dir.Parent;
            }
            return null;
        }

        private static async Task<(bool Gefunden, bool Ok, string Ausgabe)> KompiliereAsync(
            string typPfad, string pdfPfad, CancellationToken ct)
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
                if (prozess == null) return (true, false, "Prozess konnte nicht gestartet werden.");
                var outTask = prozess.StandardOutput.ReadToEndAsync(ct);
                var errTask = prozess.StandardError.ReadToEndAsync(ct);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromMinutes(2));
                try { await prozess.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    try { prozess.Kill(entireProcessTree: true); } catch { /* best effort */ }
                    return (true, false, "Zeitüberschreitung – typst abgebrochen.");
                }
                return (true, prozess.ExitCode == 0, await outTask + await errTask);
            }
            catch (System.ComponentModel.Win32Exception) { return (false, false, string.Empty); }
        }

        // ---------- Anfragen-Export + Schreiben ----------

        private static void SchreibeAnfragen(List<Anfrage> anfragen, MemberFixSettings settings, int jahr)
        {
            var pfad = string.IsNullOrWhiteSpace(settings.Anfragen)
                ? Path.Combine(
                    string.IsNullOrWhiteSpace(settings.OutputDir) ? Directory.GetCurrentDirectory() : Path.GetFullPath(settings.OutputDir),
                    $"fix-anfragen-{jahr}.csv")
                : Path.GetFullPath(settings.Anfragen);
            Directory.CreateDirectory(Path.GetDirectoryName(pfad) ?? ".");
            var sb = new StringBuilder();
            sb.AppendLine("mitgliedsnummer;name;email;aktion;notiz");
            foreach (var a in anfragen)
            {
                string Esc(string? v) => (v ?? "").Replace(";", ",").Replace("\n", " ").Replace("\r", "");
                sb.AppendLine(string.Join(";", Esc(a.Mitgliedsnummer), Esc(a.Name), Esc(a.Email), Esc(a.Aktion), Esc(a.Notiz)));
            }
            File.WriteAllText(pfad, sb.ToString());
            AnsiConsole.MarkupLine($"[yellow]◷ {anfragen.Count} Anfrage(n) geparkt:[/] {Markup.Escape(pfad)}");
            Console.WriteLine();
            AnsiConsole.MarkupLine("[grey]Textbaustein z.B.: Hallo X, für den Lastschrift-Einzug fehlt uns noch IBAN / unterschriebenes SEPA-Mandat / Nachweis. Danke![/]");
        }

        private static async Task<(int Ok, int Fehler)> SchreibePatchesAsync(
            EasyVereinClient client, List<PatchAuftrag> warteschlange, CancellationToken ct)
        {
            var ok = 0; var fehler = 0;
            foreach (var a in warteschlange)
            {
                try
                {
                    await client.PatchRawAsync($"contact-details/{a.ContactId}", a.Felder, ct);
                    AnsiConsole.MarkupLine($"[green]✔[/] {Markup.Escape(a.Plan.DisplayName)}: {Markup.Escape(a.Plan.Aktion)}");
                    ok++;
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[red]✘[/] {Markup.Escape(a.Plan.DisplayName)}: {Markup.Escape(ex.Message)}");
                    fehler++;
                }
            }
            return (ok, fehler);
        }

        private static bool Bestaetigen(string frage, bool defaultWert) =>
            AnsiConsole.Prompt(new ConfirmationPrompt(frage) { DefaultValue = defaultWert });

        private static void DruckeNaechsteSchritte(bool hatAnfragen)
        {
            Console.WriteLine();
            AnsiConsole.MarkupLine(hatAnfragen
                ? "[grey]Nächste Schritte: Anfragen-CSV abarbeiten → Antworten mit 'member-fix --member &lt;ID&gt; --apply' einpflegen.[/]"
                : "[grey]Nächste Schritte: 'member-audit --nur-probleme' zur Kontrolle erneut laufen lassen.[/]");
        }
    }
}
