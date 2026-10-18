using System;
using Avalonia;

namespace HandBrakeTreemap;

internal static class Program
{
    // Avalonia configuration; don't use any Avalonia, third-party APIs or SynchronizationContext
    // dependent code before AppMain is called: things aren't initialized yet.
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
