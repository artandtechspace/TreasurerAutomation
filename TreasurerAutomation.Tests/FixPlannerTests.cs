using System.Collections.Generic;
using TreasurerAutomation.Services.MemberAudit;
using Xunit;

namespace TreasurerAutomation.Tests
{
    /// <summary>
    /// FixPlanner leitet nur sichere Auto-Fixes ab (kein Consent erfinden).
    /// </summary>
    public class FixPlannerTests
    {
        [Fact]
        public void Plane_EmailNormUndMandatsref()
        {
            var m = new MemberRecord
            {
                Id = 5,
                MembershipNumber = "5",
                DisplayName = "Max Muster",
                Vorname = "Max",
                Nachname = "Muster",
                PrimaereEmail = "max@x.de",
                LoginEmail = "max@x.de",
                Geburtstag = new DateTime(1990, 1, 1),
                Strasse = "Weg 1",
                Plz = "48431",
                Stadt = "Rheine",
                Eintrittsdatum = new DateTime(2024, 1, 1),
                Zahlungsart = 1,
                ZahlungsartText = "Lastschrift",
                Iban = "DE89370400440532013000",
                Bic = "COBADEFFXXX",
                Mandatsdatum = new DateTime(2024, 1, 1),
                GruppenKuerzel = new List<string> { "VB03" },
                GruppenNamen = new(),
                SepaEinverstaendnis = true,
                ZahlungsintervallMonate = 12,
                Leistungsbeginn = new DateTime(2024, 1, 1),
                PrivateEmail = "  MAX@X.DE ",
            };
            var r = MemberAuditService.Audit(m, 2026, new DateTime(2026, 9, 22));

            var plaene = FixPlanner.Plane(new[] { r }, 2026);

            Assert.Contains(plaene, p => p.Aktion == "EMAIL_NORM" && p.Neu == "max@x.de" && p.Auto);
            Assert.Contains(plaene, p => p.Aktion == "MANDATSREF_NEU" && p.Neu == "EV-5-2026" && p.Auto);
        }

        [Fact]
        public void Plane_FordertEinwilligungAnStattSieZuSetzen()
        {
            var m = new MemberRecord
            {
                Id = 6,
                MembershipNumber = "6",
                DisplayName = "No Consent",
                Vorname = "No",
                Nachname = "Consent",
                PrimaereEmail = "nc@x.de",
                LoginEmail = "nc@x.de",
                Geburtstag = new DateTime(1990, 1, 1),
                Strasse = "Weg 1",
                Plz = "48431",
                Stadt = "Rheine",
                Eintrittsdatum = new DateTime(2024, 1, 1),
                Zahlungsart = 1,
                ZahlungsartText = "Lastschrift",
                Iban = "DE89370400440532013000",
                GruppenKuerzel = new List<string> { "VB03" },
                GruppenNamen = new(),
                SepaEinverstaendnis = false,
                ZahlungsintervallMonate = 12,
                Leistungsbeginn = new DateTime(2024, 1, 1),
            };
            var r = MemberAuditService.Audit(m, 2026, new DateTime(2026, 9, 22));

            var plaene = FixPlanner.Plane(new[] { r }, 2026);

            Assert.Contains(plaene, p => p.Aktion == "SEPA_EINWILLIGUNG_ANFORDERN" && !p.Auto);
            Assert.DoesNotContain(plaene, p => p.Feld == "SEPA-Einwilligung" && p.Auto);
        }
    }
}
