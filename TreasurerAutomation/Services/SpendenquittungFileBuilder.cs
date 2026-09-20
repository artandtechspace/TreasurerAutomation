using System.Text;
using System.Text.RegularExpressions;

namespace TreasurerAutomation.Services
{
    /// <summary>
    /// Eingabedaten für eine Zuwendungsbestätigung (Einzelbestätigung Geld/Mitgliedsbeitrag).
    /// </summary>
    public sealed record SpendenquittungDaten(
        string SpenderName,
        string SpenderStrasse,
        string SpenderPlzOrt,
        string BetragZiffern,
        string BetragBuchstaben,
        string TagZuwendung,
        bool Verzicht,
        bool IstMitgliedsbeitrag,
        string Ausstellungsort,
        string Ausstellungsdatum,
        string UnterzeichnerName,
        string UnterzeichnerFunktion,
        string Unterzeichner2Name,
        string Unterzeichner2Funktion,
        string BelegNr,
        string? Projektvermerk);

    /// <summary>
    /// Erzeugt den Inhalt einer spende-*.typ Datei, die "vorlage.typ" importiert.
    /// Reine String-Logik (ohne IO), damit sie einfach prüfbar bleibt.
    /// </summary>
    public static class SpendenquittungFileBuilder
    {
        /// <summary>
        /// Erzeugt den .typ-Quelltext. importPfad ist der Pfad zu vorlage.typ,
        /// relativ zur erzeugten Datei (normalerweise "vorlage.typ").
        /// </summary>
        public static string Build(SpendenquittungDaten daten, string importPfad = "vorlage.typ")
        {
            var sb = new StringBuilder();
            sb.AppendLine($"// Zuwendungsbestätigung {daten.BelegNr} – {daten.SpenderName}, {daten.BetragZiffern} vom {daten.TagZuwendung}");
            sb.AppendLine("// Erzeugt mit: TreasurerAutomation spendenquittung (Wizard)");
            sb.AppendLine($"// PDF erzeugen mit: typst compile <datei>.typ <datei>.pdf");
            if (!string.IsNullOrWhiteSpace(daten.Projektvermerk))
            {
                sb.AppendLine("// Projektvermerk (NICHT auf dem Beleg, kein Feld im Muster):");
                sb.AppendLine($"// {daten.Projektvermerk.Replace("\r", " ").Replace("\n", " ")}");
            }
            sb.AppendLine();
            sb.AppendLine($"#import \"{importPfad}\": zuwendungsbestaetigung");
            sb.AppendLine();
            sb.AppendLine("#zuwendungsbestaetigung(");
            sb.AppendLine($"  spender-name: \"{EscapeTypst(daten.SpenderName)}\",");
            sb.AppendLine($"  spender-strasse: \"{EscapeTypst(daten.SpenderStrasse)}\",");
            sb.AppendLine($"  spender-plz-ort: \"{EscapeTypst(daten.SpenderPlzOrt)}\",");
            sb.AppendLine($"  betrag-ziffern: \"{EscapeTypst(daten.BetragZiffern)}\",");
            sb.AppendLine($"  betrag-buchstaben: \"{EscapeTypst(daten.BetragBuchstaben)}\",");
            sb.AppendLine($"  tag-zuwendung: \"{EscapeTypst(daten.TagZuwendung)}\",");
            sb.AppendLine($"  verzicht: {BoolTypst(daten.Verzicht)},");
            sb.AppendLine($"  ist-mitgliedsbeitrag: {BoolTypst(daten.IstMitgliedsbeitrag)},");
            sb.AppendLine($"  ausstellungsort: \"{EscapeTypst(daten.Ausstellungsort)}\",");
            sb.AppendLine($"  ausstellungsdatum: \"{EscapeTypst(daten.Ausstellungsdatum)}\",");
            sb.AppendLine($"  unterzeichner-name: \"{EscapeTypst(daten.UnterzeichnerName)}\",");
            sb.AppendLine($"  unterzeichner-funktion: \"{EscapeTypst(daten.UnterzeichnerFunktion)}\",");
            sb.AppendLine($"  unterzeichner2-name: \"{EscapeTypst(daten.Unterzeichner2Name)}\",");
            sb.AppendLine($"  unterzeichner2-funktion: \"{EscapeTypst(daten.Unterzeichner2Funktion)}\",");
            sb.AppendLine($"  beleg-nr: \"{EscapeTypst(daten.BelegNr)}\",");
            sb.AppendLine(")");
            return sb.ToString();
        }

        /// <summary>
        /// Maskiert einen String für die Verwendung in "..." in Typst.
        /// </summary>
        public static string EscapeTypst(string value) =>
            value.Replace("\\", "\\\\").Replace("\"", "\\\"");

        private static string BoolTypst(bool value) => value ? "true" : "false";

        /// <summary>
        /// URL-/Datei-freundlicher Slug, z.B. "Stadtwerke Rheine GmbH" -&gt; "stadtwerke-rheine-gmbh".
        /// </summary>
        public static string Slug(string name)
        {
            var s = name.ToLowerInvariant()
                .Replace("ä", "ae")
                .Replace("ö", "oe")
                .Replace("ü", "ue")
                .Replace("ß", "ss");
            var sb = new StringBuilder(s.Length);
            foreach (var c in s)
                sb.Append(char.IsLetterOrDigit(c) ? c : '-');
            var slug = Regex.Replace(sb.ToString(), "-{2,}", "-").Trim('-');
            if (slug.Length > 60)
                slug = slug.Substring(0, 60).Trim('-');
            return string.IsNullOrEmpty(slug) ? "spender" : slug;
        }
    }
}
