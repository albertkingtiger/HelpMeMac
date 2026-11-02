using Avalonia;
using Avalonia.Controls;
using HelpMe.Services;

namespace HelpMe;

internal static class Program
{
    // Avalonia needs its own entry point rather than the generated one a WPF
    // application had, and the single-instance claim happens before any of it:
    // a second launch should hand over to the copy already in the menu bar
    // without creating a window or touching the audio devices.
    [STAThread]
    public static int Main(string[] args)
    {
        if (!SingleInstance.TryClaim())
        {
            SingleInstance.RequestShow();
            return 0;
        }
        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
        }
        finally
        {
            SingleInstance.Release();
        }
    }

    // Also used by the Avalonia designer and by the smoke tests.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
