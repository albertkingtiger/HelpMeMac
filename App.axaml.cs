using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using HelpMe.Services;

namespace HelpMe;

public partial class App : Application
{
    private readonly bool launchMainWindow = true;
    public MainWindow? Window { get; private set; }

    public App() { }
    public App(bool launchMainWindow) => this.launchMainWindow = launchMainWindow;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && launchMainWindow)
        {
            // Help Me! lives in the menu bar: the window going away must not end
            // the process, and Exit in the menu is what shuts it down.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            // MainWindow is deliberately left unset. Avalonia shows whatever is
            // assigned to it as soon as the lifetime starts, and this app has to
            // come up hidden with its capture already running.
            Window = new MainWindow();
            Window.StartInBackground();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
