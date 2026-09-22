using System.Text.Json;

namespace TreasurerAutomation.Services.MemberAudit
{
    /// <summary>
    /// Parst easyVerein v2.0 member + contact-details JSON tolerant.
    /// Die API liefert Alias-Felder mit/ohne Underscore (_paymentStartDate, _isCompany, ...)
    /// sowie Referenzen (int) oder eingebettete Objekte – beides wird akzeptiert.
    /// Custom-Field-Werte werden als bereits aufgelöstes Dictionary (Name->Wert) erwartet;
    /// der CLI-Befehl baut dieses aus den custom-fields Definitionen.
    /// </summary>
    public static class EasyVereinMemberParser
    {
        private static readonly System.Globalization.CultureInfo DeDe = new("de-DE");

        public static MemberRecord Parse(
            JsonElement member,
            JsonElement? contactDetails,
            IEnumerable<string> gruppenKuerzel,
            IEnumerable<string> gruppenNamen,
            IReadOnlyDictionary<string, string?> customValues)
        {
            var cd = contactDetails ?? GetObject(member, "contactDetails", "contactdetails");

            var vorname = cd.HasValue ? GetString(cd.Value, "firstName") : null;
            var nachname = cd.HasValue ? GetString(cd.Value, "familyName", "lastName") : null;
            var anzeige = $"{vorname} {nachname}".Trim();
            if (string.IsNullOrWhiteSpace(anzeige))
                anzeige = GetString(member, "membershipNumber") is { } nr
                    ? $"Mitglied {nr}"
                    : $"Mitglied #{GetInt(member, "id")?.ToString() ?? "?"}";

            var kuerzel = gruppenKuerzel
                .SelectMany(s => s.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
                .Select(s => s.Trim().ToUpperInvariant())
                .Where(s => s.Length > 0)
                .Distinct()
                .ToList();

            // Custom fields: Namen normalisiert (Excel hat trailing spaces + Sternchen).
            // Lookup einmal aufbauen statt pro Name alle Werte zu normieren (O(n²) -> O(n)).
            var normiert = customValues
                .GroupBy(kv => NormKey(kv.Key))
                .ToDictionary(g => g.Key, g => g.First().Value);
            string? Custom(params string[] namen)
            {
                foreach (var n in namen)
                    if (normiert.TryGetValue(NormKey(n), out var wert))
                        return wert;
                return null;
            }

            var sepaRoh = Custom(
                "Ja, hiermit erteile ich mein Einverständnis zum SEPA-Lastschriftverfahren!",
                "SEPA-Lastschriftverfahren", "sepaEinverstaendnis", "sepa");
            var nachweis = Custom("Nachweis für ermäßigten Mitgliedsbeitrag", "Nachweis", "ermaessigtNachweis");
            var newsletterRoh = Custom("Freiwilliger E-Mail-Newsletter", "Newsletter");
            var freiwilligRoh = Custom("Freiwilliger Beitrag", "freiwilligerBeitrag", "VBF");

            var zahlungsart = cd.HasValue ? GetInt(cd.Value, "methodOfPayment") ?? 0 : 0;

            return new MemberRecord
            {
                Id = GetInt(member, "id") ?? 0,
                MembershipNumber = GetString(member, "membershipNumber", "membershipnumber"),
                DisplayName = anzeige,
                Vorname = vorname,
                Nachname = nachname,
                LoginEmail = GetString(member, "emailOrUserName", "email"),
                PrimaereEmail = cd.HasValue
                    ? GetString(cd.Value, "primaryEmail", "privateEmail", "companyEmail")
                    ?? GetString(member, "emailOrUserName", "email")
                    : GetString(member, "emailOrUserName", "email"),
                PrivateEmail = cd.HasValue ? GetString(cd.Value, "privateEmail") : null,
                CompanyEmail = cd.HasValue ? GetString(cd.Value, "companyEmail") : null,
                Geburtstag = cd.HasValue ? GetDate(cd.Value, "dateOfBirth", "birthday") : null,
                Strasse = cd.HasValue ? GetString(cd.Value, "street") : null,
                Plz = cd.HasValue ? GetString(cd.Value, "zip", "plz", "postalCode") : null,
                Stadt = cd.HasValue ? GetString(cd.Value, "city", "stadt") : null,
                Land = cd.HasValue ? GetString(cd.Value, "country", "land") : null,

                Eintrittsdatum = GetDate(member, "joinDate", "entryDate"),
                Austrittsdatum = GetDate(member, "resignationDate", "exitDate"),
                Kuendigungsdatum = GetDate(member, "resignationNoticeDate"),
                Antragsdatum = GetDate(member, "_applicationDate", "applicationDate"),
                Aufnahmedatum = GetDate(member, "_applicationWasAcceptedAt", "applicationWasAcceptedAt", "acceptedAt"),

                Zahlungsart = zahlungsart,
                ZahlungsartText = cd.HasValue ? ZahlungsartTextFuer(zahlungsart) : "unbekannt",
                Iban = cd.HasValue ? GetString(cd.Value, "iban") : null,
                Bic = cd.HasValue ? GetString(cd.Value, "bic") : null,
                KontoinhaberAbweichend = cd.HasValue ? GetString(cd.Value, "bankAccountOwner") : null,
                Mandatsreferenz = cd.HasValue ? GetString(cd.Value, "sepaMandate", "mandateReference") : null,
                Mandatsdatum = cd.HasValue ? GetDate(cd.Value, "sepaDate", "sepadata", "mandateDate") : null,
                Saldo = cd.HasValue ? GetDecimal(cd.Value, "balance") ?? 0m : 0m,

                Leistungsbeginn = GetDate(member, "_paymentStartDate", "paymentStartDate"),
                NaechsteZahlung = GetDate(member, "nextPayment", "_nextPayment"),
                IndividuellerBeitrag = GetDecimal(member, "paymentAmount") ?? 0m,
                ZahlungsintervallMonate = GetInt(member, "paymentIntervallMonths") ?? 12,

                GruppenKuerzel = kuerzel,
                GruppenNamen = gruppenNamen.ToList(),

                Ehrenmitglied = ErkenneEhrenmitglied(gruppenNamen, kuerzel),
                Vorstand = kuerzel.Contains("VV") || gruppenNamen.Any(n => n.Contains("Vorstand", StringComparison.OrdinalIgnoreCase)),
                IstFirma = cd.HasValue && (GetBool(cd.Value, "_isCompany", "isCompany") ?? false),

                SepaEinverstaendnis = ParseJaNein(sepaRoh),
                NachweisDatei = string.IsNullOrWhiteSpace(nachweis) ? null : nachweis.Trim(),
                Newsletter = ParseJaNein(newsletterRoh),
                FreiwilligerZusatz = ParseDezimal(freiwilligRoh),
            };
        }

        private static string NormKey(string s) =>
            new string(s.Trim().TrimEnd('*').Trim().ToLowerInvariant().Where(c => char.IsLetterOrDigit(c)).ToArray());

        private static bool? ParseJaNein(string? v)
        {
            if (string.IsNullOrWhiteSpace(v)) return null;
            var s = v.Trim().ToLowerInvariant();
            if (s is "ja" or "yes" or "true" or "1" or "x") return true;
            if (s is "nein" or "no" or "false" or "0" or "-") return false;
            return null;
        }

        private static decimal ParseDezimal(string? v)
        {
            if (string.IsNullOrWhiteSpace(v)) return 0m;
            var s = v.Trim().Replace("EUR", "", StringComparison.OrdinalIgnoreCase).Trim();
            if (decimal.TryParse(s, System.Globalization.NumberStyles.Any, DeDe, out var d)) return d;
            if (decimal.TryParse(s, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d2)) return d2;
            return 0m;
        }

        private static bool ErkenneEhrenmitglied(IEnumerable<string> namen, IEnumerable<string> kuerzel)
        {
            if (namen.Any(n => n.Contains("Ehrenmitglied", StringComparison.OrdinalIgnoreCase))) return true;
            if (kuerzel.Any(k => k.Equals("EHRE", StringComparison.OrdinalIgnoreCase))) return true;
            return false;
        }

        private static string ZahlungsartTextFuer(int art) => art switch
        {
            1 => "Lastschrift",
            2 => "Überweisung",
            3 => "Bar",
            4 => "Sonstige",
            _ => "Nicht ausgewählt",
        };

        // ---- JSON-Helfer (tolerant, case-insensitiv über Varianten) ----

        public static string? GetString(JsonElement el, params string[] keys)
        {
            if (el.ValueKind != JsonValueKind.Object) return null;
            foreach (var k in keys)
            {
                if (el.TryGetProperty(k, out var p) && p.ValueKind == JsonValueKind.String)
                {
                    var s = p.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) return s.Trim();
                }
                // case-insensitiv Fallback
                foreach (var prop in el.EnumerateObject())
                {
                    if (string.Equals(prop.Name, k, StringComparison.OrdinalIgnoreCase)
                        && prop.Value.ValueKind == JsonValueKind.String)
                    {
                        var s = prop.Value.GetString();
                        if (!string.IsNullOrWhiteSpace(s)) return s.Trim();
                    }
                }
            }
            return null;
        }

