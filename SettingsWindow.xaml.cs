using System.Windows;
using HelpMe.Services;

namespace HelpMe;

public partial class SettingsWindow : Window
{
    public SettingsWindow(CaptionMethod method) : this(method, 9111, "ChatGPT", "", "") { }
    public SettingsWindow(CaptionMethod method, int chromePort, string provider = "ChatGPT", string project = "", string session = "")
    {
        InitializeComponent();
        WhisperOption.IsChecked = true;
        ChromePortText.Text = chromePort.ToString();
        ProviderCombo.ItemsSource = new[] { "ChatGPT", "Claude" };
        ProviderCombo.SelectedItem = provider is "Claude" ? "Claude" : "ChatGPT";
        ProjectCombo.Text = project;
        SessionCombo.Text = session;
    }
    public CaptionMethod SelectedMethod => CaptionMethod.Whisper;
    public int ChromeDebugPort => int.TryParse(ChromePortText.Text, out var port) && port is >= 1024 and <= 65535 ? port : 9111;
    public string AiProvider => ProviderCombo.SelectedItem as string ?? "ChatGPT";
    public string AiProject => ProjectCombo.Text.Trim();
    public string AiSession => SessionCombo.Text.Trim();
    private void Save_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
