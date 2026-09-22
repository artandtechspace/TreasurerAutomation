using System.Text.Json;
using TreasurerAutomation.Services.MemberAudit;
using Xunit;

namespace TreasurerAutomation.Tests
{
    /// <summary>
    /// Kern-Regeln: SEPA-Readiness (§7 Abs. 3), Nachweise (§7 Abs. 1), Stammdaten, Status (§5).
    /// Nutzt anonymisierte Fixtures (keine Echtdaten).
    /// </summary>
    public class MemberAuditServiceTests
    {
        private static readonly DateTime Heute = new(2026, 9, 22);
        private const int Jahr = 2026;

        private static MemberRecord Basis(
            string kuerzel = "VB03",
            int zahlart = 1,
            bool? sepaJa = true) => new()
            {
                Id = 1,
                MembershipNumber = "1",
                DisplayName = "Max Muster",
                Vorname = "Max",
                Nachname = "Muster",
                LoginEmail = "max@muster.de",
                PrimaereEmail = "max@muster.de",
                Geburtstag = new DateTime(1990, 1, 1),
                Strasse = "Musterstr. 1",
                Plz = "48431",
                Stadt = "Rheine",
                Eintrittsdatum = new DateTime(2024, 1, 10),
                Zahlungsart = zahlart,
                ZahlungsartText = zahlart == 1 ? "Lastschrift" : "Überweisung",
                Iban = "DE89 3704 0044 0532 0130 00",
                Bic = "COBADEFFXXX",
                Mandatsreferenz = "MANDAT-1",
                Mandatsdatum = new DateTime(2024, 1, 11),
                GruppenKuerzel = new List<string> { kuerzel },
                GruppenNamen = new List<string>(),
                SepaEinverstaendnis = sepaJa,
                ZahlungsintervallMonate = 12,
                Leistungsbeginn = new DateTime(2024, 1, 1),
                NaechsteZahlung = new DateTime(2027, 2, 1),
            };

        [Fact]
        public void SauberesLastschriftMitglied_IstSepaEinziehbar()
        {
            var r = MemberAuditService.Audit(Basis(), Jahr, Heute);
            Assert.True(r.SepaEinziehbar);
            Assert.True(r.Einzugsfaehig);
            Assert.Equal(60m, r.SollBeitrag);
            Assert.Equal(0, r.BlockerCount);
        }

        [Fact]
        public void LastschriftOhneEinwilligung_Blockt()
        {
            var r = MemberAuditService.Audit(Basis(sepaJa: false), Jahr, Heute);
            Assert.False(r.SepaEinziehbar);
            Assert.Contains(r.Findings, f => f.Code == "SEPA_EINWILLIGUNG_FEHLT" && f.Severity == FindingSeverity.Blocker);
        }

        [Fact]
        public void LastschriftOhneIban_Blockt_AbbildungExcelBefund()
        {
            // Excel-Realität: 58x Lastschrift, aber nur 18x SEPA-Ja + 8x ohne IBAN
            var m = Basis();
            m = new MemberRecord
            {
                Id = m.Id, MembershipNumber = m.MembershipNumber, DisplayName = m.DisplayName,
                Vorname = m.Vorname, Nachname = m.Nachname, LoginEmail = m.LoginEmail,
                PrimaereEmail = m.PrimaereEmail, Geburtstag = m.Geburtstag,
                Strasse = m.Strasse, Plz = m.Plz, Stadt = m.Stadt,
                Eintrittsdatum = m.Eintrittsdatum, Zahlungsart = 1, ZahlungsartText = "Lastschrift",
                Iban = null, Bic = null, Mandatsreferenz = "MANDAT-1", Mandatsdatum = Heute,
                GruppenKuerzel = m.GruppenKuerzel, GruppenNamen = m.GruppenNamen,
                SepaEinverstaendnis = false, ZahlungsintervallMonate = 12,
                Leistungsbeginn = m.Leistungsbeginn, NaechsteZahlung = m.NaechsteZahlung,
            };
            var r = MemberAuditService.Audit(m, Jahr, Heute);
            Assert.False(r.Einzugsfaehig);
            Assert.Contains(r.Findings, f => f.Code == "SEPA_IBAN_FEHLT");
            Assert.Contains(r.Findings, f => f.Code == "SEPA_EINWILLIGUNG_FEHLT");
        }

        [Fact]
        public void MandatsreferenzFehlt_Blockt()
        {
            var b = Basis();
            var m = KopieMit(b, mandatsref: null);
            var r = MemberAuditService.Audit(m, Jahr, Heute);
            Assert.Contains(r.Findings, f => f.Code == "SEPA_MANDATSREF_FEHLT" && f.Severity == FindingSeverity.Blocker);
        }

