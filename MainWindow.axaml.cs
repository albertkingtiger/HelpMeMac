using System.ComponentModel;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Net.Http;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HelpMe.Services;
using HelpMe.Services.Audio;
using HelpMe.Services.Mac;

namespace HelpMe;

public partial class MainWindow : Window
{
    private AppSettings settings = null!;
    private SpeechEngine engine = null!;
    private ICaptionSession? pc;
    private AudioSession? mic;
    private TranslationSession? translation;
    private WebViewBrowserSession? webViews;
    private bool primingPanels;
    private bool shownWhilePriming;
    private readonly SemaphoreSlim changes = new(1, 1);
    private bool closing;
    private bool closed;
    private bool exiting;
    private TrayIcon? trayIcon;
    private bool initialized;
    private int pcGeneration;
    private int micGeneration;
    private int translationGeneration;
    private readonly DispatcherTimer captionFlushTimer = new() { Interval = TimeSpan.FromMilliseconds(900) };
    private bool translationBusy;
    private bool translationUsesAudio;
    private string translatedSource = "";
    private string translatedTarget = "";
    private string pcTempStrVar = "";
    private string micTempStrVar = "";
    private bool pcLiveCaptionActive;
    private int pcLiveCaptionStart;
    private string pcFinalCaptionText = "";
    private int pcCaptionCommitVersion;
    private int translatedCaptionCommitVersion;
    private readonly DocumentTranslator documentTranslator = new();
    private int translationOutputVersion;
    private int savedCaptionLocation;
    private bool hasSavedCaptionLocation;
    private CancellationTokenSource? answerCancellation;
    private bool answerStopped;
    private const string SendLabel = "Send";
    private const string StopLabel = "Stop";
    private int answerOutputVersion;
    private string answerText = "";
    private Shortcut sendShortcut = new(KeyModifiers.Meta | KeyModifiers.Shift, Key.Z);
    private Shortcut telegramShortcut = new(KeyModifiers.Meta | KeyModifiers.Shift, Key.T);
    private Shortcut telegramAllShortcut = new(KeyModifiers.Meta | KeyModifiers.Shift, Key.A);
    private const string SelectionLabel = "Send Selection";
    private const string AllLabel = "All Send";
    private bool telegramSending;
    private int telegramConfirmation;
    // Auto-send state for the answer being generated right now.
    private bool autoSendActive;
    private bool autoSendPumping;
    private bool autoSendFinal;
    private bool autoSendFailed;
    private int autoSendSentences;
    private int autoSendMessages;
    // Marks where one answer ends and the next begins in the chat.
    private const string AutoSendSeparator = "**********************************************************************";
    // Closes an answer the way the separator opens one, so the reader can see
    // that nothing more of it is coming. Only a message carrying the end of an
    // answer gets it: never a piece taken from the middle, and never a prompt.
    private const string AnswerEndMark = "👌👌👌👌👌👌👌👌👌👌";
    private bool startedInTray;
    private IDisposable? showRequests;
    private bool restoreHotkeyRegistered;
    // Collapsing replaces these with Auto; restoring puts back whatever size the
    // user had dragged the splitters to.
    private GridLength translationRowHeight = new(1, GridUnitType.Star);
    private GridLength answerColumnWidth = new(1, GridUnitType.Star);
    private GridLength browserColumnWidth = new(1.3, GridUnitType.Star);
    private bool suppressAutoSendChange;

    public MainWindow()
    {
        InitializeComponent();
        // Background mode: the window is created so the panels and the global
        // shortcut can be set up, but it is kept off the desktop and out of the
        // Dock until the menu bar or the shortcut asks for it.
        ShowInTaskbar = false;
        // The configurable shortcuts have to be seen before the controls do, so
        // the tunnelling handler runs ahead of the editors' own key handling.
        AddHandler(KeyDownEvent, Window_PreviewKeyDown, RoutingStrategies.Tunnel);
        PcText.PropertyChanged += (_, change) =>
        {
            if (change.Property == TextBox.TextProperty) OnPcTextChanged();
        };
        Closing += Window_Closing;
        captionFlushTimer.Tick += (_, _) => FlushCaptionBuffers();
        try
        {
            settings = AppSettings.Load();
            AppTheme.Apply(settings.Theme);
            trayIcon = CreateTrayIcon();
            UpdateCaptionSourceHint();
            Width = settings.WindowWidth;
            Height = settings.WindowHeight;
            ApplyEditorAppearance();
            ApplySendShortcut();
            UpdateAutoSendHint();
            TranslationCollapseToggle.IsChecked = settings.TranslationCollapsed;
            ApplyTranslationCollapsed();
            AiPanelCollapseToggle.IsChecked = settings.AiPanelCollapsed;
            ChatPanelCollapseToggle.IsChecked = settings.ChatPanelCollapsed;
            ApplyBrowserPanels();
            PropertyChanged += (_, change) =>
            {
                // Width and Height are NaN until the window has been placed,
                // and a NaN cannot be written to the preferences file.
                if (initialized && change.Property == ClientSizeProperty &&
                    double.IsFinite(Width) && double.IsFinite(Height))
                    settings.SaveWindowSize(Width, Height);
            };
            engine = new SpeechEngine(settings);
            initialized = true;
            // Startup work does not hang off Loaded: the window is never shown,
            // so Loaded never fires. StartInBackground drives it instead.
        }
        catch (Exception e)
        {
            ShowError(e.Message);
            PcToggle.IsEnabled = TranslationToggle.IsEnabled = MicToggle.IsEnabled = SettingsButton.IsEnabled = false;
        }
    }

    // The panels that collapse give their space back by changing the grid
    // definition behind them, which Avalonia addresses by position.
    private ColumnDefinition AnswerSplitterColumn => RootGrid.ColumnDefinitions[1];
    private ColumnDefinition AnswerColumn => RootGrid.ColumnDefinitions[2];
    private ColumnDefinition BrowserColumn => RootGrid.ColumnDefinitions[4];
    private RowDefinition TranslationSplitterRow => CaptionGrid.RowDefinitions[1];
    private RowDefinition TranslationRow => CaptionGrid.RowDefinitions[2];

