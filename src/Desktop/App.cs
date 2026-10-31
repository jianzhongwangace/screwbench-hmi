using System.Windows;

namespace ScrewBench.Desktop;
public sealed class App : Application
{
    [STAThread]
    public static void Main(string[] args)
    {
        var application = new App();
        application.Run(new MainWindow(args.Contains("--smoke"), args.Contains("--demo"), args.Contains("--demo-check")));
    }
}
