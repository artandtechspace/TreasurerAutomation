using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using TreasurerAutomation.Services;

namespace TreasurerAutomation.Commands
{
    public sealed class LoginSettings : CommandSettings
    {
        [CommandOption("-u|--username <WERT>")]
        [Description("Login-Name oder E-Mail. Ohne ats_-Prefix wird --org-short automatisch vorangestellt.")]
        public string? Username { get; set; }

        [CommandOption("--org-short <KÜRZEL>")]
        [Description("Organisationskürzel für den Username (Default: ats).")]
        [DefaultValue("ats")]
        public string OrgShort { get; set; } = "ats";

        [CommandOption("--password <WERT>")]
        [Description("Passwort. Ohne Angabe wird interaktiv (verdeckt) gefragt.")]
        public string? Password { get; set; }

        [CommandOption("--two-fa <CODE>")]
        [Description("2FA-Code. Ohne Angabe wird bei Bedarf interaktiv gefragt.")]
        public string? TwoFA { get; set; }
    }

    /// <summary>
    /// Interaktiver Login: fragt Username/Passwort (verdeckt) und bei Bedarf 2FA,
    /// holt POST v2.0/get-token und speichert die Session für alle folgenden CLI-Aufrufe.
    /// </summary>
    public sealed class LoginCommand : AsyncCommand<LoginSettings>
    {
        protected override async Task<int> ExecuteAsync(CommandContext context, LoginSettings settings,
            CancellationToken cancellationToken)
        {
            AnsiConsole.Write(new Rule("[yellow]easyVerein Login[/]").RuleStyle("grey").LeftJustified());
            Console.WriteLine();

            try
            {
                var existing = EasyVereinSession.Load();
                var defaultUser = existing?.Email ?? "";

                var userInput = settings.Username;
                if (string.IsNullOrWhiteSpace(userInput))
                {
                    userInput = AnsiConsole.Prompt(
                        new TextPrompt<string>("Username / E-Mail:")
                            .DefaultValue(defaultUser)
                            .AllowEmpty());
                }
                if (string.IsNullOrWhiteSpace(userInput))
                {
                    AnsiConsole.MarkupLine("[red]✘[/] Username darf nicht leer sein.");
                    return 1;
                }
                var username = EasyVereinLogin.NormalizeUsername(userInput, settings.OrgShort);
                if (username != userInput.Trim())
                    AnsiConsole.MarkupLine($"[grey]Username normalisiert:[/] {Markup.Escape(username)}");

                var password = settings.Password;
                if (string.IsNullOrEmpty(password))
                    password = AnsiConsole.Prompt(new TextPrompt<string>("Passwort:").Secret());

                EasyVereinTokenResponse resp;
                try
                {
                    resp = await EasyVereinClient.GetTokenAsync(username, password, settings.TwoFA, cancellationToken: cancellationToken);
                }
                catch (Exception ex) when (settings.TwoFA is null && IstVielleicht2FAFehler(ex))
                {
                    // Einmalig 2FA nachfragen und erneut versuchen
                    var code = AnsiConsole.Prompt(new TextPrompt<string>("2FA-Code:").AllowEmpty());
                    resp = await EasyVereinClient.GetTokenAsync(username, password, code, cancellationToken: cancellationToken);
                }

                if (resp.Needs2FA && string.IsNullOrWhiteSpace(settings.TwoFA))
                {
                    AnsiConsole.MarkupLine("[yellow]2FA erforderlich.[/]");
                    var code = AnsiConsole.Prompt(new TextPrompt<string>("2FA-Code:").AllowEmpty());
                    resp = await EasyVereinClient.GetTokenAsync(username, password, code, cancellationToken: cancellationToken);
                }

                var session = EasyVereinSession.FromTokenResponse(resp);
                session.Save();

                var tage = (session.ExpiresAtUtc - DateTime.UtcNow).TotalDays;
                AnsiConsole.MarkupLine($"[green]✔ Angemeldet:[/] {Markup.Escape(session.Email.Length > 0 ? session.Email : username)} " +
                    $"[grey](gültig ca. {tage:N0} Tage bis {session.ExpiresAtUtc:dd.MM.yyyy})[/]");
                AnsiConsole.MarkupLine($"[grey]Session:[/] {Markup.Escape(EasyVereinSession.SessionPath())}");
                AnsiConsole.MarkupLine("[grey]Ab jetzt nutzen alle Befehle dieses Token (außer --easyverein-token / EASYVEREIN_TOKEN ist gesetzt). Refresh via 'refresh'.[/]");
                return 0;
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]✘ Login fehlgeschlagen:[/] {Markup.Escape(ex.Message)}");
                return 1;
            }
        }

