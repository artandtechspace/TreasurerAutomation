using System.Diagnostics;
using TreasurerAutomation.Services;
using Xunit;

namespace TreasurerAutomation.Tests
{
    /// <summary>
    /// Guard gegen kaputte Typst-Vorlage: baut ein Muster-Mandat mit der echten
    /// vorlage.typ und kompiliert es. Wird übersprungen, wenn typst fehlt.
    /// </summary>
    public sealed class SepaMandatTemplateTests
    {
        private static string? FindeVorlage()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var kandidat = Path.Combine(dir.FullName, "vorlagen", "sepa-mandat", "vorlage.typ");
                if (File.Exists(kandidat)) return kandidat;
                dir = dir.Parent;
            }
            return null;
        }

        [Fact]
        public async Task Vorlage_KompiliertMitMusterdaten()
        {
            var vorlage = FindeVorlage();
            if (vorlage == null) Assert.Skip("vorlage.typ nicht gefunden (kein Repo-Checkout).");
            var tmp = Directory.CreateTempSubdirectory("sepa-test");
            try
            {
                File.Copy(vorlage, Path.Combine(tmp.FullName, "vorlage.typ"));
                var daten = new SepaMandatDaten(
                    "ARTandTECH.space e.V.", "Lindenstraße 11", "48431 Rheine", "Deutschland",
                    "DE00ZZZ00000000000", "EV-1-2026", true,
                    "Max Mustermann", "Musterstraße 12", "48431 Rheine", "Deutschland",
                    "", "DE75512108001245126199", "Rheine, 22.09.2026", null);
                var typPfad = Path.Combine(tmp.FullName, "mandat.typ");
                var pdfPfad = Path.Combine(tmp.FullName, "mandat.pdf");
                await File.WriteAllTextAsync(typPfad, SepaMandatFileBuilder.Build(daten));

                var psi = new ProcessStartInfo("typst", $"compile \"{typPfad}\" \"{pdfPfad}\"")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                string ausgabe;
                int code;
                try
                {
                    using var prozess = Process.Start(psi);
                    if (prozess == null) Assert.Skip("typst konnte nicht gestartet werden.");
                    ausgabe = await prozess.StandardOutput.ReadToEndAsync()
                        + await prozess.StandardError.ReadToEndAsync();
                    await prozess.WaitForExitAsync();
                    code = prozess.ExitCode;
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    Assert.Skip("typst nicht im PATH.");
                    return;
                }
                Assert.True(code == 0, $"typst compile fehlgeschlagen: {ausgabe}");
                Assert.True(File.Exists(pdfPfad));
            }
            finally
            {
                try { Directory.Delete(tmp.FullName, recursive: true); } catch { /* best effort */ }
            }
        }
    }
}
