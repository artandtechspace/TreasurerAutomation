// Zuwendungsbestätigung (Geldzuwendung / Mitgliedsbeitrag) für den ARTandTECH.space e.V.
// Stand: BMF-Muster Anlage 3 (BMF-Schreiben 07.11.2013, § 50 Abs. 1 EStDV), als Typst-Funktion.
// Datenstand: Freistellungsbescheid FA Steinfurt vom 12.08.2026 für 2024 (StNr. 311/5844/1807),
// Satzung vom 25.04.2024 (§ 2 Zweck, § 16 Auflösung).
// WICHTIG: Wortlaut und Reihenfolge des amtlichen Musters dürfen nicht verändert werden.
// Vorderseite muss auf eine DIN-A4-Seite passen. Rückseite frei für Logo/Dank.
// Keine Rechtsberatung – maßgeblich bleiben BMF-Muster + aktueller Freistellungsbescheid.
// Auffälligkeit: Bescheid anerkennt Nr. 1 (Wissenschaft/Forschung), Nr. 4 (Jugendhilfe),
// Nr. 7 (Erziehung). Kunst/Kultur (Nr. 5) und bürgerschaftliches Engagement stehen nur in
// der Satzung, NICHT im Freistellungsbescheid – daher in der Bestätigung nur die drei
// bescheidgemäßen Zwecke verwenden. Mitgliedsbeiträge sind laut Bescheid ausdrücklich
// bescheinigungsfähig, der Ausschluss-Satz für nicht abziehbare Beiträge entfällt daher.
//
// ORGA-HINWEISE (kein Bestandteil des Drucks, für Kassenwart + Skript):
// - VERTRETUNG (§ 9 Abs. 2 Satzung, geprüft): Gesamtvertretung, daher IMMER zwei
//   Unterschriften (Standard: Kassenwart + Vorsitzender Jascha Wallmeier).
//   Bei Vorstandswechsel: unterzeichner2-Defaults + beispiel.typ aktualisieren!
// - Maschinelle Erstellung ohne händische Unterschrift: EDV-Verfahren vorab dem
//   Finanzamt Steinfurt anzeigen (§ 50 Abs. 1 EStDV, BMF-Schreiben 07.11.2013).
// - Gültigkeit: Freistellungsbescheid 12.08.2026 taggenau bis 11.08.2031 verwendbar
//   (§ 63 Abs. 5 AO). Danach neuen Bescheid abwarten, Vorlage aktualisieren.
// - Aufbewahrung: Doppel jeder Bestätigung 10 Jahre (mit Kontoauszug zusammen ablegen).
// - Aufwandsspende (verzicht: true): nur mit vorherigem wirksamen Anspruch aus Vertrag,
//   Beschluss oder Satzung + schriftlicher Verzichtserklärung. Beides mit ablegen.
// - Mehrere Spenden einer Person pro Jahr: Sammelbestätigung (sammel-vorlage.typ).
// - Sachspenden: NICHT mit dieser Vorlage (sach-vorlage.typ, Anlage 4).

#let checkbox(checked) = if checked [☒] else [☐]

