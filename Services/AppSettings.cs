using System.IO;
using System.Text.Json;

namespace HelpMe.Services;

public sealed class AppSettings
{
    public CaptionMethod CaptionMethod { get; set; } = CaptionMethod.Whisper;
    public string WhisperModelPath { get; set; } = "models/ggml-base.en-q8_0.bin";
    public string TranslationModelPath { get; set; } = "models/nllb-200-distilled-600M-int8";
    public string PythonPath { get; set; } = ".venv/Scripts/python.exe";
    public double SpeechThreshold { get; set; } = 0.008;
    public double MaxSegmentSeconds { get; set; } = 6;
    public double SilenceSeconds { get; set; } = 0.7;
    public int ChromeDebugPort { get; set; } = 9111;
    public string AiProvider { get; set; } = "ChatGPT";
    public string AiProject { get; set; } = "";
    public string AiSession { get; set; } = "";
    public double WindowWidth { get; set; } = 1060;
    public double WindowHeight { get; set; } = 860;
    public static string Root { get; private set; } = AppContext.BaseDirectory;
    private static string? projectRoot;
    private static string PreferencesPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HelpMe", "settings.json");
    private static string LocalPreferencesPath => Path.Combine(Root, "user-settings.json");
    public const string RegistryPath = "HelpMe";

    public static AppSettings Load(string? ignoredLegacyKey = null)
    {
        Root = AppContext.BaseDirectory;
        projectRoot = null;
        for (var directory = new DirectoryInfo(Root); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "HelpMe.csproj"))) { projectRoot = directory.FullName; break; }
        }
        var result = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Resolve("appsettings.json"))) ?? throw new InvalidDataException("Invalid appsettings.json.");
        var preferencesPath = File.Exists(PreferencesPath) ? PreferencesPath : LocalPreferencesPath;
        if (File.Exists(preferencesPath))
        {
            try
            {
                var saved = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(preferencesPath));
                if (saved is not null)
                {
                    result.ChromeDebugPort = saved.ChromeDebugPort;
                    result.AiProvider = saved.AiProvider is "ChatGPT" or "Claude" ? saved.AiProvider : result.AiProvider;
                    result.AiProject = saved.AiProject ?? "";
                    result.AiSession = saved.AiSession ?? "";
                    if (saved.WindowWidth is >= 700 and <= 5000) result.WindowWidth = saved.WindowWidth;
                    if (saved.WindowHeight is >= 300 and <= 5000) result.WindowHeight = saved.WindowHeight;
                }
            }
            catch (JsonException) { }
        }
        Validate(result);
        return result;
    }

    public void SaveCaptionMethod(CaptionMethod method)
    {
        if (method != CaptionMethod.Whisper) throw new ArgumentOutOfRangeException(nameof(method));
        CaptionMethod = method; SavePreferences();
    }
    public void RefreshCaptionMethod() => CaptionMethod = CaptionMethod.Whisper;
    public void SaveChromeDebugPort(int port)
    {
        if (port is < 1024 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        ChromeDebugPort = port; SavePreferences();
    }
    public void SaveAiPreferences(string provider, string project, string session)
    {
        if (provider is not ("ChatGPT" or "Claude")) throw new ArgumentOutOfRangeException(nameof(provider));
        AiProvider = provider; AiProject = project ?? ""; AiSession = session ?? ""; SavePreferences();
    }
    public void SaveWindowSize(double width, double height)
    {
        WindowWidth = Math.Clamp(width, 700, 5000);
        WindowHeight = Math.Clamp(height, 300, 5000);
        SavePreferences();
    }
    private void SavePreferences()
    {
        var json = JsonSerializer.Serialize(new Preferences(ChromeDebugPort, AiProvider, AiProject, AiSession, WindowWidth, WindowHeight), new JsonSerializerOptions { WriteIndented = true });
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PreferencesPath)!);
            File.WriteAllText(PreferencesPath, json);
        }
        catch (UnauthorizedAccessException)
        {
            File.WriteAllText(LocalPreferencesPath, json);
        }
    }
    private static void Validate(AppSettings result)
    {
        if (result.SpeechThreshold is <= 0 or > 1 || result.MaxSegmentSeconds is < 0.75 or > 15 || result.SilenceSeconds is < 0.1 or > 3 || result.ChromeDebugPort is < 1024 or > 65535)
            throw new InvalidDataException("Audio thresholds in appsettings.json are out of range.");
    }
    private sealed record Preferences(int ChromeDebugPort, string AiProvider, string AiProject, string AiSession, double WindowWidth = 1060, double WindowHeight = 860);
    public static string Resolve(string path)
    {
        var local = Path.GetFullPath(path, Root);
        if (Path.IsPathRooted(path) || File.Exists(local) || Directory.Exists(local)) return local;
        if (projectRoot is not null)
        {
            var project = Path.GetFullPath(path, projectRoot);
            if (File.Exists(project) || Directory.Exists(project)) return project;
        }
        return local;
    }
}
