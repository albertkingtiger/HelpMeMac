using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Automation;
using System.Windows.Threading;
using System.Windows.Input;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;
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
    private ChromeDebugSession? chrome;
    private readonly SemaphoreSlim changes = new(1, 1);
    private bool closing;
    private bool closed;
    private bool exiting;
    private readonly Forms.NotifyIcon trayIcon;
    private bool initialized;
    private int pcGeneration;
    private int micGeneration;
    private int translationGeneration;
    private readonly DispatcherTimer translationTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly DispatcherTimer captionFlushTimer = new() { Interval = TimeSpan.FromMilliseconds(900) };
    private bool translationBusy;
    private bool translationUsesAudio;
    private string translatedSource = "";
    private string translatedTarget = "";
    private string pcTempStrVar = "";
    private string micTempStrVar = "";
    private bool pcLiveCaptionActive;
    private int pcLiveCaptionStart;
    private readonly DocumentTranslator documentTranslator = new();
    private int translationOutputVersion;
    private int savedCaptionLocation;
    private bool hasSavedCaptionLocation;
    private CancellationTokenSource? answerCancellation;
    private int answerOutputVersion;

    public MainWindow()
    {
        InitializeComponent();
        trayIcon = CreateTrayIcon();
        translationTimer.Tick += async (_, _) =>
        {
            translationTimer.Stop();
            await TranslateInputAsync();
        };
        captionFlushTimer.Tick += (_, _) => FlushCaptionBuffers();
        try
        {
            settings = AppSettings.Load();
            engine = new SpeechEngine(settings);
            initialized = true;
            Loaded += async (_, _) => { if (chrome is null && !closing) await StartChromeAsync(); };
        }
        catch (Exception e)
        {
            ShowError(e.Message);
            PcToggle.IsEnabled = TranslationToggle.IsEnabled = MicToggle.IsEnabled = SettingsButton.IsEnabled = false;
        }
    }

    private async Task StartChromeAsync()
    {
        try
        {
            chrome = new ChromeDebugSession(settings.ChromeDebugPort);
            await chrome.StartAsync();
        }
        catch (Exception error)
        {
            chrome = null;
            if (!closing) ShowError(error.Message);
        }
    }

    private (ToggleButton, ToggleButton, ToggleButton, ToggleButton, Button, Button,
        TextBox, TextBox, TextBox, TextBlock, TextBlock) BuildUi()
    {
        Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(11, 18, 32));
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI");
        Width = 1060; Height = 860; MinWidth = 700; MinHeight = 250;
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
    private static void Append(TextBox box, string text)
    {
        var follow = IsNearBottom(box);
        var preserveCaret = box.IsKeyboardFocusWithin && box.CaretIndex < box.Text.Length;
        var caret = box.CaretIndex;
        var selectionStart = box.SelectionStart;
        var selectionLength = box.SelectionLength;
        var chunk = Regex.Replace(text ?? "", @"\s+", " ").Trim();
        if (chunk.Length == 0) return;
        // Whisper may close a short audio fragment with a period even when the
        // next fragment is a continuation. Keep that continuation on the same
        // natural sentence instead of displaying "apply. more".
        if (box.Text.Length > 0 && char.IsLower(chunk[0]) && box.Text[^1] is '.' or '!' or '?')
            box.Text = box.Text[..^1];
        var separator = box.Text.Length > 0 && !char.IsWhiteSpace(box.Text[^1]) && !char.IsPunctuation(chunk[0]) ? " " : "";
        box.AppendText(separator + chunk);
        if (preserveCaret)
        {
            box.Select(Math.Min(selectionStart, box.Text.Length), Math.Min(selectionLength, Math.Max(0, box.Text.Length - selectionStart)));
            box.CaretIndex = Math.Min(caret, box.Text.Length);
        }
        else if (follow) box.ScrollToEnd();
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
        var follow = IsNearBottom(PcText);
        var preserveCaret = PcText.IsKeyboardFocusWithin && PcText.CaretIndex < PcText.Text.Length;
        var caret = PcText.CaretIndex;
        var selectionStart = PcText.SelectionStart;
        var selectionLength = PcText.SelectionLength;
        if (!pcLiveCaptionActive)
        {
            if (PcText.Text.Length > 0 && !char.IsWhiteSpace(PcText.Text[^1])) PcText.AppendText(" ");
            pcLiveCaptionStart = PcText.Text.Length;
            pcLiveCaptionActive = true;
        }
        var start = Math.Clamp(pcLiveCaptionStart, 0, PcText.Text.Length);
        PcText.Text = PcText.Text[..start] + text.Trim();
        if (preserveCaret)
        {
            PcText.Select(Math.Min(selectionStart, PcText.Text.Length), Math.Min(selectionLength, Math.Max(0, PcText.Text.Length - selectionStart)));
            PcText.CaretIndex = Math.Min(caret, PcText.Text.Length);
        }
        else
        {
            PcText.CaretIndex = PcText.Text.Length;
            if (follow) PcText.ScrollToEnd();
        }
    }

    private void CommitLiveCaption(string text)
    {
        SetLiveCaption(text);
        var trimmed = text.TrimEnd();
        if (trimmed.Length > 0 && trimmed[^1] is '.' or '!' or '?' or ':' or ';')
            PcText.AppendText(Environment.NewLine);
        else if (PcText.Text.Length > 0 && !char.IsWhiteSpace(PcText.Text[^1]))
            PcText.AppendText(" ");
        pcLiveCaptionActive = false;
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
            TranslationText.Clear();
        }
        ScheduleTranslation();
    }

    private void ScheduleTranslation()
    {
        if (!closing && TranslationToggle.IsChecked == true && translation is not null &&
            !translationBusy && !translationTimer.IsEnabled) translationTimer.Start();
    }

    private async Task TranslateInputAsync()
    {
        if (closing || translationBusy || translation is null || TranslationToggle.IsChecked != true) return;
        var source = PcText.Text.Trim();
        if (source.Length == 0 || source == translatedSource) return;
        var generation = translationGeneration;
        var session = translation;
        var outputVersion = translationOutputVersion;
        translationBusy = true;
        var retry = true;
        try
        {
            var result = await documentTranslator.TranslateAsync(source, session.TranslateAsync);
            // Accept appends, but never apply a result for text the user has edited away.
            var current = PcText.Text.Trim();
            var stillValid = current == source || (current.StartsWith(source, StringComparison.Ordinal) &&
                current.Length > source.Length && char.IsWhiteSpace(current[source.Length]));
            if (!closing && generation == translationGeneration && outputVersion == translationOutputVersion && TranslationToggle.IsChecked == true && stillValid)
            {
                translatedSource = source;
                translatedTarget = result;
                var follow = IsNearBottom(TranslationText);
                TranslationText.Text = translatedTarget;
                if (follow) TranslationText.ScrollToEnd();
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
            if (retry && !closing && PcText.Text.Trim() != translatedSource) ScheduleTranslation();
        }
    }

    private void Format_Click(object sender, RoutedEventArgs e)
    {
        pcTempStrVar = "";
        pcLiveCaptionActive = false;
        PcText.Clear();
        translatedSource = translatedTarget = "";
        documentTranslator.Clear();
    }

    private void TranslationFormat_Click(object sender, RoutedEventArgs e)
    {
        ++translationOutputVersion;
        translatedSource = PcText.Text.Trim();
        translatedTarget = "";
        documentTranslator.Clear();
        TranslationText.Clear();
    }

    private void MicFormat_Click(object sender, RoutedEventArgs e)
    {
        micTempStrVar = "";
        MicText.Clear();
    }

    private void Close_Click(object sender, RoutedEventArgs e) { exiting = true; Close(); }
    private void Minimize_Click(object sender, RoutedEventArgs e) => HideToTray();

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
            Icon = Drawing.SystemIcons.Application,
            ContextMenuStrip = menu,
            Visible = true
        };
        icon.DoubleClick += (_, _) => RestoreFromTray();
        return icon;
    }

    private void HideToTray()
    {
        Hide();
        trayIcon.ShowBalloonTip(1200, "Help Me!", "Still running in the system tray.", Forms.ToolTipIcon.Info);
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
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
        if (e.Key != Key.Z || Keyboard.Modifiers != (ModifierKeys.Control | ModifierKeys.Shift)) return;
        e.Handled = true;
        _ = SendAnswerAsync();
    }

    private void AnswerFormat_Click(object sender, RoutedEventArgs e) { answerOutputVersion++; AnswerText.Clear(); }
    private async void AnswerSend_Click(object sender, RoutedEventArgs e) => await SendAnswerAsync();
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
        if (chrome is null) { ShowError("Chrome debug session is not available."); return; }
        string prompt;
        try { prompt = GetAnswerPrompt(); }
        catch (InvalidOperationException error) { ShowError(error.Message); return; }
        TranslationInputText.Text = prompt;
        using var cancellation = new CancellationTokenSource();
        answerCancellation = cancellation;
        AnswerSendButton.IsEnabled = false;
        var outputVersion = ++answerOutputVersion;
        var browser = chrome;
        var provider = settings.AiProvider;
        Status.Visibility = Visibility.Collapsed;
        void Display(string text) => DisplayAnswer(text, outputVersion);
        try
        {
            var response = await Task.Run(() => browser.SendPromptAsync(prompt, provider, Display, cancellation.Token));
            Display(response);
        }
        catch (OperationCanceledException) { if (!closing) ShowError("AI request timed out or was cancelled. Check Chrome for the reply or a sign-in/usage-limit message."); }
        catch (Exception error) { if (!closing) ShowError(error.Message); }
        finally { answerCancellation = null; if (!closing) AnswerSendButton.IsEnabled = true; }
    }
    private void DisplayAnswer(string text, int outputVersion) => UI(() =>
    {
        if (closing || outputVersion != answerOutputVersion) return;
        var follow = IsNearBottom(AnswerText);
        AnswerText.Text = text;
        if (follow) AnswerText.ScrollToEnd();
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
            "Answer" => (AnswerText, "answer.txt"),
            _ => (PcText, "captions.txt")
        };
        var dialog = new SaveFileDialog
        {
            Title = "Save caption text", Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
            DefaultExt = ".txt", AddExtension = true, FileName = filename
        };
        if (dialog.ShowDialog(this) != true) return;
        var text = box.Text;
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
                if (PcToggle.IsChecked == true) CommitLiveCaption(text);
            });
            Action<string> onPreview = text => UI(() =>
            {
                if (generation == pcGeneration && PcToggle.IsChecked == true) SetLiveCaption(text);
            });
            Action<string> onStatus = message => WorkerMessage(message, () => generation == pcGeneration);
            ICaptionSession session;
            await engine.InitializeAsync();
            session = new AudioSession(false, engine, settings, "en", onCaption, onStatus, onPreview);
            try { await session.StartAsync(); pc = session; }
            catch { await session.DisposeAsync(); throw; }
        }
        else if (!needed && pc is not null)
        {
            ++pcGeneration;
            await pc.DisposeAsync(); pc = null;
        }
    }

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(settings.CaptionMethod, settings.ChromeDebugPort,
            settings.AiProvider, settings.AiProject, settings.AiSession) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        await ChangeAsync(async () =>
        {
            var previousPort = settings.ChromeDebugPort;
            settings.SaveChromeDebugPort(dialog.ChromeDebugPort);
            settings.SaveAiPreferences(dialog.AiProvider, dialog.AiProject, dialog.AiSession);
            if (previousPort != dialog.ChromeDebugPort)
            {
                answerCancellation?.Cancel();
                if (chrome is not null) { await chrome.DisposeAsync(); chrome = null; }
                await StartChromeAsync();
            }
        });
    }

    private async void PcToggle_Click(object sender, RoutedEventArgs e)
    {
        await ChangeAsync(async () =>
        {
            // Explicit Stop wins even when translation originally requested capture.
            translationUsesAudio = false;
            try { await UpdatePcAsync(); }
            catch { PcToggle.IsChecked = false; throw; }
        });
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
                translationUsesAudio = string.IsNullOrWhiteSpace(PcText.Text);
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
                    if (translation is not null) { await translation.DisposeAsync(); translation = null; }
                    await UpdatePcAsync();
                    throw;
                }
            }
            else
            {
                ++translationGeneration;
                translationTimer.Stop();
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
        translationTimer.Stop();
        await changes.WaitAsync();
        try
        {
            if (pc is not null) await pc.DisposeAsync();
            if (mic is not null) await mic.DisposeAsync();
            if (translation is not null) await translation.DisposeAsync();
            if (chrome is not null) await chrome.DisposeAsync();
            if (initialized) engine.Dispose();
        }
        finally
        {
            closed = true;
            trayIcon.Visible = false;
            trayIcon.Dispose();
            changes.Release();
            // Cleanup may finish synchronously. Close must run after the current
            // Closing event has returned, or WPF rejects the reentrant call.
            _ = Dispatcher.BeginInvoke(new Action(Close));
        }
    }
}
