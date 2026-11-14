namespace Albiqo.Transfer.Windows;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--smoke-test")
        {
            var output = args.Length > 1 ? args[1] : "ui-proof";
            try
            {
                ApplicationConfiguration.Initialize();
                return WindowsSmokeTest.Run(output);
            }
            catch (Exception error) { return WindowsSmokeTest.RecordStartupFailure(output, error); }
        }
        ApplicationConfiguration.Initialize();
        Application.Run(new MainWindow());
        return 0;
    }
}
