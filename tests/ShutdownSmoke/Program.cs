using System.Windows;
using System.Windows.Threading;
using HelpMe;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        // The test owns the windows it closes, so startup must not claim the
        // single-instance mutex: Help Me! running in the tray would otherwise
        // turn this process away and shut the Application down under the test.
        var app = new App(launchMainWindow: false) { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        Exception? failure = null;
        app.DispatcherUnhandledException += (_, args) => { failure = args.Exception; args.Handled = true; };
        for (var i = 0; i < 5; i++)
        {
            var frame = new DispatcherFrame();
            var closed = false;
            var window = new MainWindow();
            window.Closed += (_, _) => { closed = true; frame.Continue = false; };
            var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            timeout.Tick += (_, _) => { frame.Continue = false; };
            timeout.Start();
            app.Dispatcher.BeginInvoke(new Action(window.Close));
            Dispatcher.PushFrame(frame);
            timeout.Stop();
            if (failure is not null || !closed)
            {
                Console.Error.WriteLine(failure?.ToString() ?? "Window did not finish closing.");
                return 1;
            }
        }
        Console.WriteLine("PASS: five idle windows closed without reentrant Close or unhandled exceptions");
        app.Shutdown();
        return 0;
    }
}
