using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using TreasurerAutomation.Commands;
using TreasurerAutomation.Services.MemberAudit;
using Xunit;

namespace TreasurerAutomation.Tests
{
    /// <summary>
    /// Stellt sicher, dass JEDER Finding-Code einen Kurztext für die Tabelle hat
    /// (kein roher CODE_SLOP in der Anzeige). Schlägt fehl, sobald ein neuer Code
    /// im Service entsteht, der nicht in KurzBefund gemappt wurde.
    /// Nutzt anonymisierte Fixtures, keine Echtdaten.
    /// </summary>
    public class BefundAbdeckungTests
    {
        private static readonly DateTime Heute = new(2026, 9, 22);
        private const int Jahr = 2026;
        private static int _nr;

        private static MemberRecord Neu(
            string? vorname = "Max", string? nachname = "Muster",
            string? strasse = "Weg 1", string? plz = "48431", string? stadt = "Rheine", string? land = null,
            DateTime? geburtstag = null, bool ohneGeburtstag = false,
            string? email = null, string? loginEmail = null,
            DateTime? eintritt = null, bool ohneEintritt = false,
            DateTime? austritt = null, DateTime? kuendigung = null,
            DateTime? antrag = null, DateTime? aufnahme = null,
            int zahlart = 1, bool? sepaJa = true,
            string? iban = "DE89370400440532013000", string? bic = "COBADEFFXXX",
            string? mandat = null, DateTime? mandatDatum = null, bool ohneMandatDatum = false,
            bool ohneMandat = false,
            string? inhaber = null, decimal saldo = 0m,
            List<string>? gruppen = null, bool firma = false, bool ehre = false,
            string? nachweis = "n.pdf", int intervall = 12, decimal individuell = 0m,
            bool ohneLeistung = false)
        {
            _nr++;
            return new MemberRecord
            {
                Id = _nr,
                MembershipNumber = _nr.ToString(),
                DisplayName = $"Person {_nr}",
                Vorname = vorname, Nachname = nachname,
                LoginEmail = loginEmail ?? $"p{_nr}@x.de",
                PrimaereEmail = email ?? $"p{_nr}@x.de",
                Geburtstag = ohneGeburtstag ? null : geburtstag ?? new DateTime(1990, 1, 1),
                Strasse = strasse, Plz = plz, Stadt = stadt, Land = land,
                Eintrittsdatum = ohneEintritt ? null : eintritt ?? new DateTime(2024, 1, 10),
                Austrittsdatum = austritt, Kuendigungsdatum = kuendigung,
                Antragsdatum = antrag, Aufnahmedatum = aufnahme,
                Zahlungsart = zahlart, ZahlungsartText = zahlart == 1 ? "Lastschrift" : "Überweisung",
                Iban = iban, Bic = bic, KontoinhaberAbweichend = inhaber,
                Mandatsreferenz = ohneMandat ? null : mandat ?? $"M-{_nr}",
                Mandatsdatum = ohneMandatDatum ? null : mandatDatum ?? new DateTime(2024, 1, 11),
                Saldo = saldo,
                Leistungsbeginn = ohneLeistung ? null : new DateTime(2024, 1, 1),
                IndividuellerBeitrag = individuell, ZahlungsintervallMonate = intervall,
                GruppenKuerzel = gruppen ?? new List<string> { "VB03" }, GruppenNamen = new(),
                Ehrenmitglied = ehre, IstFirma = firma,
                SepaEinverstaendnis = sepaJa, NachweisDatei = nachweis,
            };
        }