        [Fact]
        public void MandatsdatumZukunft_Blockt()
        {
            var b = Basis();
            var m = KopieMit(b, mandatsdatum: new DateTime(2027, 1, 1), useMandatsdatum: true);
            var r = MemberAuditService.Audit(m, Jahr, Heute);
            Assert.Contains(r.Findings, f => f.Code == "SEPA_MANDATSDATUM_ZUKUNFT");
        }

        [Fact]
        public void Vb01OhneNachweis_Warnt()
        {
            // Excel-Realität: ~38 VB01, nur 1 Nachweis-Datei
            var r = MemberAuditService.Audit(Basis("VB01"), Jahr, Heute);
            Assert.Contains(r.Findings, f => f.Code == "NACHWEIS_FEHLT" && f.Severity == FindingSeverity.Warnung);
            // Nachweis allein blockt den Einzug nicht, SEPA bleibt möglich
            Assert.True(r.SepaEinziehbar);
        }

        [Fact]
        public void Vb01Ueber25_WarntAltersRegel()
        {
            var b = Basis("VB01");
            var m = KopieMit(b, geburtstag: new DateTime(1980, 1, 1));
            var r = MemberAuditService.Audit(m, Jahr, Heute);
            Assert.Contains(r.Findings, f => f.Code == "NACHWEIS_ALTER_VB01" && f.Severity == FindingSeverity.Warnung);
        }

        [Fact]
        public void Vb01Ueber25_MitNachweis_NurInfo()
        {
            var b = Basis("VB01");
            b = KopieMit(b, geburtstag: new DateTime(1980, 1, 1));
            b = new MemberRecord
            {
                Id = b.Id, MembershipNumber = b.MembershipNumber, DisplayName = b.DisplayName,
                Vorname = b.Vorname, Nachname = b.Nachname, LoginEmail = b.LoginEmail,
                PrimaereEmail = b.PrimaereEmail, Geburtstag = b.Geburtstag,
                Strasse = b.Strasse, Plz = b.Plz, Stadt = b.Stadt, Land = b.Land,
                Eintrittsdatum = b.Eintrittsdatum, Zahlungsart = b.Zahlungsart, ZahlungsartText = b.ZahlungsartText,
                Iban = b.Iban, Bic = b.Bic, Mandatsreferenz = b.Mandatsreferenz, Mandatsdatum = b.Mandatsdatum,
                Saldo = b.Saldo, Leistungsbeginn = b.Leistungsbeginn, NaechsteZahlung = b.NaechsteZahlung,
                GruppenKuerzel = new List<string>(b.GruppenKuerzel), GruppenNamen = new List<string>(b.GruppenNamen),
                SepaEinverstaendnis = b.SepaEinverstaendnis, NachweisDatei = "https://easyverein.com/app/file/?category=certs&path=x/nachweis.jpeg",
            };
            var r = MemberAuditService.Audit(b, Jahr, Heute);
            Assert.Contains(r.Findings, f => f.Code == "NACHWEIS_ALTER_VB01" && f.Severity == FindingSeverity.Info);
            Assert.DoesNotContain(r.Findings, f => f.Code == "NACHWEIS_FEHLT");
        }

        [Fact]
        public void FehlendeBeitragsklasse_Blockt()
        {
            var b = Basis("VBF");
            var r = MemberAuditService.Audit(b, Jahr, Heute);
            Assert.Contains(r.Findings, f => f.Code == "BEITRAG_KLASSE_FEHLT");
            Assert.False(r.Einzugsfaehig);
        }

        [Fact]
        public void MehrereKlassen_WarntNimmtMax()
        {
            var b = Basis("VB01");
            b.GruppenKuerzel.Add("VB03");
            var r = MemberAuditService.Audit(b, Jahr, Heute);
            Assert.Contains(r.Findings, f => f.Code == "BEITRAG_MEHRERE_KLASSEN");
            Assert.Equal(60m, r.SollBeitrag);
        }

        [Fact]
        public void FehlendeEmail_Blockt()
        {
            var b = Basis();
            var m = KopieMit(b, email: "keine-mail");
            var r = MemberAuditService.Audit(m, Jahr, Heute);
            Assert.Contains(r.Findings, f => f.Code == "STAMM_EMAIL_FEHLT" && f.Severity == FindingSeverity.Blocker);
        }

        [Fact]
        public void FehlenderGeburtstag_BlocktBeiVb01_WarntSonst()
        {
            var r1 = MemberAuditService.Audit(KopieMit(Basis("VB01"), geburtstag: null, useGeburtstag: true), Jahr, Heute);
            Assert.Contains(r1.Findings, f => f.Code == "STAMM_GEBURTSTAG_FEHLT" && f.Severity == FindingSeverity.Blocker);
            var r2 = MemberAuditService.Audit(KopieMit(Basis("VB03"), geburtstag: null, useGeburtstag: true), Jahr, Heute);
            Assert.Contains(r2.Findings, f => f.Code == "STAMM_GEBURTSTAG_FEHLT" && f.Severity == FindingSeverity.Warnung);
        }

