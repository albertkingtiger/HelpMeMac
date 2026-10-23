using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using HelpMe;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var app = new App(launchMainWindow: false) { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        var frame = new DispatcherFrame();
        Exception? failure = null;
        app.Dispatcher.BeginInvoke(new Action(async () =>
        {
            var window = new MainWindow(); // Kept offscreen: no browser or audio is started.
            try
            {
                var source = (TextBox)window.FindName("PcText");
                var promptMethod = typeof(MainWindow).GetMethod("GetAnswerPrompt", BindingFlags.NonPublic | BindingFlags.Instance)!;
                var anchor = typeof(MainWindow).GetField("savedCaptionLocation", BindingFlags.NonPublic | BindingFlags.Instance)!;
                var hasAnchor = typeof(MainWindow).GetField("hasSavedCaptionLocation", BindingFlags.NonPublic | BindingFlags.Instance)!;
                string Prompt() => (string)promptMethod.Invoke(window, null)!;
                source.Text = "First sentence. Second sentence.";
                if (Prompt() != source.Text) throw new Exception("Without a bookmark, send the whole caption.");
                anchor.SetValue(window, 16); hasAnchor.SetValue(window, true);
                if (Prompt() != "Second sentence.") throw new Exception("Saved position must select the suffix.");
                source.Select(0, 15);
                if (Prompt() != "Second sentence.") throw new Exception("Selection must not replace the saved-position suffix.");
                source.Select(0, 0);
                anchor.SetValue(window, source.Text.Length);
                try { Prompt(); throw new Exception("An empty suffix must not send unrelated earlier text."); }
                catch (TargetInvocationException e) when (e.InnerException is InvalidOperationException error && error.Message.Contains("saved starting point")) { }
                source.Clear(); source.Text = "Replacement caption";
                if (Prompt() != "Replacement caption") throw new Exception("Clearing the document must reset its old bookmark.");
                Console.WriteLine("PASS: full caption, saved suffix, selection ignored, empty suffix error and bookmark reset");
                var display = typeof(MainWindow).GetMethod("DisplayAnswer", BindingFlags.NonPublic | BindingFlags.Instance)!;
                var answer = (TextBox)window.FindName("AnswerText");
                var microphone = (TextBox)window.FindName("MicText");
                await Task.Run(() => display.Invoke(window, new object[] { "Streamed answer", 0 }));
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                if (answer.Text != "Streamed answer" || microphone.Text.Length != 0) throw new Exception("AI output must reach only the Answer box.");
                Console.WriteLine("PASS: background response reaches Answer only");
                typeof(MainWindow).GetMethod("AnswerFormat_Click", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, new object?[] { null, new RoutedEventArgs() });
                await Task.Run(() => display.Invoke(window, new object[] { "Stale answer", 0 }));
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                if (answer.Text.Length != 0) throw new Exception("Format must discard queued updates from the previous reply.");
                await Task.Run(() => display.Invoke(window, new object[] { "Next answer", 1 }));
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                if (answer.Text != "Next answer") throw new Exception("The next answer must display after Format.");
                Console.WriteLine("PASS: Format suppresses stale replies while allowing the next reply");
            }
            catch (Exception e) { failure = e; }
            finally { window.Close(); frame.Continue = false; }
        }));
        Dispatcher.PushFrame(frame);
        app.Shutdown();
        if (failure is null) return 0;
        Console.Error.WriteLine(failure);
        return 1;
    }
}