        public static int? GetInt(JsonElement el, params string[] keys)
        {
            if (el.ValueKind != JsonValueKind.Object) return null;
            foreach (var prop in el.EnumerateObject())
            {
                if (keys.Any(k => string.Equals(prop.Name, k, StringComparison.OrdinalIgnoreCase)))
                {
                    if (prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetInt32(out var i)) return i;
                    if (prop.Value.ValueKind == JsonValueKind.String && int.TryParse(prop.Value.GetString(), out var i2)) return i2;
                }
            }
            return null;
        }

        public static decimal? GetDecimal(JsonElement el, params string[] keys)
        {
            if (el.ValueKind != JsonValueKind.Object) return null;
            foreach (var prop in el.EnumerateObject())
            {
                if (keys.Any(k => string.Equals(prop.Name, k, StringComparison.OrdinalIgnoreCase)))
                {
                    if (prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetDecimal(out var d)) return d;
                    if (prop.Value.ValueKind == JsonValueKind.String)
                    {
                        var s = prop.Value.GetString();
                        if (!string.IsNullOrWhiteSpace(s))
                        {
                            s = s.Replace("EUR", "", StringComparison.OrdinalIgnoreCase).Trim();
                            if (decimal.TryParse(s, System.Globalization.NumberStyles.Any, DeDe, out var d2)) return d2;
                            if (decimal.TryParse(s, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d3)) return d3;
                        }
                    }
                }
            }
            return null;
        }

