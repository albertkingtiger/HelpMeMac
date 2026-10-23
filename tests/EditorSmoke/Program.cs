using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using HelpMe;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        var frame = new DispatcherFrame();
        Exception? failure = null;
        app.DispatcherUnhandledException += (_, args) => { failure = args.Exception; args.Handled = true; frame.Continue = false; };
        app.Dispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                var window = new MainWindow();
                var settings = (HelpMe.Services.AppSettings)typeof(MainWindow).GetField("settings", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                settings.CaptionMethod = HelpMe.Services.CaptionMethod.Whisper;
                var source = (TextBox)window.FindName("PcText");
                var target = (TextBox)window.FindName("TranslationText");
                var toggle = (ToggleButton)window.FindName("TranslationToggle");
                if (source.IsReadOnly || source.FontSize != 12) throw new Exception("Expected editable 12px input.");
                source.Text = "Hello. How are you today?";
                toggle.IsChecked = true;
                toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await Until(() => target.Text.Any(IsKorean));
                var first = target.Text;
                source.Text = "Thank you very much.";
                await Until(() => target.Text != first && target.Text.Any(IsKorean));
                Console.WriteLine("PASS: typed input and corrections drive real offline Korean translation");
                var mirror = typeof(MainWindow).GetMethod("UpdateWindowsCaptionText", BindingFlags.Instance | BindingFlags.NonPublic)!;
                mirror.Invoke(window, new object[] { "Hello." });
                mirror.Invoke(window, new object[] { "Hello world." });
                if (source.Text != "Hello world.") throw new Exception("Windows snapshots must replace text without adding punctuation.");
                typeof(MainWindow).GetMethod("TranslationFormat_Click", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object?[] { null, new RoutedEventArgs() });
                if (target.Text != "" || source.Text != "Hello world.") throw new Exception("Translation Format must preserve source.");
                var microphone = (TextBox)window.FindName("MicText");
                microphone.Text = "Microphone sample";
                typeof(MainWindow).GetMethod("MicFormat_Click", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object?[] { null, new RoutedEventArgs() });
                if (microphone.Text != "") throw new Exception("Microphone Format must clear text.");
                Console.WriteLine("PASS: exact Windows snapshots and independent panel Format buttons");
                typeof(MainWindow).GetMethod("Format_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(window, new object?[] { null, new RoutedEventArgs() });
                if (source.Text.Length != 0 || target.Text.Length != 0) throw new Exception("Format must clear the input and its translation.");
                var append = typeof(MainWindow).GetMethod("Append", BindingFlags.Static | BindingFlags.NonPublic)!;
                append.Invoke(null, new object[] { source, "Hello" });
                append.Invoke(null, new object[] { source, "world" });
                if (source.Text != "Hello world") throw new Exception("Caption chunks must continue with a space, without newlines.");
                Console.WriteLine("PASS: Format clears and caption chunks continue on the same line");
                toggle.IsChecked = false;
                toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await Until(() => toggle.IsEnabled);
                var closed = false;
                window.Closed += (_, _) => closed = true;
                window.Close();
                await Until(() => closed);
            }
            catch (Exception error) { failure = error; }
            finally { frame.Continue = false; }
        }));
        Dispatcher.PushFrame(frame);
        app.Shutdown();
        if (failure is null) return 0;
        Console.Error.WriteLine(failure);
        return 1;
    }

    private static bool IsKorean(char value) => value is >= '\uac00' and <= '\ud7a3';
    private static async Task Until(Func<bool> predicate)
    {
        var timer = Stopwatch.StartNew();
        while (!predicate())
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException("Editor operation timed out.");
            await Task.Delay(50);
        }
    }
}
