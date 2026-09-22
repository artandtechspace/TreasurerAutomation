using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Spectre.Console;
using Spectre.Console.Cli;
using TreasurerAutomation.Services;
using TreasurerAutomation.Services.MemberAudit;

namespace TreasurerAutomation.Commands
{
    public sealed class MemberAuditSettings : CommandSettings
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

        [CommandOption("--format <FORMAT>")]
        [Description("Ausgabe: table, csv, json.")]
        [DefaultValue("table")]
        public string Format { get; set; } = "table";

        [CommandOption("--output <PFAD>")]
        [Description("Zieldatei für csv/json (sonst stdout).")]
        public string? Output { get; set; }

        [CommandOption("--nur-probleme")]
        [Description("Nur Blocker/Warnungen zeigen.")]
        public bool NurProbleme { get; set; }

        [CommandOption("--fail-on-blocker")]
        [Description("Exit-Code 2 bei Blocker (CI).")]
        public bool FailOnBlocker { get; set; }

        [CommandOption("--search <TEXT>")]
        [Description("Filter Name/E-Mail (?search=).")]
        public string? Search { get; set; }

        [CommandOption("--member <ID>")]
        [Description("Ein Mitglied (ID/Nummer), Detailansicht.")]
        public string? Member { get; set; }

        [CommandOption("--statistik")]
        [Description("Zusatz-Statistiken zeigen.")]
        public bool Statistik { get; set; }

        public string ResolvedToken =>
            Services.EasyVereinTokenResolver.Resolve(EasyVereinToken);

