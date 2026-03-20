namespace AmazTool;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        try
        {
            Utils.Log($"AmazTool start. Args: {string.Join(" | ", args)}");

            // If no arguments are provided, display usage guidelines and exit
            if (args.Length == 0)
            {
                ShowHelp();
                return;
            }

            // Log all arguments for debugging purposes
            foreach (var arg in args)
            {
                Console.WriteLine(arg);
            }

            // Parse command based on first argument
            switch (args[0].ToLowerInvariant())
            {
                case "upgrade" when args.Length >= 3:
                    Utils.SetTargetStartupPath(Uri.UnescapeDataString(args[1]));
                    HandleUpgrade(Uri.UnescapeDataString(args[2]));
                    break;

                case "rebootas":
                    // Handle application restart
                    HandleRebootAsync();
                    break;

                case "help":
                case "--help":
                case "-h":
                case "/?":
                    // Display help information
                    ShowHelp();
                    break;

                default:
                    // Default behavior: handle as upgrade data
                    // Maintain backward compatibility with existing usage pattern
                    var argData = Uri.UnescapeDataString(string.Join(" ", args));
                    HandleUpgrade(argData);
                    break;
            }
        }
        catch (Exception ex)
        {
            // Global exception handling
            Utils.Log($"Fatal updater error: {ex}");
            Console.WriteLine($"An error occurred: {ex.Message}");
            Console.WriteLine("Press any key to exit...");
            Console.ReadKey();
        }
    }

    /// <summary>
    /// Display help information and usage guidelines
    /// </summary>
    private static void ShowHelp()
    {
        Console.WriteLine(Resx.Resource.Guidelines);
        Console.WriteLine("Available commands:");
        Console.WriteLine("  rebootas             - Restart the application");
        Console.WriteLine("  help                 - Display this help information");
        Thread.Sleep(5000);
    }

    /// <summary>
    /// Handle application restart
    /// </summary>
    private static void HandleRebootAsync()
    {
        Utils.Log("Restarting application...");
        Thread.Sleep(1000);
        if (!Utils.StartApp())
        {
            Utils.Log("Failed to restart application.");
        }
    }

    /// <summary>
    /// Handle application upgrade with the provided data
    /// </summary>
    /// <param name="upgradeData">Data for the upgrade process</param>
    private static void HandleUpgrade(string upgradeData)
    {
        Utils.Log($"Upgrading application with package: {upgradeData}");
        UpgradeApp.Upgrade(upgradeData);
    }
}
