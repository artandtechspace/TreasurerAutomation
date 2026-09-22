using TreasurerAutomation.Services.MemberAudit;
using Xunit;

namespace TreasurerAutomation.Tests
{
    /// <summary>
    /// Beitragsordnung §3 + §7 Abs. 6: Mindestbeiträge, 50%-Regel, Ehrenmitglieder.
    /// </summary>
    public class BeitragsordnungTests
    {
        [Theory]
        [InlineData("VB01", "24")]
        [InlineData("VB02", "80")]
        [InlineData("VB02.1", "30")]
        [InlineData("VB2M", "30")]
        [InlineData("VB03", "60")]
        [InlineData("VB04", "100")]
        [InlineData("VBF", null)]
        [InlineData("VV", null)]
        public void KlassenBetrag_MapptKuerzel(string kuerzel, string? erwartet)
        {
            decimal? erwartetDez = erwartet == null ? null : decimal.Parse(erwartet, System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal(erwartetDez, Beitragsordnung.KlassenBetrag(kuerzel));
        }

        [Fact]
        public void SollBeitrag_Volljahr()
        {
            var soll = Beitragsordnung.SollBeitrag(new[] { "VB03" }, new DateTime(2024, 1, 15), 2026);
            Assert.Equal(60m, soll);
        }

        [Fact]
        public void SollBeitrag_NachJuni_HalbiertNurImEintrittsjahr()
        {
            // Eintritt nach 30.06. im Beitragsjahr => 50%
            Assert.Equal(30m, Beitragsordnung.SollBeitrag(new[] { "VB03" }, new DateTime(2026, 7, 7), 2026));
            // Gleicher Eintritt, aber späteres Beitragsjahr => voll
            Assert.Equal(60m, Beitragsordnung.SollBeitrag(new[] { "VB03" }, new DateTime(2026, 7, 7), 2027));
            // Eintritt 30.06. selbst => noch voll
            Assert.Equal(60m, Beitragsordnung.SollBeitrag(new[] { "VB03" }, new DateTime(2026, 6, 30), 2026));
            // Eintritt 01.07. => halb
            Assert.Equal(12m, Beitragsordnung.SollBeitrag(new[] { "VB01" }, new DateTime(2026, 7, 1), 2026));
        }

        [Fact]
        public void SollBeitrag_EhrenmitgliedIstNull()
        {
            Assert.Equal(0m, Beitragsordnung.SollBeitrag(new[] { "VB03" }, new DateTime(2020, 1, 1), 2026, ehrenmitglied: true));
        }

        [Fact]
        public void SollBeitrag_FreiwilligerZusatzWirdAddiert()
        {
            Assert.Equal(84m, Beitragsordnung.SollBeitrag(new[] { "VB03" }, new DateTime(2020, 1, 1), 2026, freiwilligerZusatz: 24m));
        }

        [Fact]
        public void SollBeitrag_MehrereKlassenNimmtMax()
        {
            Assert.Equal(60m, Beitragsordnung.SollBeitrag(new[] { "VB01", "VB03" }, new DateTime(2020, 1, 1), 2026));
        }
    }
}