        public static bool? GetBool(JsonElement el, params string[] keys)
        {
            if (el.ValueKind != JsonValueKind.Object) return null;
            foreach (var prop in el.EnumerateObject())
            {
                if (keys.Any(k => string.Equals(prop.Name, k, StringComparison.OrdinalIgnoreCase)))
                {
                    if (prop.Value.ValueKind == JsonValueKind.True) return true;
                    if (prop.Value.ValueKind == JsonValueKind.False) return false;
                    if (prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetInt32(out var i)) return i != 0;
                    if (prop.Value.ValueKind == JsonValueKind.String)
                    {
                        var s = prop.Value.GetString()?.Trim().ToLowerInvariant();
                        if (s is "true" or "ja" or "yes" or "1") return true;
                        if (s is "false" or "nein" or "no" or "0") return false;
                    }
                }
            }
            return null;
        }

        public static DateTime? GetDate(JsonElement el, params string[] keys)
        {
            if (el.ValueKind != JsonValueKind.Object) return null;
            foreach (var prop in el.EnumerateObject())
            {
                if (keys.Any(k => string.Equals(prop.Name, k, StringComparison.OrdinalIgnoreCase)))
                {
                    if (prop.Value.ValueKind == JsonValueKind.String)
                    {
                        var s = prop.Value.GetString();
                        if (string.IsNullOrWhiteSpace(s)) continue;
                        s = s.Trim();
                        if (s.Equals("n/a", StringComparison.OrdinalIgnoreCase) || s is "-" or "–" or "—" or "keine Angabe") continue;
                        // 1. Deutsch exakt (Excel/API-Altformat): TT.MM.JJJJ
                        if (DateTime.TryParseExact(s, new[] { "dd.MM.yyyy", "d.M.yyyy", "dd.MM.yyyy HH:mm:ss", "dd.MM.yyyy HH:mm" },
                                DeDe,
                                System.Globalization.DateTimeStyles.None, out var de))
                            return de.Date;
                        // 2. API-Format YYYY-MM-DD (+ optional Zeit, Breaking Change Juli 2026: nur Datum)
                        if (DateTime.TryParseExact(s, new[] { "yyyy-MM-dd", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm:ssZ", "yyyy-MM-dd HH:mm:ss" },
                                System.Globalization.CultureInfo.InvariantCulture,
                                System.Globalization.DateTimeStyles.AssumeUniversal, out var iso))
                            return iso.Date;
                        // 3. Fallback allgemein (de zuerst, damit 10.01. nicht als Oct 1 gelesen wird)
                        if (DateTime.TryParse(s, DeDe,
                                System.Globalization.DateTimeStyles.None, out var dt2))
                            return dt2.Date;
                        if (DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
                                System.Globalization.DateTimeStyles.AssumeUniversal, out var dt))
                            return dt.Date;
                    }
                }
            }
            return null;
        }

