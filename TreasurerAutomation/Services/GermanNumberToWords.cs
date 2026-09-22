using System;
using System.Text;

namespace TreasurerAutomation.Services
{
    /// <summary>
    /// Wandelt Euro-Beträge in deutsche Zahlwörter für die Zuwendungsbestätigung
    /// (Betrag in Buchstaben, z.B. "eintausend Euro" oder
    /// "einhundertfünfzig Euro und fünfzig Cent"). Zahlwort klein, Währung groß.
    /// </summary>
    public static class GermanNumberToWords
    {
        private static readonly string[] Einer =
        {
            "null", "eins", "zwei", "drei", "vier", "fünf", "sechs", "sieben",
            "acht", "neun", "zehn", "elf", "zwölf", "dreizehn", "vierzehn",
            "fünfzehn", "sechzehn", "siebzehn", "achtzehn", "neunzehn"
        };

        private static readonly string[] Zehner =
        {
            "", "", "zwanzig", "dreißig", "vierzig", "fünfzig",
            "sechzig", "siebzig", "achtzig", "neunzig"
        };

        /// <summary>
        /// Formatiert einen positiven Euro-Betrag (max. 2 Nachkommastellen) als Zahlwort,
        /// z.B. 150.00m -&gt; "einhundertfünfzig Euro".
        /// </summary>
        public static string BetragInWorten(decimal betrag)
        {
            if (betrag <= 0)
                throw new ArgumentOutOfRangeException(nameof(betrag), "Betrag muss größer als 0 sein.");
            if (decimal.Round(betrag, 2) != betrag)
                throw new ArgumentException("Betrag darf maximal 2 Nachkommastellen haben.", nameof(betrag));

            var euro = (long)decimal.Truncate(betrag);
            // Exakt (kein Rundungsfehler): decimal rechnet basis-10, Eingabe hat max. 2 Stellen (s.o.).
            var cent = (int)((betrag - euro) * 100);

            var sb = new StringBuilder();
            sb.Append(euro == 1 ? "ein Euro" : Kardinalzahl(euro) + " Euro");
            if (cent > 0)
                sb.Append(cent == 1 ? " und ein Cent" : " und " + Kardinalzahl(cent) + " Cent");
            return sb.ToString();
        }

        /// <summary>
        /// Deutsche Kardinalzahl, z.B. 21 -&gt; "einundzwanzig", 1000 -&gt; "eintausend",
        /// 1000000 -&gt; "eine Million". Gültig für 0 bis 999 Milliarden.
        /// </summary>
        public static string Kardinalzahl(long zahl)
        {
            if (zahl < 0)
                throw new ArgumentOutOfRangeException(nameof(zahl), "Nur positive Zahlen werden unterstützt.");
            if (zahl < 20)
                return Einer[zahl];
            if (zahl < 100)
            {
                var einer = zahl % 10;
                var zehner = zahl / 10;
                if (einer == 0)
                    return Zehner[zehner];
                var einerWort = einer == 1 ? "ein" : Einer[einer];
                return einerWort + "und" + Zehner[zehner];
            }
            if (zahl < 1000)
            {
                var hunderter = zahl / 100;
                var rest = zahl % 100;
                var prefix = hunderter == 1 ? "einhundert" : Einer[hunderter] + "hundert";
                return rest == 0 ? prefix : prefix + Kardinalzahl(rest);
            }
            if (zahl < 1_000_000)
            {
                var tausender = zahl / 1000;
                var rest = zahl % 1000;
                var prefix = tausender == 1 ? "eintausend" : Kardinalzahl(tausender) + "tausend";
                return rest == 0 ? prefix : prefix + Kardinalzahl(rest);
            }
            if (zahl < 1_000_000_000)
            {
                var millionen = zahl / 1_000_000;
                var rest = zahl % 1_000_000;
                var prefix = millionen == 1 ? "eine Million" : Kardinalzahl(millionen) + " Millionen";
                return rest == 0 ? prefix : prefix + " " + Kardinalzahl(rest);
            }
            if (zahl < 1_000_000_000_000)
            {
                var milliarden = zahl / 1_000_000_000;
                var rest = zahl % 1_000_000_000;
                var prefix = milliarden == 1 ? "eine Milliarde" : Kardinalzahl(milliarden) + " Milliarden";
                return rest == 0 ? prefix : prefix + " " + Kardinalzahl(rest);
            }
            throw new ArgumentOutOfRangeException(nameof(zahl), "Zahl zu groß (max. 999 Milliarden).");
        }
    }
}
