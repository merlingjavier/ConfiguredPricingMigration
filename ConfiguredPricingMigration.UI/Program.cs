namespace ConfiguredPricingMigration.UI;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        var loadedEnvironmentFile = EnvironmentFile.LoadNearest();

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(loadedEnvironmentFile));
    }
}
