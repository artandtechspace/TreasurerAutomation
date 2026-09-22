// SEPA-Lastschriftmandat für den ARTandTECH.space e.V.
// Stand: deutsches SEPA-Basislastschriftmandat (wiederkehrende Zahlungen, Mitgliedsbeiträge).
// Ablauf: Formular pro Mitglied erzeugen (member-fix --sepa-mandat), ausdrucken,
// unterschreiben lassen, Original ablegen + Kopie/Scan in easyVerein zum Mitglied.
// Erst NACH Unterschrift Mandatsreferenz + Mandatsdatum in easyVerein pflegen
// (sepaMandate/sepaDate) und SEPA-Einwilligung auf Ja – nie vorher.
// Keine Rechtsberatung – bei Unsicherheit Bank/Steuerberater fragen.
// Kompilieren mit: typst compile <datei>.typ <datei>.pdf

#let sepa_mandat(
  // --- Zahlungsempfänger (Verein) ---
  empfaenger-name: "",
  empfaenger-adresse: "",
  glaeubiger-id: "",
  // --- Mandat ---
  mandatsreferenz: "",
  // --- Zahler (Mitglied / Kontoinhaber) ---
  zahler-name: "",
  zahler-strasse: "",
  zahler-plz-ort: "",
  iban: "",
  bic: "",
  // --- Ausstellung ---
  ort-datum: "",
  hinweis: "",
) = {
  assert(empfaenger-name != "", message: "empfaenger-name fehlt (Pflichtangabe).")
  assert(glaeubiger-id != "", message: "glaeubiger-id fehlt (Pflichtangabe).")
  assert(mandatsreferenz != "", message: "mandatsreferenz fehlt (Pflichtangabe).")
  assert(zahler-name != "", message: "zahler-name fehlt (Pflichtangabe).")
  assert(iban != "", message: "iban fehlt (Pflichtangabe).")
  assert(ort-datum != "", message: "ort-datum fehlt (Pflichtangabe).")

  set page(paper: "a4", margin: 2cm)
  set text(lang: "de", size: 11pt)

  align(center, text(size: 16pt, weight: "bold")[SEPA-Lastschriftmandat])
  align(center, text(size: 10pt)[für wiederkehrende Zahlungen (Mitgliedsbeiträge)])
  v(0.6cm)

  text(weight: "bold")[Zahlungsempfänger]
  linebreak()
  [#empfaenger-name] \
  [#empfaenger-adresse] \
  Gläubiger-Identifikationsnummer: [#glaeubiger-id] \
  Mandatsreferenz: [#mandatsreferenz]
  v(0.4cm)

  text(weight: "bold")[Zahler / Kontoinhaber]
  linebreak()
  [#zahler-name] \
  [#zahler-strasse] \
  [#zahler-plz-ort] \
  IBAN: [#iban] \
  #if bic != "" [BIC: [#bic] \]

  v(0.4cm)
  [Ich ermächtige den oben genannten Zahlungsempfänger, Zahlungen (Mitgliedsbeiträge)
  von meinem Konto mittels Lastschrift einzuziehen. Zugleich weise ich mein
  Kreditinstitut an, die vom Zahlungsempfänger auf mein Konto gezogenen Lastschriften
  einzulösen.]
  v(0.2cm)
  [Hinweis: Ich kann innerhalb von acht Wochen, beginnend mit dem Belastungsdatum,
  die Erstattung des belasteten Betrages verlangen. Es gelten dabei die mit meinem
  Kreditinstitut vereinbarten Bedingungen.]
  #if hinweis != "" {
    v(0.3cm)
    text(weight: "bold")[Hinweis: ]
    [#hinweis]
  }

  v(1.2cm)
  [#ort-datum]
  h(1fr)
  [Unterschrift Zahler / Kontoinhaber]
  v(0.2cm)
  line(length: 100%)
}
