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
                // Collapsing saves preferences, and these run against the real
                // settings file, so whatever it says now is put back at the end.
                var collapseSettings = (AppSettings)typeof(MainWindow)
                    .GetField("settings", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
                var savedCollapse = (collapseSettings.TranslationCollapsed, collapseSettings.AnswerCollapsed,
                    collapseSettings.AiPanelCollapsed, collapseSettings.ChatPanelCollapsed);
                var collapseTranslation = typeof(MainWindow).GetMethod("TranslationCollapse_Click", BindingFlags.NonPublic | BindingFlags.Instance)!;
                foreach (var shown in new[] { translationToggle, answerToggle,
                             (ToggleButton)window.FindName("AiPanelCollapseToggle"),
                             (ToggleButton)window.FindName("ChatPanelCollapseToggle") })
                    shown.IsChecked = false;
                typeof(MainWindow).GetMethod("ApplyTranslationCollapsed", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);
                typeof(MainWindow).GetMethod("ApplyAnswerCollapsed", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);
                typeof(MainWindow).GetMethod("ApplyBrowserPanels", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);
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

                // Panel 05 shares this column, so collapsing 04 releases its own
                // row and leaves the column's width to the page below it.
                var answerPanelRow = (RowDefinition)window.FindName("AnswerPanelRow");
                var aiPanelRowShared = (RowDefinition)window.FindName("AiPanelRow");
                var aiWebViewShared = (UIElement)window.FindName("AiWebView");
                ClickCollapse(collapseAnswer, answerToggle);
                if (answerPanelRow.Height != GridLength.Auto || promptRow.Height.Value != 0 ||
                    answer.Visibility != Visibility.Collapsed || answerActions.Visibility != Visibility.Collapsed)
                    throw new Exception("Collapsing 04 must release its row and narrow the leftover header.");
                if (!answerColumn.Width.IsStar || !aiPanelRowShared.Height.IsStar ||
                    aiWebViewShared.Visibility != Visibility.Visible)
                    throw new Exception("Collapsing 04 must leave panel 05 and the column it shares alone.");
                ClickCollapse(collapseAnswer, answerToggle);
                if (!answerColumn.Width.IsStar || promptRow.Height.Value != 100 ||
                    answer.Visibility != Visibility.Visible || answerActions.Visibility != Visibility.Visible)
                    throw new Exception("Expanding 04 must restore the prompt, answer and header actions.");
                // With both of the column's panels collapsed there is nothing
                // left to show, so the width does go back to the rest.
                var aiToggleShared = (ToggleButton)window.FindName("AiPanelCollapseToggle");
                var collapseAi = typeof(MainWindow).GetMethod("AiPanelCollapse_Click", BindingFlags.NonPublic | BindingFlags.Instance)!;
                ClickCollapse(collapseAnswer, answerToggle);
                ClickCollapse(collapseAi, aiToggleShared);
                if (answerColumn.Width != GridLength.Auto)
                    throw new Exception("With 04 and 05 both collapsed the column's width must be released.");
                ClickCollapse(collapseAi, aiToggleShared);
                if (!answerColumn.Width.IsStar)
                    throw new Exception("Showing panel 05 again must take the column's width back.");
                ClickCollapse(collapseAnswer, answerToggle);
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

                // Panels 05 and 06 hold the AI and chat pages.
                var engineSettings = (AppSettings)typeof(MainWindow)
                    .GetField("settings", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
                var applyPanels = typeof(MainWindow).GetMethod("ApplyBrowserPanels", BindingFlags.NonPublic | BindingFlags.Instance)!;
                var browserColumn = (ColumnDefinition)window.FindName("BrowserColumn");
                var aiPanelRow = (RowDefinition)window.FindName("AiPanelRow");
                var aiPanelToggle = (ToggleButton)window.FindName("AiPanelCollapseToggle");
                var chatPanelToggle = (ToggleButton)window.FindName("ChatPanelCollapseToggle");
                var aiWebView = (UIElement)window.FindName("AiWebView");
                var chatWebView = (UIElement)window.FindName("ChatWebView");
                var aiTitle = (TextBlock)window.FindName("AiPanelTitle");
                var chatTitle = (TextBlock)window.FindName("ChatPanelTitle");
                applyPanels.Invoke(window, null);
                if (!browserColumn.Width.IsStar)
                    throw new Exception("The chat panel owns the right column.");
                // Read the settings rather than set them: a collapse below saves
                // preferences, which would write a test value to the real file.
                if (aiTitle.Text != $"05  {engineSettings.AiProvider.ToUpperInvariant()}" ||
                    chatTitle.Text != $"06  {engineSettings.MessengerTarget.ToUpperInvariant()}")
                    throw new Exception("Panel titles must name the provider and destination in use.");
                if (!Press(Key.D5, ModifierKeys.Control) || aiPanelToggle.IsChecked != true ||
                    aiWebView.Visibility != Visibility.Collapsed || aiPanelRow.Height.IsStar)
                    throw new Exception("Ctrl+5 must collapse panel 05 to its header.");
                if (!Press(Key.NumPad5, ModifierKeys.Control) || aiPanelToggle.IsChecked != false ||
                    aiWebView.Visibility != Visibility.Visible || !aiPanelRow.Height.IsStar)
                    throw new Exception("Ctrl+5 must restore panel 05 and the height it had.");
                if (!Press(Key.D6, ModifierKeys.Control) || chatPanelToggle.IsChecked != true ||
                    chatWebView.Visibility != Visibility.Collapsed || browserColumn.Width != GridLength.Auto)
                    throw new Exception("Ctrl+6 must collapse panel 06 and release its column.");
                if (!Press(Key.D6, ModifierKeys.Control) || chatPanelToggle.IsChecked != false ||
                    !browserColumn.Width.IsStar)
                    throw new Exception("Ctrl+6 must expand panel 06 and take the column back.");
                collapseSettings.SaveCollapsedSections(savedCollapse.Item1, savedCollapse.Item2,
                    savedCollapse.Item3, savedCollapse.Item4);
                Console.WriteLine("PASS: panels 05 and 06 carry their titles and follow Ctrl+5 and Ctrl+6");

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

                // Auto-send splits a reply that is still being typed, so a slow
                // answer reaches the reader two sentences at a time.
                string? Next(string text, int sent, int target, out int count)
                    => AnswerChunker.NextChunk(text, sent, target, out count);
                int count;
                // A size is a target to reach, so a chunk grows by whole
                // sentences until it is big enough, and never cuts one short.
                if (Next("First one. Second one. And a third", 0, 15, out count) != "First one. Second one." || count != 2)
                    throw new Exception("A chunk must take whole sentences until it reaches the size.");
                if (Next("First one. Second one. And a third", 0, 5, out count) != "First one." || count != 1)
                    throw new Exception("One sentence past the size is a chunk on its own.");
                if (Next("First one. Second one. And a third", 2, 10, out count) is not null || count != 2)
                    throw new Exception("An unfinished sentence must wait for the rest of the answer.");
                if (Next("Only one finished. Then", 0, 100, out _) is not null)
                    throw new Exception("A chunk short of the size must wait for more of the answer.");
                // A sentence end needs more than a period: these are not ends.
                if (Next("Dr. Smith arrived. Then", 0, 5, out _) != "Dr. Smith arrived.")
                    throw new Exception("A title's period must not end the sentence.");
                if (Next("We use e.g. three models. Next", 0, 5, out _) != "We use e.g. three models.")
                    throw new Exception("An abbreviation's period must not end the sentence.");
                if (Next("It costs 3.50 today. Next", 0, 5, out _) != "It costs 3.50 today.")
                    throw new Exception("A decimal point must not end the sentence.");
                if (Next("See fig. 2 for the shape. Next", 0, 5, out _) != "See fig. 2 for the shape.")
                    throw new Exception("A lower-case continuation must not end the sentence.");
                // Line breaks and list layout survive, because a run of sentences
                // is taken as one slice of the original answer.
                if (Next("Use these:\n- first thing.\n- second thing. Next", 0, 30, out _)
                    != "Use these:\n- first thing.\n- second thing.")
                    throw new Exception("A chunk must keep the answer's own line breaks.");
                // Half of a code block is no use, so an open fence holds the rest back.
                if (Next("Here it is. ```py\nvalue = 1\n", 0, 5, out _) != "Here it is.")
                    throw new Exception("Text before an unclosed fence can still be sent.");
                if (Next("Here it is. ```py\nvalue = 1\n", 1, 5, out _) is not null)
                    throw new Exception("An unclosed code fence must hold back the rest.");
                if (Next("Run this. ```py\nx = 1. Y = 2.\n``` Done now. Next", 1, 5, out _)
                    != "```py\nx = 1. Y = 2.\n``` Done now.")
                    throw new Exception("A finished code block must travel in one piece.");
                if (Next("안녕하세요. 반갑습니다. 그리고", 0, 10, out _)
                    != "안녕하세요. 반갑습니다.")
                    throw new Exception("Korean sentences must split on their periods too.");
                if (AnswerChunker.SentenceCount("First one. Second one. A trailing fragment") != 2)
                    throw new Exception("Ticking auto-send mid-answer must skip the finished sentences on screen.");
                if (AnswerChunker.SentenceCount("") != 0)
                    throw new Exception("An empty answer has no sentences to skip.");
                // The flush at the end takes the trailing fragment with it.
                if (AnswerChunker.RemainingChunk("First one. Second one. A trailing fragment", 1, out count)
                    != "Second one. A trailing fragment" || count != 3)
                    throw new Exception("The final flush must send everything still unsent.");
                // The flush reads the answer as finished text. While it streamed,
                // "First one. Second one. And" let both sentences go out; the
                // finished answer ends at "Second one.", where the streaming
                // rules see only one sentence. Reading it as unfinished here
                // offers the last sentence again, and again, and again.
                if (AnswerChunker.NextChunk("First one. Second one. And", 0, 15, out count) is null || count != 2)
                    throw new Exception("Both sentences go out while the answer is still being written.");
                if (AnswerChunker.RemainingChunk("First one. Second one.", 2, out count) is not null || count != 2)
                    throw new Exception("An answer already sent in full must flush nothing.");
                if (AnswerChunker.RemainingChunk("One. Two. Three.", 2, out _) != "Three.")
                    throw new Exception("The flush must still deliver the sentence the stream held back.");
                // A final sentence has nothing after it to prove it ended, so the
                // flush is what delivers it.
                if (AnswerChunker.RemainingChunk("First one. Second one.", 1, out _) != "Second one.")
                    throw new Exception("The final flush must deliver the closing sentence.");
                if (AnswerChunker.RemainingChunk("", 0, out _) is not null)
                    throw new Exception("An empty answer must flush nothing.");
                Console.WriteLine("PASS: auto-send splits finished sentences and holds back fragments and open code blocks");
                // Auto-send is switched at the bottom of panel 04, not in Settings.
                var autoSendCheck = (CheckBox)window.FindName("AutoSendCheck");
                var autoSendBar = (Border)window.FindName("TelegramBar");
                if (!((System.Windows.Controls.Panel)autoSendBar.Child).Children.Contains(autoSendCheck))
                    throw new Exception("The auto-send checkbox belongs on the panel 04 bar.");
                var autoSendHint = (TextBlock)window.FindName("AutoSendHint");
                var appSettings = (AppSettings)typeof(MainWindow)
                    .GetField("settings", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
                var updateHint = typeof(MainWindow).GetMethod("UpdateAutoSendHint", BindingFlags.NonPublic | BindingFlags.Instance)!;
                var sentCount = typeof(MainWindow).GetField("autoSendSentences", BindingFlags.NonPublic | BindingFlags.Instance)!;
                appSettings.AutoSendAnswer = false;
                updateHint.Invoke(window, null);
                if (autoSendHint.Visibility != Visibility.Collapsed || autoSendCheck.IsChecked == true)
                    throw new Exception("With auto-send off, the box is clear and the hint hidden.");
                appSettings.AutoSendAnswer = true;
                appSettings.AutoSendFirstChars = 100;
                appSettings.AutoSendNextChars = 500;
                updateHint.Invoke(window, null);
                if (autoSendHint.Visibility != Visibility.Visible || !autoSendHint.Text.Contains("100 then 500"))
                    throw new Exception("With auto-send on, the hint must show both chunk sizes.");
                if (autoSendCheck.IsChecked != true)
                    throw new Exception("The box must follow the saved setting.");
                var messageCount = typeof(MainWindow).GetField("autoSendMessages", BindingFlags.NonPublic | BindingFlags.Instance)!;
                messageCount.SetValue(window, 4);
                updateHint.Invoke(window, null);
                if (!autoSendHint.Text.Contains("4 sent"))
                    throw new Exception("While sending, the hint must show how much has gone out.");
                messageCount.SetValue(window, 0);
                Console.WriteLine("PASS: the auto-send hint follows the setting and the sent count");
                // The pump takes one chunk at a time and gives up on the first
                // refusal: carrying on would leave holes in what the reader gets.
                var pump = typeof(MainWindow).GetMethod("PumpAutoSendAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
                var active = typeof(MainWindow).GetField("autoSendActive", BindingFlags.NonPublic | BindingFlags.Instance)!;
                var answerVersion = typeof(MainWindow).GetField("answerOutputVersion", BindingFlags.NonPublic | BindingFlags.Instance)!;
                appSettings.AutoSendFirstChars = 20;
                appSettings.AutoSendNextChars = 20;
                answer.Text = "First one. Second one. A trailing fragment";
                sentCount.SetValue(window, 0);
                active.SetValue(window, true);
                // No browser session here, so the first delivery is refused.
                await (Task)pump.Invoke(window, new object[] { (int)answerVersion.GetValue(window)! })!;
                if (active.GetValue(window) is not false)
                    throw new Exception("A refused chunk must stop auto-send for this answer.");
                // 20 characters takes both sentences, and the run stops there.
                if (sentCount.GetValue(window) is not 2)
                    throw new Exception("Auto-send must stop after the chunk it could not deliver.");
                var status = (TextBlock)window.FindName("Status");
                if (status.Visibility != Visibility.Visible || !status.Text.StartsWith("Auto-send stopped:", StringComparison.Ordinal))
                    throw new Exception("A stopped auto-send must say so on the status line.");
                if (!autoSendHint.Text.StartsWith("stopped", StringComparison.Ordinal))
                    throw new Exception("The hint must not read as though the whole answer went out.");
                appSettings.AutoSendAnswer = false;
                sentCount.SetValue(window, 0);
                messageCount.SetValue(window, 0);
                answer.Clear();
                Console.WriteLine("PASS: a refused chunk stops auto-send and reports it once");
                // The same pump, with nothing left to flush: it must send nothing
                // at all. Any send would be refused here and show up as a stop.
                var autoSendFinal = typeof(MainWindow).GetField("autoSendFinal", BindingFlags.NonPublic | BindingFlags.Instance)!;
                var autoSendFailed = typeof(MainWindow).GetField("autoSendFailed", BindingFlags.NonPublic | BindingFlags.Instance)!;
                status.Visibility = Visibility.Collapsed;
                answer.Text = "First one. Second one.";
                sentCount.SetValue(window, 2);
                active.SetValue(window, true);
                autoSendFailed.SetValue(window, false);
                autoSendFinal.SetValue(window, true);
                await (Task)pump.Invoke(window, new object[] { (int)answerVersion.GetValue(window)! })!;
                if (autoSendFailed.GetValue(window) is true || status.Visibility == Visibility.Visible)
                    throw new Exception("A fully sent answer must not be flushed again.");
                if (sentCount.GetValue(window) is not 2)
                    throw new Exception("A flush with nothing left must not move the sent count.");
                autoSendFinal.SetValue(window, false);
                active.SetValue(window, false);
                sentCount.SetValue(window, 0);
                answer.Clear();
                Console.WriteLine("PASS: the end-of-answer flush sends nothing when nothing is left");
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

