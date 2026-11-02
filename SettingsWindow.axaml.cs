using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using HelpMe.Services;
using HelpMe.Services.Audio;
using HelpMe.Services.Mac;

namespace HelpMe;

public partial class SettingsWindow : Window
{
    // Preview rendering builds a dialog from defaults without reading the
    // user's files.
    public SettingsWindow() : this(new AppSettings()) { }

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        ThemeCombo.ItemsSource = new[] { "Dark", "Light" };
        ThemeCombo.SelectedItem = settings.Theme is "Light" ? "Light" : "Dark";
        StartAtLoginCheck.IsChecked = settings.StartAtLogin;
        BlackHoleOption.IsChecked = settings.SystemAudioSource == SystemAudioSource.BlackHole;
        ScreenCaptureOption.IsChecked = settings.SystemAudioSource == SystemAudioSource.ScreenCaptureKit;
        BlackHoleStatus.Text = DescribeBlackHole();
        ProviderCombo.ItemsSource = new[] { "ChatGPT", "Claude", "Telegram" };
        ProviderCombo.SelectedItem = settings.AiProvider is "Claude" or "Telegram" ? settings.AiProvider : "ChatGPT";
        ProjectBox.Text = settings.AiProject;
        SessionBox.Text = settings.AiSession;
        SendShortcutText.Text = Shortcut.Normalize(settings.SendShortcut, "Cmd+Shift+Z");
        RestoreShortcutText.Text = Shortcut.Normalize(settings.RestoreShortcut, "Cmd+Alt+H");
        TelegramShortcutText.Text = Shortcut.Normalize(settings.TelegramShortcut, "Cmd+Shift+T");
        TelegramAllShortcutText.Text = Shortcut.Normalize(settings.TelegramAllShortcut, "Cmd+Shift+A");
        MessengerCombo.ItemsSource = Messenger.All.Select(messenger => messenger.Name).ToArray();
        MessengerCombo.SelectedItem = Messenger.Find(settings.MessengerTarget)?.Name ?? Messenger.Telegram.Name;
        AutoSendFirstCharsText.Text = settings.AutoSendFirstChars.ToString(CultureInfo.CurrentCulture);
        AutoSendNextCharsText.Text = settings.AutoSendNextChars.ToString(CultureInfo.CurrentCulture);
        PcFontSizeText.Text = settings.PcFontSize.ToString("0.#", CultureInfo.CurrentCulture);
        TranslationFontSizeText.Text = settings.TranslationFontSize.ToString("0.#", CultureInfo.CurrentCulture);
        PcTextColorPicker.Color = ParseColor(settings.PcTextColor, "#CCCCCC");
        PcCaretColorPicker.Color = ParseColor(settings.PcCaretColor, "#AEAFAD");
        TranslationTextColorPicker.Color = ParseColor(settings.TranslationTextColor, "#CCCCCC");
    }

    // Whether the driver is actually present decides what this option can
    // promise, so the dialog looks rather than describing it in the abstract.
    private static string DescribeBlackHole()
    {
        if (!OperatingSystem.IsMacOS()) return "Only available on macOS.";
        var device = CoreAudio.FindByName(CaptureFactory.BlackHoleName);
        return device is null
            ? "Not installed. " + CaptureFactory.BlackHoleSetup
            : $"Found \"{device.Name}\". Route playback through a Multi-Output Device containing it and your speakers, " +
              "so you hear the sound and Help Me! captures it. Needs no permission and never lapses.";
    }

    public SystemAudioSource SelectedAudioSource => ScreenCaptureOption.IsChecked == true
        ? SystemAudioSource.ScreenCaptureKit : SystemAudioSource.BlackHole;
    public string AiProvider => ProviderCombo.SelectedItem as string ?? "ChatGPT";
    public string AiProject => (ProjectBox.Text ?? "").Trim();
    public string AiSession => (SessionBox.Text ?? "").Trim();
    public string SendShortcut => SendShortcutText.Text ?? "";
    public string RestoreShortcut => RestoreShortcutText.Text ?? "";
    public string TelegramShortcut => TelegramShortcutText.Text ?? "";
    public string TelegramAllShortcut => TelegramAllShortcutText.Text ?? "";
    public string MessengerTarget => MessengerCombo.SelectedItem as string ?? Messenger.Telegram.Name;
    // Out-of-range text is caught on Save, so the invalid value survives to be
    // reported.
    public int AutoSendFirstChars => ReadCount(AutoSendFirstCharsText);
    public int AutoSendNextChars => ReadCount(AutoSendNextCharsText);
    public string SelectedTheme => ThemeCombo.SelectedItem as string ?? "Dark";
    public bool StartAtLogin => StartAtLoginCheck.IsChecked == true;
    public double PcFontSize => ReadFontSize(PcFontSizeText);
    public string PcTextColor => Format(PcTextColorPicker.Color);
    public string PcCaretColor => Format(PcCaretColorPicker.Color);
    public double TranslationFontSize => ReadFontSize(TranslationFontSizeText);
    public string TranslationTextColor => Format(TranslationTextColorPicker.Color);

    private static int ReadCount(TextBox box)
        => int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var value) ? value : 0;

    private static double ReadFontSize(TextBox box)
        => double.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) ? value : double.NaN;

    private static Color ParseColor(string value, string fallback)
        => Color.TryParse(value, out var color) ? color : Color.Parse(fallback);

    // The stored form is #RRGGBB, so a colour chosen with an alpha channel is
    // written back as the opaque colour the editors actually use.
    private static string Format(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private void Title_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }

    private void Theme_SelectionChanged(object? sender, SelectionChangedEventArgs e) => AppTheme.Apply(SelectedTheme);

    private void Shortcut_KeyDown(object? sender, KeyEventArgs e)
    {
        var box = (TextBox)sender!;
        if (Shortcut.IsModifierKey(e.Key))
        {
            e.Handled = true;
            return;
        }
        var captured = new Shortcut(e.KeyModifiers, e.Key);
        // A bare letter is not a shortcut; keep what was there until the key
        // arrives with something held down.
        if (captured.ModifierCount == 0) ShowValidation("Hold Cmd, Ctrl, Alt or Shift while pressing the key.");
        else
        {
            box.Text = captured.ToString();
            HideValidation();
        }
        e.Handled = true;
    }

    private void ShowValidation(string message)
    {
        Validation.Text = message;
        Validation.IsVisible = true;
    }

    private void HideValidation() => Validation.IsVisible = false;

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        if (new[] { PcFontSize, TranslationFontSize }.Any(value => double.IsNaN(value) || value is < 8 or > 48))
        {
            ShowValidation("Enter font sizes from 8 to 48.");
            return;
        }
        if (!AppSettings.IsChunkSize(AutoSendFirstChars) || !AppSettings.IsChunkSize(AutoSendNextChars))
        {
            ShowValidation("Auto-send sizes must be between 20 and 4000 characters.");
            return;
        }
        if (!Shortcut.TryParse(SendShortcutText.Text, out var send))
        {
            ShowValidation("Click the Send shortcut field and press a valid key combination.");
            return;
        }
        if (!Shortcut.TryParse(RestoreShortcutText.Text, out var restore) || restore.ModifierCount < 2)
        {
            // A system-wide shortcut is taken away from every other application,
            // so one modifier is not enough to make it deliberate.
            ShowValidation("Use at least two modifier keys for the system-wide restore shortcut.");
            return;
        }
        if (!Shortcut.TryParse(TelegramShortcutText.Text, out var telegram) ||
            !Shortcut.TryParse(TelegramAllShortcutText.Text, out var telegramAll))
        {
            ShowValidation("Click each send-to-chat shortcut field and press a valid key combination.");
            return;
        }
        var shortcuts = new[] { send.ToString(), restore.ToString(), telegram.ToString(), telegramAll.ToString() };
        if (shortcuts.Distinct(StringComparer.OrdinalIgnoreCase).Count() != shortcuts.Length)
        {
            ShowValidation("Choose a different shortcut for each of Restore, Send, Send Selection and All Send.");
            return;
        }
        SendShortcutText.Text = shortcuts[0];
        RestoreShortcutText.Text = shortcuts[1];
        TelegramShortcutText.Text = shortcuts[2];
        TelegramAllShortcutText.Text = shortcuts[3];
        Close(true);
    }
}
