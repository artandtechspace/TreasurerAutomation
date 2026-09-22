using TreasurerAutomation.Services;
using Xunit;

namespace TreasurerAutomation.Tests
{
    public sealed class SepaMandatFileBuilderTests
    {
        private static SepaMandatDaten Muster(string zahler = "Max Mustermann") => new(
            "ARTandTECH.space e.V.", "Lindenstraße 11", "48431 Rheine", "Deutschland",
            "DE00ZZZ00000000000", "EV-1-2026", true,
            zahler, "Musterstraße 12", "48431 Rheine", "Deutschland",
            "BICCODE12", "DE75512108001245126199", "Rheine, 22.09.2026",
            "Mitgliedsnummer 1");

        [Fact]
        public void DateiName_DatumVorne_SlugHinten()
        {
            var name = SepaMandatFileBuilder.DateiName(new DateTime(2026, 9, 22), "Luca Schöneberg", "42");
            Assert.Equal("20260922_luca-schoeneberg-42_sepa-mandat", name);
        }

        [Fact]
        public void DateiName_OhneNummer()
        {
            var name = SepaMandatFileBuilder.DateiName(new DateTime(2026, 9, 22), "Max Mustermann");
            Assert.Equal("20260922_max-mustermann_sepa-mandat", name);
        }

        [Fact]
        public void Build_EnthaeltAllePflichtfelderUndImport()
        {
            var typ = SepaMandatFileBuilder.Build(Muster());
            Assert.Contains("#import \"vorlage.typ\": sepa_mandat", typ);
            Assert.Contains("glaeubiger-id: \"DE00ZZZ00000000000\"", typ);
            Assert.Contains("mandatsreferenz: \"EV-1-2026\"", typ);
            Assert.Contains("wiederkehrend: true,", typ);
            Assert.Contains("iban: \"DE75512108001245126199\"", typ);
            Assert.Contains("kreditinstitut: \"BICCODE12\"", typ);
            Assert.Contains("empfaenger-strasse: \"Lindenstraße 11\"", typ);
        }

        [Fact]
        public void Build_EscapedAnfuehrungszeichen()
        {
            var typ = SepaMandatFileBuilder.Build(Muster("Max \"Maxi\" Mustermann"));
            Assert.Contains("Max \\\"Maxi\\\" Mustermann", typ);
        }
    }
}
