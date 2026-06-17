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
            });

            return await app.RunAsync(args);
        }
    }
}