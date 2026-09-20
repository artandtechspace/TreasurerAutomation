using TreasurerAutomation.Commands;
using Xunit;

namespace TreasurerAutomation.Tests
{
    /// <summary>
    /// Tests für das deutsche Betrag-Parsing des Spendenquittungs-Wizards.
    /// </summary>
    public class ParseBetragTests
    {
        [Theory]
        [InlineData("150,00", 150.00)]
        [InlineData("1.000,00", 1000.00)]
        [InlineData("1.000,00 EUR", 1000.00)]
        [InlineData("250 €", 250.00)]
        [InlineData("1000.50", 1000.50)]
        [InlineData("1.000", 1000.00)]
        [InlineData("12.345", 12345.00)]
        [InlineData("1.234.567,89", 1234567.89)]
        [InlineData("0,50", 0.50)]
        public void ParseBetrag_AkzeptiertDeutscheUndNeutraleFormate(string eingabe, decimal erwartet)
        {
            Assert.Equal(erwartet, SpendenquittungCommand.ParseBetrag(eingabe));
        }

        [Theory]
        [InlineData("")]
        [InlineData("abc")]
        [InlineData("12,345")]
        [InlineData("1.00.00")]
        public void ParseBetrag_LehntUngueltigeEingabenAb(string eingabe)
        {
            Assert.Null(SpendenquittungCommand.ParseBetrag(eingabe));
        }
    }
}