    private async Task StartBrowserAsync()
    {
        if (closing) return;
        webViews ??= new WebViewBrowserSession(AiWebView, ChatWebView);
        try { await WhileOnScreenAsync(() => webViews.StartAsync(settings.AiProvider, settings.MessengerTarget)); }
        catch (Exception error) when (error is IOException or InvalidOperationException or PlatformNotSupportedException)
        {
            if (!closing) ShowError(error.Message);
        }
    }

    // A WKWebView is only created once its window has been realised, and Help
    // Me! starts hidden in the menu bar. Showing the window off the desktop for
    // those few seconds is invisible, and it goes straight back afterwards
    // unless the user asked for it in the meantime.
    private async Task WhileOnScreenAsync(Func<Task> work)
    {
        if (IsVisible) { await work(); return; }
        var position = Position;
        primingPanels = true;
        shownWhilePriming = false;
        ShowInTaskbar = false;
        Position = new PixelPoint(-32000, -32000);
        Show();
        try
        {
            // One layout pass has to run before the native views exist.
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            await work();
        }
        finally
        {
            primingPanels = false;
            if (shownWhilePriming)
            {
                PlaceOnScreen(position);
                RestoreFromTray();
            }
            else
            {
                Hide();
                PlaceOnScreen(position);
            }
        }
    }

