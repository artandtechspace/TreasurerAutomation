using System.Text;

namespace TreasurerAutomation.Services
{
    /// <summary>
    /// Eingabedaten für ein SEPA-Lastschriftmandat nach DK-Standardformular
    /// (DG VERLAG 440 160, SEPA-Basislastschrift / EPC SDD Core).
    /// Wird per member-fix aus dem Audit befüllt und per typst als PDF erzeugt.
    /// </summary>
    public sealed record SepaMandatDaten(
        string EmpfaengerName,
        string EmpfaengerStrasse,
        string EmpfaengerPlzOrt,
        string EmpfaengerLand,
        string GlaeubigerId,
        string Mandatsreferenz,
        bool Wiederkehrend,
        string ZahlerName,
        string ZahlerStrasse,
        string ZahlerPlzOrt,
        string ZahlerLand,
        string Kreditinstitut,
        string Iban,
        string OrtDatum,
        string? InternVermerk);

    /// <summary>
    /// Erzeugt den Inhalt einer sepa-*.typ Datei, die "vorlage.typ" importiert.
    /// Reine String-Logik (ohne IO), damit sie einfach prüfbar bleibt.
    /// Escaping/Slug kommen aus dem Spendenquittung-Builder (einheitlich).
    /// </summary>
    public static class SepaMandatFileBuilder
    {
        /// <summary>
        /// Erzeugt den .typ-Quelltext. importPfad ist der Pfad zu vorlage.typ,
        /// relativ zur erzeugten Datei (normalerweise "vorlage.typ").
        /// </summary>
        public static string Build(SepaMandatDaten daten, string importPfad = "vorlage.typ")
        {
            var sb = new StringBuilder();
            void Feld(string name, string wert) => sb.AppendLine($"  {name}: \"{SpendenquittungFileBuilder.EscapeTypst(wert)}\",");
            sb.AppendLine($"// SEPA-Lastschriftmandat {daten.Mandatsreferenz} – {daten.ZahlerName}");
            sb.AppendLine("// Erzeugt mit: TreasurerAutomation member-fix --sepa-mandat");
            sb.AppendLine($"// PDF erzeugen mit: typst compile <datei>.typ <datei>.pdf");
            sb.AppendLine("// Ablauf: unterschreiben lassen, Ausfertigung für den Zahlungsempfänger verwahren,");
            sb.AppendLine("// Mandatsreferenz + Mandatsdatum in easyVerein pflegen.");
            sb.AppendLine();
            sb.AppendLine($"#import \"{importPfad}\": sepa_mandat");
            sb.AppendLine();
            sb.AppendLine("#sepa_mandat(");
            Feld("empfaenger-name", daten.EmpfaengerName);
            Feld("empfaenger-strasse", daten.EmpfaengerStrasse);
            Feld("empfaenger-plz-ort", daten.EmpfaengerPlzOrt);
            Feld("empfaenger-land", daten.EmpfaengerLand);
            Feld("glaeubiger-id", daten.GlaeubigerId);
            Feld("mandatsreferenz", daten.Mandatsreferenz);
            sb.AppendLine($"  wiederkehrend: {(daten.Wiederkehrend ? "true" : "false")},");
            Feld("zahler-name", daten.ZahlerName);
            Feld("zahler-strasse", daten.ZahlerStrasse);
            Feld("zahler-plz-ort", daten.ZahlerPlzOrt);
            Feld("zahler-land", daten.ZahlerLand);
            Feld("kreditinstitut", daten.Kreditinstitut);
            Feld("iban", daten.Iban);
            Feld("ort-datum", daten.OrtDatum);
            Feld("intern-vermerk", daten.InternVermerk ?? "");
            sb.AppendLine(")");
            return sb.ToString();
        }

        /// <summary>
        /// Dateiname ohne Endung, z.B. 20260922_luca-schoeneberg_sepa-mandat.
        /// Datum vorne für chronologische Ablage, Slug wie bei Spendenquittungen.
        /// </summary>
        public static string DateiName(DateTime datum, string zahlerName, string? mitgliedsnummer = null)
        {
            var slug = SpendenquittungFileBuilder.Slug(zahlerName);
            var nr = System.Text.RegularExpressions.Regex.Replace((mitgliedsnummer ?? "").Trim(), @"[^A-Za-z0-9]+", "");
            var basis = $"{datum:yyyyMMdd}_{slug}";
            if (!string.IsNullOrEmpty(nr))
                basis += $"-{nr}";
            return $"{basis}_sepa-mandat";
        }
    }
}
