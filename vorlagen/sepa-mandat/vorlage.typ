// SEPA-Lastschriftmandat für den ARTandTECH.space e.V.
// Vorlage strikt nach dem DK-Standardformular (DG VERLAG 440 160) für das
// SEPA-Basis-Lastschriftverfahren (EPC SDD Core, DS-01):
// - Titel + Verfahrenshinweis, Gläubigerblock, Gläubiger-ID + Mandatsreferenz
// - Ermächtigungstext + 8-Wochen-Hinweis im EINHEITLICHEN Wortlaut der
//   Deutschen Kreditwirtschaft (darf nicht umformuliert werden)
// - Zahlungsart (wiederkehrend für Mitgliedsbeiträge), Zahlerblock,
//   Kreditinstitut + IBAN, Ort/Datum/Unterschrift
// Gestaltung frei (EPC Layout Guidelines), Inhalt + Wortlaut verbindlich.
// Quellen: EPC SDD Core Rulebook (DS-01 Attribute), EPC392-08 Layout Guidelines,
// DK FAQ SEPA, DG VERLAG 440 160. Keine Rechtsberatung.
// Ablauf: member-fix --sepa-mandat erzeugt je Mitglied eine Datei, ausdrucken,
// unterschreiben lassen, AUSFERTIGUNG FÜR DEN ZAHLUNGSEMPFÄNGER verwahren.
// Erst NACH Unterschrift Mandatsreferenz + Mandatsdatum in easyVerein pflegen
// (sepaMandate/sepaDate) und SEPA-Einwilligung auf Ja – nie vorher.
// Kompilieren mit: typst compile <datei>.typ <datei>.pdf

#let checkbox(checked) = if checked [☒] else [☐]

#let sepa_mandat(
  // --- Zahlungsempfänger (Gläubiger, vom Verein vorbelegt) ---
  empfaenger-name: "",
  empfaenger-strasse: "",
  empfaenger-plz-ort: "",
  empfaenger-land: "",
  glaeubiger-id: "",
  // --- Mandat (vom Zahlungsempfänger vergeben) ---
  mandatsreferenz: "",
  // true = wiederkehrende Zahlungen (Mitgliedsbeiträge), false = einmalige Zahlung
  wiederkehrend: true,
  // --- Zahlungspflichtiger (Mitglied / Kontoinhaber) ---
  zahler-name: "",
  zahler-strasse: "",
  zahler-plz-ort: "",
  zahler-land: "",
  kreditinstitut: "",
  iban: "",
  // --- Ausstellung ---
  ort-datum: "",
  // Optional (EPC "Creditor's use only", z.B. Mitgliedsnummer)
  intern-vermerk: "",
) = {
  assert(empfaenger-name != "", message: "empfaenger-name fehlt (Pflichtangabe).")
  assert(glaeubiger-id != "", message: "glaeubiger-id fehlt (Pflichtangabe).")
  assert(mandatsreferenz != "", message: "mandatsreferenz fehlt (Pflichtangabe).")
  assert(zahler-name != "", message: "zahler-name fehlt (Pflichtangabe).")
  assert(iban != "", message: "iban fehlt (Pflichtangabe).")
  assert(ort-datum != "", message: "ort-datum fehlt (Pflichtangabe).")

  set page(paper: "a4", margin: 2cm)
  set text(lang: "de", size: 11pt)
  set par(justify: true, leading: 0.55em, spacing: 0.65em)

  align(center)[
    #text(size: 16pt, weight: "bold")[SEPA-Lastschriftmandat]
    \ #text(size: 10pt)[SEPA Direct Debit Mandate für das SEPA-Basis-Lastschriftverfahren / for SEPA Core Direct Debit Scheme]
  ]
  v(0.4cm)

  text(size: 8pt, style: "italic")[Name und Anschrift des Zahlungsempfängers (Gläubiger)]
  v(0.1em)
  box(
    width: 100%,
    stroke: 0.6pt,
    inset: 0.6em,
    [
      #text(weight: "bold")[#empfaenger-name] \
      #empfaenger-strasse \
      #empfaenger-plz-ort \
      #empfaenger-land
    ],
  )
  v(0.3cm)

  grid(
    columns: (1fr, 1fr),
    gutter: 1em,
    [#text(size: 8pt)[Gläubiger-Identifikationsnummer] \ #text(weight: "bold")[#glaeubiger-id]],
    [#text(size: 8pt)[Mandatsreferenz] \ #text(weight: "bold")[#mandatsreferenz]],
  )
  v(0.3cm)

  // Einheitlicher Ermächtigungstext der Deutschen Kreditwirtschaft (Wortlaut verbindlich)
  [Ich ermächtige #empfaenger-name, Zahlungen von meinem Konto mittels Lastschrift einzuziehen. Zugleich weise ich mein Kreditinstitut an, die von #empfaenger-name auf mein Konto gezogenen Lastschriften einzulösen.]
  v(0.15cm)
  [*Hinweis:* Ich kann innerhalb von acht Wochen, beginnend mit dem Belastungsdatum, die Erstattung des belasteten Betrags verlangen. Es gelten dabei die mit meinem Kreditinstitut vereinbarten Bedingungen.]
  v(0.3cm)

  [
    Zahlungsart: #checkbox(wiederkehrend) Wiederkehrende Zahlungen #h(1cm) #checkbox(not wiederkehrend) Einmalige Zahlung
  ]
  v(0.3cm)

  text(size: 8pt, style: "italic")[Kontoinhaber / Zahlungspflichtiger (Vorname, Name, Straße, Hausnummer, PLZ, Ort)]
  v(0.1em)
  box(
    width: 100%,
    stroke: 0.6pt,
    inset: 0.6em,
    [
      #text(weight: "bold")[#zahler-name] \
      #zahler-strasse \
      #zahler-plz-ort \
      #zahler-land
    ],
  )
  v(0.3cm)

  [
    Kreditinstitut (Name und BIC): #kreditinstitut \
    IBAN: #iban
  ]
  text(size: 8pt)[Hinweis: Ab 01.02.2014 kann die Angabe des BIC entfallen, wenn die IBAN mit DE beginnt.]
  v(0.4cm)

  text(size: 8pt, style: "italic")[Ort, Datum, Unterschrift (Zahlungspflichtiger)]
  v(0.1em)
  [
    #ort-datum
    #h(1fr)
    Unterschrift
  ]
  v(1.2cm)
  line(length: 100%, stroke: 0.7pt)

  v(0.4cm)
  text(size: 8pt, fill: gray)[Ausfertigung für den Zahlungsempfänger – Original verwahren (Aufbewahrungspflicht).#if intern-vermerk != "" [ Nur für interne Verwendung: #intern-vermerk]]
}
