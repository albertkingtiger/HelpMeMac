using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Interop;
using HelpMe;
using HelpMe.Services;

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
                new WindowInteropHelper(window).EnsureHandle();
                var registerRestore = typeof(MainWindow).GetMethod("RegisterRestoreShortcut", BindingFlags.NonPublic | BindingFlags.Instance)!;
                if (registerRestore.Invoke(window, new object[] { "Ctrl+Alt+Shift+F12" }) is not true)
                    throw new Exception("Global restore shortcut did not register.");
                window.Hide();
                var registered = typeof(MainWindow).GetField("restoreHotkeyRegistered", BindingFlags.NonPublic | BindingFlags.Instance)!;
                if (registered.GetValue(window) is not true)
                    throw new Exception("Global restore shortcut was lost when the window was hidden.");
                Console.WriteLine("PASS: global restore shortcut stays registered while hidden");
                var translation = (TextBox)window.FindName("TranslationText");
                if (translation.IsReadOnlyCaretVisible) throw new Exception("Section 02 must not show a caret.");
                AppTheme.Apply("Light");
                if (((System.Windows.Media.SolidColorBrush)window.Background).Color !=
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#F3F3F3"))
                    throw new Exception("Light theme did not update the main window.");
                AppTheme.Apply("Dark");
                Console.WriteLine("PASS: section 02 hides its caret and themes update live");
                var completedCaptions = new List<string>();
                var provisionalCaptions = new List<string>();
                var windowsSource = new WindowsLiveCaptionsSession(completedCaptions.Add, _ => { }, provisionalCaptions.Add);
                var snapshot = typeof(WindowsLiveCaptionsSession).GetMethod("ProcessSnapshot", BindingFlags.NonPublic | BindingFlags.Instance)!;
                snapshot.Invoke(windowsSource, new object[] { "And." });
                snapshot.Invoke(windowsSource, new object[] { "And apart." });
                snapshot.Invoke(windowsSource, new object[] { "And apart from that." });
                snapshot.Invoke(windowsSource, new object[] { "And apart from that of my core stack." });
                if (completedCaptions.Count != 0 || provisionalCaptions[^1] != "And apart from that of my core stack.")
                    throw new Exception("Growing Windows captions must replace the live fragment without repeated commits.");
                snapshot.Invoke(windowsSource, new object[] { "What?" });
                if (completedCaptions.Count != 1 || completedCaptions[0] != "And apart from that of my core stack." ||
                    provisionalCaptions[^1] != "What?")
                    throw new Exception("A new utterance must commit the prior Windows caption once.");
                snapshot.Invoke(windowsSource, new object[] { "What is your experience?" });
                snapshot.Invoke(windowsSource, new object[] { "your experience? Next question" });
                if (completedCaptions.Count != 2 || completedCaptions[1] != "What is" ||
                    provisionalCaptions[^1] != "your experience? Next question")
                    throw new Exception("A rolling caption window must keep overlapping words once.");
                await windowsSource.DisposeAsync();
                Console.WriteLine("PASS: Windows caption revisions replace the live fragment and commit once");
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

                // Collapsing must free the section's space, keep its header as the
                // way back, and survive a round trip to the user's dragged sizes.
                var translationToggle = (ToggleButton)window.FindName("TranslationCollapseToggle");
                var answerToggle = (ToggleButton)window.FindName("AnswerCollapseToggle");
                var translationRow = (RowDefinition)window.FindName("TranslationRow");
                var splitterRow = (RowDefinition)window.FindName("TranslationSplitterRow");
                var answerColumn = (ColumnDefinition)window.FindName("AnswerColumn");
                var promptRow = (RowDefinition)window.FindName("PromptRow");
                var answerActions = (StackPanel)window.FindName("AnswerHeaderActions");
                var collapseTranslation = typeof(MainWindow).GetMethod("TranslationCollapse_Click", BindingFlags.NonPublic | BindingFlags.Instance)!;
                var collapseAnswer = typeof(MainWindow).GetMethod("AnswerCollapse_Click", BindingFlags.NonPublic | BindingFlags.Instance)!;
                void ClickCollapse(MethodInfo handler, ToggleButton toggle)
                {
                    toggle.IsChecked = toggle.IsChecked != true;
                    handler.Invoke(window, new object?[] { null, new RoutedEventArgs() });
                }
                translationRow.Height = new GridLength(3, GridUnitType.Star);
                ClickCollapse(collapseTranslation, translationToggle);
                if (translationRow.Height != GridLength.Auto || splitterRow.Height.Value != 0 ||
                    translation.Visibility != Visibility.Collapsed)
                    throw new Exception("Collapsing 02 must release its row and hide only its text area.");
                ClickCollapse(collapseTranslation, translationToggle);
                if (translationRow.Height.Value != 3 || !translationRow.Height.IsStar ||
                    translation.Visibility != Visibility.Visible)
                    throw new Exception("Expanding 02 must restore the dragged row size.");

                ClickCollapse(collapseAnswer, answerToggle);
                if (answerColumn.Width != GridLength.Auto || promptRow.Height.Value != 0 ||
                    answer.Visibility != Visibility.Collapsed || answerActions.Visibility != Visibility.Collapsed)
                    throw new Exception("Collapsing 04 must release its column and narrow the leftover header.");
                ClickCollapse(collapseAnswer, answerToggle);
                if (!answerColumn.Width.IsStar || promptRow.Height.Value != 100 ||
                    answer.Visibility != Visibility.Visible || answerActions.Visibility != Visibility.Visible)
                    throw new Exception("Expanding 04 must restore the prompt, answer and header actions.");
                Console.WriteLine("PASS: sections 02 and 04 collapse to their headers and restore dragged sizes");

                var tryToggle = typeof(MainWindow).GetMethod("TryToggleSection", BindingFlags.NonPublic | BindingFlags.Instance)!;
                bool Press(Key key, ModifierKeys modifiers) => (bool)tryToggle.Invoke(window, new object[] { key, modifiers })!;
                if (!Press(Key.D2, ModifierKeys.Control) || translationToggle.IsChecked != true || translation.Visibility != Visibility.Collapsed)
                    throw new Exception("Ctrl+2 must collapse section 02.");
                if (!Press(Key.NumPad2, ModifierKeys.Control) || translationToggle.IsChecked != false)
                    throw new Exception("Ctrl+2 must expand section 02 again, from the numeric keypad too.");
                if (!Press(Key.D4, ModifierKeys.Control) || answerToggle.IsChecked != true || answer.Visibility != Visibility.Collapsed)
                    throw new Exception("Ctrl+4 must collapse section 04.");
                if (!Press(Key.D4, ModifierKeys.Control) || answerToggle.IsChecked != false)
                    throw new Exception("Ctrl+4 must expand section 04 again.");
                if (Press(Key.D3, ModifierKeys.Control) || Press(Key.D2, ModifierKeys.Shift) ||
                    Press(Key.D2, ModifierKeys.Control | ModifierKeys.Shift) || Press(Key.D2, ModifierKeys.None))
                    throw new Exception("Only plain Ctrl+2 and Ctrl+4 may toggle a section.");
                Console.WriteLine("PASS: Ctrl+2 and Ctrl+4 toggle their sections and ignore other combinations");

                // Send Telegram forwards the answer, or just the part selected in it.
                var telegramMessage = typeof(MainWindow).GetMethod("GetTelegramMessage", BindingFlags.NonPublic | BindingFlags.Instance)!;
                string Message() => (string)telegramMessage.Invoke(window, new object[] { false })!;
                string AllMessage() => (string)telegramMessage.Invoke(window, new object[] { true })!;
                answer.Text = "  First answer line.\nSecond answer line.  ";
                answer.Select(0, 0);
                if (Message() != "First answer line.\nSecond answer line.")
                    throw new Exception("Without a selection the whole answer is sent, trimmed.");
                answer.Select(answer.Text.IndexOf("Second", StringComparison.Ordinal), "Second answer line.".Length);
                if (Message() != "Second answer line.")
                    throw new Exception("A selection inside the answer must be sent on its own.");
                // All Send ignores the selection that Send Telegram would honour.
                if (AllMessage() != "First answer line.\nSecond answer line.")
                    throw new Exception("All Send must send the whole answer despite a selection.");
                // A stray whitespace drag must not turn into an empty message.
                answer.Select(answer.Text.IndexOf('\n'), 1);
                if (Message() != "First answer line.\nSecond answer line.")
                    throw new Exception("A whitespace-only selection must fall back to the whole answer.");
                answer.Clear();
                answer.Select(0, 0);
                if (Message().Length != 0 || AllMessage().Length != 0)
                    throw new Exception("An empty answer must produce no message from either button.");
                var telegramButton = (Button)window.FindName("TelegramSendButton");
                var telegramBar = (Border)window.FindName("TelegramBar");
                var telegramAllButton = (Button)window.FindName("TelegramSendAllButton");
                if (telegramButton.Content as string != "Send Selection" || telegramAllButton.Content as string != "All Send")
                    throw new Exception("Both Telegram buttons need their resting labels.");
                ClickCollapse(collapseAnswer, answerToggle);
                if (telegramBar.Visibility != Visibility.Collapsed) throw new Exception("Collapsing 04 must hide its Telegram bar.");
                ClickCollapse(collapseAnswer, answerToggle);
                if (telegramBar.Visibility != Visibility.Visible) throw new Exception("Expanding 04 must restore its Telegram bar.");
                Console.WriteLine("PASS: Telegram send honours the selection, All Send ignores it, and both follow the collapse");
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

