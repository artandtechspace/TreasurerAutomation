// Beispiel für die Zuwendungsbestätigung des ARTandTECH.space e.V.
// Kompilieren mit: typst compile beispiel.typ beispiel.pdf
// Vereins-/Bescheid-Daten sind in vorlage.typ vorbelegt (Freistellungsbescheid
// FA Steinfurt vom 12.08.2026 für 2024, Satzung vom 25.04.2024).
// Hier nur noch Spender + Spende + Unterschrift eintragen. MUSTER-Daten ersetzen!

#import "vorlage.typ": zuwendungsbestaetigung

#zuwendungsbestaetigung(
  spender-name: "Max Mustermann",
  spender-strasse: "Musterstraße 12",
  spender-plz-ort: "48431 Rheine",
  betrag-ziffern: "150,00 EUR",
  betrag-buchstaben: "einhundertfünfzig Euro",
  tag-zuwendung: "12.09.2026",
  verzicht: false,
  ist-mitgliedsbeitrag: false,
  // Mitgliedsbeiträge sind laut Bescheid bescheinigungsfähig,
  // daher bleibt der Ausschluss-Hinweis aus (Standard: false).
  ausstellungsort: "Rheine",
  ausstellungsdatum: "19.09.2026",
  unterzeichner-name: "Luca Schöneberg",
  unterzeichner-funktion: "Kassenwart",
  unterzeichner2-name: "Jascha Wallmeier",
  unterzeichner2-funktion: "Vorstandsvorsitzender",
  beleg-nr: "2026-001 (MUSTER)",
)
