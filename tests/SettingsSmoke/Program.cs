using HelpMe.Services;

// Launch this check from any unrelated working directory, as Explorer does.
var testRegistryPath = AppSettings.RegistryPath + @"\Tests\" + Guid.NewGuid().ToString("N");
var settings = AppSettings.Load(testRegistryPath);
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

var preferencePath = Path.Combine(AppSettings.Root, "user-settings.json");
var oldPreferences = File.Exists(preferencePath) ? File.ReadAllBytes(preferencePath) : null;
try
{
    settings.SaveChromeDebugPort(9227);
    settings.SaveAiPreferences("Claude", "Work", "Meeting");
    var aiSettings = AppSettings.Load(testRegistryPath);
    if (aiSettings.ChromeDebugPort != 9227 || aiSettings.AiProvider != "Claude" ||
        aiSettings.AiProject != "Work" || aiSettings.AiSession != "Meeting")
        throw new Exception("AI settings did not round-trip through JSON preferences.");
    settings.SaveAiPreferences("ChatGPT", "", "");
    aiSettings = AppSettings.Load(testRegistryPath);
    if (aiSettings.AiProject != "" || aiSettings.AiSession != "")
        throw new Exception("Blank destinations must stay blank.");
    Console.WriteLine("PASS: AI provider, destination and port preference persistence");
    Console.WriteLine("PASS: cross-platform JSON preferences and Whisper-only captions");
}
finally
{
    if (oldPreferences is null) File.Delete(preferencePath);
    else File.WriteAllBytes(preferencePath, oldPreferences);
}