#let zuwendungsbestaetigung(
  // --- Spender ---
  spender-name: "",
  spender-strasse: "",
  spender-plz-ort: "",
  // --- Spende ---
  betrag-ziffern: "",
  betrag-buchstaben: "",
  tag-zuwendung: "",
  verzicht: false,
  // false = reine Geldspende, true = Mitgliedsbeitrag (bei ATS abziehbar)
  ist-mitgliedsbeitrag: false,
  // --- Gemeinnützigkeit: vorbelegt aus Freistellungsbescheid 12.08.2026 / Satzung 25.04.2024 ---
  foerderzweck: "Förderung von Wissenschaft und Forschung (§ 52 Abs. 2 Satz 1 Nr. 1 AO), Förderung der Jugendhilfe (§ 52 Abs. 2 Satz 1 Nr. 4 AO) und Förderung der Erziehung (§ 52 Abs. 2 Satz 1 Nr. 7 AO)",
  finanzamt: "Steinfurt",
  steuernr: "311/5844/1807",
  freistellungsdatum: "12.08.2026",
  veranlagungszeitraum: "2024",
  // Nur setzen, wenn zusätzlich ein separater §60a-Feststellungsbescheid vorliegt.
  // Im Freistellungsbescheid 2024 ist keiner mit Datum ausgewiesen → Standard: false.
  mit-60a-feststellung: false,
  feststellungs-finanzamt: "Steinfurt",
  feststellungs-steuernr: "311/5844/1807",
  feststellungsdatum: "",
  satzungszweck: "Förderung von Wissenschaft und Forschung, Jugendhilfe, Kunst und Kultur, Bildung und Erziehung sowie bürgerschaftlichen Engagements (§ 2 Satzung vom 25.04.2024)",
  verwendungszweck: "Förderung von Wissenschaft und Forschung, Jugendhilfe und Erziehung",
  // Nur true, wenn Mitgliedsbeiträge steuerlich NICHT abziehbar wären.
  // Bei ATS sind sie laut Bescheid bescheinigungsfähig → Standard: false (Absatz entfällt).
  zeige-mitgliedsbeitrag-hinweis: false,
  // --- Ausstellung ---
  ausstellungsort: "Rheine",
  ausstellungsdatum: "",
  unterzeichner-name: "",
  unterzeichner-funktion: "Kassenwart",
  // Gesamtvertretung nach § 9 Abs. 2 Satzung: zweite Unterschrift Pflicht.
  // Aktuell: Jascha Wallmeier (Vorstandsvorsitzender, Stand 2026 + Impressum).
  unterzeichner2-name: "Jascha Wallmeier",
  unterzeichner2-funktion: "Vorstandsvorsitzender",
  beleg-nr: "",
) = {
  assert(spender-name != "", message: "spender-name fehlt (Pflichtangabe).")
  assert(spender-strasse != "", message: "spender-strasse fehlt (Pflichtangabe).")
  assert(spender-plz-ort != "", message: "spender-plz-ort fehlt (Pflichtangabe).")
  assert(betrag-ziffern != "", message: "betrag-ziffern fehlt (Pflichtangabe).")
  assert(betrag-buchstaben != "", message: "betrag-buchstaben fehlt (Pflichtangabe).")
  assert(tag-zuwendung != "", message: "tag-zuwendung fehlt (Pflichtangabe).")
  assert(ausstellungsdatum != "", message: "ausstellungsdatum fehlt (Pflichtangabe).")
  assert(unterzeichner-name != "", message: "unterzeichner-name fehlt (Pflichtangabe).")
  assert(unterzeichner2-name != "", message: "unterzeichner2-name fehlt (Gesamtvertretung § 9 Abs. 2).")
  assert(
    not mit-60a-feststellung or feststellungsdatum != "",
    message: "feststellungsdatum fehlt (nötig bei mit-60a-feststellung: true).",
  )
  set document(
    title: "Zuwendungsbestätigung – ARTandTECH.space e. V." + if beleg-nr != "" { " – " + beleg-nr } else { "" },
    author: "ARTandTECH.space e. V.",
  )
  set page(paper: "a4", margin: (top: 1.6cm, bottom: 1.4cm, left: 2cm, right: 2cm))
  set text(size: 9.5pt, lang: "de")
  set par(justify: true, leading: 0.55em, spacing: 0.65em)

  // Kopf
  grid(
    columns: (1fr, auto),
    gutter: 1em,
    [
      #text(size: 13pt, weight: "bold")[ARTandTECH.space e. V.]
      \ Lindenstraße 11 · 48431 Rheine \
      #text(size: 8.5pt)[Tel. 05971 9545465 · team\@artandtech.space · VR 1822, AG Steinfurt]
    ],
    [
      #text(size: 8.5pt, weight: "bold")[Zuwendungsbestätigung]
      \ #text(size: 8.5pt)[#if beleg-nr != "" [Beleg-Nr. #beleg-nr] else [Einzelbestätigung]]
      \ #text(size: 8.5pt)[StNr. 311/5844/1807]
    ],
  )
  line(length: 100%, stroke: 0.7pt)
  v(0.3em)

  text(size: 8pt, style: "italic")[Aussteller (Bezeichnung und Anschrift der steuerbegünstigten Einrichtung)]
  v(0.1em)
  text(weight: "bold")[ARTandTECH.space e. V., Lindenstraße 11, 48431 Rheine]
  v(0.4em)

  align(center)[
    #text(size: 12pt, weight: "bold")[Bestätigung über Geldzuwendungen / Mitgliedsbeiträge]
    \ #text(size: 9pt)[im Sinne des § 10b des Einkommensteuergesetzes an eine der in § 5 Abs. 1 Nr. 9 des Körperschaftsteuergesetzes bezeichneten Körperschaften, Personenvereinigungen oder Vermögensmassen]
  ]
  v(0.4em)

  text(size: 8pt, style: "italic")[Name und Anschrift des Zuwendenden:]
  box(
    width: 100%,
    stroke: 0.6pt,
    inset: 0.6em,
    [
      #text(weight: "bold")[#spender-name] \
      #spender-strasse \
      #spender-plz-ort
    ],
  )
  v(0.4em)

  // Betragstabelle
  grid(
    columns: (1fr, 1fr, auto),
    gutter: 0pt,
    stroke: 0.6pt,
    inset: 0.5em,
    align(left)[#text(size: 8pt)[Betrag der Zuwendung – in Ziffern –] \ #text(weight: "bold")[#betrag-ziffern]],
    align(left)[#text(size: 8pt)[– in Buchstaben –] \ #text(weight: "bold")[#betrag-buchstaben]],
    align(left)[#text(size: 8pt)[Tag der Zuwendung:] \ #text(weight: "bold")[#tag-zuwendung]],
  )
  v(0.3em)
  text(size: 8.5pt)[Art: #if ist-mitgliedsbeitrag [Mitgliedsbeitrag] else [Geldzuwendung] · Es handelt sich um den Verzicht auf Erstattung von Aufwendungen #checkbox(verzicht) Ja \u{2003} #checkbox(not verzicht) Nein]
  v(0.3em)

  // Amtlicher Wortlaut – nicht umformulieren!
  [Wir sind wegen #foerderzweck nach dem Freistellungsbescheid bzw. nach der Anlage zum Körperschaftsteuerbescheid des Finanzamtes #finanzamt, StNr. #steuernr, vom #freistellungsdatum für den letzten Veranlagungszeitraum #veranlagungszeitraum nach § 5 Abs. 1 Nr. 9 des Körperschaftsteuergesetzes von der Körperschaftsteuer und nach § 3 Nr. 6 des Gewerbesteuergesetzes von der Gewerbesteuer befreit.]

  if mit-60a-feststellung [
    Die Einhaltung der satzungsmäßigen Voraussetzungen nach den §§ 51, 59, 60 und 61 AO wurde vom Finanzamt #feststellungs-finanzamt, StNr. #feststellungs-steuernr, mit Bescheid vom #feststellungsdatum nach § 60a AO gesondert festgestellt. Wir fördern nach unserer Satzung (#satzungszweck).
  ]

  [Es wird bestätigt, dass die Zuwendung nur zur #verwendungszweck verwendet wird.]

  if zeige-mitgliedsbeitrag-hinweis [
    Nur für steuerbegünstigte Einrichtungen, bei denen die Mitgliedsbeiträge steuerlich nicht abziehbar sind: \ #checkbox(not ist-mitgliedsbeitrag) Es wird bestätigt, dass es sich nicht um einen Mitgliedsbeitrag handelt, dessen Abzug nach § 10b Abs. 1 des Einkommensteuergesetzes ausgeschlossen ist.
  ]
  v(0.5em)

  // Unterschriftsbereich: Zeile 1 Ort und Datum, darunter die gemeinsame
  // Caption, dann beide Unterschriften nebeneinander
  // (Gesamtvertretung nach § 9 Abs. 2 Satzung).
  [#ausstellungsort, den #ausstellungsdatum]
  v(0.4em)
  text(size: 8pt)[Unterschrift des Zuwendungsempfängers]
  v(1.8em)
  grid(
    columns: (1fr, 1fr),
    gutter: 2em,
    [
      #line(length: 100%, stroke: 0.6pt)
      #text(size: 8pt)[#unterzeichner-name, #unterzeichner-funktion]
    ],
    [
      #line(length: 100%, stroke: 0.6pt)
      #text(size: 8pt)[#unterzeichner2-name, #unterzeichner2-funktion]
    ],
  )
  v(0.4em)

  align(left, text(size: 7.5pt)[
    *Hinweis:* Wer vorsätzlich oder grob fahrlässig eine unrichtige Zuwendungsbestätigung erstellt oder veranlasst, dass Zuwendungen nicht zu den in der Zuwendungsbestätigung angegebenen steuerbegünstigten Zwecken verwendet werden, haftet für die entgangene Steuer (§ 10b Abs. 4 EStG, § 9 Abs. 3 KStG, § 9 Nr. 5 GewStG).
  ])
  align(left, text(size: 7.5pt)[
    *Hinweis:* Diese Bestätigung wird nicht als Nachweis für die steuerliche Berücksichtigung der Zuwendung anerkannt, wenn das Datum des Freistellungsbescheides länger als 5 Jahre bzw. das Datum der Feststellung der Einhaltung der satzungsmäßigen Voraussetzungen nach § 60a Abs. 1 AO länger als 3 Jahre seit Ausstellung des Bescheides zurückliegt (§ 63 Abs. 5 AO).
  ])

  v(0.2em)
  line(length: 100%, stroke: 0.4pt)
  text(size: 7.5pt)[ARTandTECH.space e. V. · Lindenstraße 11 · 48431 Rheine · VR 1822 (AG Steinfurt) · Finanzamt #finanzamt, StNr. #steuernr · team\@artandtech.space]
}
