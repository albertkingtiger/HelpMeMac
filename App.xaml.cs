using System.IO;
using System.Windows;
using Application = System.Windows.Application;
using Size = System.Windows.Size;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
namespace HelpMe;
public partial class App : Application
{
    private readonly bool launchMainWindow = true;
    public App() { }
    public App(bool launchMainWindow) => this.launchMainWindow = launchMainWindow;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (!launchMainWindow) return;
        var window = new MainWindow();
        if (e.Args.Length is 2 or 4 && e.Args[0] is "--render-preview" or "--render-settings-preview")
        {
            // Offscreen rendering for layout verification; never opens an audio device.
            var settingsPreview = e.Args[0] == "--render-settings-preview";
            Window preview = settingsPreview ? new SettingsWindow(Services.CaptionMethod.Whisper) : window;
            var width = settingsPreview ? 490 : 1004;
            var height = settingsPreview ? 490 : 748;
            if (e.Args.Length == 4)
            {
                // Optional dimensions allow checking compact layouts without
                // changing the user's saved window size or starting capture.
                width = int.Parse(e.Args[2]);
                height = int.Parse(e.Args[3]);
                if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(e.Args));
            }
            var content = (Grid)preview.Content;
            content.Background = preview.Background;
            content.Measure(new Size(width, height));
            content.Arrange(new Rect(0, 0, width, height));
            content.UpdateLayout();
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(content);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(e.Args[1])) encoder.Save(file);
            Shutdown();
            return;
        }
        window.Show();
    }
}