        [Fact]
        public void JederFindingCode_HatKurztext()
        {
            var alle = new List<(MemberFinding F, MemberAuditResult R)>();
            void Sammle(MemberAuditResult r)
            {
                foreach (var f in r.Findings) alle.Add((f, r));
            }

            // F1: Totalausfall Stammdaten/Zahlart
            Sammle(MemberAuditService.Audit(Neu(
                vorname: null, nachname: null, strasse: null, plz: "XY",
                email: "kaputt", loginEmail: "x", ohneGeburtstag: true, ohneEintritt: true,
                gruppen: new(), zahlart: 0, sepaJa: null, iban: null, bic: null,
                mandat: null, ohneMandatDatum: true, ohneLeistung: true), Jahr, Heute));
            // F2: Klassen-/Beitrags-Chaos + fehlender Nachweis
            Sammle(MemberAuditService.Audit(Neu(
                gruppen: new() { "VB01", "VB03" }, firma: true, intervall: 7, individuell: -5m,
                geburtstag: new DateTime(2005, 5, 5), nachweis: null), Jahr, Heute));
            // F3: VB04 ohne Firma, kein SEPA
            Sammle(MemberAuditService.Audit(Neu(gruppen: new() { "VB04" }, zahlart: 2), Jahr, Heute));
            // F4: Lastschrift-Totalausfall + Kündigung + Rückstand
            Sammle(MemberAuditService.Audit(Neu(
                sepaJa: null, iban: null, bic: null, ohneMandat: true, ohneMandatDatum: true,
                inhaber: "Mama", kuendigung: new DateTime(2026, 12, 20), austritt: new DateTime(2026, 12, 31),
                antrag: new DateTime(2026, 2, 1), aufnahme: new DateTime(2026, 1, 1),
                saldo: 200m, nachweis: null), Jahr, Heute));
            // F5: Ausland + Zukunftsdaten + kleiner Rückstand
            Sammle(MemberAuditService.Audit(Neu(
                iban: "FR7630006000011234567890189", bic: null,
                mandatDatum: new DateTime(2030, 1, 1), geburtstag: new DateTime(2030, 1, 1),
                saldo: 10m), Jahr, Heute));
            // F6: Ehrenmitglied, ausgetreten, VB01 mit Nachweis über 25
            Sammle(MemberAuditService.Audit(Neu(
                ehre: true, gruppen: new() { "VB01" }, geburtstag: new DateTime(1980, 1, 1),
                austritt: new DateTime(2026, 3, 31), kuendigung: new DateTime(2026, 2, 1),
                bic: "XXX"), Jahr, Heute));

            // Duplikate (isolierte Liste)
            var d1 = MemberAuditService.Audit(Neu(
                email: "doppelt@x.de", mandat: "DUP-M", vorname: "Dora", nachname: "Dup",
                geburtstag: new DateTime(1995, 5, 5)), Jahr, Heute);
            var d2 = MemberAuditService.Audit(Neu(
                email: "doppelt@x.de", mandat: "DUP-M", vorname: "Dora", nachname: "Dup",
                geburtstag: new DateTime(1995, 5, 5)), Jahr, Heute);
            MemberAuditCommand.ErgänzeDuplikatFindings(new List<MemberAuditResult> { d1, d2 });
            Sammle(d1);
            Sammle(d2);

            // Technik-Lücke
            using var doc = JsonDocument.Parse(
                "{\"contactDetails\":\"http://test/v2.0/contact-details/1\",\"memberGroups\":[1]}");
            var leer = new MemberRecord { Id = 999, DisplayName = "?", GruppenKuerzel = new(), GruppenNamen = new() };
            var resTechnik = MemberAuditService.Audit(leer, Jahr, Heute);
            var records = new List<(JsonElement Json, MemberRecord Record)> { (doc.RootElement.Clone(), leer) };
            var results = new List<MemberAuditResult> { resTechnik };
            Assert.Equal(1, MemberAuditCommand.ErgänzeTechnikFindings(records, results));
            Sammle(resTechnik);

            var codes = alle.Select(x => x.F.Code).Distinct().ToList();
            var erwartet = new[]
            {
                "STATUS_AUSGETRETEN", "STAMM_NAME_FEHLT", "STAMM_ADRESSE_UNVOLLSTAENDIG",
                "STAMM_PLZ_FORMAT", "STAMM_GEBURTSTAG_FEHLT", "STAMM_GEBURTSTAG_ZUKUNFT",
                "STAMM_EMAIL_FEHLT", "STAMM_LOGIN_EMAIL", "STATUS_EINTRITT_FEHLT",
                "BEITRAG_KLASSE_FEHLT", "BEITRAG_MEHRERE_KLASSEN", "BEITRAG_FIRMA_KLASSE",
                "BEITRAG_VB04_OHNE_FIRMA", "BEITRAG_INTERVALL", "BEITRAG_NEGATIV",
                "NACHWEIS_FEHLT", "NACHWEIS_ALTER_VB01", "BEITRAG_EHRENMITGLIED",
                "SEPA_EINWILLIGUNG_FEHLT", "SEPA_IBAN_FEHLT", "SEPA_BIC_FEHLT_AUSLAND",
                "SEPA_BIC_FEHLT", "SEPA_BIC_FORMAT", "SEPA_MANDATSREF_FEHLT",
                "SEPA_MANDATSDATUM_FEHLT", "SEPA_MANDATSDATUM_ZUKUNFT", "ZAHLART_KEIN_SEPA",
                "SEPA_JA_OHNE_LASTSCHRIFT", "ZAHLART_FEHLT", "BANK_ABWEICHEND",
                "STATUS_KUENDIGUNG_FORM", "STATUS_GEKUENDIGT", "MAHN_STREICHKANDIDAT",
                "MAHN_RUECKSTAND", "MAHN_VORSCHLAG", "BEITRAG_LEISTUNGSBEGINN_FEHLT",
                "STATUS_DATUM_REIHENFOLGE", "STAMM_EMAIL_DUPLIKAT", "SEPA_MANDATSREF_DUPLIKAT",
                "BANK_IBAN_GETEILT", "STAMM_DOPPEL_EINTRAG", "API_DETAILS_UNVOLLSTAENDIG",
            };
            foreach (var code in erwartet)
                Assert.Contains(code, codes);

            foreach (var (f, r) in alle)
            {
                var kurz = MemberAuditCommand.KurzBefund(f, r);
                Assert.NotEqual(f.Code, kurz);
                Assert.DoesNotContain("_", kurz);
            }
        }
    }
}
