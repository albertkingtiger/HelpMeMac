using System.Windows;
using System.Globalization;
using HelpMe.Services;
using Button = System.Windows.Controls.Button;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;
using Media = System.Windows.Media;

namespace HelpMe;

public partial class SettingsWindow : Window
{
    public SettingsWindow(CaptionMethod method) : this(method, 9111, "ChatGPT", "", "") { }
    public SettingsWindow(CaptionMethod method, int chromePort, string provider = "ChatGPT", string project = "", string session = "",
        double pcFontSize = 14, string pcTextColor = "#CCCCCC", string pcCaretColor = "#AEAFAD",
        double translationFontSize = 14, string translationTextColor = "#CCCCCC",
        double answerInputFontSize = 14, string answerInputTextColor = "#CCCCCC",
        double answerFontSize = 14, string answerTextColor = "#CCCCCC")
    {
        InitializeComponent();
        WhisperOption.IsChecked = true;
        ChromePortText.Text = chromePort.ToString();
        ProviderCombo.ItemsSource = new[] { "ChatGPT", "Claude" };
        ProviderCombo.SelectedItem = provider is "Claude" ? "Claude" : "ChatGPT";
        ProjectCombo.Text = project;
        SessionCombo.Text = session;
        PcFontSizeText.Text = pcFontSize.ToString("0.#", CultureInfo.CurrentCulture);
        TranslationFontSizeText.Text = translationFontSize.ToString("0.#", CultureInfo.CurrentCulture);
        AnswerInputFontSizeText.Text = answerInputFontSize.ToString("0.#", CultureInfo.CurrentCulture);
        AnswerFontSizeText.Text = answerFontSize.ToString("0.#", CultureInfo.CurrentCulture);
        SetColorButton(PcTextColorButton, pcTextColor);
        SetColorButton(PcCaretColorButton, pcCaretColor);
        SetColorButton(TranslationTextColorButton, translationTextColor);
        SetColorButton(AnswerInputTextColorButton, answerInputTextColor);
        SetColorButton(AnswerTextColorButton, answerTextColor);
    }
    public CaptionMethod SelectedMethod => CaptionMethod.Whisper;
    public int ChromeDebugPort => int.TryParse(ChromePortText.Text, out var port) && port is >= 1024 and <= 65535 ? port : 9111;
    public string AiProvider => ProviderCombo.SelectedItem as string ?? "ChatGPT";
    public string AiProject => ProjectCombo.Text.Trim();
    public string AiSession => SessionCombo.Text.Trim();
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
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (new[] { PcFontSize, TranslationFontSize, AnswerInputFontSize, AnswerFontSize }.Any(value => value is < 8 or > 48 || double.IsNaN(value)))
        {
            System.Windows.MessageBox.Show(this, "Enter font sizes from 8 to 48.", "Settings",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        DialogResult = true;
    }
}
