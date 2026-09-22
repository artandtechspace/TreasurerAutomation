using Spectre.Console;

namespace TreasurerAutomation.Commands
{
    /// <summary>Geteilte Konsolen-Bausteine (einheitlicher Command-Kopf).</summary>
    internal static class ConsoleHelper
    {
        public static void PrintHeader(string titel)
        {
            AnsiConsole.Write(new Rule($"[yellow]{titel}[/]").RuleStyle("grey").LeftJustified());
            Console.WriteLine();
        }
    }
}
