namespace TreasurerAutomation.Services.MemberAudit
{
    /// <summary>
    /// Beitragsordnung ARTandTECH.space e.V. vom 25.04.2024, §3 + §7 Abs. 6.
    /// Mindestbeiträge pro Jahr. Ehrenmitglieder (§4 Abs. 3 Satzung) sind befreit.
    /// </summary>
    public static class Beitragsordnung
    {
        public const decimal Klasse01 = 24m;   // Schüler/Stud/Azubi/Freiwilligendienst bis 25, Münsterland-/Jugendleiter-/Ehrenamtskarte
        public const decimal Klasse02 = 80m;   // Familien
        public const decimal Klasse02_1 = 30m; // Familien mit Münsterlandkarte
        public const decimal Klasse03 = 60m;   // regulär
        public const decimal Klasse04 = 100m;  // juristische Personen

        /// <summary>
        /// Mappt Gruppenkürzel (VB01, VB02, VB02.1, VB03, VB04) auf Beitragshöhe.
        /// VBF (freiwilliger Zusatz) und VV (Vorstand) sind keine Beitragsklassen.
        /// Gibt null zurück, wenn kein Kürzel passt.
        /// </summary>
        public static decimal? KlassenBetrag(string? kuerzel)
        {
            if (string.IsNullOrWhiteSpace(kuerzel)) return null;
            var k = kuerzel.Trim().ToUpperInvariant();
            return k switch
            {
                "VB01" or "01" => Klasse01,
                "VB02" or "02" => Klasse02,
                // Familien mit Münsterlandkarte: live "VB2M", Export "VB02.1"
                "VB02.1" or "VB021" or "VB2M" or "02.1" => Klasse02_1,
                "VB03" or "03" => Klasse03,
                "VB04" or "04" => Klasse04,
                _ => null,
            };
        }

        public static bool IstBeitragsklasse(string? kuerzel) => KlassenBetrag(kuerzel).HasValue;

        /// <summary>
        /// Ermittelt den Soll-Jahresbeitrag für ein Beitragsjahr.
        /// §7 Abs. 6: Eintritt nach dem 30.06. des Beitragsjahres => 50%.
        /// Ehrenmitglieder zahlen 0. Freiwilliger Zusatz wird addiert.
        /// Bei mehreren Beitragsklassen wird die höchste genommen (und separat als Warnung gemeldet).
        /// </summary>
        public static decimal SollBeitrag(
            IEnumerable<string> gruppenKuerzel,
            DateTime? eintrittsdatum,
            int beitragsjahr,
            bool ehrenmitglied = false,
            decimal freiwilligerZusatz = 0m)
        {
            if (ehrenmitglied) return 0m;

            var basis = gruppenKuerzel
                .Select(KlassenBetrag)
                .Where(b => b.HasValue)
                .Select(b => b!.Value)
                .DefaultIfEmpty(0m)
                .Max();

            if (basis == 0m) return freiwilligerZusatz;

            // Halbjahresregel nur im Eintrittsjahr
            if (eintrittsdatum.HasValue
                && eintrittsdatum.Value.Year == beitragsjahr
                && eintrittsdatum.Value.Date > new DateTime(beitragsjahr, 6, 30))
            {
                basis = basis / 2m;
            }

            return basis + freiwilligerZusatz;
        }
    }
}
