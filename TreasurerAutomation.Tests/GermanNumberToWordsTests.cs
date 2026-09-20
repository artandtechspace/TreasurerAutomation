using System;
using TreasurerAutomation.Services;
using Xunit;

namespace TreasurerAutomation.Tests
{
    /// <summary>
    /// Tests für deutsche Zahlwörter (Betrag in Buchstaben der Zuwendungsbestätigung).
    /// </summary>
    public class GermanNumberToWordsTests
    {
        [Theory]
        [InlineData(1.00, "ein Euro")]
        [InlineData(1.01, "ein Euro und ein Cent")]
        [InlineData(12.00, "zwölf Euro")]
        [InlineData(16.00, "sechzehn Euro")]
        [InlineData(17.00, "siebzehn Euro")]
        [InlineData(21.00, "einundzwanzig Euro")]
        [InlineData(30.00, "dreißig Euro")]
        [InlineData(60.00, "sechzig Euro")]
        [InlineData(70.00, "siebzig Euro")]
        [InlineData(100.00, "einhundert Euro")]
        [InlineData(101.00, "einhunderteins Euro")]
        [InlineData(150.00, "einhundertfünfzig Euro")]
        [InlineData(1000.00, "eintausend Euro")]
        [InlineData(1000.50, "eintausend Euro und fünfzig Cent")]
        [InlineData(2001.00, "zweitausendeins Euro")]
        [InlineData(1234567.89, "eine Million zweihundertvierunddreißigtausendfünfhundertsiebenundsechzig Euro und neunundachtzig Cent")]
        [InlineData(2000000.00, "zwei Millionen Euro")]
        [InlineData(1000000000.00, "eine Milliarde Euro")]
        [InlineData(0.50, "null Euro und fünfzig Cent")]
        public void BetragInWorten_FormatiertKorrekt(decimal betrag, string erwartet)
        {
            Assert.Equal(erwartet, GermanNumberToWords.BetragInWorten(betrag));
        }

        [Theory]
        [InlineData(0.00)]
        [InlineData(-5.00)]
        public void BetragInWorten_LehntUngueltigeBetraegeAb(decimal betrag)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => GermanNumberToWords.BetragInWorten(betrag));
        }

        [Fact]
        public void BetragInWorten_LehntDreiNachkommastellenAb()
        {
            Assert.Throws<ArgumentException>(() => GermanNumberToWords.BetragInWorten(1.005m));
        }

        [Theory]
        [InlineData(0, "null")]
        [InlineData(1, "eins")]
        [InlineData(1000, "eintausend")]
        [InlineData(1000000, "eine Million")]
        public void Kardinalzahl_FormatiertKorrekt(long zahl, string erwartet)
        {
            Assert.Equal(erwartet, GermanNumberToWords.Kardinalzahl(zahl));
        }
    }
}
