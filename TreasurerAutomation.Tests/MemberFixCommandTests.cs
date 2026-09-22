using System.Text.Json;
using TreasurerAutomation.Commands;
using TreasurerAutomation.Services.MemberAudit;
using Xunit;

namespace TreasurerAutomation.Tests
{
    /// <summary>
    /// Regression: member-fix ist mit "Operation is not valid due to the current
    /// state of the object" abgestürzt, weil ErgänzeTechnikFindings mit
    /// default(JsonElement) statt echtem Member-JSON aufgerufen wurde.
    /// </summary>
    public sealed class MemberFixCommandTests
    {
        private static JsonElement Json(string raw)
        {
            var doc = JsonDocument.Parse(raw);
            return doc.RootElement.Clone();
        }

        private static MemberAuditResult Ergebnis(MemberRecord m, params MemberFinding[] findings) =>
            new() { Mitglied = m, Findings = findings.ToList() };

        [Fact]
        public void TechnikFindings_MitEchtemJson_StattDefault_KeinAbsturz()
        {
            var memberJson = Json("""{"id":7,"memberGroups":[{"memberGroup":5}],"contactDetails":"https://easyverein.com/api/v2.0/contact-details/99"}""");
            var leer = new MemberRecord { Id = 7, DisplayName = "Leer" };
            var res = Ergebnis(leer);
            var paare = new List<(JsonElement Json, MemberRecord Record)> { (memberJson, leer) };

            var betroffen = MemberAuditCommand.ErgänzeTechnikFindings(paare, new() { res });

            Assert.Equal(1, betroffen);
            Assert.Contains(res.Findings, f => f.Code == "API_DETAILS_UNVOLLSTAENDIG");
        }

        [Fact]
        public void TechnikFindings_OhneReferenzen_KeineTechnikWarnung()
        {
            var memberJson = Json("""{"id":8}""");
            var leer = new MemberRecord { Id = 8, DisplayName = "Leer" };
            var res = Ergebnis(leer);
            var paare = new List<(JsonElement Json, MemberRecord Record)> { (memberJson, leer) };

            var betroffen = MemberAuditCommand.ErgänzeTechnikFindings(paare, new() { res });

            Assert.Equal(0, betroffen);
            Assert.DoesNotContain(res.Findings, f => f.Code == "API_DETAILS_UNVOLLSTAENDIG");
        }

        [Fact]
        public void KontaktIdAus_UrlReferenz()
        {
            var member = Json("""{"id":1,"contactDetails":"https://easyverein.com/api/v2.0/contact-details/36271917"}""");
            Assert.Equal("36271917", MemberFixCommand.KontaktIdAus(member, null));
        }

        [Fact]
        public void MandatKandidaten_FiltertEhrenUndSaubere()
        {
            var blockiert = Ergebnis(
                new MemberRecord { Id = 1, DisplayName = "A" },
                new MemberFinding("SEPA_MANDATSREF_FEHLT", "Bank", FindingSeverity.Blocker, "fehlt"));
            var ehre = Ergebnis(
                new MemberRecord { Id = 2, DisplayName = "B", Ehrenmitglied = true },
                new MemberFinding("SEPA_MANDATSREF_FEHLT", "Bank", FindingSeverity.Blocker, "fehlt"));
            var sauber = Ergebnis(new MemberRecord { Id = 3, DisplayName = "C" });

            var kandidaten = MemberFixCommand.MandatKandidaten(new() { blockiert, ehre, sauber });

            Assert.Single(kandidaten);
            Assert.Equal(1, kandidaten[0].Mitglied.Id);
        }
    }
}