        public static JsonElement? GetObject(JsonElement el, params string[] keys)
        {
            if (el.ValueKind != JsonValueKind.Object) return null;
            foreach (var prop in el.EnumerateObject())
            {
                if (keys.Any(k => string.Equals(prop.Name, k, StringComparison.OrdinalIgnoreCase))
                    && prop.Value.ValueKind == JsonValueKind.Object)
                    return prop.Value;
            }
            return null;
        }

        /// <summary>
        /// Extrahiert Gruppenkürzel/-namen aus eingebetteten memberGroups-Arrays.
        /// Akzeptiert Strings ("VB01"), Objekte mit short/shortName/abbreviation oder {memberGroup:{...}}.
        /// </summary>
        public static (List<string> Kuerzel, List<string> Namen) ParseGruppen(JsonElement member)
        {
            var kuerzel = new List<string>();
            var namen = new List<string>();
            if (member.ValueKind != JsonValueKind.Object) return (kuerzel, namen);
            JsonElement? arr = null;
            foreach (var prop in member.EnumerateObject())
            {
                if (string.Equals(prop.Name, "memberGroups", StringComparison.OrdinalIgnoreCase)
                    && prop.Value.ValueKind == JsonValueKind.Array)
                { arr = prop.Value; break; }
            }
            if (arr == null) return (kuerzel, namen);
            foreach (var item in arr.Value.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var s = item.GetString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(s))
                    {
                        if (SiehtAusWieKuerzel(s))
                            kuerzel.Add(s.ToUpperInvariant());
                        else namen.Add(s);
                    }
                }
                else if (item.ValueKind == JsonValueKind.Object)
                {
                    var kurz = GetString(item, "short", "shortName", "shortcut", "abbreviation", "code", "kuerzel");
                    var name = GetString(item, "name", "title", "label");
                    // verschachtelt: {memberGroup: {...}}
                    if (kurz == null || name == null)
                    {
                        var inner = GetObject(item, "memberGroup", "membergroup", "group");
                        if (inner.HasValue)
                        {
                            kurz ??= GetString(inner.Value, "short", "shortName", "shortcut", "abbreviation", "code");
                            name ??= GetString(inner.Value, "name", "title", "label");
                        }
                    }
                    if (!string.IsNullOrWhiteSpace(kurz)) kuerzel.Add(kurz.Trim().ToUpperInvariant());
                    if (!string.IsNullOrWhiteSpace(name)) namen.Add(name.Trim());
                    // Fallback: nur Name vorhanden, der wie Kürzel aussieht (kurz + VB/VV)
                    if (kurz == null && name != null && name.Length <= 8 && SiehtAusWieKuerzel(name))
                        kuerzel.Add(name.ToUpperInvariant());
                }
            }
            return (kuerzel.Distinct().ToList(), namen.Distinct().ToList());
        }

        /// <summary>Heuristik: kurz oder VB-Präfix → Kürzel, sonst Name.</summary>
        private static bool SiehtAusWieKuerzel(string s) =>
            s.Length <= 8 || s.StartsWith("VB", StringComparison.OrdinalIgnoreCase) || s == "VV";
    }
}