    // A window that has never been placed reports the origin, which would put
    // it in the corner of the first screen; centre it on the main display
    // instead.
    private void PlaceOnScreen(PixelPoint previous)
    {
        if (previous.X > -30000 && previous.Y > -30000) { Position = previous; return; }
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null) return;
        var work = screen.WorkingArea;
        Position = new PixelPoint(
            work.X + (int)Math.Max(0, (work.Width - Width) / 2),
            work.Y + (int)Math.Max(0, (work.Height - Height) / 2));
    }

    private void UI(Action action) => Dispatcher.UIThread.Post(() => { if (!closing) action(); });

    private void ShowError(string message)
    {
        Status.Text = message;
        Status.IsVisible = true;
    }

    private void WorkerMessage(string message, Func<bool> isCurrent)
    {
        // Routine worker updates never enter the UI queue.
        if (message.StartsWith("Error:", StringComparison.Ordinal))
            UI(() => { if (isCurrent()) ShowError(message); });
    }

    private static string TextOf(TextBox box) => box.Text ?? "";

    private static void AppendText(TextBox box, string text) => box.Text = TextOf(box) + text;

    private void Append(TextBox box, string text)
    {
        var chunk = Regex.Replace(text ?? "", @"\s+", " ").Trim();
        if (chunk.Length == 0) return;
        UpdateTextPreservingView(box, () =>
        {
            var current = TextOf(box);
            // A lowercase continuation should stay on the same natural sentence.
            if (current.Length > 0 && char.IsLower(chunk[0]) && current[^1] is '.' or '!' or '?')
                box.Text = current[..^1];
            current = TextOf(box);
            var separator = current.Length > 0 && !char.IsWhiteSpace(current[^1]) && !char.IsPunctuation(chunk[0]) ? " " : "";
            AppendText(box, separator + chunk);
        });
    }

    private void QueueCaption(string text, bool pcAudio)
    {
        text = Regex.Replace(text ?? "", @"\s+", " ").Trim();
        if (text.Length == 0) return;
        var buffer = pcAudio ? pcTempStrVar : micTempStrVar;
        if (buffer.Length > 0 && !char.IsWhiteSpace(buffer[^1])) buffer += " ";
        buffer += text;
        if (pcAudio) pcTempStrVar = buffer; else micTempStrVar = buffer;
        captionFlushTimer.Stop();
        captionFlushTimer.Start();
    }

    private void FlushCaptionBuffers()
    {
        captionFlushTimer.Stop();
        if (pcTempStrVar.Length > 0)
        {
            Append(PcText, pcTempStrVar);
            pcTempStrVar = "";
        }
        if (micTempStrVar.Length > 0)
        {
            Append(MicText, micTempStrVar);
            micTempStrVar = "";
        }
    }

    private void SetLiveCaption(string text)
        => UpdateTextPreservingView(PcText, () => ReplaceLiveCaption(text));

    private void ReplaceLiveCaption(string text)
    {
        if (!pcLiveCaptionActive)
        {
            var existing = TextOf(PcText);
            if (existing.Length > 0 && !char.IsWhiteSpace(existing[^1])) AppendText(PcText, " ");
            pcLiveCaptionStart = TextOf(PcText).Length;
            pcLiveCaptionActive = true;
        }
        var start = Math.Clamp(pcLiveCaptionStart, 0, TextOf(PcText).Length);
        PcText.Text = TextOf(PcText)[..start] + text.Trim();
    }

    private void CommitLiveCaption(string text)
    {
        UpdateTextPreservingView(PcText, () =>
        {
            ReplaceLiveCaption(text);
            var trimmed = text.TrimEnd();
            if (trimmed.Length > 0 && trimmed[^1] is '.' or '!' or '?' or ':' or ';')
                AppendText(PcText, Environment.NewLine);
            else
            {
                var current = TextOf(PcText);
                if (current.Length > 0 && !char.IsWhiteSpace(current[^1])) AppendText(PcText, " ");
            }
            pcLiveCaptionActive = false;
        });
        // Translation follows each finalized PC-audio caption segment. Interim
        // Whisper previews only revise section 01 and never start translation.
        pcFinalCaptionText = TextOf(PcText).Trim();
        ++pcCaptionCommitVersion;
        ScheduleTranslation();
    }

    // An Avalonia TextBox keeps its scroll position in the ScrollViewer its
    // template builds, which is where the viewport has to be read and put back:
    // appending to a box the user has scrolled away from must not drag them to
    // the bottom, and a caption arriving while they are correcting a word must
    // not move the caret out from under them.
    private static ScrollViewer? EditorScroll(TextBox box)
        => box.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();

    private static void UpdateTextPreservingView(TextBox box, Action update)
    {
        var scroll = EditorScroll(box);
        var follow = IsNearBottom(scroll);
        var offset = scroll?.Offset ?? default;
        var selectionStart = box.SelectionStart;
        var selectionEnd = box.SelectionEnd;

        update();

        var length = TextOf(box).Length;
        box.SelectionStart = Math.Min(selectionStart, length);
        box.SelectionEnd = Math.Min(selectionEnd, length);
        // The new text has to be measured before the viewport means anything.
        Dispatcher.UIThread.Post(() =>
        {
            var viewer = EditorScroll(box);
            if (viewer is null) return;
            if (follow) viewer.Offset = new Vector(viewer.Offset.X, Math.Max(0, viewer.Extent.Height - viewer.Viewport.Height));
            else viewer.Offset = offset;
        }, DispatcherPriority.Render);
    }

    private static bool IsNearBottom(ScrollViewer? scroll)
        => scroll is null ||
           scroll.Extent.Height <= scroll.Viewport.Height ||
           scroll.Offset.Y >= scroll.Extent.Height - scroll.Viewport.Height - 32;

    // Avalonia raises TextChanged for typing but not for a document replaced
    // in code, and Format and the caption writers both replace it in code. The
    // property itself changes either way, so that is what is watched.
    private void OnPcTextChanged()
    {
        if (!initialized) return;
        if (string.IsNullOrWhiteSpace(PcText.Text))
        {
            hasSavedCaptionLocation = false;
            savedCaptionLocation = 0;
            ++translationOutputVersion;
            translatedSource = translatedTarget = "";
            pcFinalCaptionText = "";
            ++pcCaptionCommitVersion;
            translatedCaptionCommitVersion = pcCaptionCommitVersion;
            TranslationText.Text = "";
        }
    }

    private void ScheduleTranslation()
    {
        if (!closing && TranslationToggle.IsChecked == true && translation is not null &&
            !translationBusy && pcCaptionCommitVersion != translatedCaptionCommitVersion)
            _ = TranslateInputAsync();
    }

    private async Task TranslateInputAsync()
    {
        if (closing || translationBusy || translation is null || TranslationToggle.IsChecked != true) return;
        var source = pcFinalCaptionText;
        var captionVersion = pcCaptionCommitVersion;
        if (source.Length == 0 || captionVersion == translatedCaptionCommitVersion) return;
        var generation = translationGeneration;
        var session = translation;
        var outputVersion = translationOutputVersion;
        translationBusy = true;
        var retry = true;
        try
        {
            var result = await documentTranslator.TranslateAsync(source, session.TranslateAsync);
            // Show completed work even if another final caption arrived during
            // inference. Discarding every older result can starve the display
            // during continuous speech; the newer snapshot is processed next.
            var current = pcFinalCaptionText;
            var stillValid = current == source || (current.StartsWith(source, StringComparison.Ordinal) &&
                current.Length > source.Length && char.IsWhiteSpace(current[source.Length]));
            if (!closing && generation == translationGeneration && outputVersion == translationOutputVersion &&
                TranslationToggle.IsChecked == true && stillValid)
            {
                translatedSource = source;
                translatedTarget = result;
                translatedCaptionCommitVersion = captionVersion;
                UpdateTextPreservingView(TranslationText, () => TranslationText.Text = translatedTarget);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            retry = false;
            if (!closing && generation == translationGeneration) ShowError(error.Message);
        }
        finally
        {
            translationBusy = false;
            if (retry && !closing && pcCaptionCommitVersion != translatedCaptionCommitVersion) ScheduleTranslation();
        }
    }

    private void Format_Click(object? sender, RoutedEventArgs e)
    {
        pcTempStrVar = "";
        pcLiveCaptionActive = false;
        PcText.Text = "";
        pcFinalCaptionText = "";
        ++pcCaptionCommitVersion;
        translatedCaptionCommitVersion = pcCaptionCommitVersion;
        translatedSource = translatedTarget = "";
        documentTranslator.Clear();
    }

    private void TranslationFormat_Click(object? sender, RoutedEventArgs e)
    {
        ++translationOutputVersion;
        translatedSource = TextOf(PcText).Trim();
        translatedCaptionCommitVersion = pcCaptionCommitVersion;
        translatedTarget = "";
        documentTranslator.Clear();
        TranslationText.Text = "";
    }

    private void MicFormat_Click(object? sender, RoutedEventArgs e)
    {
        micTempStrVar = "";
        MicText.Text = "";
    }

    private void Minimize_Click(object? sender, RoutedEventArgs e) => HideToTray();

    private void TranslationCollapse_Click(object? sender, RoutedEventArgs e)
    {
        ApplyTranslationCollapsed();
        SaveCollapsedSections();
    }

    private void SaveCollapsedSections()
    {
        if (initialized)
            settings.SaveCollapsedSections(TranslationCollapseToggle.IsChecked == true,
                AiPanelCollapseToggle.IsChecked == true, ChatPanelCollapseToggle.IsChecked == true);
    }

    // Null while the panels can take a message, otherwise what to tell the user.
    private string? BrowserUnavailable =>
        webViews?.IsReady == true ? null : "The browser panels are not ready yet.";

    private Task<string> BrowserSendAsync(string text, string provider, Action<string>? progress, CancellationToken token)
        => webViews!.SendPromptAsync(text, provider, progress, token);

    private void AiPanelCollapse_Click(object? sender, RoutedEventArgs e)
    {
        ApplyAiPanelCollapsed();
        SaveCollapsedSections();
    }

    private void ChatPanelCollapse_Click(object? sender, RoutedEventArgs e)
    {
        ApplyChatPanelCollapsed();
        SaveCollapsedSections();
    }

    private void BrowserReload_Click(object? sender, RoutedEventArgs e)
        => webViews?.Reload((sender as Button)?.Tag as string == "Ai");

    // Panels 05 and 06 name the pages they carry, so the headers say which
    // assistant answered and where the answer is being sent.
    private void ApplyBrowserPanels()
    {
        ApplyAiPanelCollapsed();
        ApplyChatPanelCollapsed();
        // Three columns need the room; a window left at the old two-column width
        // would squeeze the web pages into a strip.
        if (Width < 1300) Width = 1300;
        AiPanelTitle.Text = $"05  {settings.AiProvider.ToUpperInvariant()}";
        ChatPanelTitle.Text = $"06  {settings.MessengerTarget.ToUpperInvariant()}";
    }

    // Collapsing the AI page leaves its header as the way back.
    private void ApplyAiPanelCollapsed()
    {
        var collapsed = AiPanelCollapseToggle.IsChecked == true;
        AiWebView.IsVisible = !collapsed;
        TelegramBar.IsVisible = !collapsed;
        AiHeaderActions.IsVisible = !collapsed;
        // The AI page is the only thing in the middle column, so collapsing it
        // hands the whole width to the captions and the chat.
        if (collapsed)
        {
            if (AnswerColumn.Width.IsStar) answerColumnWidth = AnswerColumn.Width;
            AnswerColumn.Width = GridLength.Auto;
            AnswerSplitterColumn.Width = new GridLength(0);
            AnswerSplitter.IsVisible = false;
        }
        else
        {
            AnswerColumn.Width = answerColumnWidth;
            AnswerSplitterColumn.Width = new GridLength(5);
            AnswerSplitter.IsVisible = true;
        }
    }

    // Panel 06 owns the right column, so collapsing it gives the width back.
    private void ApplyChatPanelCollapsed()
    {
        var collapsed = ChatPanelCollapseToggle.IsChecked == true;
        if (collapsed)
        {
            if (BrowserColumn.Width.IsStar) browserColumnWidth = BrowserColumn.Width;
            BrowserColumn.Width = GridLength.Auto;
        }
        else BrowserColumn.Width = browserColumnWidth;
        ChatWebView.IsVisible = !collapsed;
        ChatPanelReloadButton.IsVisible = !collapsed;
    }

    // A collapsed section keeps its header bar so it can be brought back with the
    // mouse; only the text area and the space it occupied go away.
    private void ApplyTranslationCollapsed()
    {
        var collapsed = TranslationCollapseToggle.IsChecked == true;
        if (collapsed)
        {
            if (TranslationRow.Height.IsStar) translationRowHeight = TranslationRow.Height;
            TranslationRow.Height = GridLength.Auto;
            TranslationSplitterRow.Height = new GridLength(0);
        }
        else
        {
            TranslationRow.Height = translationRowHeight;
            TranslationSplitterRow.Height = new GridLength(5);
        }
        TranslationSplitter.IsVisible = !collapsed;
        TranslationText.IsVisible = !collapsed;
    }

    // Background start. The window is deliberately never shown here: the menu
    // bar owns its first appearance.
    public void StartInBackground()
    {
        if (startedInTray || !initialized) return;
        startedInTray = true;
        showRequests = SingleInstance.CreateShowListener(
            () => Dispatcher.UIThread.Post(() => { if (!closing) RestoreFromTray(); }));
        if (!MacHotkey.Register(settings.RestoreShortcut, RestoreFromTray))
        {
            if (OperatingSystem.IsMacOS())
                ShowError($"Could not register {settings.RestoreShortcut} to restore Help Me!; choose another shortcut in Settings.");
        }
        else restoreHotkeyRegistered = true;
        _ = BootstrapAsync();
    }

    private async Task BootstrapAsync()
    {
        var startupError = LoginItem.Apply(settings.StartAtLogin);
        if (startupError is not null) ShowError(startupError);
        if (!closing) await StartBrowserAsync();
        if (closing || PcToggle.IsChecked == true) return;
        PcToggle.IsChecked = true;
        try
        {
            await ChangeAsync(async () =>
            {
                translationUsesAudio = false;
                await UpdatePcAsync();
            });
        }
        catch
        {
            PcToggle.IsChecked = false;
        }
    }

    // The menu bar item. macOS has no balloon notifications for one, so the
    // hints the Windows tray icon popped up live in its tooltip and in the
    // status line instead.
    private TrayIcon? CreateTrayIcon()
    {
        // A platform with no menu bar of its own, such as the headless one the
        // UI checks run on, has no icon to create and must not stop the window.
        if (Application.Current is null) return null;
        try { return BuildTrayIcon(); }
        catch (Exception error) when (error is InvalidOperationException or PlatformNotSupportedException or NotSupportedException)
        {
            return null;
        }
    }

    private TrayIcon BuildTrayIcon()
    {
        var menu = new NativeMenu();
        var show = new NativeMenuItem("Show Help Me!");
        show.Click += (_, _) => RestoreFromTray();
        menu.Add(show);
        menu.Add(new NativeMenuItemSeparator());
        var exit = new NativeMenuItem("Exit");
        exit.Click += (_, _) =>
        {
            exiting = true;
            Dispatcher.UIThread.Post(Close);
        };
        menu.Add(exit);

        var icon = new TrayIcon
        {
            ToolTipText = "Help Me!  ·  " + settings.RestoreShortcut + " to show",
            Menu = menu,
            IsVisible = true
        };
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://HelpMe/Assets/helpme.png"));
            icon.Icon = new WindowIcon(new Bitmap(stream));
        }
        // A menu bar item with no picture is still usable; failing to decorate
        // it must not stop the app from starting.
        catch (Exception error) when (error is IOException or FileNotFoundException) { }
        icon.Clicked += (_, _) => RestoreFromTray();
        TrayIcon.SetIcons(Application.Current!, new TrayIcons { icon });
        return icon;
    }

    private void ApplyEditorAppearance()
    {
        PcText.FontSize = settings.PcFontSize;
        PcText.Foreground = EditorBrush(settings.PcTextColor);
        PcText.CaretBrush = EditorBrush(settings.PcCaretColor);
        TranslationText.FontSize = settings.TranslationFontSize;
        TranslationText.Foreground = EditorBrush(settings.TranslationTextColor);
    }

    private IBrush EditorBrush(string color)
        => AppTheme.Brush(settings.Theme == "Light" && color.Equals("#CCCCCC", StringComparison.OrdinalIgnoreCase)
            ? "#242424" : color);

    private void UpdateCaptionSourceHint() => CaptionSourceHint.Text = CaptureFactory.Describe(settings);

    private void HideToTray() => Hide();

    private void RestoreFromTray()
    {
        // Asked for while the window sits off the desktop priming the panels:
        // that run puts it back where it belongs and shows it when it finishes.
        if (primingPanels) { shownWhilePriming = true; return; }
        ShowInTaskbar = true;
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private bool RegisterRestoreShortcut(string shortcut)
    {
        if (!Shortcut.TryParse(shortcut, out var parsed) || parsed.ModifierCount < 2) return false;
        restoreHotkeyRegistered = MacHotkey.Register(shortcut, RestoreFromTray);
        return restoreHotkeyRegistered;
    }

    private void AlwaysOnTop_Click(object? sender, RoutedEventArgs e) => Topmost = AlwaysOnTopToggle.IsChecked == true;

    // Clicking a word marks where a send should start. The caret index is read
    // after the box has handled the click, because that is what decides where
    // the click actually landed.
    private void PcText_PointerPressed(object? sender, PointerPressedEventArgs e)
        => Dispatcher.UIThread.Post(() =>
        {
            if (closing) return;
            var index = PcText.CaretIndex;
            // Blank space past the end must not silently move the bookmark to
            // the document end.
            if (index < 0 || index >= TextOf(PcText).Length) return;
            savedCaptionLocation = index;
            hasSavedCaptionLocation = true;
            _ = SaveCaptionSuffixToClipboardAsync();
        }, DispatcherPriority.Input);

    private async Task SaveCaptionSuffixToClipboardAsync()
    {
        if (!hasSavedCaptionLocation || string.IsNullOrWhiteSpace(PcText.Text)) return;
        var text = TextOf(PcText);
        var start = Math.Clamp(savedCaptionLocation, 0, text.Length);
        var suffix = text[start..].Trim();
        if (suffix.Length == 0) return;
        try
        {
            if (GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(suffix);
        }
        // The clipboard may be momentarily owned by another application.
        catch (Exception error) when (error is InvalidOperationException or TimeoutException) { }
    }

    private void Window_PreviewKeyDown(object? sender, KeyEventArgs e)
    {
        // The configurable send shortcut wins, so a user who binds Cmd+2 to Send
        // keeps it rather than silently toggling a section instead.
        if (sendShortcut.Matches(e.Key, e.KeyModifiers))
        {
            e.Handled = true;
            _ = SendAnswerAsync();
            return;
        }
        if (telegramShortcut.Matches(e.Key, e.KeyModifiers))
        {
            e.Handled = true;
            _ = SendAnswerToTelegramAsync(TelegramSendButton, SelectionLabel, wholeAnswer: false);
            return;
        }
        if (telegramAllShortcut.Matches(e.Key, e.KeyModifiers))
        {
            e.Handled = true;
            _ = SendAnswerToTelegramAsync(TelegramSendAllButton, AllLabel, wholeAnswer: true);
            return;
        }
        if (TryToggleSection(e.Key, e.KeyModifiers)) e.Handled = true;
    }

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }

    private async void TelegramSend_Click(object? sender, RoutedEventArgs e)
        => await SendAnswerToTelegramAsync(TelegramSendButton, SelectionLabel, wholeAnswer: false);

    private async void TelegramSendAll_Click(object? sender, RoutedEventArgs e)
        => await SendAnswerToTelegramAsync(TelegramSendAllButton, AllLabel, wholeAnswer: true);

    // A selection on the AI page wins over the whole answer, unless the caller
    // asked for everything. Whitespace-only selections fall back to the whole
    // answer rather than refusing to send.
    private async Task<string> GetTelegramMessageAsync(bool wholeAnswer)
    {
        if (wholeAnswer) return WithEndMark(answerText.Trim());
        var selected = webViews is null ? "" : (await webViews.GetAiSelectionAsync()).Trim();
        // A highlighted passage is a piece the user picked out, not the end of
        // the answer, so it goes as it stands.
        return selected.Length > 0 ? selected : WithEndMark(answerText.Trim());
    }

    // The end mark goes on its own line: an answer may end in a code fence or a
    // list item, and a character tacked onto that line would be read as part of
    // it. Nothing is marked when there is no answer to close.
    private static string WithEndMark(string message)
        => message.Length == 0 ? message : message + "\n" + AnswerEndMark;

    // The answer goes to the chat app as its own new message.
    private async Task SendAnswerToTelegramAsync(Button button, string restingLabel, bool wholeAnswer)
    {
        if (closing || telegramSending) return;
        var message = await GetTelegramMessageAsync(wholeAnswer);
        if (message.Length == 0)
        {
            ShowError("There is no AI answer to send. Select text on the AI page, or send a prompt first.");
            return;
        }
        telegramSending = true;
        // Both buttons drive one page, so neither may start a second send.
        TelegramSendButton.IsEnabled = TelegramSendAllButton.IsEnabled = false;
        button.Content = "Sending…";
        Status.IsVisible = false;
        string? failure = null;
        var delivered = false;
        try
        {
            failure = await DeliverToMessengerAsync(message);
            delivered = failure is null;
        }
        finally
        {
            telegramSending = false;
            if (!closing)
            {
                TelegramSendButton.IsEnabled = TelegramSendAllButton.IsEnabled = true;
                // A short confirmation on the button itself: the status line is
                // for errors, and a sent message needs no standing notice.
                button.Content = delivered ? "Sent" : restingLabel;
            }
        }
        if (closing) return;
        if (failure is not null) ShowError(failure);
        else if (delivered) await RestoreTelegramButtonAsync(button, restingLabel);
    }

    // One delivery to the chosen chat app. Returns null when the message was
    // accepted, otherwise the text to show the user. The buttons and auto-send
    // both come through here, so neither drives the page while the other holds it.
    private async Task<string?> DeliverToMessengerAsync(string message)
    {
        if (BrowserUnavailable is { } unavailable) return unavailable;
        var target = settings.MessengerTarget;
        try
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await BrowserSendAsync(message, target, null, cancellation.Token);
            return null;
        }
        catch (OperationCanceledException)
        {
            return $"{target} did not accept the message in time. Check its page in panel 06.";
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or HttpRequestException)
        {
            return error.Message;
        }
    }

    // Auto-send forwards the answer a few sentences at a time while the AI is
    // still typing. A progress update only nudges the pump: one send holds the
    // chat page, so the pump drains whatever accumulated while it was busy.
    private async Task PumpAutoSendAsync(int outputVersion)
    {
        if (autoSendPumping) return;
        autoSendPumping = true;
        try
        {
            while (!closing && autoSendActive && outputVersion == answerOutputVersion)
            {
                // A button press owns the page until it finishes.
                if (telegramSending) { await Task.Delay(200); continue; }
                var sentences = autoSendSentences;
                // The opening chunk is small so the reader sees something while
                // the answer is still being written; the rest come in larger.
                var target = autoSendMessages == 0 ? settings.AutoSendFirstChars : settings.AutoSendNextChars;
                var chunk = autoSendFinal
                    ? AnswerChunker.RemainingChunk(answerText, sentences, out sentences)
                    : AnswerChunker.NextChunk(answerText, sentences, target, out sentences);
                if (chunk is null) return;
                // The flush carries the end of the answer, so it is the piece
                // that closes it. The ones before it are the middle.
                var message = autoSendFinal ? WithEndMark(chunk) : chunk;
                if (autoSendMessages == 0) message = AutoSendSeparator + "\n" + message;
                autoSendSentences = sentences;
                telegramSending = true;
                string? failure;
                try { failure = await DeliverToMessengerAsync(message); }
                finally { telegramSending = false; }
                if (failure is null) autoSendMessages++;
                if (failure is not null)
                {
                    // Carrying on would leave holes in what the reader receives.
                    autoSendActive = false;
                    autoSendFailed = true;
                    if (!closing)
                    {
                        ShowError($"Auto-send stopped: {failure}");
                        UpdateAutoSendHint();
                    }
                    return;
                }
                // The flush is the whole rest of the answer in one message, so
                // the run is over: nothing may be taken from this answer again.
                if (autoSendFinal) autoSendActive = false;
                if (!closing) UpdateAutoSendHint();
                if (!autoSendActive) return;
            }
        }
        finally { autoSendPumping = false; }
    }

    // Auto-send is switched on the panel 05 bar, beside the buttons it feeds,
    // because it is turned on and off for one answer at a time.
    private void AutoSend_Changed(object? sender, RoutedEventArgs e)
    {
        if (suppressAutoSendChange || !initialized) return;
        var enabled = AutoSendCheck.IsChecked == true;
        // The hint keeps the box in step with the setting, so only a real change
        // of mind re-anchors a run that is already under way.
        var changed = enabled != settings.AutoSendAnswer;
        if (changed) settings.SaveAutoSend(enabled, settings.AutoSendFirstChars, settings.AutoSendNextChars);
        if (!enabled) autoSendActive = false;
        else if (changed && answerCancellation is not null)
        {
            // Switched on while an answer is arriving: start from what comes
            // next, so the part already on screen is not sent a second time.
            autoSendSentences = AnswerChunker.SentenceCount(answerText);
            autoSendActive = true;
            autoSendFailed = false;
            // Its first message still opens with the separator and the small
            // size, so the reader sees where this answer started.
            autoSendMessages = 0;
        }
        UpdateAutoSendHint();
    }

    private void UpdateAutoSendHint()
    {
        suppressAutoSendChange = true;
        AutoSendCheck.IsChecked = settings.AutoSendAnswer;
        suppressAutoSendChange = false;
        if (!settings.AutoSendAnswer)
        {
            AutoSendHint.IsVisible = false;
            return;
        }
        AutoSendHint.IsVisible = true;
        // A run that was stopped by a refusal says so, so the count on the bar is
        // never read as "all of it went out".
        AutoSendHint.Text = autoSendFailed ? $"stopped · {autoSendMessages} sent"
            : autoSendMessages > 0 ? $"{autoSendMessages} sent"
            : $"{settings.AutoSendFirstChars} then {settings.AutoSendNextChars}";
        ToolTip.SetTip(AutoSendHint,
            $"The answer goes to {settings.MessengerTarget} as it is written: about {settings.AutoSendFirstChars} " +
            $"characters first, then about {settings.AutoSendNextChars} at a time, in whole sentences");
    }

    private async Task RestoreTelegramButtonAsync(Button button, string restingLabel)
    {
        var token = ++telegramConfirmation;
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        if (!closing && token == telegramConfirmation) button.Content = restingLabel;
    }

    // Cmd+2, Cmd+5 and Cmd+6 hide or show their sections.
    private bool TryToggleSection(Key key, KeyModifiers modifiers)
    {
        if (modifiers != KeyModifiers.Meta) return false;
        ToggleButton section;
        if (key is Key.D2 or Key.NumPad2) section = TranslationCollapseToggle;
        else if (key is Key.D5 or Key.NumPad5) section = AiPanelCollapseToggle;
        else if (key is Key.D6 or Key.NumPad6) section = ChatPanelCollapseToggle;
        else return false;
        section.IsChecked = section.IsChecked != true;
        if (section == TranslationCollapseToggle) ApplyTranslationCollapsed();
        else if (section == AiPanelCollapseToggle) ApplyAiPanelCollapsed();
        else ApplyChatPanelCollapsed();
        SaveCollapsedSections();
        return true;
    }

    private void ApplySendShortcut()
    {
        if (!Shortcut.TryParse(settings.SendShortcut, out sendShortcut))
        {
            sendShortcut = new Shortcut(KeyModifiers.Meta | KeyModifiers.Shift, Key.Z);
            settings.SendShortcut = sendShortcut.ToString();
        }
        SendShortcutHint.Text = $"{sendShortcut}  Send";
        if (answerCancellation is null) ToolTip.SetTip(AnswerSendButton, $"Send captions ({sendShortcut})");

        if (!Shortcut.TryParse(settings.TelegramShortcut, out telegramShortcut))
        {
            telegramShortcut = new Shortcut(KeyModifiers.Meta | KeyModifiers.Shift, Key.T);
            settings.TelegramShortcut = telegramShortcut.ToString();
        }
        ToolTip.SetTip(TelegramSendButton,
            $"Send the text selected in the AI answer to {settings.MessengerTarget}, or all of it when nothing is selected ({telegramShortcut})");

        if (!Shortcut.TryParse(settings.TelegramAllShortcut, out telegramAllShortcut))
        {
            telegramAllShortcut = new Shortcut(KeyModifiers.Meta | KeyModifiers.Shift, Key.A);
            settings.TelegramAllShortcut = telegramAllShortcut.ToString();
        }
        ToolTip.SetTip(TelegramSendAllButton,
            $"Send the whole AI answer to {settings.MessengerTarget}, ignoring any selection ({telegramAllShortcut})");
    }

    private async void AnswerSend_Click(object? sender, RoutedEventArgs e)
    {
        if (answerCancellation is { } running)
        {
            answerStopped = true;
            // Nothing more from this answer should reach the chat.
            autoSendActive = false;
            UpdateAutoSendHint();
            running.Cancel();
            return;
        }
        await SendAnswerAsync();
    }

    private void MoveCaptionCaretToEnd()
    {
        var length = TextOf(PcText).Length;
        savedCaptionLocation = length;
        hasSavedCaptionLocation = true;
        PcText.Focus();
        PcText.CaretIndex = length;
    }

    private string GetAnswerPrompt()
    {
        if (string.IsNullOrWhiteSpace(PcText.Text)) throw new InvalidOperationException("PC caption text is empty.");
        var text = TextOf(PcText);
        var start = hasSavedCaptionLocation ? Math.Clamp(savedCaptionLocation, 0, text.Length) : 0;
        var prompt = text[start..].Trim();
        if (prompt.Length == 0)
            throw new InvalidOperationException("There is no text after the saved starting point. Click an earlier word in PC AUDIO, or select the text you want to send.");
        return prompt;
    }

    private async Task SendAnswerAsync()
    {
        if (closing || answerCancellation is not null) return;
        if (BrowserUnavailable is { } unavailable) { ShowError(unavailable); return; }
        string prompt;
        try { prompt = GetAnswerPrompt(); }
        catch (InvalidOperationException error) { ShowError(error.Message); return; }
        MoveCaptionCaretToEnd();
        using var cancellation = new CancellationTokenSource();
        answerCancellation = cancellation;
        answerStopped = false;
        AnswerSendButton.Content = StopLabel;
        ToolTip.SetTip(AnswerSendButton, "Stop reading this reply. The AI keeps writing it on the page.");
        var outputVersion = ++answerOutputVersion;
        var provider = settings.AiProvider;
        Status.IsVisible = false;
        void Display(string text) => DisplayAnswer(text, outputVersion);
        // Each answer starts its own auto-send run, so a setting changed between
        // answers takes effect and the sentence count starts from zero.
        autoSendActive = settings.AutoSendAnswer;
        autoSendFinal = false;
        autoSendFailed = false;
        autoSendSentences = 0;
        autoSendMessages = 0;
        UpdateAutoSendHint();
        try
        {
            var response = await BrowserSendAsync(prompt, provider, Display, cancellation.Token);
            Display(response);
            if (autoSendActive && outputVersion == answerOutputVersion)
            {
                // The reply is finished, so a trailing fragment no longer has to
                // wait for punctuation that will never arrive.
                autoSendFinal = true;
                await PumpAutoSendAsync(outputVersion);
            }
        }
        catch (OperationCanceledException)
        {
            // Stopping is a decision, not a failure: the page keeps the reply
            // and whatever was read is still there to send by hand.
            if (!closing && !answerStopped)
                ShowError("The AI request timed out. Check the page in panel 05 for the reply or a sign-in or usage-limit message.");
        }
        catch (Exception error) { if (!closing) ShowError(error.Message); }
        finally
        {
            answerCancellation = null;
            if (!closing)
            {
                AnswerSendButton.Content = SendLabel;
                ApplySendShortcut();
            }
        }
    }

    private void DisplayAnswer(string text, int outputVersion) => UI(() =>
    {
        if (closing || outputVersion != answerOutputVersion) return;
        answerText = text;
        if (autoSendActive) _ = PumpAutoSendAsync(outputVersion);
    });

    private async void Save_Click(object? sender, RoutedEventArgs e)
    {
        var button = (Button)sender!;
        var (box, filename) = (button.Tag as string) switch
        {
            "Translation" => (TranslationText, "translation.txt"),
            "Microphone" => (MicText, "microphone.txt"),
            // The answer has no box of its own; it is saved from what was read.
            "Answer" => (null, "answer.txt"),
            _ => (PcText, "captions.txt")
        };
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save caption text",
            SuggestedFileName = filename,
            DefaultExtension = "txt",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("Text files") { Patterns = new[] { "*.txt" } },
                FilePickerFileTypes.All
            }
        });
        if (file is null) return;
        var text = box is null ? answerText : TextOf(box);
        button.IsEnabled = false;
        try
        {
            await using var stream = await file.OpenWriteAsync();
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            await writer.WriteAsync(text);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            if (!closing) ShowError(error.Message);
        }
        finally { if (!closing) button.IsEnabled = true; }
    }

    private async Task UpdatePcAsync()
    {
        var needed = PcToggle.IsChecked == true || (TranslationToggle.IsChecked == true && translationUsesAudio);
        if (needed && pc is null)
        {
            var generation = ++pcGeneration;
            Action<string> onCaption = text => UI(() =>
            {
                if (generation != pcGeneration) return;
                if (PcToggle.IsChecked == true || (TranslationToggle.IsChecked == true && translationUsesAudio))
                    CommitLiveCaption(text);
            });
            Action<string> onPreview = text => UI(() =>
            {
                if (generation == pcGeneration &&
                    (PcToggle.IsChecked == true || (TranslationToggle.IsChecked == true && translationUsesAudio)))
                    SetLiveCaption(text);
            });
            Action<string> onStatus = message => WorkerMessage(message, () => generation == pcGeneration);
            await engine.InitializeAsync();
            var session = new AudioSession(CaptureFactory.CreateSystemAudio(settings),
                engine, settings, "en", onCaption, onStatus, onPreview);
            try
            {
                await session.StartAsync();
                pc = session;
            }
            catch { await session.DisposeAsync(); throw; }
        }
        else if (!needed && pc is not null)
        {
            ++pcGeneration;
            await pc.DisposeAsync(); pc = null;
        }
    }

    private async void Settings_Click(object? sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(settings);
        if (await dialog.ShowDialog<bool>(this) != true)
        {
            AppTheme.Apply(settings.Theme);
            return;
        }
        await ChangeAsync(async () =>
        {
            var previousProvider = settings.AiProvider;
            var previousMessenger = settings.MessengerTarget;
            settings.SaveAiPreferences(dialog.AiProvider, dialog.AiProject, dialog.AiSession);
            settings.SaveSendShortcut(dialog.SendShortcut);
            settings.SaveTelegramShortcut(dialog.TelegramShortcut);
            settings.SaveTelegramAllShortcut(dialog.TelegramAllShortcut);
            settings.SaveMessengerTarget(dialog.MessengerTarget);
            // The toggle lives on the panel 05 bar; Settings only sets the sizes.
            settings.SaveAutoSend(settings.AutoSendAnswer, dialog.AutoSendFirstChars, dialog.AutoSendNextChars);
            if (dialog.StartAtLogin != settings.StartAtLogin)
            {
                var startupError = LoginItem.Apply(dialog.StartAtLogin);
                if (startupError is not null) ShowError(startupError);
                else settings.SaveStartAtLogin(dialog.StartAtLogin);
            }
            if (dialog.RestoreShortcut != settings.RestoreShortcut)
            {
                var previous = settings.RestoreShortcut;
                if (RegisterRestoreShortcut(dialog.RestoreShortcut))
                    settings.SaveRestoreShortcut(dialog.RestoreShortcut);
                else
                {
                    RegisterRestoreShortcut(previous);
                    ShowError($"{dialog.RestoreShortcut} is already in use. The restore shortcut remains {previous}.");
                }
            }
            settings.SaveTheme(dialog.SelectedTheme);
            // Persist every Settings value on every Save, including the source
            // selected when it is already the current source.
            var audioSourceChanged = settings.SystemAudioSource != dialog.SelectedAudioSource;
            settings.SaveSystemAudioSource(dialog.SelectedAudioSource);
            AppTheme.Apply(settings.Theme);
            settings.SaveEditorAppearance(dialog.PcFontSize, dialog.PcTextColor, dialog.PcCaretColor,
                dialog.TranslationFontSize, dialog.TranslationTextColor);
            ApplyEditorAppearance();
            ApplySendShortcut();
            UpdateAutoSendHint();
            if (audioSourceChanged)
            {
                UpdateCaptionSourceHint();
                if (pc is not null) { ++pcGeneration; await pc.DisposeAsync(); pc = null; }
                await UpdatePcAsync();
            }
            ApplyBrowserPanels();
            if (previousProvider != settings.AiProvider || previousMessenger != settings.MessengerTarget)
            {
                // Only the panel whose page actually changed is reloaded.
                webViews?.Navigate(settings.AiProvider, settings.MessengerTarget);
            }
        });
    }

    private async void PcToggle_Click(object? sender, RoutedEventArgs e)
    {
        await ChangeAsync(async () =>
        {
            translationUsesAudio = PcToggle.IsChecked != true && TranslationToggle.IsChecked == true;
            try { await UpdatePcAsync(); }
            catch { PcToggle.IsChecked = false; throw; }
        });
    }

    private async void TranslationToggle_Click(object? sender, RoutedEventArgs e)
    {
        await ChangeAsync(async () =>
        {
            if (TranslationToggle.IsChecked == true)
            {
                var generation = ++translationGeneration;
                translatedSource = translatedTarget = "";
                documentTranslator.Clear();
                var existingCaption = TextOf(PcText).Trim();
                if (existingCaption.Length > 0)
                {
                    pcFinalCaptionText = existingCaption;
                    ++pcCaptionCommitVersion;
                }
                translationUsesAudio = PcToggle.IsChecked != true;
                try
                {
                    translation = new TranslationSession(settings, "eng_Latn", _ => { },
                        message => WorkerMessage(message, () => generation == translationGeneration));
                    await translation.StartAsync();
                    await UpdatePcAsync();
                    ScheduleTranslation();
                }
                catch
                {
                    TranslationToggle.IsChecked = false; ++translationGeneration;
                    translationUsesAudio = false;
                    if (translation is not null) { await translation.DisposeAsync(); translation = null; }
                    await UpdatePcAsync();
                    throw;
                }
            }
            else
            {
                ++translationGeneration;
                translationUsesAudio = false;
                if (translation is not null) { await translation.DisposeAsync(); translation = null; }
                await UpdatePcAsync();
            }
        });
    }

    private async void MicToggle_Click(object? sender, RoutedEventArgs e)
    {
        await ChangeAsync(async () =>
        {
            if (MicToggle.IsChecked == true)
            {
                try
                {
                    await engine.InitializeAsync();
                    var generation = ++micGeneration;
                    mic = new AudioSession(CaptureFactory.CreateMicrophone(), engine, settings, "en", text => UI(() =>
                    {
                        if (generation == micGeneration && MicToggle.IsChecked == true) QueueCaption(text, pcAudio: false);
                    }), message => WorkerMessage(message, () => generation == micGeneration));
                    await mic.StartAsync();
                }
                catch
                {
                    MicToggle.IsChecked = false;
                    if (mic is not null) { await mic.DisposeAsync(); mic = null; }
                    throw;
                }
            }
            else
            {
                ++micGeneration;
                if (mic is not null) { await mic.DisposeAsync(); mic = null; }
            }
        });
    }

    private async Task ChangeAsync(Func<Task> action)
    {
        await changes.WaitAsync();
        if (closing) { changes.Release(); return; }
        PcToggle.IsEnabled = TranslationToggle.IsEnabled = MicToggle.IsEnabled = SettingsButton.IsEnabled = false;
        Status.IsVisible = false;
        try { await action(); }
        catch (Exception e) { ShowError(e.Message); }
        finally
        {
            PcToggle.IsEnabled = TranslationToggle.IsEnabled = MicToggle.IsEnabled = SettingsButton.IsEnabled = true;
            changes.Release();
        }
    }

    private async void Window_Closing(object? sender, WindowClosingEventArgs e)
    {
        if (closed) return;
        // Closing a visible window always means "get out of my way": it returns
        // to the menu bar and keeps capturing. A hidden window has no reachable
        // close, so a close arriving there is the system shutting us down and
        // must be allowed through.
        if (!exiting && IsVisible)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }
        e.Cancel = true;
        if (closing) return;
        closing = true; IsEnabled = false;
        answerCancellation?.Cancel();
        await changes.WaitAsync();
        try
        {
            if (pc is not null) await pc.DisposeAsync();
            if (mic is not null) await mic.DisposeAsync();
            if (translation is not null) await translation.DisposeAsync();
            if (webViews is not null) await webViews.DisposeAsync();
            if (initialized) engine.Dispose();
        }
        finally
        {
            showRequests?.Dispose();
            showRequests = null;
            SingleInstance.Release();
            if (restoreHotkeyRegistered) MacHotkey.Unregister();
            restoreHotkeyRegistered = false;
            closed = true;
            if (trayIcon is not null)
            {
                trayIcon.IsVisible = false;
                trayIcon.Dispose();
                trayIcon = null;
            }
            changes.Release();
            // Cleanup may finish synchronously. Closing again, and the shutdown
            // that follows it, must run after this handler has returned.
            Dispatcher.UIThread.Post(() =>
            {
                Close();
                if (Application.Current?.ApplicationLifetime is
                    Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
                    desktop.Shutdown();
            });
        }
    }
}
