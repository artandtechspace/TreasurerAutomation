using System.Text.Json;

namespace TreasurerAutomation.Services
{
    /// <summary>
    /// Antwort von POST v2.0/get-token:
    /// { id, email, needs2FA, expiresIn (Sekunden, 30 Tage), token }.
    /// </summary>
    public sealed record EasyVereinTokenResponse(
        int Id,
        string Email,
        bool Needs2FA,
        int ExpiresIn,
        string Token)
    {
        public static EasyVereinTokenResponse Parse(JsonElement el)
        {
            int Id() => el.TryGetProperty("id", out var p) && p.TryGetInt32(out var i) ? i : 0;
            string Email() => el.TryGetProperty("email", out var p) ? p.GetString() ?? "" : "";
            bool Needs2FA() =>
                (el.TryGetProperty("needs2FA", out var p) && p.ValueKind == JsonValueKind.True) ||
                (el.TryGetProperty("needs2fa", out var p2) && p2.ValueKind == JsonValueKind.True);
            int Expires() => el.TryGetProperty("expiresIn", out var p) && p.TryGetInt32(out var i) ? i : 0;
            string Token() => el.TryGetProperty("token", out var p) ? p.GetString() ?? "" : "";
            var t = Token();
            if (string.IsNullOrWhiteSpace(t))
                throw new Exception("get-token Antwort enthält keinen 'token'.");
            return new EasyVereinTokenResponse(Id(), Email(), Needs2FA(), Expires(), t);
        }
    }

    /// <summary>API-Fehler mit Statuscode (u.a. für 2FA-Erkennung beim Login).</summary>
    public sealed class EasyVereinApiException : Exception
    {
        public int StatusCode { get; }
        public EasyVereinApiException(int statusCode, string message) : base(message) => StatusCode = statusCode;
    }

    /// <summary>
    /// Persistente CLI-Session: Token + Ablauf, damit login für die "komplette Session"
    /// (alle folgenden CLI-Aufrufe) gilt. Datei liegt außerhalb des Repos:
    /// ~/.config/treasurer-automation/easyverein-session.json (0600).
    /// Per EASYVEREIN_SESSION_PATH übersteuerbar (für Tests).
    /// </summary>
    public sealed record EasyVereinSession(
        string Token,
        string Email,
        int UserId,
        DateTime CreatedAtUtc,
        DateTime ExpiresAtUtc)
    {
        /// <summary>Fallback-Lebensdauer, wenn die API kein expiresIn liefert (30 Tage).</summary>
        public const int DefaultLifetimeDays = 30;
        public bool IsExpired(DateTime? now = null) =>
            (now ?? DateTime.UtcNow) >= ExpiresAtUtc.AddMinutes(-5);

        public static string SessionPath()
        {
            var overridePath = Environment.GetEnvironmentVariable("EASYVEREIN_SESSION_PATH");
            if (!string.IsNullOrWhiteSpace(overridePath)) return overridePath;
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(home)) home = Environment.CurrentDirectory;
            return Path.Combine(home, ".config", "treasurer-automation", "easyverein-session.json");
        }

        public static EasyVereinSession? Load()
        {
            try
            {
                var path = SessionPath();
                if (!File.Exists(path)) return null;
                var json = File.ReadAllText(path);
                using var doc = JsonDocument.Parse(json);
                var r = doc.RootElement;
                return new EasyVereinSession(
                    r.GetProperty("token").GetString() ?? "",
                    r.TryGetProperty("email", out var e) ? e.GetString() ?? "" : "",
                    r.TryGetProperty("userId", out var u) && u.TryGetInt32(out var i) ? i : 0,
                    r.TryGetProperty("createdAtUtc", out var c) && c.TryGetDateTime(out var dt) ? dt : DateTime.UtcNow,
                    r.TryGetProperty("expiresAtUtc", out var x) && x.TryGetDateTime(out var dt2) ? dt2 : DateTime.UtcNow);
            }
            catch
            {
                return null;
            }
        }

        public void Save()
        {
            var path = SessionPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            var json = JsonSerializer.Serialize(new
            {
                token = Token,
                email = Email,
                userId = UserId,
                createdAtUtc = CreatedAtUtc,
                expiresAtUtc = ExpiresAtUtc,
            }, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
            try
            {
                // 0600 auf Unix, auf Windows best effort ignorieren
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch { /* best effort */ }
        }

        public static void Clear()
        {
            try
            {
                var path = SessionPath();
                if (File.Exists(path)) File.Delete(path);
            }
            catch { /* best effort */ }
        }

        public static EasyVereinSession FromTokenResponse(EasyVereinTokenResponse resp, DateTime? now = null)
        {
            var t = now ?? DateTime.UtcNow;
            var expires = resp.ExpiresIn > 0 ? t.AddSeconds(resp.ExpiresIn) : t.AddDays(DefaultLifetimeDays);
            return new EasyVereinSession(resp.Token, resp.Email, resp.Id, t, expires);
        }
    }

    /// <summary>
    /// Baut den Login-Username als $orgShort_$emailOrUsername.
    /// Eingabe mit vorhandenem Prefix bleibt unverändert, sonst wird orgShort vorangestellt.
    /// </summary>
    public static class EasyVereinLogin
    {
        public const string DefaultOrgShort = "ats";

        public static string NormalizeUsername(string? eingabe, string? orgShort = null)
        {
            var user = (eingabe ?? "").Trim();
            if (string.IsNullOrWhiteSpace(user))
                throw new ArgumentException("Username/E-Mail darf nicht leer sein.", nameof(eingabe));
            var org = (orgShort ?? DefaultOrgShort).Trim().TrimEnd('_');
            // Nur unverändert lassen, wenn DAS Org-Prefix bereits dransteht –
            // ein beliebiger Unterstrich (z.B. max_muster@mail.de) genügt nicht.
            if (!string.IsNullOrWhiteSpace(org) && user.StartsWith(org + "_", StringComparison.OrdinalIgnoreCase))
                return user;
            if (string.IsNullOrWhiteSpace(org)) return user;
            return $"{org}_{user}";
        }
    }

    /// <summary>
    /// Token-Auflösung für alle Commands: explizit &gt; Env &gt; Session-Datei (nicht abgelaufen).
    /// </summary>
    public static class EasyVereinTokenResolver
    {
        public static string Resolve(string? explicitToken) => ResolveMitQuelle(explicitToken).Token;

        public static string DescribeSource(string? explicitToken)
        {
            var (token, quelle, session) = ResolveMitQuelle(explicitToken);
            if (string.IsNullOrWhiteSpace(token)) return "keine";
            if (quelle == "Session" && session != null)
                return $"Session ({session.Email}, gültig bis {session.ExpiresAtUtc:dd.MM.yyyy})";
            return quelle;
        }

        private static (string Token, string Quelle, EasyVereinSession? Session) ResolveMitQuelle(string? explicitToken)
        {
            if (!string.IsNullOrWhiteSpace(explicitToken)) return (explicitToken.Trim(), "--easyverein-token", null);
            var env = Environment.GetEnvironmentVariable("EASYVEREIN_TOKEN");
            if (!string.IsNullOrWhiteSpace(env)) return (env.Trim(), "EASYVEREIN_TOKEN", null);
            var session = EasyVereinSession.Load();
            if (session != null && !string.IsNullOrWhiteSpace(session.Token) && !session.IsExpired())
                return (session.Token, "Session", session);
            return (string.Empty, "keine", null);
        }
    }
}
