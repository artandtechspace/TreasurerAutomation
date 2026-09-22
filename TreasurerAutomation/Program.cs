using System.Threading.Tasks;
using Spectre.Console.Cli;
using TreasurerAutomation.Commands;

namespace TreasurerAutomation
{
    /// <summary>
    /// The main entry point for the TreasurerAutomation CLI runner.
    /// This routes execution to the selected automation command using Spectre.Console.
    /// </summary>
    internal class Program
    {
        public static async Task<int> Main(string[] args)
        {
            var app = new CommandApp();
            app.Configure(config =>
            {
                config.SetApplicationName("dotnet run --");
                config.AddCommand<SumUpSyncCommand>("sumup-sync")
                      .WithDescription("Synchronizes successful transactions and receipts from SumUp to easyVerein bookings.");
                config.AddCommand<EasyVereinTestCommand>("easyverein-test")
                      .WithDescription("Tests the connection, document upload and booking association with easyVerein.");
                config.AddCommand<SpendenquittungCommand>("spendenquittung")
                      .WithDescription("Interaktiver Wizard für Zuwendungsbestätigungen (Typst-Vorlage ausfüllen + PDF erzeugen).");
                config.AddCommand<EasyVereinProbeCommand>("ev-probe")
                      .WithDescription("Reine Lese-Probe der easyVerein API (Endpunkt-Felder anzeigen, Vorbereitung Kassenprüfung).");
                config.AddCommand<MemberAuditCommand>("member-audit")
                      .WithDescription("Prüft alle Mitglieder (Zustimmungen, Unterlagen, Stammdaten, SEPA-Readiness) read-only für den Beitragseinzug.");
                config.AddCommand<MemberFixCommand>("member-fix")
                      .WithDescription("Interaktiver Fix-Wizard für Audit-Blocker (dry-run ohne --apply, SEPA-Mandate via Typst).");
                config.AddCommand<LoginCommand>("login")
                      .WithDescription("Interaktiver Login (Username/Passwort/2FA) via POST get-token, speichert Session für alle Befehle.");
                config.AddCommand<LogoutCommand>("logout")
                      .WithDescription("Löscht die gespeicherte easyVerein-Session.");
                config.AddCommand<AuthStatusCommand>("auth-status")
                      .WithDescription("Zeigt Login-Status, optional mit Token-Refresh (--refresh).");
            });

            return await app.RunAsync(args);
        }
    }
}