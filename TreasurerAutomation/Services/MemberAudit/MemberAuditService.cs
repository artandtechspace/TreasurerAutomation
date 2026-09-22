namespace TreasurerAutomation.Services.MemberAudit
{
    /// <summary>
    /// Prüft einen MemberRecord gegen Satzung + Beitragsordnung + SEPA-Regeln.
    /// Read-only: keine API-Schreibzugriffe, nur Findings + Soll-Beitrag.
    /// Schwere: Blocker = kein Einzug möglich; Warnung = bitte klären; Info = Hinweis.
    /// </summary>
    public static class MemberAuditService
    {
        public static MemberAuditResult Audit(MemberRecord m, int beitragsjahr, DateTime heute)
        {
            var findings = new List<MemberFinding>();
            heute = heute.Date;

            // ---------- 0. Ausgetreten? ----------
            var ausgetreten = m.Austrittsdatum.HasValue && m.Austrittsdatum.Value.Date <= heute;
            if (ausgetreten)
            {
                findings.Add(new("STATUS_AUSGETRETEN", "Status", FindingSeverity.Info,
                    $"Austrittsdatum {m.Austrittsdatum:dd.MM.yyyy} erreicht – kein Einzug für {beitragsjahr}."));
            }

            // ---------- 1. Stammdaten ----------
            if (string.IsNullOrWhiteSpace(m.Vorname) && string.IsNullOrWhiteSpace(m.Nachname) && !m.IstFirma)
                findings.Add(new("STAMM_NAME_FEHLT", "Stammdaten", FindingSeverity.Blocker,
                    "Vor- und Nachname fehlen – Zuordnung/Mahnung nicht möglich."));

            if (string.IsNullOrWhiteSpace(m.Strasse) || string.IsNullOrWhiteSpace(m.Plz) || string.IsNullOrWhiteSpace(m.Stadt))
                findings.Add(new("STAMM_ADRESSE_UNVOLLSTAENDIG", "Stammdaten", FindingSeverity.Warnung,
                    "Adresse unvollständig (Straße/PLZ/Stadt nötig für Mahnungen, §5 Satzung / §14)."));

            if (!string.IsNullOrWhiteSpace(m.Plz) && !FieldValidators.IstGueltigeDePlz(m.Plz) &&
                string.IsNullOrWhiteSpace(m.Land))
                findings.Add(new("STAMM_PLZ_FORMAT", "Stammdaten", FindingSeverity.Warnung,
                    $"PLZ '{m.Plz}' sieht nicht wie 5-stellige DE-PLZ aus."));

            if (!m.Geburtstag.HasValue)
                findings.Add(new("STAMM_GEBURTSTAG_FEHLT", "Stammdaten",
                    m.GruppenKuerzel.Contains("VB01") ? FindingSeverity.Blocker : FindingSeverity.Warnung,
                    "Geburtstag fehlt – Alter für VB01 (bis 25J) und Stimmrecht ab 14J (§8 Abs. 10) nicht prüfbar."));
            else if (m.Geburtstag.Value.Date > heute)
                findings.Add(new("STAMM_GEBURTSTAG_ZUKUNFT", "Stammdaten", FindingSeverity.Blocker,
                    "Geburtstag liegt in der Zukunft."));

            if (!FieldValidators.IstGueltigeEmail(m.PrimaereEmail))
                findings.Add(new("STAMM_EMAIL_FEHLT", "Stammdaten", FindingSeverity.Blocker,
                    "Keine gültige primäre E-Mail – Einladungen/Mahnungen laufen per E-Mail (§8, §14)."));
            if (!FieldValidators.IstGueltigeEmail(m.LoginEmail))
                findings.Add(new("STAMM_LOGIN_EMAIL", "Stammdaten", FindingSeverity.Warnung,
                    "Login-E-Mail fehlt/ungültig – Mitglied kann sich nicht einloggen."));

            if (!m.Eintrittsdatum.HasValue)
                findings.Add(new("STATUS_EINTRITT_FEHLT", "Status", FindingSeverity.Warnung,
                    "Eintrittsdatum (joinDate) fehlt – 50%-Regel (§7 Abs. 6) nicht prüfbar."));

            // ---------- 2. Beitragsklasse ----------
            var klassen = m.GruppenKuerzel.Where(Beitragsordnung.IstBeitragsklasse).Distinct().ToList();
            if (klassen.Count == 0 && !m.Ehrenmitglied)
                findings.Add(new("BEITRAG_KLASSE_FEHLT", "Beitrag", FindingSeverity.Blocker,
                    $"Keine Beitragsklasse (VB01/VB02/VB03/VB04) zugeordnet – Soll nicht berechenbar. Kürzel: {(m.GruppenKuerzel.Count == 0 ? "–" : string.Join(",", m.GruppenKuerzel))}."));
            if (klassen.Count > 1)
                findings.Add(new("BEITRAG_MEHRERE_KLASSEN", "Beitrag", FindingSeverity.Warnung,
                    $"Mehrere Beitragsklassen {string.Join(",", klassen)} – für Soll wird höchste genommen, bitte genau eine pflegen."));

            if (m.IstFirma && !klassen.Contains("VB04") && !m.Ehrenmitglied)
                findings.Add(new("BEITRAG_FIRMA_KLASSE", "Beitrag", FindingSeverity.Warnung,
                    "Als Firma markiert, aber nicht VB04 (juristische Personen 100€) – Klasse prüfen."));
            if (!m.IstFirma && klassen.Contains("VB04"))
                findings.Add(new("BEITRAG_VB04_OHNE_FIRMA", "Beitrag", FindingSeverity.Warnung,
                    "VB04 zugeordnet, aber nicht als Firma markiert – prüfen."));

            if (m.ZahlungsintervallMonate != 12 && m.ZahlungsintervallMonate != 1 && m.ZahlungsintervallMonate != -1)
                findings.Add(new("BEITRAG_INTERVALL", "Beitrag", FindingSeverity.Warnung,
                    $"Zahlungsintervall {m.ZahlungsintervallMonate} Monate ungewöhnlich – erwartet 12 (Jahresbeitrag §2)."));
            if (m.IndividuellerBeitrag < 0)
                findings.Add(new("BEITRAG_NEGATIV", "Beitrag", FindingSeverity.Warnung,
                    "Individueller Beitrag negativ – prüfen."));

            // ---------- 3. Nachweise für ermäßigt (Beitragsordnung §3 + §7 Abs. 1) ----------
            var brauchtNachweis = klassen.Contains("VB01") || klassen.Contains("VB02.1") || klassen.Contains("VB021") || klassen.Contains("VB2M");
            if (brauchtNachweis && string.IsNullOrWhiteSpace(m.NachweisDatei))
                findings.Add(new("NACHWEIS_FEHLT", "Unterlagen", FindingSeverity.Warnung,
                    "Ermäßigte Klasse (VB01/VB02.1) ohne Nachweis-Datei – Schüler-/Studien-/Ausbildungs-/Dienst-Nachweis oder Karten-Nachweis (Münsterland/Jugendleiter/Ehrenamt) nach §7 Abs. 1 erforderlich."));
            if (klassen.Contains("VB01") && m.Geburtstag.HasValue)
            {
                var alter = m.AlterAm(new DateTime(beitragsjahr, 2, 1));
                if (alter.HasValue && alter.Value > 25)
                {
                    // Über 25 ist VB01 nur mit Karte (Münsterland/Jugendleiter/Ehrenamt) zulässig.
                    // Mit hinterlegtem Nachweis ist das plausibel -> nur Info; ohne Nachweis -> Warnung.
                    var hatNachweis = !string.IsNullOrWhiteSpace(m.NachweisDatei);
                    findings.Add(new("NACHWEIS_ALTER_VB01", "Unterlagen",
                        hatNachweis ? FindingSeverity.Info : FindingSeverity.Warnung,
                        $"Alter am 01.02.{beitragsjahr}: {alter}J – VB01 über 25J nur mit Karten-Nachweis zulässig." +
                        (hatNachweis ? " Nachweis ist hinterlegt, Kartentyp (Münsterland/Jugendleiter/Ehrenamt) bei Bedarf prüfen." : " Kein Nachweis hinterlegt – bitte prüfen/hinterlegen.")));
                }
            }

            // ---------- 4. SEPA-Readiness (§7 Abs. 3) ----------
            var soll = Beitragsordnung.SollBeitrag(m.GruppenKuerzel, m.Eintrittsdatum, beitragsjahr, m.Ehrenmitglied, m.FreiwilligerZusatz);
            var basis = Beitragsordnung.SollBeitrag(m.GruppenKuerzel, m.Eintrittsdatum, beitragsjahr, m.Ehrenmitglied);
            var sepaFaehig = true;

            if (m.Ehrenmitglied)
            {
                findings.Add(new("BEITRAG_EHRENMITGLIED", "Beitrag", FindingSeverity.Info,
                    "Ehrenmitglied (§4 Abs. 3 Satzung) – von Beiträgen befreit, Soll 0€."));
            }

            if (m.Zahlungsart == 1) // Lastschrift
            {
                if (m.SepaEinverstaendnis != true)
                {
                    findings.Add(new("SEPA_EINWILLIGUNG_FEHLT", "Zustimmung", FindingSeverity.Blocker,
                        "Zahlungsart Lastschrift, aber SEPA-Einverständnis nicht 'Ja' – ohne Einwilligung kein Einzug (§7 Abs. 3)."));
                    sepaFaehig = false;
                }
                if (string.IsNullOrWhiteSpace(m.Iban) || !FieldValidators.IstGueltigeIban(m.Iban))
                {
                    findings.Add(new("SEPA_IBAN_FEHLT", "Bank", FindingSeverity.Blocker,
                        "IBAN fehlt/ungültig (Mod97) – Lastschrift unmöglich."));
                    sepaFaehig = false;
                }
                if (string.IsNullOrWhiteSpace(m.Bic))
                {
                    if (FieldValidators.IstAuslandsIban(m.Iban, m.Land))
                    {
                        findings.Add(new("SEPA_BIC_FEHLT_AUSLAND", "Bank", FindingSeverity.Blocker,
                            "Auslands-IBAN ohne BIC – für SEPA-Einzug zwingend nötig."));
                        sepaFaehig = false;
                    }
                    else
                    {
                        findings.Add(new("SEPA_BIC_FEHLT", "Bank", FindingSeverity.Warnung,
                            "BIC fehlt – bei DE-IBAN meist auto-ergänzbar, bitte prüfen/nachtragen."));
                    }
                }
                else if (!FieldValidators.IstGueltigeBic(m.Bic))
                {
                    findings.Add(new("SEPA_BIC_FORMAT", "Bank", FindingSeverity.Warnung,
                        $"BIC '{m.Bic}' hat ungültiges Format."));
                }
                if (string.IsNullOrWhiteSpace(m.Mandatsreferenz))
                {
                    findings.Add(new("SEPA_MANDATSREF_FEHLT", "Bank", FindingSeverity.Blocker,
                        "Mandatsreferenz fehlt – für Lastschrift zwingend (muss eindeutig sein)."));
                    sepaFaehig = false;
                }
                if (!m.Mandatsdatum.HasValue)
                {
                    findings.Add(new("SEPA_MANDATSDATUM_FEHLT", "Bank", FindingSeverity.Blocker,
                        "Mandatsdatum/Unterschriftsdatum fehlt – für Lastschrift zwingend."));
                    sepaFaehig = false;
                }
                else if (m.Mandatsdatum.Value.Date > heute)
                {
                    findings.Add(new("SEPA_MANDATSDATUM_ZUKUNFT", "Bank", FindingSeverity.Blocker,
                        $"Mandatsdatum {m.Mandatsdatum:dd.MM.yyyy} liegt in der Zukunft – prüfen."));
                    sepaFaehig = false;
                }
            }
            else if (m.Zahlungsart == 2 || m.Zahlungsart == 3 || m.Zahlungsart == 4)
            {
                sepaFaehig = false;
                findings.Add(new("ZAHLART_KEIN_SEPA", "Beitrag", FindingSeverity.Info,
                    $"{m.ZahlungsartText}: kein SEPA-Einzug – Rechnung/Überweisung manuell (Standardkonto auf Rechnung, §8 Beitragsordnung)."));
                if (m.SepaEinverstaendnis == true)
                    findings.Add(new("SEPA_JA_OHNE_LASTSCHRIFT", "Zustimmung", FindingSeverity.Warnung,
                        "SEPA-Einverständnis 'Ja', aber Zahlungsart ist nicht Lastschrift – Zahlungsart oder Einwilligung prüfen."));
            }
            else
            {
                sepaFaehig = false;
                findings.Add(new("ZAHLART_FEHLT", "Beitrag", FindingSeverity.Blocker,
                    "Zahlungsart nicht gewählt – bitte Lastschrift/Überweisung/Bar pflegen."));
            }

            // Hinweis: Die API liefert kein "Nächste Zahlung"-Feld (nur Export) –
            // Fälligkeit folgt aus §2 (01.02.) + Leistungsbeginn, daher keine eigene Prüfung.
            // Nur bei Lastschrift relevant (Mandat muss zum Kontoinhaber passen, z.B. Elternkonto).
            if (m.Zahlungsart == 1 && !string.IsNullOrWhiteSpace(m.KontoinhaberAbweichend))
                findings.Add(new("BANK_ABWEICHEND", "Bank", FindingSeverity.Info,
                    $"Abweichender Kontoinhaber: {m.KontoinhaberAbweichend} (z.B. Elternkonto) – ok, Mandat muss dazu passen."));

            // ---------- 5. Status / Mahnwesen (Satzung §5, Beitragsordnung §5) ----------
            if (m.Kuendigungsdatum.HasValue)
            {
                if (!IstQuartalsendeMitFrist(m.Kuendigungsdatum.Value, m.Austrittsdatum))
                    findings.Add(new("STATUS_KUENDIGUNG_FORM", "Status", FindingSeverity.Warnung,
                        $"Kündigung {m.Kuendigungsdatum:dd.MM.yyyy} entspricht evtl. nicht Quartalsende + 4 Wochen (§5 Abs. 2 Satzung) – prüfen."));
                else
                    findings.Add(new("STATUS_GEKUENDIGT", "Status", FindingSeverity.Info,
                        $"Gekündigt zum {m.Austrittsdatum:dd.MM.yyyy} – kein Einzug nach Austritt, offene Forderung bleibt."));
            }
            if (m.Saldo > 0 && !ausgetreten)
            {
                if (soll > 0 && m.Saldo > soll)
                    findings.Add(new("MAHN_STREICHKANDIDAT", "Mahnwesen", FindingSeverity.Blocker,
                        $"Saldo {m.Saldo:N2}€ übersteigt einen Jahresbeitrag (Soll {soll:N2}€) – Streichverfahren nach §5 Abs. 3 prüfen (2x Mahnung + 1 Monat + Androhung)."));
                else
                    findings.Add(new("MAHN_RUECKSTAND", "Mahnwesen", FindingSeverity.Warnung,
                        $"Saldo {m.Saldo:N2}€ offen – Zahlungserinnerung/Mahnung prüfen (§5 Beitragsordnung: 1€/7T Säumnis, Mahn 5€/10€)."));
            }

            // ---------- 6. Forderung: Säumniszuschlag + Mahnvorschlag (§5 Beitragsordnung) ----------
            // Fällig 01.02. (§2), Verzug ab Folgetag (Werktags-Regel vereinfacht: 02.02.).
            // Zuschlag 1 € je angefangene? Nein: je volle 7 Kalendertage. Unverbindliche Rechenhilfe.
            var (tageVerzug, saeumnis) = BerechneSaeumnis(m.Saldo, beitragsjahr, heute, ausgetreten);
            string? mahnVorschlag = null;
            if (m.Saldo > 0 && !ausgetreten)
            {
                mahnVorschlag = MahnVorschlagFuer(tageVerzug, m.Saldo, soll);
                findings.Add(new("MAHN_VORSCHLAG", "Mahnwesen", FindingSeverity.Info,
                    $"Forderung ca. {m.Saldo + saeumnis:N2}€ (Saldo {m.Saldo:N2}€ + Säumnis {saeumnis:N2}€ bei {tageVerzug} Tagen Verzug) – Vorschlag: {mahnVorschlag}."));
            }
            if (!m.Leistungsbeginn.HasValue && !m.Ehrenmitglied)
                findings.Add(new("BEITRAG_LEISTUNGSBEGINN_FEHLT", "Beitrag", FindingSeverity.Warnung,
                    "Beginn des ersten Leistungszeitraums fehlt – für anteilige Abrechnung wichtig."));

            // Plausibilität Antrag/Aufnahme/Eintritt
            if (m.Antragsdatum.HasValue && m.Aufnahmedatum.HasValue && m.Antragsdatum > m.Aufnahmedatum)
                findings.Add(new("STATUS_DATUM_REIHENFOLGE", "Status", FindingSeverity.Warnung,
                    "Antragsdatum liegt nach Aufnahmedatum – Reihenfolge prüfen."));

            var blocker = findings.Any(f => f.Severity == FindingSeverity.Blocker);
            var einzugsfaehig = !blocker && !ausgetreten && (m.Zahlungsart == 1 || m.Zahlungsart == 2);
            if (m.Ehrenmitglied) einzugsfaehig = !blocker && !ausgetreten; // Soll 0, nichts einzuziehen
            var sepaEinziehbar = einzugsfaehig && m.Zahlungsart == 1 && sepaFaehig;

            // Ehrenmitglied mit Soll 0 ist formal "einzugsfähig" (nichts zu tun) – klarstellen
            if (m.Ehrenmitglied && !blocker) { einzugsfaehig = true; sepaEinziehbar = false; }

            return new MemberAuditResult
            {
                Mitglied = m,
                Findings = findings,
                SollBeitrag = ausgetreten ? 0m : soll,
                SollBasis = ausgetreten ? 0m : basis,
                Einzugsfaehig = einzugsfaehig,
                SepaEinziehbar = sepaEinziehbar,
                TageVerzug = tageVerzug,
                SaeumnisZuschlag = saeumnis,
                MahnVorschlag = mahnVorschlag,
            };
        }

        /// <summary>
        /// Rechenhilfe zu §5 Beitragsordnung: 1,00 € je 7 Kalendertage Verzug auf den
        /// ausstehenden Beitrag. Fälligkeit 01.02. des Beitragsjahres, Verzug ab 02.02.
        /// (Bankarbeitstags-Regel vereinfacht; ohne Gewähr, nur für Mahnlauf-Vorbereitung.)
        /// </summary>
        public static (int TageVerzug, decimal Saeumnis) BerechneSaeumnis(
            decimal saldo, int beitragsjahr, DateTime heute, bool ausgetreten)
        {
            if (saldo <= 0 || ausgetreten) return (0, 0m);
            var verzugAb = new DateTime(beitragsjahr, 2, 2);
            var tage = (heute.Date - verzugAb).Days;
            if (tage < 0) tage = 0;
            return (tage, (tage / 7) * 1m);
        }

        /// <summary>
        /// Grober Mahnlauf-Vorschlag aus Verzugstagen (Fristen §5: Mahn 1 nach 4 Wo / 5 €, Mahn 2 nach 6 Wo / 10 €).
        /// </summary>
        public static string MahnVorschlagFuer(int tageVerzug, decimal saldo, decimal soll)
        {
            if (soll > 0 && saldo > soll)
                return "Streichverfahren prüfen (§5 Abs. 3 Satzung)";
            if (tageVerzug >= 70) return "Mahnung 2 (10 €, 6 Wochen)";
            if (tageVerzug >= 28) return "Mahnung 1 (5 €, 4 Wochen)";
            return "Zahlungserinnerung";
        }

        /// <summary>
        /// Satzung §5 Abs. 2: Austritt nur zum Quartalsende mit 4 Wochen Frist.
        /// Prüft grob: Austritt muss Quartalsletzter sein und Kündigung >= 28 Tage davor.
        /// </summary>
        public static bool IstQuartalsendeMitFrist(DateTime kuendigung, DateTime? austritt)
        {
            if (!austritt.HasValue) return false;
            var a = austritt.Value.Date;
            var quartalsEnde = a.Month is 3 or 6 or 9 or 12
                && a.Day == DateTime.DaysInMonth(a.Year, a.Month);
            if (!quartalsEnde) return false;
            return (a - kuendigung.Date).TotalDays >= 28;
        }

        public static MemberAuditSummary Zusammenfassen(IEnumerable<MemberAuditResult> ergebnisse, int beitragsjahr)
        {
            var list = ergebnisse.ToList();
            return new MemberAuditSummary
            {
                Beitragsjahr = beitragsjahr,
                Geprueft = list.Count,
                Einzugsfaehig = list.Count(r => r.Einzugsfaehig),
                SepaEinziehbar = list.Count(r => r.SepaEinziehbar),
                MitBlocker = list.Count(r => r.BlockerCount > 0),
                MitWarnung = list.Count(r => r.WarnungCount > 0),
                SummeSollEinzugsfaehig = list.Where(r => r.Einzugsfaehig).Sum(r => r.SollBeitrag),
                SummeSollSepa = list.Where(r => r.SepaEinziehbar).Sum(r => r.SollBeitrag),
                SummeFreiwillig = list.Where(r => r.Einzugsfaehig).Sum(r => r.Mitglied.FreiwilligerZusatz),
                SummeSaldoOffen = list.Where(r => r.Mitglied.Saldo > 0).Sum(r => r.Mitglied.Saldo),
                SummeSaeumnis = list.Sum(r => r.SaeumnisZuschlag),
                MitForderung = list.Count(r => r.Mitglied.Saldo > 0),
                Ergebnisse = list
                    .OrderByDescending(r => r.BlockerCount)
                    .ThenByDescending(r => r.WarnungCount)
                    .ThenBy(r => r.Mitglied.DisplayName)
                    .ToList(),
            };
        }
    }
}
