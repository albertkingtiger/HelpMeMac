using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Automation;
using System.Windows.Threading;
using System.Windows.Interop;
using System.Windows.Input;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using System.Net.Http;
using Microsoft.Win32;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;
using Drawing2D = System.Drawing.Drawing2D;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;
using ScrollBar = System.Windows.Controls.Primitives.ScrollBar;
using Clipboard = System.Windows.Clipboard;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using HelpMe.Services;

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
    private readonly Forms.NotifyIcon trayIcon;
    private readonly Drawing.Icon trayAppIcon;
    private bool initialized;
    private int pcGeneration;
    private int micGeneration;
    private int translationGeneration;
    private readonly DispatcherTimer captionFlushTimer = new() { Interval = TimeSpan.FromMilliseconds(900) };
    private readonly DispatcherTimer liveCaptionTranslationTimer = new() { Interval = TimeSpan.FromMilliseconds(1000) };
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
    private KeyGesture sendShortcut = new(Key.Z, ModifierKeys.Control | ModifierKeys.Shift);
    private KeyGesture telegramShortcut = new(Key.T, ModifierKeys.Control | ModifierKeys.Shift);
    private KeyGesture telegramAllShortcut = new(Key.A, ModifierKeys.Control | ModifierKeys.Shift);
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
    private const string AutoSendSeparator = "--------------------------------------------------";
    private bool startedInTray;
    private EventWaitHandle? showRequest;
    private RegisteredWaitHandle? showRequestWait;
    // Collapsing replaces these with Auto; restoring puts back whatever size the
    // user had dragged the splitters to.
    private GridLength translationRowHeight = new(1, GridUnitType.Star);
    private GridLength answerColumnWidth = new(1, GridUnitType.Star);
    private GridLength browserColumnWidth = new(1.3, GridUnitType.Star);
    private HwndSource? hotkeySource;
    private bool restoreHotkeyRegistered;
    private const int RestoreHotkeyId = 0x484D;
    private const int WmHotkey = 0x0312;
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint virtualKey);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr window, int id);

    public MainWindow()
    {
        InitializeComponent();
        // Background mode: the window is still created so SourceInitialized can
        // register the global shortcut and Loaded can start capture, but it is
        // minimized and off the taskbar so it never paints before going to the
        // tray. Exit lives only in the tray menu.
        ShowActivated = false;
        ShowInTaskbar = false;
        trayAppIcon = CreateTrayAppIcon();
        trayIcon = CreateTrayIcon();
        captionFlushTimer.Tick += (_, _) => FlushCaptionBuffers();
        liveCaptionTranslationTimer.Tick += (_, _) =>
        {
            liveCaptionTranslationTimer.Stop();
            if (settings.CaptionMethod != CaptionMethod.WindowsLiveCaptions || !pcLiveCaptionActive) return;
            var source = PcText.Text.Trim();
            if (source == pcFinalCaptionText) return;
            pcFinalCaptionText = source;
            ++pcCaptionCommitVersion;
            ScheduleTranslation();
        };
        try
        {
            settings = AppSettings.Load();
            AppTheme.Apply(settings.Theme);
            UpdateCaptionSourceHint();
            UpdateCaptionWindowControl();
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
            SourceInitialized += (_, _) =>
            {
                hotkeySource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
                hotkeySource?.AddHook(HotkeyHook);
                if (!RegisterRestoreShortcut(settings.RestoreShortcut))
                    ShowError($"Could not register {settings.RestoreShortcut} to restore Help Me!; choose another shortcut in Settings.");
            };
            SizeChanged += (_, _) => { if (initialized) settings.SaveWindowSize(Width, Height); };
            // Signing out or shutting down is a real exit, not a trip to the tray.
            if (System.Windows.Application.Current is { } host)
                host.SessionEnding += (_, _) => exiting = true;
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

    private async Task StartBrowserAsync()
    {
        if (closing) return;
        webViews ??= new WebViewBrowserSession(AiWebView, ChatWebView);
        try { await WhileOnScreenAsync(() => webViews.StartAsync(settings.AiProvider, settings.MessengerTarget)); }
        catch (Exception error) when (error is IOException or InvalidOperationException)
        {
            if (!closing) ShowError(error.Message);
        }
    }

    // WebView2 creates its browser process only once its window has been shown,
    // and Help Me! starts hidden in the tray. Showing the window off the desktop
    // for those few seconds is invisible, and it goes straight back to the tray
    // afterwards unless the user asked for it in the meantime.
    private async Task WhileOnScreenAsync(Func<Task> work)
    {
        if (IsVisible) { await work(); return; }
        var left = Left;
        var top = Top;
        primingPanels = true;
        shownWhilePriming = false;
        ShowInTaskbar = false;
        Left = -32000;
        Top = -32000;
        Show();
        try { await work(); }
        finally
        {
            primingPanels = false;
            if (shownWhilePriming)
            {
                PlaceOnScreen(left, top);
                RestoreFromTray();
            }
            else
            {
                Hide();
                PlaceOnScreen(left, top);
            }
        }
    }

    // Left and Top are NaN until Windows has placed the window itself, and NaN
    // cannot be assigned back, so an unplaced window is centred by hand.
    private void PlaceOnScreen(double left, double top)
    {
        if (!double.IsNaN(left) && !double.IsNaN(top)) { Left = left; Top = top; return; }
        var work = SystemParameters.WorkArea;
        Left = work.Left + Math.Max(0, (work.Width - Width) / 2);
        Top = work.Top + Math.Max(0, (work.Height - Height) / 2);
    }

    private (ToggleButton, ToggleButton, ToggleButton, ToggleButton, Button, Button,
        TextBox, TextBox, TextBox, TextBlock, TextBlock) BuildUi()
    {
        Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(11, 18, 32));
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI");
        Width = 1060; Height = 860; MinWidth = 700; MinHeight = 300;
        var root = new Grid { Margin = new Thickness(28) };
        for (var i = 0; i < 6; i++) root.RowDefinitions.Add(new RowDefinition { Height = i < 2 || i == 5 ? GridLength.Auto : new GridLength(1, GridUnitType.Star) });
        var title = new DockPanel { Margin = new Thickness(0, 0, 0, 16) };
        var badge = new Border { Background = Brush("#183A36"), CornerRadius = new CornerRadius(12), Padding = new Thickness(12, 7, 12, 7), VerticalAlignment = VerticalAlignment.Center, Child = new TextBlock { Text = "LOCAL · OFFLINE", Foreground = Brush("#8AE2C6"), FontSize = 11, FontWeight = FontWeights.Bold } };
        DockPanel.SetDock(badge, Dock.Right); title.Children.Add(badge);
        SettingsButton = Button("Settings", Settings_Click); SettingsButton.Margin = new Thickness(0, 0, 12, 0); SettingsButton.VerticalAlignment = VerticalAlignment.Center; DockPanel.SetDock(SettingsButton, Dock.Right); title.Children.Add(SettingsButton);
        var heading = new StackPanel(); heading.Children.Add(new TextBlock { Text = "Help Me!", FontSize = 32, FontWeight = FontWeights.Bold }); heading.Children.Add(new TextBlock { Text = "Listen. Understand. Speak.", Foreground = Brush("#A1ADC5"), Margin = new Thickness(0, 4, 0, 0) }); title.Children.Add(heading); root.Children.Add(title);
        var languages = new Grid { Margin = new Thickness(0, 0, 0, 16) }; languages.ColumnDefinitions.Add(new ColumnDefinition()); languages.ColumnDefinitions.Add(new ColumnDefinition()); languages.Children.Add(new TextBlock { Text = "PC audio language: English", Foreground = Brush("#A1ADC5"), VerticalAlignment = VerticalAlignment.Center }); var micLang = new TextBlock { Text = "Microphone language: English", Foreground = Brush("#A1ADC5"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16, 0, 0, 0) }; Grid.SetColumn(micLang, 1); languages.Children.Add(micLang); Grid.SetRow(languages, 1); root.Children.Add(languages);
        var pcPanel = Panel("01   PC AUDIO", "#8DAEFF", out var pcHeader, out var pcBox); PcText = pcBox; PcToggle = Toggle(PcToggle_Click); FormatHeader(pcPanel, PcToggle, null, Button("Format", Format_Click), SaveButton = Button("Save", Save_Click), pcHeader); Grid.SetRow(pcPanel, 2); root.Children.Add(pcPanel);
        var trPanel = Panel("02   Translation", "#79DDC2", out var trHeader, out var trBox); TranslationText = trBox; TranslationToggle = Toggle(TranslationToggle_Click); FormatHeader(trPanel, TranslationToggle, null, Button("Format", TranslationFormat_Click), Button("Save", Save_Click, "Translation"), trHeader); Grid.SetRow(trPanel, 3); root.Children.Add(trPanel);
        var micPanel = Panel("03   MICROPHONE", "#D0AAFF", out var micHeader, out var micBox); MicText = micBox; MicToggle = Toggle(MicToggle_Click); FormatHeader(micPanel, MicToggle, null, Button("Format", MicFormat_Click), Button("Save", Save_Click, "Microphone"), micHeader); Grid.SetRow(micPanel, 4); root.Children.Add(micPanel);
        Status = new TextBlock { Visibility = Visibility.Collapsed, Foreground = Brush("#FFB4A8"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 16, 0, 0) }; Grid.SetRow(Status, 5); root.Children.Add(Status); Content = root;
        return (PcToggle, TranslationToggle, MicToggle, null!, SettingsButton, SaveButton, PcText, TranslationText, MicText, Status, null!);
    }
    private static System.Windows.Media.SolidColorBrush Brush(string hex) => new((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));
    private Button Button(string text, RoutedEventHandler click, string? tag = null) { var b = new Button { Content = text, Tag = tag, Margin = new Thickness(0, 0, 8, 0) }; b.Click += click; return b; }
    private ToggleButton Toggle(RoutedEventHandler click) { var b = new ToggleButton(); b.Click += click; return b; }
    private Border Panel(string title, string color, out TextBlock method, out TextBox box) { var border = new Border { Style = (Style)FindResource("Panel"), Margin = new Thickness(0, 0, 0, title.StartsWith("03") ? 0 : 12) }; var grid = new Grid(); grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(30) }); grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); border.Child = grid; method = new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeights.Bold, Foreground = Brush(color), VerticalAlignment = VerticalAlignment.Center }; box = new TextBox { Margin = new Thickness(0), FontSize = 15 }; AutomationProperties.SetName(box, title); Grid.SetRow(box, 1); grid.Children.Add(box); return border; }
    private void FormatHeader(Border panel, ToggleButton toggle, ToggleButton? windowToggle, Button format, Button save, TextBlock? header, TextBlock? subheader = null) { var grid = (Grid)panel.Child; var dock = new DockPanel { Height = 30, Margin = new Thickness(0) }; Grid.SetRow(dock, 0); DockPanel.SetDock(toggle, Dock.Right); dock.Children.Add(toggle); if (windowToggle is not null) { DockPanel.SetDock(windowToggle, Dock.Right); windowToggle.Margin = new Thickness(0, 0, 8, 0); dock.Children.Add(windowToggle); } DockPanel.SetDock(format, Dock.Right); dock.Children.Add(format); DockPanel.SetDock(save, Dock.Right); dock.Children.Add(save); if (header is not null) { var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center }; stack.Children.Add(header); if (subheader is not null) stack.Children.Add(subheader); dock.Children.Add(stack); } grid.Children.Add(dock); }

    private void UI(Action action) => Dispatcher.BeginInvoke(() => { if (!closing) action(); });
    private void ShowError(string message)
    {
        Status.Text = message;
        Status.Visibility = Visibility.Visible;
    }
    private void WorkerMessage(string message, Func<bool> isCurrent)
    {
        // Routine worker updates never enter the UI queue.
        if (message.StartsWith("Error:", StringComparison.Ordinal))
            UI(() => { if (isCurrent()) ShowError(message); });
    }
    private void Append(TextBox box, string text)
    {
        var chunk = Regex.Replace(text ?? "", @"\s+", " ").Trim();
        if (chunk.Length == 0) return;
        UpdateTextPreservingView(box, () =>
        {
            // A lowercase continuation should stay on the same natural sentence.
            if (box.Text.Length > 0 && char.IsLower(chunk[0]) && box.Text[^1] is '.' or '!' or '?')
                box.Text = box.Text[..^1];
            var separator = box.Text.Length > 0 && !char.IsWhiteSpace(box.Text[^1]) && !char.IsPunctuation(chunk[0]) ? " " : "";
            box.AppendText(separator + chunk);
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
    {
        UpdateTextPreservingView(PcText, () => ReplaceLiveCaption(text));
        // Live Captions may leave its final snapshot onscreen indefinitely.
        // Translate a stable preview without waiting for the next utterance.
        liveCaptionTranslationTimer.Stop();
        liveCaptionTranslationTimer.Start();
    }

    private void ReplaceLiveCaption(string text)
    {
        if (!pcLiveCaptionActive)
        {
            if (PcText.Text.Length > 0 && !char.IsWhiteSpace(PcText.Text[^1])) PcText.AppendText(" ");
            pcLiveCaptionStart = PcText.Text.Length;
            pcLiveCaptionActive = true;
        }
        var start = Math.Clamp(pcLiveCaptionStart, 0, PcText.Text.Length);
        PcText.Text = PcText.Text[..start] + text.Trim();
    }

    private void CommitLiveCaption(string text)
    {
        liveCaptionTranslationTimer.Stop();
        UpdateTextPreservingView(PcText, () =>
        {
            ReplaceLiveCaption(text);
            var trimmed = text.TrimEnd();
            if (trimmed.Length > 0 && trimmed[^1] is '.' or '!' or '?' or ':' or ';')
                PcText.AppendText(Environment.NewLine);
            else if (PcText.Text.Length > 0 && !char.IsWhiteSpace(PcText.Text[^1]))
                PcText.AppendText(" ");
            pcLiveCaptionActive = false;
        });
        // Translation follows each finalized PC-audio caption segment. Interim
        // Whisper previews only revise section 01 and never start translation.
        pcFinalCaptionText = PcText.Text.Trim();
        ++pcCaptionCommitVersion;
        ScheduleTranslation();
    }

    private static void UpdateTextPreservingView(TextBox box, Action update)
    {
        var follow = IsNearBottom(box);
        var verticalOffset = box.VerticalOffset;
        var horizontalOffset = box.HorizontalOffset;
        var selectionStart = box.SelectionStart;
        var selectionLength = box.SelectionLength;
        using (box.DeclareChangeBlock())
        {
            update();
            // Resolve text layout first so restoring the selection does not
            // queue a caret scroll that would override the viewport below.
            box.UpdateLayout();
            var start = Math.Min(selectionStart, box.Text.Length);
            box.Select(start, Math.Min(selectionLength, box.Text.Length - start));
        }
        // Lay out the new text and caret before restoring the viewport. Focus
        // must never override a user's decision to scroll away from the bottom.
        box.UpdateLayout();
        if (follow) box.ScrollToEnd();
        else box.ScrollToVerticalOffset(verticalOffset);
        box.ScrollToHorizontalOffset(horizontalOffset);
    }

    private static bool IsNearBottom(TextBox box)
        => box.ExtentHeight <= box.ViewportHeight ||
           box.VerticalOffset >= box.ExtentHeight - box.ViewportHeight - 32;

    private void PcText_TextChanged(object sender, TextChangedEventArgs e)
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
            TranslationText.Clear();
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

    private async void Format_Click(object sender, RoutedEventArgs e)
    {
        liveCaptionTranslationTimer.Stop();
        pcTempStrVar = "";
        pcLiveCaptionActive = false;
        PcText.Clear();
        pcFinalCaptionText = "";
        ++pcCaptionCommitVersion;
        translatedCaptionCommitVersion = pcCaptionCommitVersion;
        translatedSource = translatedTarget = "";
        documentTranslator.Clear();
        if (settings.CaptionMethod == CaptionMethod.WindowsLiveCaptions)
            await ChangeAsync(async () =>
            {
                if (pc is not null) { ++pcGeneration; await pc.DisposeAsync(); pc = null; }
                if (PcToggle.IsChecked == true) await UpdatePcAsync();
            });
    }

    private void TranslationFormat_Click(object sender, RoutedEventArgs e)
    {
        ++translationOutputVersion;
        translatedSource = PcText.Text.Trim();
        translatedCaptionCommitVersion = pcCaptionCommitVersion;
        translatedTarget = "";
        documentTranslator.Clear();
        TranslationText.Clear();
    }

    private void MicFormat_Click(object sender, RoutedEventArgs e)
    {
        micTempStrVar = "";
        MicText.Clear();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => HideToTray();

    private void TranslationCollapse_Click(object sender, RoutedEventArgs e)
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

    private void AiPanelCollapse_Click(object sender, RoutedEventArgs e)
    {
        ApplyAiPanelCollapsed();
        SaveCollapsedSections();
    }

    private void ChatPanelCollapse_Click(object sender, RoutedEventArgs e)
    {
        ApplyChatPanelCollapsed();
        SaveCollapsedSections();
    }

    private void BrowserReload_Click(object sender, RoutedEventArgs e)
        => webViews?.Reload(((Button)sender).Tag as string == "Ai");

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
        AiWebView.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        TelegramBar.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        AiHeaderActions.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        // The AI page is the only thing in the middle column, so collapsing it
        // hands the whole width to the captions and the chat.
        if (collapsed)
        {
            if (AnswerColumn.Width.IsStar) answerColumnWidth = AnswerColumn.Width;
            AnswerColumn.Width = GridLength.Auto;
            AnswerSplitterColumn.Width = new GridLength(0);
            AnswerSplitter.Visibility = Visibility.Collapsed;
        }
        else
        {
            AnswerColumn.Width = answerColumnWidth;
            AnswerSplitterColumn.Width = new GridLength(5);
            AnswerSplitter.Visibility = Visibility.Visible;
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
        ChatWebView.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        ChatPanelReloadButton.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
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
        TranslationSplitter.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        TranslationText.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
    }

    // Background start. The window is deliberately never shown: calling Hide()
    // from Loaded raced the Show() that raised it and sometimes lost, leaving the
    // window on screen. Creating the handle directly raises SourceInitialized so
    // the global shortcut still registers, and the first Show comes from the tray.
    public void StartInBackground()
    {
        if (startedInTray || !initialized) return;
        startedInTray = true;
        new WindowInteropHelper(this).EnsureHandle();
        ListenForShowRequests();
        trayIcon.ShowBalloonTip(1500, "Help Me!",
            $"Running in the background. Press {settings.RestoreShortcut} or double-click this icon to open it.",
            Forms.ToolTipIcon.Info);
        _ = BootstrapAsync();
    }

    // Launching Help Me! again while it is already running in the tray should
    // bring the existing window up rather than start a second copy.
    private void ListenForShowRequests()
    {
        try
        {
            showRequest = SingleInstance.CreateShowListener();
            showRequestWait = ThreadPool.RegisterWaitForSingleObject(showRequest,
                (_, _) => Dispatcher.BeginInvoke(() => { if (!closing) RestoreFromTray(); }),
                null, Timeout.Infinite, executeOnlyOnce: false);
        }
        catch (WaitHandleCannotBeOpenedException) { }
        catch (UnauthorizedAccessException) { }
    }

    private async Task BootstrapAsync()
    {
        var startupError = StartupRegistration.Apply(settings.StartWithWindows);
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

    private Forms.NotifyIcon CreateTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Show Help Me!", null, (_, _) => RestoreFromTray());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) =>
        {
            exiting = true;
            Dispatcher.BeginInvoke(new Action(Close));
        });
        var icon = new Forms.NotifyIcon
        {
            Text = "Help Me!",
            Icon = trayAppIcon,
            ContextMenuStrip = menu,
            Visible = true
        };
        icon.DoubleClick += (_, _) => RestoreFromTray();
        return icon;
    }

    private static Drawing.Icon CreateTrayAppIcon()
    {
        using var bitmap = new Drawing.Bitmap(32, 32, Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = Drawing.Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = Drawing2D.SmoothingMode.AntiAlias;
            graphics.Clear(Drawing.Color.Transparent);
            using var background = new Drawing.SolidBrush(Drawing.Color.FromArgb(11, 18, 32));
            using var bubble = new Drawing.SolidBrush(Drawing.Color.FromArgb(30, 156, 190));
            using var sparkle = new Drawing.SolidBrush(Drawing.Color.FromArgb(167, 243, 208));
            using var frame = RoundedRectangle(new Drawing.RectangleF(1, 1, 30, 30), 7);
            using var message = RoundedRectangle(new Drawing.RectangleF(6, 6, 20, 15), 4);
            graphics.FillPath(background, frame);
            graphics.FillPath(bubble, message);
            graphics.FillPolygon(bubble, new[]
            {
                new Drawing.PointF(10, 19), new Drawing.PointF(10, 25), new Drawing.PointF(16, 20)
            });
            graphics.FillPolygon(sparkle, new[]
            {
                new Drawing.PointF(16, 8), new Drawing.PointF(18, 12), new Drawing.PointF(22, 14),
                new Drawing.PointF(18, 16), new Drawing.PointF(16, 20), new Drawing.PointF(14, 16),
                new Drawing.PointF(10, 14), new Drawing.PointF(14, 12)
            });
        }
        var handle = bitmap.GetHicon();
        try
        {
            using var borrowed = Drawing.Icon.FromHandle(handle);
            return (Drawing.Icon)borrowed.Clone();
        }
        finally { DestroyIcon(handle); }
    }

    private static Drawing2D.GraphicsPath RoundedRectangle(Drawing.RectangleF bounds, float radius)
    {
        var path = new Drawing2D.GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    private void ApplyEditorAppearance()
    {
        PcText.FontSize = settings.PcFontSize;
        PcText.Foreground = EditorBrush(settings.PcTextColor);
        PcText.CaretBrush = EditorBrush(settings.PcCaretColor);
        TranslationText.FontSize = settings.TranslationFontSize;
        TranslationText.Foreground = EditorBrush(settings.TranslationTextColor);
    }

    private System.Windows.Media.Brush EditorBrush(string color)
        => Brush(settings.Theme == "Light" && color.Equals("#CCCCCC", StringComparison.OrdinalIgnoreCase) ? "#242424" : color);

    private void UpdateCaptionSourceHint() => CaptionSourceHint.Text = settings.CaptionMethod == CaptionMethod.WindowsLiveCaptions
        ? "Windows 11 Live Captions  •  Offline captions" : "Whisper  •  Offline captions";

    private void UpdateCaptionWindowControl()
    {
        var enabled = settings.CaptionMethod == CaptionMethod.WindowsLiveCaptions;
        CaptionWindowButton.IsEnabled = enabled;
        CaptionWindowButton.Content = enabled ? "Show" : "Hide";
    }

    private void HideToTray()
    {
        Hide();
        var hint = restoreHotkeyRegistered ? $"Press {settings.RestoreShortcut} to restore the window."
            : "Still running in the system tray. Double-click the tray icon to restore.";
        trayIcon.ShowBalloonTip(1200, "Help Me!", hint, Forms.ToolTipIcon.Info);
    }

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

    private IntPtr HotkeyHook(IntPtr handle, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmHotkey && wParam.ToInt32() == RestoreHotkeyId)
        {
            RestoreFromTray();
            handled = true;
        }
        return IntPtr.Zero;
    }

    private bool RegisterRestoreShortcut(string shortcut)
    {
        if (hotkeySource is null) return false;
        if (restoreHotkeyRegistered)
        {
            UnregisterHotKey(hotkeySource.Handle, RestoreHotkeyId);
            restoreHotkeyRegistered = false;
        }
        KeyGesture gesture;
        try
        {
            gesture = new KeyGestureConverter().ConvertFromInvariantString(shortcut) as KeyGesture
                ?? throw new FormatException("Invalid shortcut.");
        }
        catch (Exception error) when (error is NotSupportedException or ArgumentException or FormatException)
        {
            return false;
        }
        if (System.Numerics.BitOperations.PopCount((uint)gesture.Modifiers) < 2) return false;
        uint modifiers = 0x4000; // MOD_NOREPEAT
        if (gesture.Modifiers.HasFlag(ModifierKeys.Alt)) modifiers |= 0x0001;
        if (gesture.Modifiers.HasFlag(ModifierKeys.Control)) modifiers |= 0x0002;
        if (gesture.Modifiers.HasFlag(ModifierKeys.Shift)) modifiers |= 0x0004;
        if (gesture.Modifiers.HasFlag(ModifierKeys.Windows)) modifiers |= 0x0008;
        restoreHotkeyRegistered = RegisterHotKey(hotkeySource.Handle, RestoreHotkeyId,
            modifiers, (uint)KeyInterop.VirtualKeyFromKey(gesture.Key));
        return restoreHotkeyRegistered;
    }

    private void AlwaysOnTop_Click(object sender, RoutedEventArgs e) => Topmost = AlwaysOnTopToggle.IsChecked == true;
    private void PcText_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var point = e.GetPosition(PcText);
        // Blank space must not silently move the send bookmark to the document end.
        var index = PcText.GetCharacterIndexFromPoint(point, false);
        if (index < 0 || index >= PcText.Text.Length) return;
        savedCaptionLocation = index;
        hasSavedCaptionLocation = true;
        SaveCaptionSuffixToClipboard();
    }

    private void SaveCaptionSuffixToClipboard()
    {
        if (!hasSavedCaptionLocation || string.IsNullOrWhiteSpace(PcText.Text)) return;
        var start = Math.Clamp(savedCaptionLocation, 0, PcText.Text.Length);
        var text = PcText.Text[start..].Trim();
        if (text.Length == 0) return;
        try { Clipboard.SetText(text); }
        catch (ExternalException) { /* Clipboard may be temporarily owned by another app. */ }
    }
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        // The configurable send shortcut wins, so a user who binds Ctrl+2 to Send
        // keeps it rather than silently toggling a section instead.
        if (key == sendShortcut.Key && Keyboard.Modifiers == sendShortcut.Modifiers)
        {
            e.Handled = true;
            _ = SendAnswerAsync();
            return;
        }
        if (key == telegramShortcut.Key && Keyboard.Modifiers == telegramShortcut.Modifiers)
        {
            e.Handled = true;
            _ = SendAnswerToTelegramAsync(TelegramSendButton, SelectionLabel, wholeAnswer: false);
            return;
        }
        if (key == telegramAllShortcut.Key && Keyboard.Modifiers == telegramAllShortcut.Modifiers)
        {
            e.Handled = true;
            _ = SendAnswerToTelegramAsync(TelegramSendAllButton, AllLabel, wholeAnswer: true);
            return;
        }
        if (TryToggleSection(key, Keyboard.Modifiers)) e.Handled = true;
    }

    private async void TelegramSend_Click(object sender, RoutedEventArgs e)
        => await SendAnswerToTelegramAsync(TelegramSendButton, SelectionLabel, wholeAnswer: false);

    private async void TelegramSendAll_Click(object sender, RoutedEventArgs e)
        => await SendAnswerToTelegramAsync(TelegramSendAllButton, AllLabel, wholeAnswer: true);

    // A selection on the AI page wins over the whole answer, unless the caller
    // asked for everything. Whitespace-only selections fall back to the whole
    // answer rather than refusing to send.
    private async Task<string> GetTelegramMessageAsync(bool wholeAnswer)
    {
        if (wholeAnswer) return answerText.Trim();
        var selected = webViews is null ? "" : (await webViews.GetAiSelectionAsync()).Trim();
        return selected.Length > 0 ? selected : answerText.Trim();
    }

    // The answer goes to Telegram as its own new message.
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
        // Both buttons drive one browser tab, so neither may start a second send.
        TelegramSendButton.IsEnabled = TelegramSendAllButton.IsEnabled = false;
        button.Content = "Sending…";
        Status.Visibility = Visibility.Collapsed;
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
    // both come through here, so neither drives the tab while the other holds it.
    private async Task<string?> DeliverToMessengerAsync(string message)
    {
        if (BrowserUnavailable is { } unavailable) return unavailable;
        var target = settings.MessengerTarget;
        try
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await Task.Run(() => BrowserSendAsync(message, target, null, cancellation.Token));
            return null;
        }
        catch (OperationCanceledException)
        {
            return $"{target} did not accept the message in time. Check its tab in Chrome.";
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or HttpRequestException)
        {
            return error.Message;
        }
    }

    // Auto-send forwards the answer a few sentences at a time while the AI is
    // still typing. A progress update only nudges the pump: one send holds the
    // chat tab, so the pump drains whatever accumulated while it was busy.
    private async Task PumpAutoSendAsync(int outputVersion)
    {
        if (autoSendPumping) return;
        autoSendPumping = true;
        try
        {
            while (!closing && autoSendActive && outputVersion == answerOutputVersion)
            {
                // A button press owns the tab until it finishes.
                if (telegramSending) { await Task.Delay(200); continue; }
                var sentences = autoSendSentences;
                // The opening chunk is small so the reader sees something while
                // the answer is still being written; the rest come in larger.
                var target = autoSendMessages == 0 ? settings.AutoSendFirstChars : settings.AutoSendNextChars;
                var chunk = autoSendFinal
                    ? AnswerChunker.RemainingChunk(answerText, sentences, out sentences)
                    : AnswerChunker.NextChunk(answerText, sentences, target, out sentences);
                if (chunk is null) return;
                if (autoSendMessages == 0) chunk = AutoSendSeparator + "\n" + chunk;
                autoSendSentences = sentences;
                telegramSending = true;
                string? failure;
                try { failure = await DeliverToMessengerAsync(chunk); }
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
    private void AutoSend_Changed(object sender, RoutedEventArgs e)
    {
        var enabled = AutoSendCheck.IsChecked == true;
        // The hint keeps the box in step with the setting, so only a real change
        // of mind re-anchors a run that is already under way.
        var changed = enabled != settings.AutoSendAnswer;
        if (initialized && changed)
            settings.SaveAutoSend(enabled, settings.AutoSendFirstChars, settings.AutoSendNextChars);
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
        AutoSendCheck.IsChecked = settings.AutoSendAnswer;
        if (!settings.AutoSendAnswer)
        {
            AutoSendHint.Visibility = Visibility.Collapsed;
            return;
        }
        AutoSendHint.Visibility = Visibility.Visible;
        // A run that was stopped by a refusal says so, so the count on the bar is
        // never read as "all of it went out".
        AutoSendHint.Text = autoSendFailed ? $"stopped · {autoSendMessages} sent"
            : autoSendMessages > 0 ? $"{autoSendMessages} sent"
            : $"{settings.AutoSendFirstChars} then {settings.AutoSendNextChars}";
        AutoSendHint.ToolTip =
            $"The answer goes to {settings.MessengerTarget} as it is written: about {settings.AutoSendFirstChars} " +
            $"characters first, then about {settings.AutoSendNextChars} at a time, in whole sentences";
    }

    private async Task RestoreTelegramButtonAsync(Button button, string restingLabel)
    {
        var token = ++telegramConfirmation;
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        if (!closing && token == telegramConfirmation) button.Content = restingLabel;
    }

    // Ctrl+2, Ctrl+5 and Ctrl+6 hide or show their sections.
    private bool TryToggleSection(Key key, ModifierKeys modifiers)
    {
        if (modifiers != ModifierKeys.Control) return false;
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
        try
        {
            sendShortcut = new KeyGestureConverter().ConvertFromInvariantString(settings.SendShortcut) as KeyGesture
                ?? new KeyGesture(Key.Z, ModifierKeys.Control | ModifierKeys.Shift);
        }
        catch (Exception error) when (error is NotSupportedException or ArgumentException or FormatException)
        {
            sendShortcut = new KeyGesture(Key.Z, ModifierKeys.Control | ModifierKeys.Shift);
            settings.SendShortcut = "Ctrl+Shift+Z";
        }
        var label = new KeyGestureConverter().ConvertToInvariantString(sendShortcut) ?? "Ctrl+Shift+Z";
        SendShortcutHint.Text = $"{label}  Send";
        if (answerCancellation is null) AnswerSendButton.ToolTip = $"Send captions ({label})";
        try
        {
            telegramShortcut = new KeyGestureConverter().ConvertFromInvariantString(settings.TelegramShortcut) as KeyGesture
                ?? new KeyGesture(Key.T, ModifierKeys.Control | ModifierKeys.Shift);
        }
        catch (Exception error) when (error is NotSupportedException or ArgumentException or FormatException)
        {
            telegramShortcut = new KeyGesture(Key.T, ModifierKeys.Control | ModifierKeys.Shift);
            settings.TelegramShortcut = "Ctrl+Shift+T";
        }
        var telegramLabel = new KeyGestureConverter().ConvertToInvariantString(telegramShortcut) ?? "Ctrl+Shift+T";
        TelegramSendButton.ToolTip =
            $"Send the text selected in the AI answer to {settings.MessengerTarget}, or all of it when nothing is selected ({telegramLabel})";
        try
        {
            telegramAllShortcut = new KeyGestureConverter().ConvertFromInvariantString(settings.TelegramAllShortcut) as KeyGesture
                ?? new KeyGesture(Key.A, ModifierKeys.Control | ModifierKeys.Shift);
        }
        catch (Exception error) when (error is NotSupportedException or ArgumentException or FormatException)
        {
            telegramAllShortcut = new KeyGesture(Key.A, ModifierKeys.Control | ModifierKeys.Shift);
            settings.TelegramAllShortcut = "Ctrl+Shift+A";
        }
        var allLabel = new KeyGestureConverter().ConvertToInvariantString(telegramAllShortcut) ?? "Ctrl+Shift+A";
        TelegramSendAllButton.ToolTip =
            $"Send the whole AI answer to {settings.MessengerTarget}, ignoring any selection ({allLabel})";
    }

    private async void AnswerSend_Click(object sender, RoutedEventArgs e)
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
        savedCaptionLocation = PcText.Text.Length;
        hasSavedCaptionLocation = true;
        PcText.Focus();
        PcText.CaretIndex = PcText.Text.Length;
        PcText.ScrollToEnd();
    }
    private string GetAnswerPrompt()
    {
        if (string.IsNullOrWhiteSpace(PcText.Text)) throw new InvalidOperationException("PC caption text is empty.");
        var start = hasSavedCaptionLocation ? Math.Clamp(savedCaptionLocation, 0, PcText.Text.Length) : 0;
        var prompt = PcText.Text[start..].Trim();
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
        AnswerSendButton.ToolTip = "Stop reading this reply. The AI keeps writing it on the page.";
        var outputVersion = ++answerOutputVersion;
        var provider = settings.AiProvider;
        Status.Visibility = Visibility.Collapsed;
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
            var response = await Task.Run(() => BrowserSendAsync(prompt, provider, Display, cancellation.Token));
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
    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        var (box, filename) = (button.Tag as string) switch
        {
            "Translation" => (TranslationText, "translation.txt"),
            "Microphone" => (MicText, "microphone.txt"),
            // The answer has no box of its own; it is saved from what was read.
            "Answer" => (null, "answer.txt"),
            _ => (PcText, "captions.txt")
        };
        var dialog = new SaveFileDialog
        {
            Title = "Save caption text", Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
            DefaultExt = ".txt", AddExtension = true, FileName = filename
        };
        if (dialog.ShowDialog(this) != true) return;
        var text = box?.Text ?? answerText;
        button.IsEnabled = false;
        try { await File.WriteAllTextAsync(dialog.FileName, text, new UTF8Encoding(false)); }
        catch (Exception error) { if (!closing) ShowError(error.Message); }
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
            ICaptionSession session;
            if (settings.CaptionMethod == CaptionMethod.WindowsLiveCaptions)
                session = new WindowsLiveCaptionsSession(onCaption, onStatus, onPreview);
            else
            {
                await engine.InitializeAsync();
                session = new AudioSession(false, engine, settings, "en", onCaption, onStatus, onPreview);
            }
            try
            {
                await session.StartAsync();
                pc = session;
                CaptionWindowButton.Content = settings.CaptionMethod == CaptionMethod.WindowsLiveCaptions ? "Show" : "Hide";
            }
            catch { await session.DisposeAsync(); throw; }
        }
        else if (!needed && pc is not null)
        {
            liveCaptionTranslationTimer.Stop();
            ++pcGeneration;
            await pc.DisposeAsync(); pc = null;
            CaptionWindowButton.Content = "Show";
        }
    }

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(settings) { Owner = this };
        if (dialog.ShowDialog() != true)
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
            if (dialog.StartWithWindows != settings.StartWithWindows)
            {
                var startupError = StartupRegistration.Apply(dialog.StartWithWindows);
                if (startupError is not null) ShowError(startupError);
                else settings.SaveStartWithWindows(dialog.StartWithWindows);
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
            var captionMethodChanged = settings.CaptionMethod != dialog.SelectedMethod;
            settings.SaveCaptionMethod(dialog.SelectedMethod);
            AppTheme.Apply(settings.Theme);
            settings.SaveEditorAppearance(dialog.PcFontSize, dialog.PcTextColor, dialog.PcCaretColor,
                dialog.TranslationFontSize, dialog.TranslationTextColor);
            ApplyEditorAppearance();
            ApplySendShortcut();
            UpdateAutoSendHint();
            if (captionMethodChanged)
            {
                UpdateCaptionSourceHint();
                UpdateCaptionWindowControl();
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

    private async void PcToggle_Click(object sender, RoutedEventArgs e)
    {
        await ChangeAsync(async () =>
        {
            translationUsesAudio = PcToggle.IsChecked != true && TranslationToggle.IsChecked == true;
            try { await UpdatePcAsync(); }
            catch { PcToggle.IsChecked = false; throw; }
        });
    }

    private void CaptionWindowButton_Click(object sender, RoutedEventArgs e)
    {
        if (pc is WindowsLiveCaptionsSession captions)
            CaptionWindowButton.Content = captions.ToggleVisibility() ? "Hide" : "Show";
    }

    private async void TranslationToggle_Click(object sender, RoutedEventArgs e)
    {
        await ChangeAsync(async () =>
        {
            if (TranslationToggle.IsChecked == true)
            {
                var generation = ++translationGeneration;
                translatedSource = translatedTarget = "";
                documentTranslator.Clear();
                var existingCaption = PcText.Text.Trim();
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

    private async void MicToggle_Click(object sender, RoutedEventArgs e)
    {
        await ChangeAsync(async () =>
        {
            if (MicToggle.IsChecked == true)
            {
                try
                {
                    await engine.InitializeAsync();
                    var generation = ++micGeneration;
                    mic = new AudioSession(true, engine, settings, "en", text => UI(() =>
                    {
                        if (generation == micGeneration && MicToggle.IsChecked == true) QueueCaption(text, pcAudio: false);
                    }), message => WorkerMessage(message, () => generation == micGeneration));
                    mic.Start();
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
        Status.Visibility = Visibility.Collapsed;
        try { await action(); }
        catch (Exception e) { ShowError(e.Message); }
        finally
        {
            PcToggle.IsEnabled = TranslationToggle.IsEnabled = MicToggle.IsEnabled = SettingsButton.IsEnabled = true;
            changes.Release();
        }
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (closed) return;
        // The title bar has no close button, so closing a visible window always
        // means "get out of my way": it returns to the tray and keeps capturing.
        // A hidden window has no user-reachable close, so a close arriving there
        // is the OS or a host shutting us down and must be allowed through.
        if (!exiting && IsVisible)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }
        e.Cancel = true;
        if (closing) return;
        closing = true; IsEnabled = false;
        liveCaptionTranslationTimer.Stop();
        answerCancellation?.Cancel();
        await changes.WaitAsync();
        try
        {
            if (pc is not null) await pc.DisposeAsync();
            if (mic is not null) await mic.DisposeAsync();
            if (translation is not null) await translation.DisposeAsync();
            if (initialized) engine.Dispose();
        }
        finally
        {
            showRequestWait?.Unregister(null);
            showRequestWait = null;
            showRequest?.Dispose();
            showRequest = null;
            SingleInstance.Release();
            if (restoreHotkeyRegistered && hotkeySource is not null)
                UnregisterHotKey(hotkeySource.Handle, RestoreHotkeyId);
            restoreHotkeyRegistered = false;
            hotkeySource?.RemoveHook(HotkeyHook);
            closed = true;
            trayIcon.Visible = false;
            trayIcon.Dispose();
            trayAppIcon.Dispose();
            changes.Release();
            // Cleanup may finish synchronously. Close must run after the current
            // Closing event has returned, or WPF rejects the reentrant call.
            _ = Dispatcher.BeginInvoke(new Action(Close));
        }
    }
}
