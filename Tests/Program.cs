namespace PlaycastCompanion.Tests;

internal static class Program
{
    private static async Task<int> Main()
    {
        try
        {
            SessionLogContractTests.Run();
            DiagnosticClientIdentityContractTests.Run();
            await DiagnosticFeedContractTests.RunAsync();
            await SessionLifecycleContractTests.RunAsync();
            GameInventoryContractTests.Run();
            await LogAnalysisContractTests.RunAsync();
            SettingsFooterContractTests.Run();
            Console.WriteLine("PASS: session storage, redaction, diagnostic protocol/pipe, lifecycle, game inventory, log analysis, and settings footer UI contracts.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
