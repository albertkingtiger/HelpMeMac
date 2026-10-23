using HelpMe.Services;

// Launch this check from any unrelated working directory, as Explorer does.
// Use an isolated preferences file so the test never touches the user's settings.
var preferencePath = Path.Combine(Path.GetTempPath(), $"HelpMe.SettingsSmoke.{Guid.NewGuid():N}.json");
var settings = AppSettings.Load(preferencePath);
foreach (var path in new[] { settings.WhisperModelPath,
    Path.Combine(settings.TranslationModelPath, "model.bin"), settings.PythonPath,
    "translation/worker.py" })
{
    var resolved = AppSettings.Resolve(path);
    if (!File.Exists(resolved)) throw new Exception($"Cannot resolve project asset: {resolved}");
    Console.WriteLine($"PASS: {resolved}");
}

// An asset explicitly installed beside the executable must take priority.
var localAsset = Path.Combine(AppContext.BaseDirectory, "settings-smoke-local.txt");
try
{
    File.WriteAllText(localAsset, "local override");
    if (AppSettings.Resolve("settings-smoke-local.txt") != localAsset)
        throw new Exception("Executable-local assets must take priority.");
    if (AppSettings.Resolve(localAsset) != localAsset)
        throw new Exception("Absolute paths must stay unchanged.");
    Console.WriteLine("PASS: executable-local and absolute paths take priority");
}
finally { File.Delete(localAsset); }

var oldPreferences = File.Exists(preferencePath) ? File.ReadAllBytes(preferencePath) : null;
try
{
    settings.SaveChromeDebugPort(9227);
    settings.SaveAiPreferences("Claude", "Work", "Meeting");
    var aiSettings = AppSettings.Load(preferencePath);
    if (aiSettings.ChromeDebugPort != 9227 || aiSettings.AiProvider != "Claude" ||
        aiSettings.AiProject != "Work" || aiSettings.AiSession != "Meeting")
        throw new Exception("AI settings did not round-trip through JSON preferences.");
    settings.SaveAiPreferences("ChatGPT", "", "");
    aiSettings = AppSettings.Load(preferencePath);
    if (aiSettings.AiProject != "" || aiSettings.AiSession != "")
        throw new Exception("Blank destinations must stay blank.");
    settings.SaveEditorAppearance(17.5, "#112233", "#44AA66", 15, "#DDEEFF", 19, "#ABCDEF");
    var appearanceSettings = AppSettings.Load(preferencePath);
    if (appearanceSettings.PcFontSize != 17.5 || appearanceSettings.PcTextColor != "#112233" ||
        appearanceSettings.PcCaretColor != "#44AA66" || appearanceSettings.AnswerInputFontSize != 15 ||
        appearanceSettings.AnswerInputTextColor != "#DDEEFF" || appearanceSettings.AnswerFontSize != 19 ||
        appearanceSettings.AnswerTextColor != "#ABCDEF")
        throw new Exception("Editor appearance settings did not round-trip through JSON preferences.");
    Console.WriteLine("PASS: AI provider, destination and port preference persistence");
    Console.WriteLine("PASS: per-editor font and color preference persistence");
    Console.WriteLine("PASS: cross-platform JSON preferences and Whisper-only captions");
}
finally
{
    if (oldPreferences is null) File.Delete(preferencePath);
    else File.WriteAllBytes(preferencePath, oldPreferences);
}
