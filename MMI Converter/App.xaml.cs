using System.Windows;
using MmiConverter.Services;

namespace MmiConverter;

public partial class App : Application
{
    private Mutex? instance;

    protected override void OnStartup(StartupEventArgs e)
    {
        instance = new Mutex(true, @"Local\MmiConverter.39c1af09-10b5-4795-bd94-c4666e49ddd5", out bool firstInstance);
        if (!firstInstance)
        {
            Shutdown();
            return;
        }
        base.OnStartup(e);
        ThemeService.LoadAndApply();
        MainWindow = new MainWindow(e.Args);
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { base.OnExit(e); }
        finally { instance?.Dispose(); }
    }
}
