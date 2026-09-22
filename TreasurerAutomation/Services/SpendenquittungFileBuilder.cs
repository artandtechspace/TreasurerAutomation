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
            void Feld(string name, string wert) => sb.AppendLine($"  {name}: \"{EscapeTypst(wert)}\",");
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
            Feld("spender-name", daten.SpenderName);
            Feld("spender-strasse", daten.SpenderStrasse);
            Feld("spender-plz-ort", daten.SpenderPlzOrt);
            Feld("betrag-ziffern", daten.BetragZiffern);
            Feld("betrag-buchstaben", daten.BetragBuchstaben);
            Feld("tag-zuwendung", daten.TagZuwendung);
            sb.AppendLine($"  verzicht: {BoolTypst(daten.Verzicht)},");
            sb.AppendLine($"  ist-mitgliedsbeitrag: {BoolTypst(daten.IstMitgliedsbeitrag)},");
            Feld("ausstellungsort", daten.Ausstellungsort);
            Feld("ausstellungsdatum", daten.Ausstellungsdatum);
            Feld("unterzeichner-name", daten.UnterzeichnerName);
            Feld("unterzeichner-funktion", daten.UnterzeichnerFunktion);
            Feld("unterzeichner2-name", daten.Unterzeichner2Name);
            Feld("unterzeichner2-funktion", daten.Unterzeichner2Funktion);
            Feld("beleg-nr", daten.BelegNr);
            sb.AppendLine(")");
            return sb.ToString();
        }

        /// <summary>
        /// Maskiert einen String für die Verwendung in "..." in Typst.
        /// Auch Steuerzeichen (\n, \r, \t), sonst bricht ein Zeilenumbruch das Literal.
        /// </summary>
        public static string EscapeTypst(string value) =>
            value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");

        private static string BoolTypst(bool value) => value ? "true" : "false";

        /// <summary>
        /// URL-/Datei-freundlicher Slug, z.B. "Stadtwerke Rheine GmbH" -&gt; "stadtwerke-rheine-gmbh".
        /// </summary>
        private const int MaxSlugLaenge = 60;
        private const string FallbackSlug = "spender";
        private static readonly (string Von, string Nach)[] UmlautMap =
        {
            ("ä", "ae"), ("ö", "oe"), ("ü", "ue"), ("ß", "ss"),
        };

        public static string Slug(string name)
        {
            var s = name.ToLowerInvariant();
            foreach (var (von, nach) in UmlautMap)
                s = s.Replace(von, nach);
            var sb = new StringBuilder(s.Length);
            foreach (var c in s)
                sb.Append(char.IsLetterOrDigit(c) ? c : '-');
            var slug = Regex.Replace(sb.ToString(), "-{2,}", "-").Trim('-');
            if (slug.Length > MaxSlugLaenge)
                slug = slug.Substring(0, MaxSlugLaenge).Trim('-');
            return string.IsNullOrEmpty(slug) ? FallbackSlug : slug;
        }
    }
}
