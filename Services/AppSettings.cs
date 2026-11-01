using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

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
    public string SendShortcut { get; set; } = "Ctrl+Shift+Z";
    public string RestoreShortcut { get; set; } = "Ctrl+Alt+H";
    public string TelegramShortcut { get; set; } = "Ctrl+Shift+T";
    public string TelegramAllShortcut { get; set; } = "Ctrl+Shift+A";
    public string MessengerTarget { get; set; } = "Telegram";
    public bool AutoSendAnswer { get; set; }
    public int AutoSendSentences { get; set; } = 2;
    public string Theme { get; set; } = "Dark";
    public bool StartWithWindows { get; set; } = true;
    public bool TranslationCollapsed { get; set; }
    public bool AnswerCollapsed { get; set; }
    public double WindowWidth { get; set; } = 1060;
    public double WindowHeight { get; set; } = 860;
    public double PcFontSize { get; set; } = 14;
    public string PcTextColor { get; set; } = "#CCCCCC";
    public string PcCaretColor { get; set; } = "#AEAFAD";
    public double TranslationFontSize { get; set; } = 14;
    public string TranslationTextColor { get; set; } = "#CCCCCC";
    public double AnswerInputFontSize { get; set; } = 14;
    public string AnswerInputTextColor { get; set; } = "#CCCCCC";
    public double AnswerFontSize { get; set; } = 14;
    public string AnswerTextColor { get; set; } = "#CCCCCC";
    public static string Root { get; private set; } = AppContext.BaseDirectory;
    private static string? projectRoot;
    private static string PreferencesPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HelpMe", "settings.json");
    private static string LocalPreferencesPath => Path.Combine(Root, "user-settings.json");
    private string preferencesWritePath = PreferencesPath;
    public const string RegistryPath = "HelpMe";

    public static AppSettings Load(string? preferencesPathOverride = null)
    {
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
                    result.ChromeDebugPort = saved.ChromeDebugPort;
                    result.AiProvider = saved.AiProvider is "ChatGPT" or "Claude" or "Telegram" ? saved.AiProvider : result.AiProvider;
                    result.AiProject = saved.AiProject ?? "";
                    result.AiSession = saved.AiSession ?? "";
                    if (!string.IsNullOrWhiteSpace(saved.SendShortcut)) result.SendShortcut = saved.SendShortcut;
                    if (!string.IsNullOrWhiteSpace(saved.RestoreShortcut)) result.RestoreShortcut = saved.RestoreShortcut;
                    if (!string.IsNullOrWhiteSpace(saved.TelegramShortcut)) result.TelegramShortcut = saved.TelegramShortcut;
                    if (!string.IsNullOrWhiteSpace(saved.TelegramAllShortcut)) result.TelegramAllShortcut = saved.TelegramAllShortcut;
                    result.MessengerTarget = Messenger.Find(saved.MessengerTarget)?.Name ?? result.MessengerTarget;
                    result.AutoSendAnswer = saved.AutoSendAnswer;
                    if (IsSentenceCount(saved.AutoSendSentences)) result.AutoSendSentences = saved.AutoSendSentences;
                    result.Theme = saved.Theme is "Light" or "Dark" ? saved.Theme : "Dark";
                    result.StartWithWindows = saved.StartWithWindows;
                    result.TranslationCollapsed = saved.TranslationCollapsed;
                    result.AnswerCollapsed = saved.AnswerCollapsed;
                    result.CaptionMethod = saved.CaptionMethod is CaptionMethod.WindowsLiveCaptions ? saved.CaptionMethod : CaptionMethod.Whisper;
                    if (saved.WindowWidth is >= 700 and <= 5000) result.WindowWidth = saved.WindowWidth;
                    if (saved.WindowHeight is >= 300 and <= 5000) result.WindowHeight = saved.WindowHeight;
                    if (IsFontSize(saved.PcFontSize)) result.PcFontSize = saved.PcFontSize;
                    if (IsColor(saved.PcTextColor)) result.PcTextColor = saved.PcTextColor.ToUpperInvariant();
                    if (IsColor(saved.PcCaretColor)) result.PcCaretColor = saved.PcCaretColor.ToUpperInvariant();
                    if (IsFontSize(saved.TranslationFontSize)) result.TranslationFontSize = saved.TranslationFontSize;
                    if (IsColor(saved.TranslationTextColor)) result.TranslationTextColor = saved.TranslationTextColor.ToUpperInvariant();
                    if (IsFontSize(saved.AnswerInputFontSize)) result.AnswerInputFontSize = saved.AnswerInputFontSize;
                    if (IsColor(saved.AnswerInputTextColor)) result.AnswerInputTextColor = saved.AnswerInputTextColor.ToUpperInvariant();
                    if (IsFontSize(saved.AnswerFontSize)) result.AnswerFontSize = saved.AnswerFontSize;
                    if (IsColor(saved.AnswerTextColor)) result.AnswerTextColor = saved.AnswerTextColor.ToUpperInvariant();
                }
            }
            catch (JsonException) { }
        }
        Validate(result);
        return result;
    }

    public void SaveCaptionMethod(CaptionMethod method)
    {
        if (!Enum.IsDefined(method)) throw new ArgumentOutOfRangeException(nameof(method));
        CaptionMethod = method; SavePreferences();
    }
    public void SaveChromeDebugPort(int port)
    {
        if (port is < 1024 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        ChromeDebugPort = port; SavePreferences();
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
    public void SaveAutoSend(bool enabled, int sentencesPerMessage)
    {
        if (!IsSentenceCount(sentencesPerMessage))
            throw new ArgumentOutOfRangeException(nameof(sentencesPerMessage), "Auto-send must use 1 to 10 sentences.");
        AutoSendAnswer = enabled;
        AutoSendSentences = sentencesPerMessage;
        SavePreferences();
    }
    public void SaveTheme(string theme)
    {
        if (theme is not ("Light" or "Dark")) throw new ArgumentOutOfRangeException(nameof(theme));
        Theme = theme;
        SavePreferences();
    }
    public void SaveStartWithWindows(bool enabled)
    {
        StartWithWindows = enabled;
        SavePreferences();
    }
    public void SaveCollapsedSections(bool translationCollapsed, bool answerCollapsed)
    {
        TranslationCollapsed = translationCollapsed;
        AnswerCollapsed = answerCollapsed;
        SavePreferences();
    }
    public void SaveWindowSize(double width, double height)
    {
        WindowWidth = Math.Clamp(width, 700, 5000);
        WindowHeight = Math.Clamp(height, 300, 5000);
        SavePreferences();
    }
    public void SaveEditorAppearance(double pcFontSize, string pcTextColor, string pcCaretColor,
        double translationFontSize, string translationTextColor,
        double answerInputFontSize, string answerInputTextColor, double answerFontSize, string answerTextColor)
    {
        if (!IsFontSize(pcFontSize) || !IsFontSize(translationFontSize) || !IsFontSize(answerInputFontSize) || !IsFontSize(answerFontSize))
            throw new ArgumentOutOfRangeException(nameof(pcFontSize), "Font sizes must be between 8 and 48.");
        foreach (var color in new[] { pcTextColor, pcCaretColor, translationTextColor, answerInputTextColor, answerTextColor })
            if (!IsColor(color)) throw new ArgumentException("Colors must use #RRGGBB format.", nameof(pcTextColor));
        PcFontSize = pcFontSize;
        PcTextColor = pcTextColor.ToUpperInvariant();
        PcCaretColor = pcCaretColor.ToUpperInvariant();
        TranslationFontSize = translationFontSize;
        TranslationTextColor = translationTextColor.ToUpperInvariant();
        AnswerInputFontSize = answerInputFontSize;
        AnswerInputTextColor = answerInputTextColor.ToUpperInvariant();
        AnswerFontSize = answerFontSize;
        AnswerTextColor = answerTextColor.ToUpperInvariant();
        SavePreferences();
    }
    private void SavePreferences()
    {
        var json = JsonSerializer.Serialize(new Preferences(ChromeDebugPort, AiProvider, AiProject, AiSession, WindowWidth, WindowHeight,
            PcFontSize, PcTextColor, PcCaretColor, TranslationFontSize, TranslationTextColor,
            AnswerInputFontSize, AnswerInputTextColor, AnswerFontSize, AnswerTextColor, SendShortcut, Theme, CaptionMethod, RestoreShortcut,
            StartWithWindows, TranslationCollapsed, AnswerCollapsed, TelegramShortcut, TelegramAllShortcut, MessengerTarget,
            AutoSendAnswer, AutoSendSentences),
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
        if (result.SpeechThreshold is <= 0 or > 1 || result.MaxSegmentSeconds is < 0.75 or > 15 || result.SilenceSeconds is < 0.1 or > 3 || result.ChromeDebugPort is < 1024 or > 65535)
            throw new InvalidDataException("Audio thresholds in appsettings.json are out of range.");
    }
    private static bool IsFontSize(double value) => value is >= 8 and <= 48;
    public static bool IsSentenceCount(int value) => value is >= 1 and <= 10;
    private static bool IsColor(string? value) => value is not null && Regex.IsMatch(value, "^#[0-9A-Fa-f]{6}$", RegexOptions.CultureInvariant);
    private sealed record Preferences(int ChromeDebugPort, string AiProvider, string AiProject, string AiSession,
        double WindowWidth = 1060, double WindowHeight = 860,
        double PcFontSize = 14, string PcTextColor = "#CCCCCC", string PcCaretColor = "#AEAFAD",
        double TranslationFontSize = 14, string TranslationTextColor = "#CCCCCC",
        double AnswerInputFontSize = 14, string AnswerInputTextColor = "#CCCCCC",
        double AnswerFontSize = 14, string AnswerTextColor = "#CCCCCC",
        string SendShortcut = "Ctrl+Shift+Z", string Theme = "Dark", CaptionMethod CaptionMethod = CaptionMethod.Whisper,
        string RestoreShortcut = "Ctrl+Alt+H", bool StartWithWindows = true,
        bool TranslationCollapsed = false, bool AnswerCollapsed = false,
        string TelegramShortcut = "Ctrl+Shift+T", string TelegramAllShortcut = "Ctrl+Shift+A", string MessengerTarget = "Telegram",
        bool AutoSendAnswer = false, int AutoSendSentences = 2);
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
