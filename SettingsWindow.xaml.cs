using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Globalization;
using HelpMe.Services;
using Button = System.Windows.Controls.Button;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;
using Media = System.Windows.Media;

namespace HelpMe;

public partial class SettingsWindow : Window
{
    // Preview rendering builds a dialog from defaults without reading the user's files.
    public SettingsWindow(CaptionMethod method, string theme = "Dark")
        : this(new AppSettings { CaptionMethod = method, Theme = theme }) { }

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        ThemeCombo.ItemsSource = new[] { "Dark", "Light" };
        ThemeCombo.SelectedItem = settings.Theme is "Light" ? "Light" : "Dark";
        ThemeCombo.SelectionChanged += (_, _) => AppTheme.Apply(SelectedTheme);
        StartWithWindowsCheck.IsChecked = settings.StartWithWindows;
        WhisperOption.IsChecked = settings.CaptionMethod == CaptionMethod.Whisper;
        WindowsCaptionsOption.IsChecked = settings.CaptionMethod == CaptionMethod.WindowsLiveCaptions;
        ProviderCombo.ItemsSource = new[] { "ChatGPT", "Claude", "Telegram" };
        ProviderCombo.SelectedItem = settings.AiProvider is "Claude" or "Telegram" ? settings.AiProvider : "ChatGPT";
        ProjectCombo.Text = settings.AiProject;
        SessionCombo.Text = settings.AiSession;
        SendShortcutText.Text = TryNormalizeShortcut(settings.SendShortcut, out var normalizedShortcut) ? normalizedShortcut : "Ctrl+Shift+Z";
        RestoreShortcutText.Text = TryNormalizeShortcut(settings.RestoreShortcut, out var normalizedRestore) ? normalizedRestore : "Ctrl+Alt+H";
        TelegramShortcutText.Text = TryNormalizeShortcut(settings.TelegramShortcut, out var normalizedTelegram) ? normalizedTelegram : "Ctrl+Shift+T";
        TelegramAllShortcutText.Text = TryNormalizeShortcut(settings.TelegramAllShortcut, out var normalizedAll) ? normalizedAll : "Ctrl+Shift+A";
        MessengerCombo.ItemsSource = Messenger.All.Select(messenger => messenger.Name).ToArray();
        MessengerCombo.SelectedItem = Messenger.Find(settings.MessengerTarget)?.Name ?? Messenger.Telegram.Name;
        AutoSendFirstCharsText.Text = settings.AutoSendFirstChars.ToString(CultureInfo.CurrentCulture);
        AutoSendNextCharsText.Text = settings.AutoSendNextChars.ToString(CultureInfo.CurrentCulture);
        PcFontSizeText.Text = settings.PcFontSize.ToString("0.#", CultureInfo.CurrentCulture);
        TranslationFontSizeText.Text = settings.TranslationFontSize.ToString("0.#", CultureInfo.CurrentCulture);
        AnswerInputFontSizeText.Text = settings.AnswerInputFontSize.ToString("0.#", CultureInfo.CurrentCulture);
        AnswerFontSizeText.Text = settings.AnswerFontSize.ToString("0.#", CultureInfo.CurrentCulture);
        SetColorButton(PcTextColorButton, settings.PcTextColor);
        SetColorButton(PcCaretColorButton, settings.PcCaretColor);
        SetColorButton(TranslationTextColorButton, settings.TranslationTextColor);
        SetColorButton(AnswerInputTextColorButton, settings.AnswerInputTextColor);
        SetColorButton(AnswerTextColorButton, settings.AnswerTextColor);
    }
    public CaptionMethod SelectedMethod => WindowsCaptionsOption.IsChecked == true ? CaptionMethod.WindowsLiveCaptions : CaptionMethod.Whisper;
    public string AiProvider => ProviderCombo.SelectedItem as string ?? "ChatGPT";
    public string AiProject => ProjectCombo.Text.Trim();
    public string AiSession => SessionCombo.Text.Trim();
    public string SendShortcut => SendShortcutText.Text;
    public string RestoreShortcut => RestoreShortcutText.Text;
    public string TelegramShortcut => TelegramShortcutText.Text;
    public string TelegramAllShortcut => TelegramAllShortcutText.Text;
    public string MessengerTarget => MessengerCombo.SelectedItem as string ?? Messenger.Telegram.Name;
    // Out-of-range text is caught on Save, so the invalid value survives to be reported.
    public int AutoSendFirstChars => ReadCount(AutoSendFirstCharsText);
    public int AutoSendNextChars => ReadCount(AutoSendNextCharsText);
    private static int ReadCount(System.Windows.Controls.TextBox box)
        => int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var value) ? value : 0;
    public string SelectedTheme => ThemeCombo.SelectedItem as string ?? "Dark";
    public bool StartWithWindows => StartWithWindowsCheck.IsChecked == true;

    public double PcFontSize => ReadFontSize(PcFontSizeText);
    public string PcTextColor => PcTextColorButton.Content?.ToString() ?? "#CCCCCC";
    public string PcCaretColor => PcCaretColorButton.Content?.ToString() ?? "#AEAFAD";
    public double TranslationFontSize => ReadFontSize(TranslationFontSizeText);
    public string TranslationTextColor => TranslationTextColorButton.Content?.ToString() ?? "#CCCCCC";
    public double AnswerInputFontSize => ReadFontSize(AnswerInputFontSizeText);
    public string AnswerInputTextColor => AnswerInputTextColorButton.Content?.ToString() ?? "#CCCCCC";
    public double AnswerFontSize => ReadFontSize(AnswerFontSizeText);
    public string AnswerTextColor => AnswerTextColorButton.Content?.ToString() ?? "#CCCCCC";
    private void Title_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) DragMove();
    }
    private static double ReadFontSize(System.Windows.Controls.TextBox box)
        => double.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) ? value : double.NaN;
    private static void SetColorButton(Button button, string color)
    {
        var parsed = (Media.Color)Media.ColorConverter.ConvertFromString(color);
        button.Content = color.ToUpperInvariant();
        button.Background = new Media.SolidColorBrush(parsed);
        var brightness = (parsed.R * 299 + parsed.G * 587 + parsed.B * 114) / 1000;
        button.Foreground = new Media.SolidColorBrush(brightness >= 145 ? Media.Colors.Black : Media.Colors.White);
    }
    private void ColorButton_Click(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        using var picker = new Forms.ColorDialog
        {
            FullOpen = true,
            AnyColor = true,
            Color = Drawing.ColorTranslator.FromHtml(button.Content?.ToString() ?? "#CCCCCC")
        };
        if (picker.ShowDialog() == Forms.DialogResult.OK)
            SetColorButton(button, $"#{picker.Color.R:X2}{picker.Color.G:X2}{picker.Color.B:X2}");
    }
    private void SendShortcutText_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        => CaptureShortcut(SendShortcutText, e, "Ctrl+Shift+Z");

    private void RestoreShortcutText_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        => CaptureShortcut(RestoreShortcutText, e, "Ctrl+Alt+H");

    private void TelegramShortcutText_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        => CaptureShortcut(TelegramShortcutText, e, "Ctrl+Shift+T");

    private void TelegramAllShortcutText_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        => CaptureShortcut(TelegramAllShortcutText, e, "Ctrl+Shift+A");

    private void CaptureShortcut(System.Windows.Controls.TextBox target, System.Windows.Input.KeyEventArgs e, string fallback)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or
            Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
        {
            e.Handled = true;
            return;
        }
        try
        {
            var gesture = new KeyGesture(key, Keyboard.Modifiers);
            target.Text = new KeyGestureConverter().ConvertToInvariantString(gesture) ?? fallback;
        }
        catch (Exception error) when (error is NotSupportedException or ArgumentException or FormatException)
        {
            System.Windows.MessageBox.Show(this, "Use Ctrl, Alt, or a function key in the shortcut.", "Shortcut",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        e.Handled = true;
    }
    private static bool TryNormalizeShortcut(string value, out string normalized)
    {
        try
        {
            var gesture = new KeyGestureConverter().ConvertFromInvariantString(value) as KeyGesture;
            if (gesture is null) throw new NotSupportedException();
            normalized = new KeyGestureConverter().ConvertToInvariantString(gesture) ?? value;
            return true;
        }
        catch (Exception error) when (error is NotSupportedException or ArgumentException or FormatException)
        {
            normalized = "";
            return false;
        }
    }
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (new[] { PcFontSize, TranslationFontSize, AnswerInputFontSize, AnswerFontSize }.Any(value => value is < 8 or > 48 || double.IsNaN(value)))
        {
            System.Windows.MessageBox.Show(this, "Enter font sizes from 8 to 48.", "Settings",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!AppSettings.IsChunkSize(AutoSendFirstChars) || !AppSettings.IsChunkSize(AutoSendNextChars))
        {
            System.Windows.MessageBox.Show(this, "Auto-send sizes must be between 20 and 4000 characters.", "Settings",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!TryNormalizeShortcut(SendShortcutText.Text, out var shortcut))
        {
            System.Windows.MessageBox.Show(this, "Click the shortcut field and press a valid key combination.", "Settings",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        SendShortcutText.Text = shortcut;
        if (!TryNormalizeShortcut(RestoreShortcutText.Text, out var restore) ||
            new KeyGestureConverter().ConvertFromInvariantString(restore) is not KeyGesture restoreGesture ||
            System.Numerics.BitOperations.PopCount((uint)restoreGesture.Modifiers) < 2)
        {
            System.Windows.MessageBox.Show(this, "Use at least two modifier keys for the global restore shortcut.", "Shortcut",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!TryNormalizeShortcut(TelegramShortcutText.Text, out var telegram) ||
            !TryNormalizeShortcut(TelegramAllShortcutText.Text, out var telegramAll))
        {
            System.Windows.MessageBox.Show(this, "Click each Telegram shortcut field and press a valid key combination.", "Settings",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var shortcuts = new[] { shortcut, restore, telegram, telegramAll };
        if (shortcuts.Distinct(StringComparer.OrdinalIgnoreCase).Count() != shortcuts.Length)
        {
            System.Windows.MessageBox.Show(this, "Choose a different shortcut for each of Restore, Send, Send Selection and All Send.",
                "Shortcut", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        RestoreShortcutText.Text = restore;
        TelegramShortcutText.Text = telegram;
        TelegramAllShortcutText.Text = telegramAll;
        DialogResult = true;
    }
}
