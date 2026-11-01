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
            // Show only the textbox: MainWindow.Loaded must not start Chrome or audio.
            var window = new MainWindow();
            var source = (TextBox)window.FindName("PcText");
            ((Grid)source.Parent).Children.Remove(source);
            var host = new Window { Content = source, Width = 400, Height = 220,
                Left = -16000, Top = -16000, ShowInTaskbar = false };
            try
            {
                host.Show();
                host.Activate();
                source.Focus();
                source.Text = string.Join(Environment.NewLine,
                    Enumerable.Range(1, 100).Select(i => $"Caption line {i}: some previous words.")) + Environment.NewLine;
                source.Select(40, 0);
                await Settle();
                if (!source.IsKeyboardFocusWithin) throw new Exception("Test requires an active caret.");
                if (!source.IsReadOnly || !source.IsReadOnlyCaretVisible)
                    throw new Exception("PC captions must stay read-only with a visible caret.");

                source.ScrollToVerticalOffset(200);
                await Settle();
                var offset = source.VerticalOffset;
                Invoke(window, "SetLiveCaption", "An interim caption is being recognized");
                await Settle();
                AssertView(source, offset, 40, 0);
                Invoke(window, "SetLiveCaption", "An interim caption is corrected and extended with more words");
                Invoke(window, "CommitLiveCaption", "A completed caption with a paragraph break.");
                await Settle();
                AssertView(source, offset, 40, 0);
                Console.WriteLine("PASS: focused caret and scroll-up viewport survive interim corrections and final paragraphs");

                var finalSnapshot = (string)typeof(MainWindow).GetField("pcFinalCaptionText", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
                Invoke(window, "SetLiveCaption", "This preview must not be sent for translation.");
                if ((string)typeof(MainWindow).GetField("pcFinalCaptionText", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)! != finalSnapshot)
                    throw new Exception("An interim caption changed the finalized translation snapshot.");
                Console.WriteLine("PASS: translation snapshot changes only after a final caption");

                source.ScrollToEnd();
                await Settle();
                var previousExtent = source.ExtentHeight;
                Invoke(window, "SetLiveCaption", string.Join(Environment.NewLine, Enumerable.Repeat("More speech.", 20)));
                await Settle();
                AssertBottom(source);
                AssertSelection(source, 40, 0);
                if (source.ExtentHeight <= previousExtent) throw new Exception("Test must add scrollable text.");
                Console.WriteLine("PASS: returning to bottom resumes following without moving the caret");

                source.Select(100, 30);
                source.ScrollToVerticalOffset(300);
                await Settle();
                offset = source.VerticalOffset;
                Invoke(window, "SetLiveCaption", "Short revised preview");
                await Settle();
                AssertView(source, offset, 100, 30);
                Invoke(window, "CommitLiveCaption", "A final caption.");
                await Settle();
                AssertView(source, offset, 100, 30);
                Invoke(window, "Append", source, "Another caption.");
                await Settle();
                AssertView(source, offset, 100, 30);
                Console.WriteLine("PASS: selected text is preserved during replacement, commit and append");

                source.ScrollToEnd();
                await Settle();
                source.ScrollToVerticalOffset(source.ExtentHeight - source.ViewportHeight - 20);
                await Settle();
                Invoke(window, "CommitLiveCaption", "Near the bottom should keep following.");
                await Settle();
                AssertBottom(source);
                Invoke(window, "SetLiveCaption", "Caption before the user scrolls");
                source.ScrollToVerticalOffset(450);
                await Settle();
                offset = source.VerticalOffset;
                AssertView(source, 450, 100, 30);
                Invoke(window, "SetLiveCaption", "Caption after the user scrolls");
                await Settle();
                AssertView(source, offset, 100, 30);
                Console.WriteLine("PASS: near-bottom following and immediate user scrolling remain independent of caret focus");

            }
            catch (Exception error) { failure = error; }
            finally
            {
                host.Close();
                window.Close();
                frame.Continue = false;
            }
        }));
        Dispatcher.PushFrame(frame);
        app.Shutdown();
        if (failure is null) return 0;
        Console.Error.WriteLine(failure);
        return 1;
    }

    private static void Invoke(MainWindow window, string method, params object[] args)
        => typeof(MainWindow).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, args);

    private static async Task Settle()
    {
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }

    private static void AssertView(TextBox box, double offset, int selectionStart, int selectionLength)
    {
        if (Math.Abs(box.VerticalOffset - offset) > 1)
            throw new Exception($"Viewport jumped from {offset} to {box.VerticalOffset}.");
        AssertSelection(box, selectionStart, selectionLength);
    }

    private static void AssertSelection(TextBox box, int start, int length)
    {
        if (box.SelectionStart != start || box.SelectionLength != length)
            throw new Exception($"Selection changed from ({start}, {length}) to ({box.SelectionStart}, {box.SelectionLength}).");
    }

    private static void AssertBottom(TextBox box)
    {
        if (box.ExtentHeight - box.ViewportHeight - box.VerticalOffset > 1)
            throw new Exception($"Expected bottom; distance is {box.ExtentHeight - box.ViewportHeight - box.VerticalOffset}.");
    }
}
