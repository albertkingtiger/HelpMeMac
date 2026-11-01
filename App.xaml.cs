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
        var rendering = e.Args.Length is 2 or 4 or 5 && e.Args[0] is "--render-preview" or "--render-settings-preview";
        // Offscreen rendering writes a PNG and exits without a tray icon, audio
        // or Chrome, so it must not be turned away while the app is running.
        if (!rendering && !Services.SingleInstance.TryClaim())
        {
            Services.SingleInstance.RequestShow();
            Shutdown();
            return;
        }
        var window = new MainWindow();
        if (rendering)
        {
            var settingsPreview = e.Args[0] == "--render-settings-preview";
            Window preview = settingsPreview
                ? new SettingsWindow(Services.CaptionMethod.Whisper, theme: e.Args.Length == 5 ? e.Args[4] : "Dark")
                : window;
            var width = settingsPreview ? 580 : 1004;
            var height = settingsPreview ? 720 : 748;
            if (e.Args.Length >= 4)
            {
                // Optional dimensions allow checking compact layouts without
                // changing the user's saved window size or starting capture.
                width = int.Parse(e.Args[2]);
                height = int.Parse(e.Args[3]);
                if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(e.Args));
            }
            if (e.Args.Length == 5) Services.AppTheme.Apply(e.Args[4]);
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
        // Background mode: no Show here. The tray owns the first appearance.
        window.StartInBackground();
    }
}
