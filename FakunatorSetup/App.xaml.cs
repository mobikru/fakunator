using System.Windows;

namespace FakunatorSetup;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Флаг --uninstall — режим удаления. Хендлится в MainWindow.
        Uninstall = e.Args.Length > 0 && e.Args[0].Equals("--uninstall", System.StringComparison.OrdinalIgnoreCase);
    }
    public static bool Uninstall { get; private set; }
}
