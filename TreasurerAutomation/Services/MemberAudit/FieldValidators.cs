using System.Text.RegularExpressions;

namespace TreasurerAutomation.Services.MemberAudit
{
    /// <summary>
    /// Kleine, bewusste Validatoren ohne externe Pakete (IBAN Mod97, E-Mail grob, PLZ).
    /// </summary>
    public static class FieldValidators
    {
        private static readonly Regex EmailRegex =
            new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex PlzRegex =
            new(@"^\d{5}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex BicRegex =
            new(@"^[A-Za-z]{6}[A-Za-z0-9]{2}([A-Za-z0-9]{3})?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public static bool IstGueltigeEmail(string? mail)
        {
            if (string.IsNullOrWhiteSpace(mail)) return false;
            var m = mail.Trim();
            if (m.Length > 254 || m.Contains(' ')) return false;
            return EmailRegex.IsMatch(m);
        }

        public static bool IstGueltigeDePlz(string? plz)
        {
            if (string.IsNullOrWhiteSpace(plz)) return false;
            return PlzRegex.IsMatch(plz.Trim());
        }

        /// <summary>
        /// IBAN-Prüfziffer nach ISO 13616 (Mod 97 == 1). Leerzeichen werden ignoriert.
        /// </summary>
        public static bool IstGueltigeIban(string? iban)
        {
            if (string.IsNullOrWhiteSpace(iban)) return false;
            var s = new string(iban.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
            if (s.Length < 15 || s.Length > 34) return false;
            if (!char.IsLetter(s[0]) || !char.IsLetter(s[1])) return false;
            var umgestellt = s.Substring(4) + s.Substring(0, 4);
            var zahl = new System.Text.StringBuilder(umgestellt.Length * 2);
            foreach (var c in umgestellt)
            {
                if (char.IsDigit(c)) zahl.Append(c);
                else zahl.Append(c - 'A' + 10);
            }
            // Mod97 iterativ (ohne BigInteger-Overhead für lange IBANs)
            var rest = 0;
            foreach (var c in zahl.ToString())
                rest = (rest * 10 + (c - '0')) % 97;
            return rest == 1;
        }

        /// <summary>
        /// Inland = DE-IBAN, alles andere = Ausland (BIC-pflichtig).
        /// Bewusst nur Präfix-Heuristik: Für den SEPA-Einzug zählt das IBAN-Land,
        /// nicht die Wohnadresse (z.B. DE-IBAN eines Franzosen braucht keine BIC).
        /// </summary>
        public static bool IstAuslandsIban(string? iban)
        {
            if (string.IsNullOrWhiteSpace(iban)) return false;
            var s = iban.Trim().ToUpperInvariant();
            if (s.Length < 2) return false;
            return !s.StartsWith("DE", StringComparison.Ordinal);
        }

        public static bool IstGueltigeBic(string? bic)
        {
            if (string.IsNullOrWhiteSpace(bic)) return false;
            return BicRegex.IsMatch(bic.Trim());
        }

        /// <summary>
        /// Gläubiger-Identifikationsnummer nach ISO 13616 (wie IBAN: Mod 97 == 1).
        /// Leerzeichen werden ignoriert. DE-IDs sind 18-stellig (DE + 2 Prüfziffern
        /// + 3-stelliger Geschäftsbereichscode + 11-stellige nationale Kennung).
        /// Eine falsche ID führt zur Ablehnung aller Lastschriften – daher hart prüfen.
        /// </summary>
        public static bool IstGueltigeGlaeubigerId(string? id)
        {
            if (string.IsNullOrWhiteSpace(id)) return false;
            var s = new string(id.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
            if (s.Length is < 8 or > 35) return false;
            if (!char.IsLetter(s[0]) || !char.IsLetter(s[1])) return false;
            if (!char.IsDigit(s[2]) || !char.IsDigit(s[3])) return false;
            return Mod97Eins(s);
        }

        /// <summary>ISO-13616-Prüfziffer (Stellen 5+ nach vorne, Buchstaben A=10..Z=35).</summary>
        internal static bool Mod97Eins(string s)
        {
            var umgestellt = s.Substring(4) + s.Substring(0, 4);
            var rest = 0;
            foreach (var c in umgestellt)
            {
                var wert = char.IsDigit(c) ? c - '0' : c - 'A' + 10;
                if (char.IsDigit(c))
                    rest = (rest * 10 + wert) % 97;
                else
                    rest = (rest * 100 + wert) % 97;
            }
            return rest == 1;
        }

        /// <summary>IBAN-Normalform für Vergleiche/Anzeige (nur Zeichen, groß).</summary>
        public static string NormalisiereIban(string? iban) =>
            string.IsNullOrWhiteSpace(iban)
                ? ""
                : new string(iban.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
    }
}
