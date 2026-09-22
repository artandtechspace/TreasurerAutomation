namespace TreasurerAutomation.Services.MemberAudit
{
    public enum FindingSeverity
    {
        Blocker,
        Warnung,
        Info
    }

    public sealed record MemberFinding(
        string Code,
        string Kategorie,
        FindingSeverity Severity,
        string Nachricht);

    /// <summary>
    /// Normalisierter Mitglieder-Datensatz: Felder aus member + contact-details + custom-fields + groups.
    /// Tolerant befüllt (API liefert je nach Expand teils IDs statt Objekte).
    /// </summary>
    public sealed class MemberRecord
    {
        public int Id { get; init; }
        public string? MembershipNumber { get; init; }
        public string DisplayName { get; init; } = string.Empty;

        public string? Vorname { get; init; }
        public string? Nachname { get; init; }
        public string? LoginEmail { get; init; }
        public string? PrimaereEmail { get; init; }
        public string? PrivateEmail { get; init; }
        public string? CompanyEmail { get; init; }
        public DateTime? Geburtstag { get; init; }

        public string? Strasse { get; init; }
        public string? Plz { get; init; }
        public string? Stadt { get; init; }
        public string? Land { get; init; }

        public DateTime? Eintrittsdatum { get; init; }      // joinDate
        public DateTime? Austrittsdatum { get; init; }      // resignationDate
        public DateTime? Kuendigungsdatum { get; init; }    // resignationNoticeDate
        public DateTime? Antragsdatum { get; init; }        // _applicationDate
        public DateTime? Aufnahmedatum { get; init; }       // _applicationWasAcceptedAt

        // Zahlung / SEPA (contact-details)
        public int Zahlungsart { get; init; }               // 0=nicht gewählt, 1=Lastschrift, 2=Überweisung, 3=Bar, 4=sonst
        public string ZahlungsartText { get; init; } = string.Empty;
        public string? Iban { get; init; }
        public string? Bic { get; init; }
        public string? KontoinhaberAbweichend { get; init; }
        public string? Mandatsreferenz { get; init; }       // sepaMandate
        public DateTime? Mandatsdatum { get; init; }        // sepaDate
        public decimal Saldo { get; init; }

        public DateTime? Leistungsbeginn { get; init; }     // _paymentStartDate
        public DateTime? NaechsteZahlung { get; init; }
        public decimal IndividuellerBeitrag { get; init; }
        public int ZahlungsintervallMonate { get; init; } = 12;

        public List<string> GruppenKuerzel { get; init; } = new();
        public List<string> GruppenNamen { get; init; } = new();

        public bool Ehrenmitglied { get; init; }
        public bool Vorstand { get; init; }
        public bool IstFirma { get; init; }

        // Custom fields (Namen normalisiert, vgl. Excel-Export)
        public bool? SepaEinverstaendnis { get; init; }     // Ja=true, Nein=false, null=unbekannt
        public string? NachweisDatei { get; init; }         // Nachweis für ermäßigten Beitrag
        public bool? Newsletter { get; init; }
        public decimal FreiwilligerZusatz { get; init; }

        public int? AlterAm(DateTime stichtag)
        {
            if (!Geburtstag.HasValue) return null;
            var alter = stichtag.Year - Geburtstag.Value.Year;
            if (Geburtstag.Value.Date > stichtag.AddYears(-alter)) alter--;
            return alter;
        }
    }

    public sealed class MemberAuditResult
    {
        public MemberRecord Mitglied { get; init; } = null!;
        public List<MemberFinding> Findings { get; init; } = new();
        public decimal SollBeitrag { get; init; }
        public bool Einzugsfaehig { get; init; }
        public bool SepaEinziehbar { get; init; }

        // Forderung (§5 Beitragsordnung: 1 € Säumnis je 7 Kalendertage ab Verzug)
        public int TageVerzug { get; init; }
        public decimal SaeumnisZuschlag { get; init; }
        public decimal ForderungGesamt => Mitglied.Saldo + SaeumnisZuschlag;
        public string? MahnVorschlag { get; init; }

        public int BlockerCount => Findings.Count(f => f.Severity == FindingSeverity.Blocker);
        public int WarnungCount => Findings.Count(f => f.Severity == FindingSeverity.Warnung);
    }

    public sealed class MemberAuditSummary
    {
        public int Beitragsjahr { get; init; }
        public int Geprueft { get; init; }
        public int Einzugsfaehig { get; init; }
        public int SepaEinziehbar { get; init; }
        public int MitBlocker { get; init; }
        public int MitWarnung { get; init; }
        public decimal SummeSollEinzugsfaehig { get; init; }
        public decimal SummeSollSepa { get; init; }
        public decimal SummeSaldoOffen { get; init; }
        public decimal SummeSaeumnis { get; init; }
        public int MitForderung { get; init; }
        public List<MemberAuditResult> Ergebnisse { get; init; } = new();
    }
}
