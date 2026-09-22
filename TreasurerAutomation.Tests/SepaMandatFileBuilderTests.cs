using TreasurerAutomation.Services;
using Xunit;

namespace TreasurerAutomation.Tests
{
    public sealed class SepaMandatFileBuilderTests
    {
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
        public void Build_EnthaeltAlleFelderUndImport()
        {
            var daten = new SepaMandatDaten(
                "ARTandTECH.space e.V.", "Rheine", "DE00ZZZ00000000000", "EV-1-2026",
                "Max Mustermann", "Musterstraße 12", "48431 Rheine",
                "DE75512108001245126199", "BICCODE12", "Rheine, 22.09.2026", null);
            var typ = SepaMandatFileBuilder.Build(daten);
            Assert.Contains("#import \"vorlage.typ\": sepa_mandat", typ);
            Assert.Contains("mandatsreferenz: \"EV-1-2026\"", typ);
            Assert.Contains("iban: \"DE75512108001245126199\"", typ);
        }

        [Fact]
        public void Build_EscapedAnfuehrungszeichen()
        {
            var daten = new SepaMandatDaten(
                "Verein", "Rheine", "ID", "EV-1-2026",
                "Max \"Maxi\" Mustermann", "Str. 1", "48431 Rheine",
                "DE75512108001245126199", "", "Rheine, 22.09.2026", null);
            var typ = SepaMandatFileBuilder.Build(daten);
            Assert.Contains("Max \\\"Maxi\\\" Mustermann", typ);
        }
    }
}