        public override ValidationResult Validate()
        {
            if (string.IsNullOrWhiteSpace(ResolvedToken))
                return ValidationResult.Error("Kein Token: erst 'dotnet run -- login' oder EASYVEREIN_TOKEN / --easyverein-token setzen.");
            if (MaxPages < 1) return ValidationResult.Error("--max-pages muss >= 1 sein.");
            if (Limit is < 1 or > 1000) return ValidationResult.Error("--limit muss 1..1000 sein.");
            var f = (Format ?? "").Trim().ToLowerInvariant();
            if (f is not ("table" or "csv" or "json"))
                return ValidationResult.Error("--format muss table, csv oder json sein.");
            var jahr = BeitragJahr ?? DateTime.Today.Year;
            if (jahr is < 2020 or > 2100) return ValidationResult.Error("--beitrag-jahr unplausibel.");
            return ValidationResult.Success();
        }
    }

    /// <summary>
    /// Read-only Audit aller Mitglieder für den Beitragseinzug.
    /// Prüft Zustimmungen (SEPA-Einwilligung), Unterlagen (Nachweis ermäßigt),
    /// Stammdaten/Beiträge/SEPA-Readiness/Mahnstatus – ohne zu schreiben.
    /// </summary>
    public sealed class MemberAuditCommand : AsyncCommand<MemberAuditSettings>
    {
        protected override async Task<int> ExecuteAsync(CommandContext context, MemberAuditSettings settings,
            CancellationToken cancellationToken)
        {
            var jahr = settings.BeitragJahr ?? DateTime.Today.Year;
            var heute = DateTime.Today;
            var format = settings.Format.Trim().ToLowerInvariant();

            AnsiConsole.Write(new Rule($"[yellow]Mitglieder-Audit {jahr} (read-only)[/]").RuleStyle("grey").LeftJustified());
            Console.WriteLine();

            try
            {
                using var client = new EasyVereinClient(settings.ResolvedToken);

                // Einzelmitglied direkt laden (--member ID oder Mitgliedsnummer)
                if (!string.IsNullOrWhiteSpace(settings.Member))
                {
                    var single = await LadeEinzelmitglied(client, settings.Member.Trim(), cancellationToken);
                    if (single is null)
                    {
                        AnsiConsole.MarkupLine($"[red]✘ Mitglied '{Markup.Escape(settings.Member.Trim())}' nicht gefunden.[/]");
                        return 1;
                    }
                    var gruppenLookup1 = await LadeLookup(client, "member-group", cancellationToken);
                    var customDefLookup1 = await LadeLookup(client, "custom-field", cancellationToken);
                    var (cd1, k1, n1, c1) = await Anreichern(client, single.Value, gruppenLookup1, customDefLookup1, cancellationToken);
                    var rec1 = EasyVereinMemberParser.Parse(single.Value, cd1, k1, n1, c1);
                    var res1 = MemberAuditService.Audit(rec1, jahr, heute);
                    ZeigeDetail(res1, jahr);
                    await AutoRefreshHinweis(client, settings.EasyVereinToken);
                    return res1.BlockerCount > 0 && settings.FailOnBlocker ? 2 : 0;
                }

                var query = $"limit={settings.Limit}";
                if (!string.IsNullOrWhiteSpace(settings.Search))
                    query += $"&search={Uri.EscapeDataString(settings.Search.Trim())}";

                List<JsonElement> members = new();
                await AnsiConsole.Status().Spinner(Spinner.Known.Dots).SpinnerStyle(Style.Parse("yellow bold"))
                    .StartAsync("Lade Mitglieder (GET v2.0/member)…", async _ =>
                    {
                        members = (await client.ListRawAsync("member", query, settings.MaxPages, cancellationToken)).ToList();
                    });
                AnsiConsole.MarkupLine($"[grey]Mitglieder:[/] {members.Count}" +
                    (string.IsNullOrWhiteSpace(settings.Search) ? "" : $"  [grey](Filter:[/] {Markup.Escape(settings.Search.Trim())}[grey])[/]"));

                // Hilfs-Lookups (best effort, Fehler tolerieren – Audit läuft auch ohne).
                // Live-Endpunkte heißen singular: member-group, custom-field.
                var gruppenLookup = await LadeLookup(client, "member-group", cancellationToken);
                var customDefLookup = await LadeLookup(client, "custom-field", cancellationToken);

                var records = new List<(JsonElement Json, MemberRecord Record)>();
                await AnsiConsole.Status().Spinner(Spinner.Known.Dots).SpinnerStyle(Style.Parse("yellow bold"))
                    .StartAsync("Lade Details (Kontakt, Gruppen, Nachweise)…", async ctx =>
                    {
                        var n = 0;
                        foreach (var m in members)
                        {
                            n++;
                            ctx.Status($"Lade Details ({n}/{members.Count})…");
                            var (cd, kuerzel, namen, customs) = await Anreichern(client, m, gruppenLookup, customDefLookup, cancellationToken);
                            records.Add((m, EasyVereinMemberParser.Parse(m, cd, kuerzel, namen, customs)));
                            // Drosselung: API-Limit 100/min bei ~3 Requests/Mitglied
                            if (n < members.Count)
                                await Task.Delay(500, cancellationToken);
                        }
                    });

                var results = records.Select(x => MemberAuditService.Audit(x.Record, jahr, heute)).ToList();
                var technischUnvollstaendig = ErgänzeTechnikFindings(records, results);
                ErgänzeDuplikatFindings(results);
                var summary = MemberAuditService.Zusammenfassen(results, jahr);
                if (technischUnvollstaendig > 0)
                    AnsiConsole.MarkupLine($"[yellow]⚠ Bei {technischUnvollstaendig} Mitglied(ern) konnten Kontakt-/Gruppendaten nicht geladen werden (Rate-Limit). " +
                        $"Befunde dort ggf. unvollständig – Lauf wiederholen.[/]");

                if (format == "json")
                {
                    var json = BuildJson(summary);
                    await Ausgeben(json, settings.Output, cancellationToken);
                }
                else if (format == "csv")
                {
                    var csv = BuildCsv(summary);
                    await Ausgeben(csv, settings.Output, cancellationToken);
                }
                else
                {
                    ZeigeTabelle(summary, settings.NurProbleme);
                    // Interaktive Einzelauswahl bei Suche mit mehreren Treffern
                    if (!string.IsNullOrWhiteSpace(settings.Search) && summary.Ergebnisse.Count > 1
                        && summary.Ergebnisse.Count <= 30 && settings.Output is null)
                    {
                        var gewaehlt = FrageMitgliedAuswahl(summary);
                        if (gewaehlt != null) ZeigeDetail(gewaehlt, jahr);
                    }
                }

                Console.WriteLine();
                AnsiConsole.MarkupLine($"[grey]Geprüft:[/] {summary.Geprueft}  [green]einzugsfähig:[/] {summary.Einzugsfaehig}  [green]SEPA:[/] {summary.SepaEinziehbar}  [red]mit Blocker:[/] {summary.MitBlocker}  [yellow]mit Warnung:[/] {summary.MitWarnung}");
                AnsiConsole.MarkupLine($"[grey]Soll-Summe einzugsfähig:[/] {summary.SummeSollEinzugsfaehig:N2} €  [grey]davon SEPA:[/] {summary.SummeSollSepa:N2} €");
                if (summary.SummeFreiwillig > 0)
                    AnsiConsole.MarkupLine($"[grey]Darin freiwillige Zusätze (VBF):[/] {summary.SummeFreiwillig:N2} €");
                if (summary.MitForderung > 0)
                    AnsiConsole.MarkupLine($"[grey]Offene Salden:[/] {summary.MitForderung} Mitglieder, Saldo {summary.SummeSaldoOffen:N2} € + Säumnis ca. {summary.SummeSaeumnis:N2} € (§5: 1 €/7 Tage, unverbindlich)");
                AnsiConsole.MarkupLine("[grey]Regeln: Beitragsordnung §2/§3/§7 (01=24€, 02=80€, 02.1=30€, 03=60€, 04=100€, 50% nach 30.06.), Satzung §4/§5/§8, SEPA §7 Abs. 3. Nur GET, nichts geschrieben.[/]");

                if (settings.Statistik && format == "table")
                    ZeigeStatistik(summary);

                await AutoRefreshHinweis(client, settings.EasyVereinToken);

                if (settings.FailOnBlocker && summary.MitBlocker > 0) return 2;
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                if (ex.Message.Contains("401") || ex.Message.Contains("403"))
                    AnsiConsole.MarkupLine($"[red]✘ Fehler:[/] {Markup.Escape(ex.Message)} [grey](Token abgelaufen? Erneut 'dotnet run -- login'.)[/]");
                else
                    AnsiConsole.MarkupLine($"[red]✘ Fehler:[/] {Markup.Escape(ex.Message)}");
                return 1;
            }
        }

        // ---------- Laden/Anreichern (nur GET) ----------

        private static async Task<JsonElement?> LadeEinzelmitglied(
            EasyVereinClient client, string eingabe, CancellationToken ct)
        {
            // 1. Direkter GET bei numerischer ID
            if (int.TryParse(eingabe, out _))
            {
                try
                {
                    var direkt = await client.GetSingleRawAsync($"member/{eingabe}", ct);
                    if (direkt.HasValue) return direkt;
                }
                catch { /* Fallback Suche */ }
            }
            // 2. Fallback: Server-Suche (trifft Name/E-Mail)
            try
            {
                var treffer = await client.ListRawAsync("member", $"limit=10&search={Uri.EscapeDataString(eingabe)}", maxPages: 1, ct);
                // exakte Mitgliedsnummer bevorzugen
                foreach (var t in treffer)
                {
                    var nr = EasyVereinMemberParser.GetString(t, "membershipNumber", "membershipnumber");
                    if (string.Equals(nr?.Trim(), eingabe.Trim(), StringComparison.OrdinalIgnoreCase))
                        return t;
                }
                if (treffer.Count == 1) return treffer[0];
                // bei ID-Eingabe auch nach id filtern
                foreach (var t in treffer)
                {
                    if (EasyVereinMemberParser.GetInt(t, "id")?.ToString() == eingabe.Trim())
                        return t;
                }
                if (treffer.Count > 0) return treffer[0];
            }
            catch { /* lokaler Fallback unten */ }
            // 3. Lokaler Fallback: ?search= kennt keine Mitgliedsnummern – Liste scannen
            try
            {
                var alle = await client.ListRawAsync("member", "limit=100", maxPages: 3, ct);
                foreach (var t in alle)
                {
                    var nr = EasyVereinMemberParser.GetString(t, "membershipNumber", "membershipnumber");
                    if (string.Equals(nr?.Trim(), eingabe.Trim(), StringComparison.OrdinalIgnoreCase))
                        return t;
                    if (EasyVereinMemberParser.GetInt(t, "id")?.ToString() == eingabe.Trim())
                        return t;
                }
            }
            catch { return null; }
            return null;
        }

        private static MemberAuditResult? FrageMitgliedAuswahl(MemberAuditSummary summary)
        {
            try
            {
                if (!AnsiConsole.Profile.Capabilities.Interactive) return null;
                var auswahl = new SelectionPrompt<string>()
                    .Title("Detail für einzelnes Mitglied anzeigen?")
                    .AddChoices(new[] { "Nein, Übersicht reicht" }.Concat(
                        summary.Ergebnisse.Select(r =>
                            $"{r.Mitglied.MembershipNumber ?? r.Mitglied.Id.ToString()} · {r.Mitglied.DisplayName} " +
                            $"({(r.BlockerCount > 0 ? $"{r.BlockerCount}x ✘" : r.WarnungCount > 0 ? $"{r.WarnungCount}x ⚠" : "ok")}, Soll {r.SollBeitrag:N2} €)")));
                var gewaehlt = AnsiConsole.Prompt(auswahl);
                if (gewaehlt.StartsWith("Nein,")) return null;
                var nr = gewaehlt.Split('·')[0].Trim();
                return summary.Ergebnisse.FirstOrDefault(r =>
                    (r.Mitglied.MembershipNumber ?? r.Mitglied.Id.ToString()) == nr);
            }
            catch { return null; }
        }

        private static void ZeigeDetail(MemberAuditResult r, int jahr)
        {
            var m = r.Mitglied;
            Console.WriteLine();
            AnsiConsole.Write(new Rule($"[yellow]{Markup.Escape(m.MembershipNumber ?? m.Id.ToString())} · {Markup.Escape(m.DisplayName)}[/]").RuleStyle("grey").LeftJustified());
            var tabelle = new Table().Border(TableBorder.Rounded);
            tabelle.AddColumn(new TableColumn("[grey]Feld[/]") { NoWrap = true });
            tabelle.AddColumn(new TableColumn("[bold]Wert[/]"));
            var einzugKurz = r.SepaEinziehbar ? "SEPA-einziehbar" : r.Einzugsfaehig ? "einzugsfähig (kein SEPA)" : "blockiert";
            tabelle.AddRow("Soll-Beitrag " + jahr, m.FreiwilligerZusatz > 0
                ? $"{r.SollBeitrag:N2} €  ({r.SollBasis:N2} € Klasse + {m.FreiwilligerZusatz:N2} € freiwillig, {einzugKurz})"
                : $"{r.SollBeitrag:N2} €  ({einzugKurz})");
            if (m.FreiwilligerZusatz > 0)
                tabelle.AddRow("Freiwilliger Zusatz", $"{m.FreiwilligerZusatz:N2} € / Jahr (Feld 'Freiwilliger Beitrag' / VBF)");
            tabelle.AddRow("Gruppen", Markup.Escape(m.GruppenKuerzel.Count == 0 ? "–" : string.Join(", ", m.GruppenKuerzel)));
            tabelle.AddRow("E-Mail", Markup.Escape(m.PrimaereEmail ?? "–"));
            tabelle.AddRow("Adresse", Markup.Escape($"{m.Strasse ?? "–"}, {m.Plz ?? "–"} {m.Stadt ?? "–"}"));
            tabelle.AddRow("Geburtstag", m.Geburtstag.HasValue ? $"{m.Geburtstag:dd.MM.yyyy} (Alter am 01.02.{jahr}: {m.AlterAm(new DateTime(jahr, 2, 1))})" : "– fehlt");
            tabelle.AddRow("Eintritt", $"{m.Eintrittsdatum:dd.MM.yyyy}" + (m.Austrittsdatum.HasValue ? $"  → Austritt {m.Austrittsdatum:dd.MM.yyyy}" : ""));
            tabelle.AddRow("Zahlungsart", Markup.Escape($"{m.ZahlungsartText} / SEPA-Einwilligung: {(m.SepaEinverstaendnis == true ? "Ja" : m.SepaEinverstaendnis == false ? "Nein" : "unbekannt")}"));
            tabelle.AddRow("IBAN", Markup.Escape(MaskiereIban(m.Iban)));
            tabelle.AddRow("BIC / Mandat", Markup.Escape($"{m.Bic ?? "–"} / {m.Mandatsreferenz ?? "–"} ({m.Mandatsdatum:dd.MM.yyyy})"));
            tabelle.AddRow("Nachweis ermäßigt", Markup.Escape(m.NachweisDatei ?? "– fehlt"));
            tabelle.AddRow("Saldo", $"{m.Saldo:N2} €");
            if (m.Saldo > 0)
                tabelle.AddRow("Forderung", $"{r.ForderungGesamt:N2} € (Saldo {m.Saldo:N2} € + Säumnis {r.SaeumnisZuschlag:N2} €, {r.TageVerzug} Tage) – {Markup.Escape(r.MahnVorschlag ?? "–")}");
            AnsiConsole.Write(tabelle);
            Console.WriteLine();
            foreach (var f in r.Findings.OrderBy(f => f.Severity == FindingSeverity.Blocker ? 0 : f.Severity == FindingSeverity.Warnung ? 1 : 2))
            {
                var icon = f.Severity == FindingSeverity.Blocker ? "[red]✘[/]" : f.Severity == FindingSeverity.Warnung ? "[yellow]⚠[/]" : "[blue]ℹ[/]";
                AnsiConsole.MarkupLine($"{icon} [bold]{Markup.Escape(f.Code)}[/] ({f.Severity}) {Markup.Escape(f.Nachricht)}");
            }
            if (r.Findings.Count == 0) AnsiConsole.MarkupLine("[green]✔ Keine Befunde – einzugsfähig.[/]");
        }

        private static string MaskiereIban(string? iban)
        {
            if (string.IsNullOrWhiteSpace(iban)) return "– fehlt";
            var s = new string(iban.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
            if (s.Length < 8) return "****";
            return s.Substring(0, 4) + " **** **** " + s.Substring(s.Length - 4);
        }

        /// <summary>
        /// Automatisierung: wenn die API per Header meldet, dass ein Refresh fällig ist
        /// und das Token aus der Session stammt, wird es sofort erneuert + gespeichert.
        /// Sonst nur Hinweis. Schlägt nie den Audit fehl.
        /// </summary>
        private static async Task AutoRefreshHinweis(EasyVereinClient client, string? explicitToken)
        {
            if (!client.TokenRefreshNeeded) return;
            var quelle = EasyVereinTokenResolver.DescribeSource(explicitToken);
            if (!quelle.StartsWith("Session", StringComparison.OrdinalIgnoreCase))
            {
                AnsiConsole.MarkupLine("[yellow]⚠ Token-Refresh fällig (Header tokenRefreshNeeded).[/] Bei Session-Login automatisch – hier läuft Token über --easyverein-token/Env, daher: [grey]dotnet run -- auth-status --refresh[/]");
                return;
            }
            try
            {
                var resp = await client.RefreshTokenAsync();
                if (!string.IsNullOrWhiteSpace(resp.Token))
                {
                    var alt = EasyVereinSession.Load();
                    var neu = new EasyVereinSession(resp.Token, alt?.Email ?? "", alt?.UserId ?? 0,
                        DateTime.UtcNow, DateTime.UtcNow.AddSeconds(resp.ExpiresIn > 0 ? resp.ExpiresIn : 30 * 86400));
                    neu.Save();
                    AnsiConsole.MarkupLine("[green]✔ Token automatisch erneuert (refresh-token).[/]");
                }
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[yellow]⚠ Auto-Refresh fehlgeschlagen:[/] {Markup.Escape(ex.Message)} [grey](manuell: auth-status --refresh)[/]");
            }
        }

        public static async Task<Dictionary<string, JsonElement>> LadeLookup(
            EasyVereinClient client, string endpoint, CancellationToken ct)
        {
            var dict = new Dictionary<string, JsonElement>();
            try
            {
                var items = await client.ListRawAsync(endpoint, "limit=100", maxPages: 5, ct);
                foreach (var it in items)
                {
                    var id = EasyVereinMemberParser.GetInt(it, "id")?.ToString()
                        ?? EasyVereinMemberParser.GetString(it, "id");
                    if (id != null) dict[id] = it;
                }
            }
            catch
            {
                // best effort – Audit läuft auch ohne Lookup
            }
            return dict;
        }

        /// <summary>
        /// Extrahiert die PK aus int, plain ID-String oder API-URL
        /// (z.B. https://easyverein.com/api/v2.0/contact-details/36271917 -> "36271917").
        /// Live liefert member genau solche URL-Referenzen.
        /// </summary>
        public static string? ExtractPk(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var i)) return i.ToString();
            if (value.ValueKind == JsonValueKind.String) return ExtractPk(value.GetString());
            if (value.ValueKind == JsonValueKind.Object)
            {
                var id = EasyVereinMemberParser.GetInt(value, "id")?.ToString();
                if (id != null) return id;
            }
            return null;
        }

        public static string? ExtractPk(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var s = raw.Trim().TrimEnd('/');
            // URL? -> letztes Pfadsegment (vor ?)
            var q = s.IndexOf('?');
            if (q >= 0) s = s.Substring(0, q);
            var slash = s.LastIndexOf('/');
            if (slash >= 0) s = s.Substring(slash + 1);
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }

        public static async Task<(JsonElement? Cd, List<string> Kuerzel, List<string> Namen, Dictionary<string, string?> Customs)>
            Anreichern(EasyVereinClient client, JsonElement member,
                Dictionary<string, JsonElement> gruppenLookup,
                Dictionary<string, JsonElement> customDefLookup,
                CancellationToken ct)
        {
            // 1. contactDetails: live eine URL-Referenz -> PK extrahieren -> GET
            JsonElement? cd = EasyVereinMemberParser.GetObject(member, "contactDetails", "contactdetails");
            if (cd == null)
            {
                var refId = KontaktId(member);
                if (refId != null)
                {
                    try { cd = await client.GetSingleRawAsync($"contact-details/{refId}", ct); }
                    catch { /* best effort */ }
                }
            }

            // 2. Gruppen live: GET member/{id}/groups (Assoziationen mit memberGroup-URL + paymentActive),
            //    Short/Name aus gecachtem member-group Lookup. Nur paymentActive zählt für den Beitrag;
            //    Namen aller (inkl. inaktivem VV) für Vorstandserkennung behalten.
            var kuerzel = new List<string>();
            var namen = new List<string>();
            var mid = EasyVereinMemberParser.GetInt(member, "id");
            if (mid.HasValue)
            {
                try
                {
                    var assocs = await client.ListRawAsync($"member/{mid}/groups", "limit=100", maxPages: 2, ct);
                    foreach (var a in assocs)
                    {
                        var aktiv = EasyVereinMemberParser.GetBool(a, "paymentActive", "paymentactive") ?? true;
                        string? gid = null;
                        foreach (var p in a.EnumerateObject())
                        {
                            if (string.Equals(p.Name, "memberGroup", StringComparison.OrdinalIgnoreCase))
                                gid = ExtractPk(p.Value);
                        }
                        if (gid != null && gruppenLookup.TryGetValue(gid, out var gdef))
                        {
                            var k = EasyVereinMemberParser.GetString(gdef, "short", "shortName", "shortcut", "abbreviation", "code");
                            var n = EasyVereinMemberParser.GetString(gdef, "name", "title", "label");
                            if (!string.IsNullOrWhiteSpace(n)) namen.Add(n.Trim());
                            if (aktiv && !string.IsNullOrWhiteSpace(k)) kuerzel.Add(k.Trim().ToUpperInvariant());
                            else if (!aktiv && !string.IsNullOrWhiteSpace(k) && k.Trim().Equals("VV", StringComparison.OrdinalIgnoreCase))
                                kuerzel.Add("VV"); // Vorstand auch bei inaktivem VV erkennen
                        }
                    }
                }
                catch { /* Fallback unten */ }
            }
            if (kuerzel.Count == 0 && namen.Count == 0)
            {
                // Fallback: eingebettete Formen (Export/Strings/IDs)
                var (embK, embN) = EasyVereinMemberParser.ParseGruppen(member);
                kuerzel.AddRange(embK);
                namen.AddRange(embN);
                foreach (var gid in GruppenIds(member))
                {
                    if (gruppenLookup.TryGetValue(gid, out var gdef))
                    {
                        var k = EasyVereinMemberParser.GetString(gdef, "short", "shortName", "shortcut", "abbreviation", "code");
                        var n = EasyVereinMemberParser.GetString(gdef, "name", "title", "label");
                        if (!string.IsNullOrWhiteSpace(k)) kuerzel.Add(k.Trim().ToUpperInvariant());
                        if (!string.IsNullOrWhiteSpace(n)) namen.Add(n.Trim());
                    }
                }
            }
            kuerzel = kuerzel.Distinct().ToList();
            namen = namen.Distinct().ToList();

            // 3. Custom fields live: GET member/{id}/custom-fields ({customField: URL, value}),
            //    Name via custom-field Lookup (PK aus URL). value enthält auch Datei-URLs (Nachweis).
            var customs = ParseEingebetteteCustoms(member, customDefLookup);
            if (customs.Count == 0 && mid.HasValue)
            {
                try
                {
                    var items = await client.ListRawAsync($"member/{mid}/custom-fields", "limit=100", maxPages: 2, ct);
                    foreach (var it in items)
                    {
                        var (name, wert) = ParseCustomItem(it, customDefLookup);
                        if (name != null && !customs.ContainsKey(name)) customs[name] = wert;
                    }
                }
                catch { /* best effort */ }
            }

            return (cd, kuerzel, namen, customs);
        }

        private static string? KontaktId(JsonElement member)
        {
            foreach (var prop in member.EnumerateObject())
            {
                if (!string.Equals(prop.Name, "contactDetails", StringComparison.OrdinalIgnoreCase)) continue;
                return ExtractPk(prop.Value);
            }
            return null;
        }

        private static List<string> GruppenIds(JsonElement member)
        {
            var ids = new List<string>();
            foreach (var prop in member.EnumerateObject())
            {
                if (!string.Equals(prop.Name, "memberGroups", StringComparison.OrdinalIgnoreCase)) continue;
                if (prop.Value.ValueKind != JsonValueKind.Array) continue;
                foreach (var item in prop.Value.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        var pk = ExtractPk(item.GetString());
                        if (pk != null) ids.Add(pk);
                    }
                    else if (item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var i)) ids.Add(i.ToString());
                    else if (item.ValueKind == JsonValueKind.Object)
                    {
                        // {memberGroup: 5 | URL | {id:5}}
                        foreach (var p2 in item.EnumerateObject())
                        {
                            if (!string.Equals(p2.Name, "memberGroup", StringComparison.OrdinalIgnoreCase)) continue;
                            var pk = ExtractPk(p2.Value);
                            if (pk != null) ids.Add(pk);
                        }
                    }
                }
            }
            return ids;
        }

        private static Dictionary<string, string?> ParseEingebetteteCustoms(
            JsonElement member, Dictionary<string, JsonElement> defLookup)
        {
            var d = new Dictionary<string, string?>();
            foreach (var prop in member.EnumerateObject())
            {
                if (!string.Equals(prop.Name, "customFields", StringComparison.OrdinalIgnoreCase)) continue;
                if (prop.Value.ValueKind != JsonValueKind.Array) continue;
                foreach (var it in prop.Value.EnumerateArray())
                {
                    var (name, wert) = ParseCustomItem(it, defLookup);
                    if (name != null && !d.ContainsKey(name)) d[name] = wert;
                }
            }
            return d;
        }

        private static (string? Name, string? Wert) ParseCustomItem(JsonElement it, Dictionary<string, JsonElement> defLookup)
        {
            if (it.ValueKind != JsonValueKind.Object) return (null, null);
            string? wert = EasyVereinMemberParser.GetString(it, "value", "val", "text");
            // file-Uploads: Nachweis-Datei steckt oft in "file"/"path"/"document" statt value
            wert ??= EasyVereinMemberParser.GetString(it, "file", "path", "document", "url", "link");

            string? name = null;
            foreach (var p in it.EnumerateObject())
            {
                if (!string.Equals(p.Name, "customField", StringComparison.OrdinalIgnoreCase)) continue;
                // Live: URL wie .../custom-field/41958459 -> PK extrahieren
                var pk = ExtractPk(p.Value);
                if (pk != null && defLookup.TryGetValue(pk, out var def))
                    name = EasyVereinMemberParser.GetString(def, "name", "title", "label");
                if (name == null)
                {
                    if (p.Value.ValueKind == JsonValueKind.Object)
                        name = EasyVereinMemberParser.GetString(p.Value, "name", "title", "label");
                    name ??= pk != null ? $"customField:{pk}" : p.Value.GetString();
                }
            }
            // Direktform {name, value}
            name ??= EasyVereinMemberParser.GetString(it, "name", "title", "label", "key");
            return (name, wert);
        }

        /// <summary>
        /// Unterscheidet echte Datenlücken von Ladefehlern (z.B. Rate-Limit 429):
        /// Hat das member-JSON eine contactDetails-Referenz bzw. memberGroups-Einträge,
        /// aber der Record ist leer, war der GET erfolglos -> Technik-Warnung statt
        /// irreführender Daten-Befunde (die bleiben zur Vorsicht trotzdem stehen).
        /// Gibt die Zahl betroffener Mitglieder zurück.
        /// </summary>
        public static int ErgänzeTechnikFindings(
            List<(JsonElement Json, MemberRecord Record)> records,
            List<MemberAuditResult> results)
        {
            var betroffen = 0;
            for (var i = 0; i < records.Count && i < results.Count; i++)
            {
                var (json, rec) = records[i];
                var res = results[i];
                var hatKontaktRef = KontaktId(json) != null;
                var hatGruppenRef = false;
                foreach (var p in json.EnumerateObject())
                {
                    if (string.Equals(p.Name, "memberGroups", StringComparison.OrdinalIgnoreCase)
                        && p.Value.ValueKind == JsonValueKind.Array && p.Value.GetArrayLength() > 0)
                        hatGruppenRef = true;
                }
                var kontaktLeer = string.IsNullOrWhiteSpace(rec.Vorname) && string.IsNullOrWhiteSpace(rec.Nachname)
                    && string.IsNullOrWhiteSpace(rec.Strasse);
                var gruppenLeer = rec.GruppenKuerzel.Count == 0 && rec.GruppenNamen.Count == 0;
                if ((hatKontaktRef && kontaktLeer) || (hatGruppenRef && gruppenLeer))
                {
                    betroffen++;
                    res.Findings.Add(new("API_DETAILS_UNVOLLSTAENDIG", "Technik", FindingSeverity.Warnung,
                        "Kontakt-/Gruppendaten konnten nicht geladen werden (z.B. API-Rate-Limit) – Daten-Befunde ggf. unvollständig, Lauf wiederholen."));
                }
            }
            return betroffen;
        }

        // ---------- Duplikate (über alle Mitglieder) ----------

        internal static void ErgänzeDuplikatFindings(List<MemberAuditResult> results)
        {
            foreach (var grp in results
                         .Where(r => FieldValidators_IstEmail(r.Mitglied.PrimaereEmail))
                         .GroupBy(r => r.Mitglied.PrimaereEmail!.Trim().ToLowerInvariant())
                         .Where(g => g.Count() > 1))
            {
                foreach (var r in grp)
                    r.Findings.Add(new("STAMM_EMAIL_DUPLIKAT", "Stammdaten", FindingSeverity.Warnung,
                        $"Primäre E-Mail '{grp.Key}' mehrfach vergeben – Eindeutigkeit prüfen."));
            }
            foreach (var grp in results
                         .Where(r => !string.IsNullOrWhiteSpace(r.Mitglied.Mandatsreferenz))
                         .GroupBy(r => r.Mitglied.Mandatsreferenz!.Trim())
                         .Where(g => g.Count() > 1))
            {
                foreach (var r in grp)
                    r.Findings.Add(new("SEPA_MANDATSREF_DUPLIKAT", "Bank", FindingSeverity.Blocker,
                        $"Mandatsreferenz '{grp.Key}' mehrfach vergeben – muss eindeutig sein."));
            }
            // Gleiche IBAN bei mehreren Mitgliedern: oft Familie/Elternkonto (ok), aber prüfen.
            foreach (var grp in results
                         .Where(r => !string.IsNullOrWhiteSpace(r.Mitglied.Iban))
                         .GroupBy(r => new string(r.Mitglied.Iban!.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant())
                         .Where(g => g.Count() > 1))
            {
                foreach (var r in grp)
                    r.Findings.Add(new("BANK_IBAN_GETEILT", "Bank", FindingSeverity.Info,
                        $"IBAN wird von {grp.Count()} Mitgliedern genutzt – ok bei Familie/Elternkonto, sonst prüfen."));
            }
            // Gleicher Name + Geburtstag: möglicher Doppel-Eintrag.
            foreach (var grp in results
                         .Where(r => !string.IsNullOrWhiteSpace(r.Mitglied.Vorname)
                             && !string.IsNullOrWhiteSpace(r.Mitglied.Nachname) && r.Mitglied.Geburtstag.HasValue)
                         .GroupBy(r => (r.Mitglied.Vorname!.Trim().ToLowerInvariant(),
                             r.Mitglied.Nachname!.Trim().ToLowerInvariant(), r.Mitglied.Geburtstag!.Value.Date))
                         .Where(g => g.Count() > 1))
            {
                foreach (var r in grp)
                    r.Findings.Add(new("STAMM_DOPPEL_EINTRAG", "Stammdaten", FindingSeverity.Warnung,
                        "Gleicher Vor-/Nachname + Geburtstag mehrfach vorhanden – Doppel-Eintrag prüfen."));
            }
        }

        private static bool FieldValidators_IstEmail(string? m) =>
            Services.MemberAudit.FieldValidators.IstGueltigeEmail(m);

        // ---------- Ausgabe ----------

        private static void ZeigeTabelle(MemberAuditSummary s, bool nurProbleme)
        {
            var tabelle = new Table().Border(TableBorder.Rounded);
            tabelle.Expand = true;
            // Kompakt: erste 4 Spalten nie umbrechen (einzeilig), Befund bekommt den Rest.
            // Icon+Code per NBSP verbinden, damit ein Befund nie in zwei Zeilen zerfällt.
            tabelle.AddColumn(new TableColumn("[bold]Mitglied[/]") { NoWrap = true });
            tabelle.AddColumn(new TableColumn("[bold]Gruppen[/]") { NoWrap = true });
            tabelle.AddColumn(new TableColumn("[bold]Soll[/]") { NoWrap = true, Alignment = Justify.Right });
            tabelle.AddColumn(new TableColumn("[bold]Einzug[/]") { NoWrap = true });
            tabelle.AddColumn(new TableColumn("[bold]Befund[/]"));

            var liste = s.Ergebnisse
                .Where(r => !nurProbleme || r.Findings.Any(f => f.Severity != FindingSeverity.Info))
                .ToList();

            foreach (var r in liste.Take(200))
            {
                var status = r.SepaEinziehbar ? "[green]SEPA[/]"
                    : r.Einzugsfaehig ? "[green]ja[/]"
                    : r.BlockerCount > 0 ? "[red]blockiert[/]" : "[yellow]prüfen[/]";
                var nameRoh = $"{r.Mitglied.MembershipNumber ?? r.Mitglied.Id.ToString()} · {r.Mitglied.DisplayName}";
                if (nameRoh.Length > 24) nameRoh = nameRoh.Substring(0, 23) + "…";
                var top = r.Findings
                    .OrderBy(f => f.Severity == FindingSeverity.Blocker ? 0 : f.Severity == FindingSeverity.Warnung ? 1 : 2)
                    .Take(2)
                    .Select(f => $"{(f.Severity == FindingSeverity.Blocker ? "✘" : f.Severity == FindingSeverity.Warnung ? "⚠" : "ℹ")}\u00A0{KurzBefund(f, r)}")
                    .ToArray();
                var befund = top.Length == 0 ? "[grey]ok[/]" : string.Join(", ", top);
                if (r.Findings.Count > 2) befund += $" [grey]+{r.Findings.Count - 2}[/]";
                var sollZelle = r.Mitglied.FreiwilligerZusatz > 0
                    ? $"{r.SollBeitrag:N2} € (+{r.Mitglied.FreiwilligerZusatz:N2})"
                    : $"{r.SollBeitrag:N2} €";
                tabelle.AddRow(
                    Markup.Escape(nameRoh),
                    Markup.Escape(r.Mitglied.GruppenKuerzel.Count == 0 ? "–" : string.Join(",", r.Mitglied.GruppenKuerzel)),
                    sollZelle,
                    status,
                    befund);
            }
            AnsiConsole.Write(tabelle);
            if (liste.Count > 200)
                AnsiConsole.MarkupLine($"[grey]… {liste.Count - 200} weitere (per --format csv/json vollständig).[/]");
            if (liste.Any(r => r.Findings.Count > 2))
                AnsiConsole.MarkupLine("[grey]Befunde gekürzt (+n) – Details: --member <ID/Nummer> oder Klick-Auswahl bei Suche.[/]");
        }

        /// <summary>
        /// Kurztext für die Tabellen-Spalte: sagt in 2–4 Worten, was genau fehlt
        /// (statt kryptischem Code). Langfassung steht in Finding.Nachricht
        /// (Detailansicht --member, csv/json). Unbekannte Codes fallen auf den Code zurück.
        /// </summary>
        private static string KurzBefund(MemberFinding f, MemberAuditResult r) => f.Code switch
        {
            "STATUS_AUSGETRETEN" => "ausgetreten, kein Einzug",
            "STAMM_NAME_FEHLT" => "Name fehlt",
            "STAMM_ADRESSE_UNVOLLSTAENDIG" => "Adresse unvollständig",
            "STAMM_PLZ_FORMAT" => "PLZ prüfen",
            "STAMM_GEBURTSTAG_FEHLT" => "Geburtstag fehlt",
            "STAMM_GEBURTSTAG_ZUKUNFT" => "Geburtstag ungültig",
            "STAMM_EMAIL_FEHLT" => "E-Mail fehlt/ungültig",
            "STAMM_LOGIN_EMAIL" => "Login-E-Mail fehlt",
            "STATUS_EINTRITT_FEHLT" => "Eintrittsdatum fehlt",
            "BEITRAG_KLASSE_FEHLT" => "Beitragsklasse fehlt",
            "BEITRAG_MEHRERE_KLASSEN" => "mehrere Klassen",
            "BEITRAG_FIRMA_KLASSE" => "Firma ohne VB04",
            "BEITRAG_VB04_OHNE_FIRMA" => "VB04 ohne Firma",
            "BEITRAG_INTERVALL" => "Intervall prüfen",
            "BEITRAG_NEGATIV" => "Beitrag negativ",
            "NACHWEIS_FEHLT" => "Nachweis fehlt",
            "NACHWEIS_ALTER_VB01" => "VB01-Alter prüfen",
            "BEITRAG_EHRENMITGLIED" => "Ehrenmitglied",
            "SEPA_EINWILLIGUNG_FEHLT" => "SEPA-Einwilligung fehlt",
            "SEPA_IBAN_FEHLT" => "IBAN fehlt/ungültig",
            "SEPA_BIC_FEHLT_AUSLAND" => "BIC fehlt (Ausland)",
            "SEPA_BIC_FEHLT" => "BIC fehlt",
            "SEPA_BIC_FORMAT" => "BIC prüfen",
            "SEPA_MANDATSREF_FEHLT" => "Mandatsref. fehlt",
            "SEPA_MANDATSDATUM_FEHLT" => "Mandatsdatum fehlt",
            "SEPA_MANDATSDATUM_ZUKUNFT" => "Mandatsdatum Zukunft",
            "ZAHLART_KEIN_SEPA" => "kein SEPA-Einzug",
            "SEPA_JA_OHNE_LASTSCHRIFT" => "SEPA-Ja ohne Lastschrift",
            "ZAHLART_FEHLT" => "Zahlungsart fehlt",
            "BANK_ABWEICHEND" => "abw. Kontoinhaber",
            "STATUS_KUENDIGUNG_FORM" => "Kündigung prüfen",
            "STATUS_GEKUENDIGT" => "gekündigt",
            "MAHN_STREICHKANDIDAT" => $"Rückstand {r.Mitglied.Saldo:N2} €",
            "MAHN_RUECKSTAND" => $"Rückstand {r.Mitglied.Saldo:N2} €",
            "MAHN_VORSCHLAG" => $"Forderung {r.ForderungGesamt:N2} €",
            "BEITRAG_LEISTUNGSBEGINN_FEHLT" => "Leistungsbeginn fehlt",
            "STATUS_DATUM_REIHENFOLGE" => "Datumsfolge prüfen",
            "API_DETAILS_UNVOLLSTAENDIG" => "API-Daten unvollständig",
            "STAMM_EMAIL_DUPLIKAT" => "E-Mail doppelt",
            "SEPA_MANDATSREF_DUPLIKAT" => "Mandatsref. doppelt",
            "BANK_IBAN_GETEILT" => "IBAN geteilt",
            "STAMM_DOPPEL_EINTRAG" => "Doppel-Eintrag?",
            _ => f.Code,
        };

        private static void ZeigeStatistik(MemberAuditSummary s)
        {
            Console.WriteLine();
            AnsiConsole.Write(new Rule("[yellow]Statistik[/]").RuleStyle("grey").LeftJustified());

            var g = new Table().Border(TableBorder.Rounded);
            g.AddColumn("[bold]Gruppe[/]");
            g.AddColumn("[bold]Anzahl[/]");
            g.AddColumn("[bold]Soll-Summe[/]");
            foreach (var grp in s.Ergebnisse
                         .SelectMany(r => r.Mitglied.GruppenKuerzel.DefaultIfEmpty("–").Select(k => (Mitglied: r, Kuerzel: k)))
                         .GroupBy(x => x.Kuerzel).OrderByDescending(x => x.Count()))
                g.AddRow(Markup.Escape(grp.Key), grp.Count().ToString(), $"{grp.Sum(x => x.Mitglied.SollBeitrag):N2} €");
            AnsiConsole.Write(g);

            var z = new Table().Border(TableBorder.Rounded);
            z.AddColumn("[bold]Zahlungsart[/]");
            z.AddColumn("[bold]Anzahl[/]");
            foreach (var grp in s.Ergebnisse.GroupBy(r => r.Mitglied.ZahlungsartText).OrderByDescending(x => x.Count()))
                z.AddRow(Markup.Escape(grp.Key), grp.Count().ToString());
            AnsiConsole.Write(z);

            var e = new Table().Border(TableBorder.Rounded);
            e.AddColumn("[bold]Eintrittsjahr[/]");
            e.AddColumn("[bold]Anzahl[/]");
            foreach (var grp in s.Ergebnisse
                         .Where(r => r.Mitglied.Eintrittsdatum.HasValue)
                         .GroupBy(r => r.Mitglied.Eintrittsdatum!.Value.Year).OrderBy(x => x.Key))
                e.AddRow(grp.Key.ToString(), grp.Count().ToString());
            AnsiConsole.Write(e);

            var top = s.Ergebnisse.Where(r => r.Mitglied.Saldo > 0).OrderByDescending(r => r.ForderungGesamt).Take(10).ToList();
            if (top.Count > 0)
            {
                var f = new Table().Border(TableBorder.Rounded);
                f.AddColumn("[bold]Top-Forderungen[/]");
                f.AddColumn("[bold]Saldo + Säumnis[/]");
                f.AddColumn("[bold]Vorschlag[/]");
                foreach (var r in top)
                    f.AddRow(Markup.Escape($"{r.Mitglied.MembershipNumber ?? r.Mitglied.Id.ToString()} · {r.Mitglied.DisplayName}"),
                        $"{r.ForderungGesamt:N2} €", Markup.Escape(r.MahnVorschlag ?? "–"));
                AnsiConsole.Write(f);
            }
        }

        private static string BuildCsv(MemberAuditSummary s)
        {
            var de = System.Globalization.CultureInfo.GetCultureInfo("de-DE");
            var sb = new StringBuilder();
            sb.AppendLine("mitgliedsnummer;name;email;gruppen;soll_eur;freiwillig_eur;saldo_eur;saeumnis_eur;forderung_eur;mahnvorschlag;einzugsfaehig;sepa_einziehbar;blocker;warnungen;codes");
            foreach (var r in s.Ergebnisse)
            {
                string Esc(string? v) => (v ?? "").Replace(";", ",").Replace("\n", " ").Replace("\r", "");
                var codes = string.Join("|", r.Findings.Select(f => $"{f.Severity}:{f.Code}"));
                sb.AppendLine(string.Join(";",
                    Esc(r.Mitglied.MembershipNumber ?? r.Mitglied.Id.ToString()),
                    Esc(r.Mitglied.DisplayName),
                    Esc(r.Mitglied.PrimaereEmail),
                    Esc(string.Join(",", r.Mitglied.GruppenKuerzel)),
                    r.SollBeitrag.ToString("N2", de),
                    r.Mitglied.FreiwilligerZusatz.ToString("N2", de),
                    r.Mitglied.Saldo.ToString("N2", de),
                    r.SaeumnisZuschlag.ToString("N2", de),
                    r.ForderungGesamt.ToString("N2", de),
                    Esc(r.MahnVorschlag),
                    r.Einzugsfaehig ? "ja" : "nein",
                    r.SepaEinziehbar ? "ja" : "nein",
                    r.BlockerCount.ToString(),
                    r.WarnungCount.ToString(),
                    Esc(codes)));
            }
            return sb.ToString();
        }

        private static string BuildJson(MemberAuditSummary s)
        {
            var payload = new
            {
                beitragsjahr = s.Beitragsjahr,
                geprueft = s.Geprueft,
                einzugsfaehig = s.Einzugsfaehig,
                sepaEinziehbar = s.SepaEinziehbar,
                mitBlocker = s.MitBlocker,
                mitWarnung = s.MitWarnung,
                summeSollEinzugsfaehig = s.SummeSollEinzugsfaehig,
                summeSollSepa = s.SummeSollSepa,
                summeFreiwillig = s.SummeFreiwillig,
                summeSaldoOffen = s.SummeSaldoOffen,
                summeSaeumnis = s.SummeSaeumnis,
                mitForderung = s.MitForderung,
                mitglieder = s.Ergebnisse.Select(r => new
                {
                    id = r.Mitglied.Id,
                    mitgliedsnummer = r.Mitglied.MembershipNumber,
                    name = r.Mitglied.DisplayName,
                    email = r.Mitglied.PrimaereEmail,
                    gruppen = r.Mitglied.GruppenKuerzel,
                    sollBeitrag = r.SollBeitrag,
                    sollBasis = r.SollBasis,
                    freiwilligerZusatz = r.Mitglied.FreiwilligerZusatz,
                    saldo = r.Mitglied.Saldo,
                    saeumnisZuschlag = r.SaeumnisZuschlag,
                    forderungGesamt = r.ForderungGesamt,
                    mahnVorschlag = r.MahnVorschlag,
                    einzugsfaehig = r.Einzugsfaehig,
                    sepaEinziehbar = r.SepaEinziehbar,
                    findings = r.Findings.Select(f => new
                    {
                        code = f.Code,
                        kategorie = f.Kategorie,
                        severity = f.Severity.ToString(),
                        nachricht = f.Nachricht,
                    }),
                }),
            };
            return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        }

        private static async Task Ausgeben(string inhalt, string? pfad, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(pfad))
            {
                Console.WriteLine(inhalt);
                return;
            }
            // Ohne BOM: sauber für Excel (Trennzeichen ;) wie für Skripte/CI
            await File.WriteAllTextAsync(pfad, inhalt, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), ct);
            AnsiConsole.MarkupLine($"[green]✔[/] Geschrieben: {Markup.Escape(Path.GetFullPath(pfad))}");
        }
    }
}
