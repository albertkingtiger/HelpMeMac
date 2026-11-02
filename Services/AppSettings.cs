using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using HelpMe.Services.Audio;

namespace HelpMe.Services;

public sealed class AppSettings
{
    public SystemAudioSource SystemAudioSource { get; set; } = SystemAudioSource.BlackHole;
    public string WhisperModelPath { get; set; } = "models/ggml-base.en-q8_0.bin";
    public string TranslationModelPath { get; set; } = "models/nllb-200-distilled-600M-int8";
    public string PythonPath { get; set; } = ".venv/bin/python3";
    public double SpeechThreshold { get; set; } = 0.008;
    public double MaxSegmentSeconds { get; set; } = 6;
    public double SilenceSeconds { get; set; } = 0.7;
    public string AiProvider { get; set; } = "ChatGPT";
    public string AiProject { get; set; } = "";
    public string AiSession { get; set; } = "";
    public string SendShortcut { get; set; } = "Cmd+Shift+Z";
    public string RestoreShortcut { get; set; } = "Cmd+Alt+H";
    public string TelegramShortcut { get; set; } = "Cmd+Shift+T";
    public string TelegramAllShortcut { get; set; } = "Cmd+Shift+A";
    public string MessengerTarget { get; set; } = "Telegram";
    public bool AutoSendAnswer { get; set; }
    public int AutoSendFirstChars { get; set; } = 100;
    public int AutoSendNextChars { get; set; } = 500;
    public string Theme { get; set; } = "Dark";
    public bool StartAtLogin { get; set; } = true;
    public bool TranslationCollapsed { get; set; }
    public bool AiPanelCollapsed { get; set; }
    public bool ChatPanelCollapsed { get; set; }
    public double WindowWidth { get; set; } = 1060;
    public double WindowHeight { get; set; } = 860;
    public double PcFontSize { get; set; } = 14;
    public string PcTextColor { get; set; } = "#CCCCCC";
    public string PcCaretColor { get; set; } = "#AEAFAD";
    public double TranslationFontSize { get; set; } = 14;
    public string TranslationTextColor { get; set; } = "#CCCCCC";
    public static string Root { get; private set; } = AppContext.BaseDirectory;
    private static string? projectRoot;
    // A Mac application keeps its preferences in Application Support. The
    // framework's ApplicationData folder is the XDG one on this platform, which
    // would put them somewhere no Mac user would look.
    private static string PreferencesPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library", "Application Support", "HelpMe", "settings.json");
    private static string LocalPreferencesPath => Path.Combine(Root, "user-settings.json");
    private string preferencesWritePath = PreferencesPath;

    // Set by the offscreen preview renderer so looking at the layout never
    // touches the preferences the running app saved.
    public static string? PreferencesPathOverride { get; set; }

