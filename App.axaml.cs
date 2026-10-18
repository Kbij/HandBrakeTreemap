using System;
using System.Threading.Tasks;
using Avalonia.Threading;
using HandBrakeTreemap.Services;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using HandBrakeTreemap.ViewModels;
using HandBrakeTreemap.Views;

namespace HandBrakeTreemap;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // Make sure no error disappears silently: log everything that escapes.
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Log.Error("Onverwachte fout", e.Exception);
            e.Handled = true; // keep the app running
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Error("Onverwachte fout (achtergrond): " + e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("Onverwachte fout (taak)", e.Exception);
            e.SetObserved();
        };

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainViewModel()
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
