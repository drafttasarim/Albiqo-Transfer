namespace Albiqo.Transfer.Windows;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.Length > 0 && args[0] == "--smoke-test")
            return WindowsSmokeTest.Run(args.Length > 1 ? args[1] : "ui-proof");
        Application.Run(new MainWindow());
        return 0;
    }
}
