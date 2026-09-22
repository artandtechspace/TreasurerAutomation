using System.Numerics;
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
            return Regex.IsMatch(plz.Trim(), @"^\d{5}$");
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

        public static bool IstAuslandsIban(string? iban, string? land = null)
        {
            if (string.IsNullOrWhiteSpace(iban)) return false;
            var s = iban.Trim().ToUpperInvariant();
            if (s.Length < 2) return false;
            // DE-IBAN gilt als Inland; alles andere als Ausland (vereinfachte Heuristik für BIC-Pflicht)
            if (s.StartsWith("DE")) return false;
            if (!string.IsNullOrWhiteSpace(land))
            {
                var l = land.Trim().ToLowerInvariant();
                if (l is "deutschland" or "de" or "germany") return s.StartsWith("DE") ? false : false;
            }
            return true;
        }

        public static bool IstGueltigeBic(string? bic)
        {
            if (string.IsNullOrWhiteSpace(bic)) return false;
            return Regex.IsMatch(bic.Trim(), @"^[A-Za-z]{6}[A-Za-z0-9]{2}([A-Za-z0-9]{3})?$");
        }
    }
}
