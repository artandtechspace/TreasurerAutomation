using TreasurerAutomation.Services.MemberAudit;
using Xunit;

namespace TreasurerAutomation.Tests
{
    public class FieldValidatorsTests
    {
        [Theory]
        [InlineData("luca.schoeneberg@artandtech.space", true)]
        [InlineData("a@b.de", true)]
        [InlineData("", false)]
        [InlineData(null, false)]
        [InlineData("keine-mail", false)]
        [InlineData("a@b", false)]
        [InlineData("a @b.de", false)]
        public void Email(string? mail, bool erwartet)
        {
            Assert.Equal(erwartet, FieldValidators.IstGueltigeEmail(mail));
        }

        [Theory]
        // Echte, öffentlich dokumentierte Beispiel-IBANs (keine Echtdaten)
        [InlineData("DE75 4035 0005 0000 0632 97", true)]  // Vereins-IBAN aus Beitragsordnung §8
        [InlineData("DE89 3704 0044 0532 0130 00", true)]
        [InlineData("DE00 0000 0000 0000 0000 00", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        [InlineData("DE12", false)]
        public void Iban(string? iban, bool erwartet)
        {
            Assert.Equal(erwartet, FieldValidators.IstGueltigeIban(iban));
        }

        [Theory]
        [InlineData("WELADED1RHN", true)]
        [InlineData("GENODEM1IBB", true)]
        [InlineData("PBNKDEFFXXX", true)]
        [InlineData("XXX", false)]
        [InlineData("", false)]
        public void Bic(string? bic, bool erwartet)
        {
            Assert.Equal(erwartet, FieldValidators.IstGueltigeBic(bic));
        }

        [Theory]
        [InlineData("48431", true)]
        [InlineData("4843", false)]
        [InlineData("ABCDE", false)]
        public void Plz(string? plz, bool erwartet)
        {
            Assert.Equal(erwartet, FieldValidators.IstGueltigeDePlz(plz));
        }

        [Theory]
        // DE65… ist eine synthetische Fixture (gleicher Rumpf, korrekte Prüfziffern) – keine Echtdaten
        [InlineData("DE65 ZZZ 0000 2513 771", true)]
        [InlineData("DE89 ZZZ 0000 2513 771", false)]  // falsche Prüfziffern (mod97=25)
        [InlineData("DE00ZZZ00000000000", false)]      // Platzhalter
        [InlineData("", false)]
        [InlineData(null, false)]
        [InlineData("DE65", false)]
        public void GlaeubigerId(string? id, bool erwartet)
        {
            Assert.Equal(erwartet, FieldValidators.IstGueltigeGlaeubigerId(id));
        }
    }
}
