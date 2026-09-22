using System.Linq;
using TreasurerAutomation.Services;
using Xunit;

namespace TreasurerAutomation.Tests
{
    /// <summary>
    /// Tests für Slug, Typst-Escaping und .typ-Erzeugung der Zuwendungsbestätigung.
    /// </summary>
    public class SpendenquittungFileBuilderTests
    {
        [Theory]
        [InlineData("Stadtwerke Rheine GmbH", "stadtwerke-rheine-gmbh")]
        [InlineData("Max Mustermann", "max-mustermann")]
        [InlineData("Jürgen Müller-Böhm", "juergen-mueller-boehm")]
        [InlineData("  ", "spender")]
        public void Slug_NormalisiertNamen(string name, string erwartet)
        {
            Assert.Equal(erwartet, SpendenquittungFileBuilder.Slug(name));
        }

        [Fact]
        public void EscapeTypst_MaskiertAnfuehrungszeichenUndBackslash()
        {
            Assert.Equal("Müller \\\"GmbH\\\" \\\\ Co", SpendenquittungFileBuilder.EscapeTypst("Müller \"GmbH\" \\ Co"));
        }

        [Fact]
        public void EscapeTypst_MaskiertSteuerzeichen()
        {
            Assert.Equal("Zeile1\\nZeile2\\r\\nTab\\tEnde", SpendenquittungFileBuilder.EscapeTypst("Zeile1\nZeile2\r\nTab\tEnde"));
        }

        private static SpendenquittungDaten MusterDaten() => new(
            "Stadtwerke Rheine GmbH", "Hafenbahn 10", "48431 Rheine",
            "1.000,00 EUR", "eintausend Euro", "14.09.2026",
            false, false,
            "Rheine", "20.09.2026",
            "Luca Schöneberg", "Kassenwart",
            "Jascha Wallmeier", "Vorstandsvorsitzender",
            "2026-002", null);

        [Fact]
        public void Build_EnthaeltAllePflichtfelderUndImport()
        {
            var typ = SpendenquittungFileBuilder.Build(MusterDaten());

            Assert.Contains("#import \"vorlage.typ\": zuwendungsbestaetigung", typ);
            Assert.Contains("spender-name: \"Stadtwerke Rheine GmbH\"", typ);
            Assert.Contains("spender-strasse: \"Hafenbahn 10\"", typ);
            Assert.Contains("spender-plz-ort: \"48431 Rheine\"", typ);
            Assert.Contains("betrag-ziffern: \"1.000,00 EUR\"", typ);
            Assert.Contains("betrag-buchstaben: \"eintausend Euro\"", typ);
            Assert.Contains("tag-zuwendung: \"14.09.2026\"", typ);
            Assert.Contains("beleg-nr: \"2026-002\"", typ);
            Assert.Contains("unterzeichner2-name: \"Jascha Wallmeier\"", typ);
        }

        [Fact]
        public void Build_MaskiertSonderzeichenInStrings()
        {
            var daten = MusterDaten() with { SpenderName = "Müller \"Spezial\" GmbH" };
            var typ = SpendenquittungFileBuilder.Build(daten);

            Assert.Contains("spender-name: \"Müller \\\"Spezial\\\" GmbH\"", typ);
        }

        [Fact]
        public void Build_ProjektvermerkNurAlsKommentar()
        {
            var daten = MusterDaten() with { Projektvermerk = "Jugend forscht" };
            var typ = SpendenquittungFileBuilder.Build(daten);

            var zeilenMitVermerk = typ.Split('\n').Where(z => z.Contains("Jugend forscht")).ToList();
            Assert.NotEmpty(zeilenMitVermerk);
            Assert.All(zeilenMitVermerk, z => Assert.StartsWith("//", z.TrimStart()));
        }

        [Fact]
        public void Build_VerwendetAngepasstenImportPfad()
        {
            var typ = SpendenquittungFileBuilder.Build(MusterDaten(), "../vorlage.typ");

            Assert.Contains("#import \"../vorlage.typ\": zuwendungsbestaetigung", typ);
        }
    }
}
