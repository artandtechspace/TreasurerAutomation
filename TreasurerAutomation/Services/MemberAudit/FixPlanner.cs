namespace TreasurerAutomation.Services.MemberAudit
{
    /// <summary>
    /// Ein Fix-Vorschlag. Auto=true: technisch ableitbar, per PATCH ausführbar
    /// (Dry-Run Standard, --apply schreibt). Auto=false: manuell klären
    /// (Zustimmungen erfinden wir nie – nur anfordern).
    /// </summary>
    public sealed record FixVorschlag(
        int MemberId,
        string? MembershipNumber,
        string DisplayName,
        string Aktion,
        string Feld,
        string Alt,
        string Neu,
        bool Auto,
        string Begruendung);

    /// <summary>
    /// Reine Planungslogik (ohne HTTP): leitet aus Audit-Ergebnissen sichere
    /// Auto-Fixes + manuelle To-dos ab. Getestet ohne Netzwerk.
    /// </summary>
    public static class FixPlanner
    {
        public static List<FixVorschlag> Plane(
            IReadOnlyList<MemberAuditResult> results,
            int beitragsjahr)
        {
            var plaene = new List<FixVorschlag>();
            var vergebeneRefs = results
                .Where(r => !string.IsNullOrWhiteSpace(r.Mitglied.Mandatsreferenz))
                .Select(r => r.Mitglied.Mandatsreferenz!.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var r in results)
            {
                var m = r.Mitglied;
                var nr = m.MembershipNumber;
                var name = m.DisplayName;

                // 1. E-Mail-Normalisierung (Trim + lowercase) – technisch, zustimmungsfrei
                PlaneEmailNorm(plaene, m, nr, name, m.PrivateEmail, "privateEmail");
                PlaneEmailNorm(plaene, m, nr, name, m.CompanyEmail, "companyEmail");

                // 2. Fehlende Mandatsreferenz erzeugen (nur wenn Rest SEPA-ok + Einwilligung Ja)
                if (m.Zahlungsart == 1 && m.SepaEinverstaendnis == true
                    && string.IsNullOrWhiteSpace(m.Mandatsreferenz)
                    && !string.IsNullOrWhiteSpace(m.Iban) && FieldValidators.IstGueltigeIban(m.Iban)
                    && m.Mandatsdatum.HasValue && m.Mandatsdatum.Value.Date <= DateTime.Today)
                {
                    var neu = FreieMandatsref(m.Id, beitragsjahr, vergebeneRefs);
                    vergebeneRefs.Add(neu);
                    plaene.Add(new(m.Id, nr, name, "MANDATSREF_NEU", "sepaMandate", "– fehlt", neu, true,
                        "Lastschrift sonst blockiert; Referenz muss eindeutig sein (Vorschlag, kein Consent)."));
                }

                // 3. Zahlungsart auf Lastschrift heben (nur wenn alles SEPA-ok + Einwilligung Ja)
                if (m.Zahlungsart != 1 && m.SepaEinverstaendnis == true
                    && !string.IsNullOrWhiteSpace(m.Iban) && FieldValidators.IstGueltigeIban(m.Iban)
                    && !string.IsNullOrWhiteSpace(m.Mandatsreferenz) && m.Mandatsdatum.HasValue)
                {
                    plaene.Add(new(m.Id, nr, name, "ZAHLART_LASTSCHRIFT", "methodOfPayment",
                        m.ZahlungsartText, "Lastschrift", true,
                        "Einwilligung + IBAN + Mandat vorhanden – Zahlart kann auf Lastschrift."));
                }

                // 4. Manuell: VB01 über 25 ohne Nachweis -> Klassenwechsel prüfen
                if (m.GruppenKuerzel.Contains("VB01") && string.IsNullOrWhiteSpace(m.NachweisDatei))
                {
                    var alter = m.AlterAm(new DateTime(beitragsjahr, 2, 1));
                    if (alter is null)
                        plaene.Add(new(m.Id, nr, name, "NACHWEIS_ANFORDERN", "Nachweis ermäßigt",
                            "– fehlt", "Nachweis anfordern", false,
                            "VB01 braucht Schüler-/Studien-/Dienst- oder Karten-Nachweis (§7 Abs. 1)."));
                    else if (alter > 25)
                        plaene.Add(new(m.Id, nr, name, "GRUPPE_WECHSEL_VORSCHLAG", "memberGroups",
                            "VB01", "VB03 prüfen (60 €)", false,
                            $"Alter {alter}J am 01.02.{beitragsjahr}: VB01 nur mit Karte zulässig – sonst Wechsel auf VB03."));
                    else
                        plaene.Add(new(m.Id, nr, name, "NACHWEIS_ANFORDERN", "Nachweis ermäßigt",
                            "– fehlt", "Nachweis anfordern", false,
                            "VB01 braucht Schüler-/Studien-/Dienst- oder Karten-Nachweis (§7 Abs. 1)."));
                }

                // 5. Manuell: Lastschrift ohne Einwilligung -> Einwilligung anfordern (nie erfinden!)
                if (m.Zahlungsart == 1 && m.SepaEinverstaendnis != true)
                {
                    plaene.Add(new(m.Id, nr, name, "SEPA_EINWILLIGUNG_ANFORDERN", "SEPA-Einwilligung",
                        m.SepaEinverstaendnis is null ? "unbekannt" : "Nein",
                        "Unterschriebenes Mandat anfordern", false,
                        "Ohne Einwilligung kein Einzug (§7 Abs. 3) – Einwilligung einholen, nicht setzen."));
                }
            }

            return plaene
                .OrderBy(p => p.Auto ? 0 : 1)
                .ThenBy(p => p.DisplayName)
                .ToList();
        }

        private static void PlaneEmailNorm(
            List<FixVorschlag> plaene, MemberRecord m,
            string? nr, string name, string? roh, string feld)
        {
            if (string.IsNullOrWhiteSpace(roh)) return;
            var norm = roh.Trim().ToLowerInvariant();
            if (norm != roh && FieldValidators.IstGueltigeEmail(norm))
            {
                plaene.Add(new(m.Id, nr, name, "EMAIL_NORM", feld, roh, norm, true,
                    "Trim + lowercase – technisch, keine Adressänderung."));
            }
        }

        internal static string FreieMandatsref(int memberId, int jahr, HashSet<string> vergeben)
        {
            var basis = $"EV-{memberId}-{jahr}";
            if (!vergeben.Contains(basis)) return basis;
            var i = 2;
            while (vergeben.Contains($"{basis}-{i}")) i++;
            return $"{basis}-{i}";
        }
    }
}