        [Fact]
        public void Ausgetreten_WirdNichtEingezogen()
        {
            var b = Basis();
            var m = KopieMit(b, austritt: new DateTime(2026, 9, 1), useAustritt: true);
            var r = MemberAuditService.Audit(m, Jahr, Heute);
            Assert.False(r.Einzugsfaehig);
            Assert.Equal(0m, r.SollBeitrag);
            Assert.Contains(r.Findings, f => f.Code == "STATUS_AUSGETRETEN");
        }

        [Fact]
        public void HoherSaldo_MarkiertStreichkandidat()
        {
            var b = Basis();
            var m = KopieMit(b, saldo: 120m); // > 1 Jahresbeitrag (60)
            var r = MemberAuditService.Audit(m, Jahr, Heute);
            Assert.Contains(r.Findings, f => f.Code == "MAHN_STREICHKANDIDAT" && f.Severity == FindingSeverity.Blocker);
        }

        [Fact]
        public void KuendigungAbseitsQuartalsende_Warnt()
        {
            // §5 Abs. 2: nur Quartalsende + 4 Wochen
            Assert.True(MemberAuditService.IstQuartalsendeMitFrist(new DateTime(2026, 2, 1), new DateTime(2026, 3, 31)));
            Assert.False(MemberAuditService.IstQuartalsendeMitFrist(new DateTime(2026, 3, 20), new DateTime(2026, 3, 31)));
            Assert.False(MemberAuditService.IstQuartalsendeMitFrist(new DateTime(2026, 1, 1), new DateTime(2026, 2, 15)));
        }

        [Fact]
        public void Ehrenmitglied_SollNull_KeinSepa()
        {
            var b = Basis("VB03");
            var m = KopieMit(b, ehrenmitglied: true);
            var r = MemberAuditService.Audit(m, Jahr, Heute);
            Assert.Equal(0m, r.SollBeitrag);
            Assert.Contains(r.Findings, f => f.Code == "BEITRAG_EHRENMITGLIED");
        }

        private static MemberRecord KopieMit(MemberRecord b,
            string? mandatsref = "KEEP", DateTime? mandatsdatum = null, bool useMandatsdatum = false,
            DateTime? geburtstag = null, bool useGeburtstag = false,
            string? email = "KEEP", DateTime? austritt = null, bool useAustritt = false,
            decimal saldo = 0m, bool ehrenmitglied = false)
        {
            return new MemberRecord
            {
                Id = b.Id, MembershipNumber = b.MembershipNumber, DisplayName = b.DisplayName,
                Vorname = b.Vorname, Nachname = b.Nachname,
                LoginEmail = b.LoginEmail,
                PrimaereEmail = email == "KEEP" ? b.PrimaereEmail : email,
                Geburtstag = useGeburtstag ? geburtstag : (geburtstag ?? b.Geburtstag),
                Strasse = b.Strasse, Plz = b.Plz, Stadt = b.Stadt, Land = b.Land,
                Eintrittsdatum = b.Eintrittsdatum,
                Austrittsdatum = useAustritt ? austritt : (austritt ?? b.Austrittsdatum),
                Kuendigungsdatum = b.Kuendigungsdatum,
                Antragsdatum = b.Antragsdatum, Aufnahmedatum = b.Aufnahmedatum,
                Zahlungsart = b.Zahlungsart, ZahlungsartText = b.ZahlungsartText,
                Iban = b.Iban, Bic = b.Bic, KontoinhaberAbweichend = b.KontoinhaberAbweichend,
                Mandatsreferenz = mandatsref == "KEEP" ? b.Mandatsreferenz : mandatsref,
                Mandatsdatum = useMandatsdatum ? mandatsdatum : (mandatsdatum ?? b.Mandatsdatum),
                Saldo = saldo,
                Leistungsbeginn = b.Leistungsbeginn, NaechsteZahlung = b.NaechsteZahlung,
                IndividuellerBeitrag = b.IndividuellerBeitrag, ZahlungsintervallMonate = b.ZahlungsintervallMonate,
                GruppenKuerzel = new List<string>(b.GruppenKuerzel), GruppenNamen = new List<string>(b.GruppenNamen),
                Ehrenmitglied = ehrenmitglied, Vorstand = b.Vorstand, IstFirma = b.IstFirma,
                SepaEinverstaendnis = b.SepaEinverstaendnis, NachweisDatei = b.NachweisDatei,
                Newsletter = b.Newsletter, FreiwilligerZusatz = b.FreiwilligerZusatz,
            };
        }
    }
}