        private static bool IstVielleicht2FAFehler(Exception ex) =>
            ex.Message.Contains("401", StringComparison.Ordinal) ||
            ex.Message.Contains("400", StringComparison.Ordinal) ||
            ex.Message.Contains("2FA", StringComparison.OrdinalIgnoreCase);
    }

    public sealed class LogoutCommand : AsyncCommand<CommandSettings>
    {
        protected override Task<int> ExecuteAsync(CommandContext context, CommandSettings settings,
            CancellationToken cancellationToken)
        {
            EasyVereinSession.Clear();
            AnsiConsole.MarkupLine("[green]✔ Abgemeldet.[/] Session-Datei gelöscht.");
            return Task.FromResult(0);
        }
    }

    public sealed class AuthStatusSettings : CommandSettings
    {
        [CommandOption("--refresh")]
        [Description("Token jetzt per GET refresh-token auffrischen (nur wenn fällig, sonst no-op).")]
        public bool Refresh { get; set; }
    }

    /// <summary>
    /// Zeigt Session-Status (E-Mail, Ablauf). Mit --refresh wird der Token bei Bedarf erneuert.
    /// </summary>
    public sealed class AuthStatusCommand : AsyncCommand<AuthStatusSettings>
    {
        protected override async Task<int> ExecuteAsync(CommandContext context, AuthStatusSettings settings,
            CancellationToken cancellationToken)
        {
            var session = EasyVereinSession.Load();
            var env = Environment.GetEnvironmentVariable("EASYVEREIN_TOKEN");
            if (!string.IsNullOrWhiteSpace(env))
                AnsiConsole.MarkupLine("[yellow]ℹ EASYVEREIN_TOKEN gesetzt – hat Vorrang vor der Session.[/]");

            if (session is null || string.IsNullOrWhiteSpace(session.Token))
            {
                AnsiConsole.MarkupLine("[yellow]Nicht angemeldet.[/] Nutze: [bold]dotnet run -- login[/]");
                return 1;
            }

            var rest = session.ExpiresAtUtc - DateTime.UtcNow;
            AnsiConsole.MarkupLine($"[grey]User:[/] {Markup.Escape(session.Email)}  [grey]gültig bis:[/] {session.ExpiresAtUtc:dd.MM.yyyy HH:mm} UTC " +
                $"[grey](noch {rest.TotalDays:N1} Tage)[/]{(session.IsExpired() ? " [red]ABGELAUFEN[/]" : "")}");

            if (settings.Refresh)
            {
                try
                {
                    using var client = new EasyVereinClient(session.Token);
                    var resp = await client.RefreshTokenAsync(cancellationToken);
                    if (!string.IsNullOrWhiteSpace(resp.Token) && resp.Token != session.Token)
                    {
                        var neu = EasyVereinSession.FromTokenResponse(
                            new EasyVereinTokenResponse(session.UserId, session.Email, false, resp.ExpiresIn > 0 ? resp.ExpiresIn : 30 * 86400, resp.Token));
                        neu.Save();
                        AnsiConsole.MarkupLine("[green]✔ Token erneuert.[/]");
                    }
                    else
                    {
                        AnsiConsole.MarkupLine("[green]✔ Token aktuell – kein Refresh nötig.[/]");
                    }
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[red]✘ Refresh fehlgeschlagen:[/] {Markup.Escape(ex.Message)}");
                    return 1;
                }
            }
            else
            {
                AnsiConsole.MarkupLine("[grey]Tipp: Token-Auffrischung mit:[/] dotnet run -- auth-status --refresh");
            }
            return 0;
        }
    }
}
