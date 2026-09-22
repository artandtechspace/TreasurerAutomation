using System.Text.Json;
using TreasurerAutomation.Services.MemberAudit;
using Xunit;

namespace TreasurerAutomation.Tests
{
    /// <summary>
    /// Parser muss Alias-Felder (_paymentStartDate, _isCompany, sepaMandate, ...)
    /// und verschiedene Gruppen/Customfield-Formen akzeptieren.
    /// </summary>
    public class EasyVereinMemberParserTests
    {
        private static JsonElement Json(string s) =>
            JsonDocument.Parse(s).RootElement.Clone();

        [Fact]
        public void ParstMitgliedMitEingebettetenKontaktdaten()
        {
            var member = Json("""
                {"id": 7, "membershipNumber": "7",
                 "joinDate": "2024-01-10",
                 "_paymentStartDate": "2024-01-01",
                 "contactDetails": {
                   "firstName": "Ava", "familyName": "Muster",
                   "primaryEmail": "ava@muster.de",
                   "street": "Weg 1", "zip": "48431", "city": "Rheine",
                   "dateOfBirth": "2000-05-01",
                   "methodOfPayment": 1,
                   "iban": "DE89 3704 0044 0532 0130 00",
                   "bic": "COBADEFFXXX",
                   "sepaMandate": "M-7",
                   "sepaDate": "2024-01-11"
                 },
                 "memberGroups": [{"short": "VB03", "name": "Regulär"}]}
                """);

            var cd = EasyVereinMemberParser.GetObject(member, "contactDetails");
            var (k, n) = EasyVereinMemberParser.ParseGruppen(member);
            var rec = EasyVereinMemberParser.Parse(member, cd, k, n,
                new Dictionary<string, string?> { ["Ja, hiermit erteile ich mein Einverständnis zum SEPA-Lastschriftverfahren!*  "] = "Ja" });

            Assert.Equal("Ava", rec.Vorname);
            Assert.Equal("Muster", rec.Nachname);
            Assert.Equal(1, rec.Zahlungsart);
            Assert.Equal("M-7", rec.Mandatsreferenz);
            Assert.Contains("VB03", rec.GruppenKuerzel);
            Assert.True(rec.SepaEinverstaendnis);
            Assert.Equal(new DateTime(2000, 5, 1), rec.Geburtstag);
        }

        [Fact]
        public void ParstDeutschesDatumsformatUndSepaVarianten()
        {
            var member = Json("""{"id": 8, "membershipNumber": "8", "joinDate": "10.01.2024"}""");
            var cd = Json("""{"methodOfPayment": 2, "dateOfBirth": "01.05.2000"}""");
            var rec = EasyVereinMemberParser.Parse(member, cd, new[] { "VB01" }, Array.Empty<string>(),
                new Dictionary<string, string?> { ["Nachweis für ermäßigten Mitgliedsbeitrag"] = "nachweis.pdf", ["Freiwilliger E-Mail-Newsletter"] = "Nein" });
            Assert.Equal(new DateTime(2024, 1, 10), rec.Eintrittsdatum);
            Assert.Equal(new DateTime(2000, 5, 1), rec.Geburtstag);
            Assert.Equal("nachweis.pdf", rec.NachweisDatei);
            Assert.False(rec.Newsletter);
        }

        [Fact]
        public void ParstGruppenAlsStringsUndIds()
        {
            var m1 = Json("""{"id": 1, "memberGroups": ["VB01", "VBF"]}""");
            var (k1, _) = EasyVereinMemberParser.ParseGruppen(m1);
            Assert.Contains("VB01", k1);
            Assert.Contains("VBF", k1);

            var m2 = Json("""{"id": 2, "memberGroups": [{"memberGroup": {"short": "VB04", "name": "Juristisch"}}]}""");
            var (k2, n2) = EasyVereinMemberParser.ParseGruppen(m2);
            Assert.Contains("VB04", k2);
            Assert.Contains("Juristisch", n2);
        }

        [Fact]
        public void FehlendeOptionaleFelder_ErgebenKeineException()
        {
            var member = Json("""{"id": 9}""");
            var rec = EasyVereinMemberParser.Parse(member, null, Array.Empty<string>(), Array.Empty<string>(),
                new Dictionary<string, string?>());
            Assert.Equal(9, rec.Id);
            Assert.Null(rec.PrimaereEmail);
            Assert.Null(rec.SepaEinverstaendnis);
        }
    }
}