    public static AppSettings Load(string? preferencesPathOverride = null)
    {
        preferencesPathOverride ??= PreferencesPathOverride;
        Root = AppContext.BaseDirectory;
        projectRoot = null;
        for (var directory = new DirectoryInfo(Root); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "HelpMe.csproj"))) { projectRoot = directory.FullName; break; }
        }
        var result = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Resolve("appsettings.json"))) ?? throw new InvalidDataException("Invalid appsettings.json.");
        var preferencesPath = string.IsNullOrWhiteSpace(preferencesPathOverride)
            ? (File.Exists(PreferencesPath) ? PreferencesPath : LocalPreferencesPath)
            : Path.GetFullPath(preferencesPathOverride);
        result.preferencesWritePath = string.IsNullOrWhiteSpace(preferencesPathOverride) ? PreferencesPath : preferencesPath;
        if (File.Exists(preferencesPath))
        {
            try
            {
                var saved = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(preferencesPath));
                if (saved is not null)
                {
                    result.AiProvider = saved.AiProvider is "ChatGPT" or "Claude" or "Telegram" ? saved.AiProvider : result.AiProvider;
                    result.AiProject = saved.AiProject ?? "";
                    result.AiSession = saved.AiSession ?? "";
                    if (!string.IsNullOrWhiteSpace(saved.SendShortcut)) result.SendShortcut = saved.SendShortcut;
                    if (!string.IsNullOrWhiteSpace(saved.RestoreShortcut)) result.RestoreShortcut = saved.RestoreShortcut;
                    if (!string.IsNullOrWhiteSpace(saved.TelegramShortcut)) result.TelegramShortcut = saved.TelegramShortcut;
                    if (!string.IsNullOrWhiteSpace(saved.TelegramAllShortcut)) result.TelegramAllShortcut = saved.TelegramAllShortcut;
                    result.MessengerTarget = Messenger.Find(saved.MessengerTarget)?.Name ?? result.MessengerTarget;
                    result.AutoSendAnswer = saved.AutoSendAnswer;
                    if (IsChunkSize(saved.AutoSendFirstChars)) result.AutoSendFirstChars = saved.AutoSendFirstChars;
                    if (IsChunkSize(saved.AutoSendNextChars)) result.AutoSendNextChars = saved.AutoSendNextChars;
                    result.Theme = saved.Theme is "Light" or "Dark" ? saved.Theme : "Dark";
                    result.StartAtLogin = saved.StartAtLogin;
                    result.TranslationCollapsed = saved.TranslationCollapsed;
                    result.AiPanelCollapsed = saved.AiPanelCollapsed;
                    result.ChatPanelCollapsed = saved.ChatPanelCollapsed;
                    result.SystemAudioSource = Enum.IsDefined(saved.SystemAudioSource) ? saved.SystemAudioSource : SystemAudioSource.BlackHole;
                    if (saved.WindowWidth is >= 700 and <= 5000) result.WindowWidth = saved.WindowWidth;
                    if (saved.WindowHeight is >= 300 and <= 5000) result.WindowHeight = saved.WindowHeight;
                    if (IsFontSize(saved.PcFontSize)) result.PcFontSize = saved.PcFontSize;
                    if (IsColor(saved.PcTextColor)) result.PcTextColor = saved.PcTextColor.ToUpperInvariant();
                    if (IsColor(saved.PcCaretColor)) result.PcCaretColor = saved.PcCaretColor.ToUpperInvariant();
                    if (IsFontSize(saved.TranslationFontSize)) result.TranslationFontSize = saved.TranslationFontSize;
                    if (IsColor(saved.TranslationTextColor)) result.TranslationTextColor = saved.TranslationTextColor.ToUpperInvariant();
                }
            }
            catch (JsonException) { }
        }
        Validate(result);
        return result;
    }

    public void SaveSystemAudioSource(SystemAudioSource source)
    {
        if (!Enum.IsDefined(source)) throw new ArgumentOutOfRangeException(nameof(source));
        SystemAudioSource = source; SavePreferences();
    }
    public void SaveAiPreferences(string provider, string project, string session)
    {
        if (provider is not ("ChatGPT" or "Claude" or "Telegram")) throw new ArgumentOutOfRangeException(nameof(provider));
        AiProvider = provider; AiProject = project ?? ""; AiSession = session ?? ""; SavePreferences();
    }
    public void SaveSendShortcut(string shortcut)
    {
        if (string.IsNullOrWhiteSpace(shortcut)) throw new ArgumentException("Shortcut cannot be empty.", nameof(shortcut));
        SendShortcut = shortcut.Trim();
        SavePreferences();
    }
    public void SaveRestoreShortcut(string shortcut)
    {
        if (string.IsNullOrWhiteSpace(shortcut)) throw new ArgumentException("Shortcut cannot be empty.", nameof(shortcut));
        RestoreShortcut = shortcut.Trim();
        SavePreferences();
    }
    public void SaveTelegramShortcut(string shortcut)
    {
        if (string.IsNullOrWhiteSpace(shortcut)) throw new ArgumentException("Shortcut cannot be empty.", nameof(shortcut));
        TelegramShortcut = shortcut.Trim();
        SavePreferences();
    }
    public void SaveTelegramAllShortcut(string shortcut)
    {
        if (string.IsNullOrWhiteSpace(shortcut)) throw new ArgumentException("Shortcut cannot be empty.", nameof(shortcut));
        TelegramAllShortcut = shortcut.Trim();
        SavePreferences();
    }
    public void SaveMessengerTarget(string target)
    {
        MessengerTarget = Messenger.Find(target)?.Name ?? throw new ArgumentOutOfRangeException(nameof(target));
        SavePreferences();
    }
    public void SaveAutoSend(bool enabled, int firstChars, int nextChars)
    {
        if (!IsChunkSize(firstChars) || !IsChunkSize(nextChars))
            throw new ArgumentOutOfRangeException(nameof(firstChars), "Auto-send sizes must be 20 to 4000 characters.");
        AutoSendAnswer = enabled;
        AutoSendFirstChars = firstChars;
        AutoSendNextChars = nextChars;
        SavePreferences();
    }
    public void SaveTheme(string theme)
    {
        if (theme is not ("Light" or "Dark")) throw new ArgumentOutOfRangeException(nameof(theme));
        Theme = theme;
        SavePreferences();
    }
    public void SaveStartAtLogin(bool enabled)
    {
        StartAtLogin = enabled;
        SavePreferences();
    }
    public void SaveCollapsedSections(bool translationCollapsed,
        bool aiPanelCollapsed = false, bool chatPanelCollapsed = false)
    {
        TranslationCollapsed = translationCollapsed;
        AiPanelCollapsed = aiPanelCollapsed;
        ChatPanelCollapsed = chatPanelCollapsed;
        SavePreferences();
    }
    public void SaveWindowSize(double width, double height)
    {
        WindowWidth = Math.Clamp(width, 700, 5000);
        WindowHeight = Math.Clamp(height, 300, 5000);
        SavePreferences();
    }
    public void SaveEditorAppearance(double pcFontSize, string pcTextColor, string pcCaretColor,
        double translationFontSize, string translationTextColor)
    {
        if (!IsFontSize(pcFontSize) || !IsFontSize(translationFontSize))
            throw new ArgumentOutOfRangeException(nameof(pcFontSize), "Font sizes must be between 8 and 48.");
        foreach (var color in new[] { pcTextColor, pcCaretColor, translationTextColor })
            if (!IsColor(color)) throw new ArgumentException("Colors must use #RRGGBB format.", nameof(pcTextColor));
        PcFontSize = pcFontSize;
        PcTextColor = pcTextColor.ToUpperInvariant();
        PcCaretColor = pcCaretColor.ToUpperInvariant();
        TranslationFontSize = translationFontSize;
        TranslationTextColor = translationTextColor.ToUpperInvariant();
        SavePreferences();
    }
    private void SavePreferences()
    {
        var json = JsonSerializer.Serialize(new Preferences(AiProvider, AiProject, AiSession, WindowWidth, WindowHeight,
            PcFontSize, PcTextColor, PcCaretColor, TranslationFontSize, TranslationTextColor,
            SendShortcut, Theme, SystemAudioSource, RestoreShortcut,
            StartAtLogin, TranslationCollapsed, TelegramShortcut, TelegramAllShortcut, MessengerTarget,
            AutoSendAnswer, AutoSendFirstChars, AutoSendNextChars, AiPanelCollapsed, ChatPanelCollapsed),
            new JsonSerializerOptions { WriteIndented = true });
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(preferencesWritePath)!);
            File.WriteAllText(preferencesWritePath, json);
        }
        catch (UnauthorizedAccessException) when (string.Equals(preferencesWritePath, PreferencesPath, StringComparison.OrdinalIgnoreCase))
        {
            File.WriteAllText(LocalPreferencesPath, json);
        }
    }
    private static void Validate(AppSettings result)
    {
        if (result.SpeechThreshold is <= 0 or > 1 || result.MaxSegmentSeconds is < 0.75 or > 15 || result.SilenceSeconds is < 0.1 or > 3)
            throw new InvalidDataException("Audio thresholds in appsettings.json are out of range.");
    }
    private static bool IsFontSize(double value) => value is >= 8 and <= 48;
    // Telegram and WhatsApp both refuse a message of more than about 4000
    // characters, so a chunk size above that could never be delivered.
    public static bool IsChunkSize(int value) => value is >= 20 and <= 4000;
    private static bool IsColor(string? value) => value is not null && Regex.IsMatch(value, "^#[0-9A-Fa-f]{6}$", RegexOptions.CultureInvariant);
    private sealed record Preferences(string AiProvider, string AiProject, string AiSession,
        double WindowWidth = 1060, double WindowHeight = 860,
        double PcFontSize = 14, string PcTextColor = "#CCCCCC", string PcCaretColor = "#AEAFAD",
        double TranslationFontSize = 14, string TranslationTextColor = "#CCCCCC",
        string SendShortcut = "Cmd+Shift+Z", string Theme = "Dark",
        SystemAudioSource SystemAudioSource = SystemAudioSource.BlackHole,
        string RestoreShortcut = "Cmd+Alt+H", bool StartAtLogin = true,
        bool TranslationCollapsed = false,
        string TelegramShortcut = "Cmd+Shift+T", string TelegramAllShortcut = "Cmd+Shift+A", string MessengerTarget = "Telegram",
        bool AutoSendAnswer = false, int AutoSendFirstChars = 100, int AutoSendNextChars = 500,
        bool AiPanelCollapsed = false, bool ChatPanelCollapsed = false);
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
