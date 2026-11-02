using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using HelpMe;
using HelpMe.Services;

// The window checks, run on Avalonia's headless platform with Skia drawing.
//
// This replaces the WPF smoke tests: it asserts the same behaviour the Windows
// build was checked for, minus the Live Captions reader that has no macOS
// counterpart, and it also writes a picture of each window so the layout can be
// looked at from a machine that is not the Mac.
//
// Usage: UiSmoke [output-directory]
internal static class Program
{
    private static int failures;

    [STAThread]
    private static int Main(string[] args)
    {
        var output = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "HelpMe-ui-smoke");
        Directory.CreateDirectory(output);

        // Never touch the preferences of a real run: the window saves its
        // collapsed sections and its size as soon as either changes.
        AppSettings.PreferencesPathOverride =
            Path.Combine(Path.GetTempPath(), $"HelpMe.UiSmoke.{Guid.NewGuid():N}.json");

        AppBuilder.Configure(() => new App(launchMainWindow: false))
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();

        Exception? failure = null;
        Dispatcher.UIThread.Invoke(() =>
        {
            try { Run(output).GetAwaiter().GetResult(); }
            catch (Exception error) { failure = error; }
        });

        try { File.Delete(AppSettings.PreferencesPathOverride); } catch (IOException) { }

        if (failure is not null)
        {
            Console.Error.WriteLine(failure);
            return 1;
        }
        Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILED");
        return failures == 0 ? 0 : 1;
    }

    private static void Check(bool condition, string message)
    {
        if (condition) Console.WriteLine("PASS: " + message);
        else { failures++; Console.Error.WriteLine("FAIL: " + message); }
    }

    private static async Task Run(string output)
    {
        var window = new MainWindow();
        try
        {
            Render(window, Path.Combine(output, "main-dark.png"), 1500, 860);
            AppTheme.Apply("Light");
            Render(window, Path.Combine(output, "main-light.png"), 1200, 700);
            AppTheme.Apply("Dark");

            var settingsWindow = new SettingsWindow();
            Render(settingsWindow, Path.Combine(output, "settings-dark.png"), 580, 900);
            settingsWindow.Close();
            Console.WriteLine($"Wrote window pictures to {output}");

            ThemeFollows(window);
            Prompts(window);
            AnswerVersions(window);
            StopCancels(window);
            Collapsing(window);
            await MessagesAsync(window);
            Chunking();
            AutoSendHint(window);
            await AutoSendPumpAsync(window);
            RestoreShortcut(window);
        }
        finally { window.Close(); }
    }

    private static void Render(Window window, string path, int width, int height)
    {
        window.Width = width;
        window.Height = height;
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var frame = window.CaptureRenderedFrame();
        if (frame is null) { failures++; Console.Error.WriteLine("FAIL: could not render " + path); return; }
        using var file = File.Create(path);
        frame.Save(file);
        window.Hide();
    }

    // Reflection reaches the window's own state, the same way the WPF checks
    // did: these are internal decisions with no public surface of their own.
    private static T Field<T>(object target, string name)
        => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    private static void SetField(object target, string name, object? value)
        => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private static object? Call(object target, string name, params object?[] arguments)
        => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, arguments);

    private static T Control<T>(Window window, string name) where T : Avalonia.Controls.Control
        => window.FindControl<T>(name) ?? throw new Exception($"No control named {name}.");

    private static void ThemeFollows(MainWindow window)
    {
        var translation = Control<TextBox>(window, "TranslationText");
        Check(translation.IsReadOnly, "section 02 is read-only");
        AppTheme.Apply("Light");
        var light = (Application.Current!.Resources["WindowBackground"] as Avalonia.Media.SolidColorBrush)?.Color;
        Check(light == Avalonia.Media.Color.Parse("#F3F3F3"), "the light theme updates the palette live");
        AppTheme.Apply("Dark");
    }

    private static void Prompts(MainWindow window)
    {
        var source = Control<TextBox>(window, "PcText");
        string Prompt() => (string)Call(window, "GetAnswerPrompt")!;

        source.Text = "First sentence. Second sentence.";
        SetField(window, "hasSavedCaptionLocation", false);
        Check(Prompt() == source.Text, "without a bookmark, the whole caption is sent");

        SetField(window, "savedCaptionLocation", 16);
        SetField(window, "hasSavedCaptionLocation", true);
        Check(Prompt() == "Second sentence.", "a saved position selects the suffix");

        source.SelectionStart = 0; source.SelectionEnd = 15;
        Check(Prompt() == "Second sentence.", "a selection does not replace the saved-position suffix");

        source.SelectionStart = source.SelectionEnd = 0;
        SetField(window, "savedCaptionLocation", (source.Text ?? "").Length);
        var refused = false;
        try { Prompt(); }
        catch (TargetInvocationException e) when (e.InnerException is InvalidOperationException error &&
                                                  error.Message.Contains("saved starting point"))
        { refused = true; }
        Check(refused, "an empty suffix refuses rather than sending unrelated earlier text");

        source.Text = "";
        source.Text = "Replacement caption";
        Check(Prompt() == "Replacement caption", "clearing the document resets its old bookmark");
    }

    private static void AnswerVersions(MainWindow window)
    {
        var microphone = Control<TextBox>(window, "MicText");
        string Answer() => Field<string>(window, "answerText");

        SetField(window, "answerOutputVersion", 0);
        Call(window, "DisplayAnswer", "Streamed answer", 0);
        Dispatcher.UIThread.RunJobs();
        Check(Answer() == "Streamed answer" && (microphone.Text ?? "").Length == 0,
            "a reply reaches the answer and nothing else");

        SetField(window, "answerOutputVersion", 1);
        Call(window, "DisplayAnswer", "Stale answer", 0);
        Dispatcher.UIThread.RunJobs();
        Check(Answer() == "Streamed answer", "a reply from a superseded request is discarded");

        Call(window, "DisplayAnswer", "Next answer", 1);
        Dispatcher.UIThread.RunJobs();
        Check(Answer() == "Next answer", "the current request's reply lands");
    }

    private static void StopCancels(MainWindow window)
    {
        var sendButton = Control<Button>(window, "AnswerSendButton");
        using var pending = new CancellationTokenSource();
        SetField(window, "answerCancellation", pending);
        SetField(window, "autoSendActive", true);
        sendButton.Content = "Stop";
        Call(window, "AnswerSend_Click", sendButton, new RoutedEventArgs());
        Check(pending.IsCancellationRequested, "Stop cancels the reply being read");
        Check(Field<bool>(window, "answerStopped"), "a stopped request is remembered, not reported as a timeout");
        Check(!Field<bool>(window, "autoSendActive"), "stopping keeps the rest of that answer out of the chat");
        SetField(window, "answerCancellation", null);
        SetField(window, "autoSendActive", false);
        SetField(window, "answerStopped", false);
        sendButton.Content = "Send";
    }

    private static void Collapsing(MainWindow window)
    {
        var root = Control<Grid>(window, "RootGrid");
        var captions = Control<Grid>(window, "CaptionGrid");
        var translationRow = captions.RowDefinitions[2];
        var splitterRow = captions.RowDefinitions[1];
        var answerColumn = root.ColumnDefinitions[2];
        var browserColumn = root.ColumnDefinitions[4];
        var translation = Control<TextBox>(window, "TranslationText");
        var translationToggle = Control<ToggleButton>(window, "TranslationCollapseToggle");
        var aiToggle = Control<ToggleButton>(window, "AiPanelCollapseToggle");
        var chatToggle = Control<ToggleButton>(window, "ChatPanelCollapseToggle");
        var aiBar = Control<Border>(window, "TelegramBar");
        var aiActions = Control<StackPanel>(window, "AiHeaderActions");

        foreach (var toggle in new[] { translationToggle, aiToggle, chatToggle }) toggle.IsChecked = false;
        Call(window, "ApplyTranslationCollapsed");
        Call(window, "ApplyBrowserPanels");

        translationRow.Height = new GridLength(3, GridUnitType.Star);
        translationToggle.IsChecked = true;
        Call(window, "TranslationCollapse_Click", null, new RoutedEventArgs());
        Check(translationRow.Height == GridLength.Auto && splitterRow.Height.Value == 0 && !translation.IsVisible,
            "collapsing 02 releases its row and hides only its text area");
        translationToggle.IsChecked = false;
        Call(window, "TranslationCollapse_Click", null, new RoutedEventArgs());
        Check(translationRow.Height.Value == 3 && translationRow.Height.IsStar && translation.IsVisible,
            "expanding 02 restores the dragged row size");

        bool Press(Key key, KeyModifiers modifiers) => (bool)Call(window, "TryToggleSection", key, modifiers)!;

        Check(Press(Key.D2, KeyModifiers.Meta) && translationToggle.IsChecked == true && !translation.IsVisible,
            "Cmd+2 collapses section 02");
        Check(Press(Key.NumPad2, KeyModifiers.Meta) && translationToggle.IsChecked == false,
            "Cmd+2 expands section 02 again, from the numeric keypad too");
        Check(!Press(Key.D3, KeyModifiers.Meta) && !Press(Key.D2, KeyModifiers.Shift) &&
              !Press(Key.D2, KeyModifiers.Meta | KeyModifiers.Shift) && !Press(Key.D2, KeyModifiers.None),
            "only plain Cmd+2 toggles its section");

        var settings = Field<AppSettings>(window, "settings");
        var aiTitle = Control<TextBlock>(window, "AiPanelTitle");
        var chatTitle = Control<TextBlock>(window, "ChatPanelTitle");
        Check(aiTitle.Text == $"05  {settings.AiProvider.ToUpperInvariant()}" &&
              chatTitle.Text == $"06  {settings.MessengerTarget.ToUpperInvariant()}",
            "panels 05 and 06 name the provider and destination in use");

        var aiWebView = Control<Avalonia.Controls.Control>(window, "AiWebView");
        var chatWebView = Control<Avalonia.Controls.Control>(window, "ChatWebView");
        Check(Press(Key.D5, KeyModifiers.Meta) && aiToggle.IsChecked == true && !aiWebView.IsVisible &&
              !aiBar.IsVisible && !aiActions.IsVisible && answerColumn.Width == GridLength.Auto,
            "Cmd+5 collapses panel 05 to its header and releases its column");
        Check(Press(Key.NumPad5, KeyModifiers.Meta) && aiToggle.IsChecked == false && aiWebView.IsVisible &&
              aiBar.IsVisible && answerColumn.Width.IsStar,
            "Cmd+5 restores panel 05, its send bar and the width it had");
        Check(Press(Key.D6, KeyModifiers.Meta) && chatToggle.IsChecked == true && !chatWebView.IsVisible &&
              browserColumn.Width == GridLength.Auto,
            "Cmd+6 collapses panel 06 and releases its column");
        Check(Press(Key.D6, KeyModifiers.Meta) && chatToggle.IsChecked == false && browserColumn.Width.IsStar,
            "Cmd+6 expands panel 06 and takes the column back");
    }

    private static async Task MessagesAsync(MainWindow window)
    {
        async Task<string> Message(bool whole) => await (Task<string>)Call(window, "GetTelegramMessageAsync", whole)!;

        SetField(window, "answerText", "  First answer line.\nSecond answer line.  ");
        const string closed = "First answer line.\nSecond answer line.\n👌👌👌👌👌👌👌👌👌👌";
        Check(await Message(false) == closed && await Message(true) == closed,
            "with nothing selected, the whole answer is sent, trimmed and closed by the end mark");

        SetField(window, "answerText", "");
        Check((await Message(false)).Length == 0 && (await Message(true)).Length == 0,
            "an empty answer produces no message from either button");

        var withEndMark = typeof(MainWindow).GetMethod("WithEndMark", BindingFlags.Static | BindingFlags.NonPublic)!;
        Check((string)withEndMark.Invoke(null, new object[] { "Ends in a fence\n```" })! ==
              "Ends in a fence\n```\n👌👌👌👌👌👌👌👌👌👌",
            "the end mark goes on a line of its own, below the answer");

        Check(Control<Button>(window, "TelegramSendButton").Content as string == "Send Selection" &&
              Control<Button>(window, "TelegramSendAllButton").Content as string == "All Send",
            "both send buttons carry their resting labels");
    }

    private static void Chunking()
    {
        string? Next(string text, int sent, int target, out int count)
            => AnswerChunker.NextChunk(text, sent, target, out count);
        int count;

        Check(Next("First one. Second one. And a third", 0, 15, out count) == "First one. Second one." && count == 2,
            "a chunk takes whole sentences until it reaches the size");
        Check(Next("First one. Second one. And a third", 0, 5, out count) == "First one." && count == 1,
            "one sentence past the size is a chunk on its own");
        Check(Next("First one. Second one. And a third", 2, 10, out count) is null && count == 2,
            "an unfinished sentence waits for the rest of the answer");
        Check(Next("Only one finished. Then", 0, 100, out _) is null,
            "a chunk short of the size waits for more of the answer");
        Check(Next("Dr. Smith arrived. Then", 0, 5, out _) == "Dr. Smith arrived.",
            "a title's period does not end the sentence");
        Check(Next("We use e.g. three models. Next", 0, 5, out _) == "We use e.g. three models.",
            "an abbreviation's period does not end the sentence");
        Check(Next("It costs 3.50 today. Next", 0, 5, out _) == "It costs 3.50 today.",
            "a decimal point does not end the sentence");
        Check(Next("See fig. 2 for the shape. Next", 0, 5, out _) == "See fig. 2 for the shape.",
            "a lower-case continuation does not end the sentence");
        Check(Next("Use these:\n- first thing.\n- second thing. Next", 0, 30, out _)
              == "Use these:\n- first thing.\n- second thing.",
            "a chunk keeps the answer's own line breaks");
        Check(Next("Here it is. ```py\nvalue = 1\n", 0, 5, out _) == "Here it is.",
            "text before an unclosed fence can still be sent");
        Check(Next("Here it is. ```py\nvalue = 1\n", 1, 5, out _) is null,
            "an unclosed code fence holds back the rest");
        Check(Next("Run this. ```py\nx = 1. Y = 2.\n``` Done now. Next", 1, 5, out _)
              == "```py\nx = 1. Y = 2.\n``` Done now.",
            "a finished code block travels in one piece");
        Check(Next("안녕하세요. 반갑습니다. 그리고", 0, 10, out _) == "안녕하세요. 반갑습니다.",
            "Korean sentences split on their periods too");
        Check(AnswerChunker.SentenceCount("First one. Second one. A trailing fragment") == 2,
            "ticking auto-send mid-answer skips the finished sentences on screen");
        Check(AnswerChunker.SentenceCount("") == 0, "an empty answer has no sentences to skip");
        Check(AnswerChunker.RemainingChunk("First one. Second one. A trailing fragment", 1, out count)
              == "Second one. A trailing fragment" && count == 3,
            "the final flush sends everything still unsent");
        Check(AnswerChunker.NextChunk("First one. Second one. And", 0, 15, out count) is not null && count == 2,
            "both sentences go out while the answer is still being written");
        Check(AnswerChunker.RemainingChunk("First one. Second one.", 2, out count) is null && count == 2,
            "an answer already sent in full flushes nothing");
        Check(AnswerChunker.RemainingChunk("One. Two. Three.", 2, out _) == "Three.",
            "the flush delivers the sentence the stream held back");
        Check(AnswerChunker.RemainingChunk("First one. Second one.", 1, out _) == "Second one.",
            "the final flush delivers the closing sentence");
        Check(AnswerChunker.RemainingChunk("", 0, out _) is null, "an empty answer flushes nothing");
    }

    private static void AutoSendHint(MainWindow window)
    {
        var check = Control<CheckBox>(window, "AutoSendCheck");
        var hint = Control<TextBlock>(window, "AutoSendHint");
        var bar = Control<Border>(window, "TelegramBar");
        var settings = Field<AppSettings>(window, "settings");

        Check(bar.Child is Panel panel && panel.Children.Contains(check),
            "the auto-send checkbox belongs on the panel 05 bar");

        settings.AutoSendAnswer = false;
        Call(window, "UpdateAutoSendHint");
        Check(!hint.IsVisible && check.IsChecked != true, "with auto-send off, the box is clear and the hint hidden");

        settings.AutoSendAnswer = true;
        settings.AutoSendFirstChars = 100;
        settings.AutoSendNextChars = 500;
        Call(window, "UpdateAutoSendHint");
        Check(hint.IsVisible && (hint.Text ?? "").Contains("100 then 500"),
            "with auto-send on, the hint shows both chunk sizes");
        Check(check.IsChecked == true, "the box follows the saved setting");

        SetField(window, "autoSendMessages", 4);
        Call(window, "UpdateAutoSendHint");
        Check((hint.Text ?? "").Contains("4 sent"), "while sending, the hint shows how much has gone out");
        SetField(window, "autoSendMessages", 0);
    }

    private static async Task AutoSendPumpAsync(MainWindow window)
    {
        var settings = Field<AppSettings>(window, "settings");
        var status = Control<TextBlock>(window, "Status");
        var hint = Control<TextBlock>(window, "AutoSendHint");

        settings.AutoSendFirstChars = 20;
        settings.AutoSendNextChars = 20;
        SetField(window, "answerText", "First one. Second one. A trailing fragment");
        SetField(window, "autoSendSentences", 0);
        SetField(window, "autoSendActive", true);
        // No web panel here, so the first delivery is refused.
        await (Task)Call(window, "PumpAutoSendAsync", Field<int>(window, "answerOutputVersion"))!;
        Check(!Field<bool>(window, "autoSendActive"), "a refused chunk stops auto-send for this answer");
        Check(Field<int>(window, "autoSendSentences") == 2, "auto-send stops after the chunk it could not deliver");
        Check(status.IsVisible && (status.Text ?? "").StartsWith("Auto-send stopped:", StringComparison.Ordinal),
            "a stopped auto-send says so on the status line");
        Check((hint.Text ?? "").StartsWith("stopped", StringComparison.Ordinal),
            "the hint does not read as though the whole answer went out");

        // The same pump with nothing left to flush must send nothing at all.
        status.IsVisible = false;
        SetField(window, "answerText", "First one. Second one.");
        SetField(window, "autoSendSentences", 2);
        SetField(window, "autoSendActive", true);
        SetField(window, "autoSendFailed", false);
        SetField(window, "autoSendFinal", true);
        await (Task)Call(window, "PumpAutoSendAsync", Field<int>(window, "answerOutputVersion"))!;
        Check(!Field<bool>(window, "autoSendFailed") && !status.IsVisible,
            "a fully sent answer is not flushed again");
        Check(Field<int>(window, "autoSendSentences") == 2, "a flush with nothing left does not move the sent count");

        SetField(window, "autoSendFinal", false);
        SetField(window, "autoSendActive", false);
        SetField(window, "autoSendSentences", 0);
        SetField(window, "answerText", "");
        settings.AutoSendAnswer = false;
    }

    private static void RestoreShortcut(MainWindow window)
    {
        // Carbon owns the system-wide shortcut, so away from macOS the only
        // thing to assert is that it declines cleanly rather than throwing.
        var registered = (bool)Call(window, "RegisterRestoreShortcut", "Cmd+Alt+Shift+F12")!;
        if (OperatingSystem.IsMacOS())
            Check(registered && Field<bool>(window, "restoreHotkeyRegistered"),
                "the system-wide restore shortcut registers and stays registered while hidden");
        else
            Check(!registered, "off macOS the system-wide shortcut declines without throwing");

        Check(!(bool)Call(window, "RegisterRestoreShortcut", "Cmd+H")!,
            "a shortcut with one modifier is refused as a system-wide one");
    }
}
