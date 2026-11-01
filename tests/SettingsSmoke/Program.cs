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
    settings.SaveAiPreferences("Claude", "Work", "Meeting");
    var aiSettings = AppSettings.Load(preferencePath);
    if (aiSettings.AiProvider != "Claude" ||
        aiSettings.AiProject != "Work" || aiSettings.AiSession != "Meeting")
        throw new Exception("AI settings did not round-trip through JSON preferences.");
    settings.SaveAiPreferences("ChatGPT", "", "");
    aiSettings = AppSettings.Load(preferencePath);
    if (aiSettings.AiProject != "" || aiSettings.AiSession != "")
        throw new Exception("Blank destinations must stay blank.");
    settings.SaveEditorAppearance(17.5, "#112233", "#44AA66", 16, "#778899", 15, "#DDEEFF", 19, "#ABCDEF");
    var appearanceSettings = AppSettings.Load(preferencePath);
    if (appearanceSettings.PcFontSize != 17.5 || appearanceSettings.PcTextColor != "#112233" ||
        appearanceSettings.PcCaretColor != "#44AA66" || appearanceSettings.AnswerInputFontSize != 15 ||
        appearanceSettings.TranslationFontSize != 16 || appearanceSettings.TranslationTextColor != "#778899" ||
        appearanceSettings.AnswerInputTextColor != "#DDEEFF" || appearanceSettings.AnswerFontSize != 19 ||
        appearanceSettings.AnswerTextColor != "#ABCDEF")
        throw new Exception("Editor appearance settings did not round-trip through JSON preferences.");
    settings.SaveSendShortcut("Ctrl+Alt+K");
    if (AppSettings.Load(preferencePath).SendShortcut != "Ctrl+Alt+K")
        throw new Exception("Send shortcut did not round-trip through JSON preferences.");
    settings.SaveRestoreShortcut("Ctrl+Alt+H");
    if (AppSettings.Load(preferencePath).RestoreShortcut != "Ctrl+Alt+H")
        throw new Exception("Restore shortcut did not round-trip through JSON preferences.");
    settings.SaveTelegramShortcut("Ctrl+Alt+G");
    if (AppSettings.Load(preferencePath).TelegramShortcut != "Ctrl+Alt+G")
        throw new Exception("Telegram shortcut did not round-trip through JSON preferences.");
    settings.SaveTelegramAllShortcut("Ctrl+Alt+J");
    if (AppSettings.Load(preferencePath).TelegramAllShortcut != "Ctrl+Alt+J")
        throw new Exception("All Send shortcut did not round-trip through JSON preferences.");
    Console.WriteLine("PASS: configurable Send Selection and All Send shortcut persistence");
    settings.SaveMessengerTarget("WhatsApp");
    if (AppSettings.Load(preferencePath).MessengerTarget != "WhatsApp")
        throw new Exception("Messenger choice did not round-trip through JSON preferences.");
    settings.SaveMessengerTarget("Telegram");
    Console.WriteLine("PASS: Telegram or WhatsApp destination persistence");
    settings.SaveAutoSend(enabled: true, firstChars: 120, nextChars: 600);
    var autoSend = AppSettings.Load(preferencePath);
    if (!autoSend.AutoSendAnswer || autoSend.AutoSendFirstChars != 120 || autoSend.AutoSendNextChars != 600)
        throw new Exception("Auto-send settings did not round-trip through JSON preferences.");
    // Out-of-range sizes are refused rather than saved and clamped later.
    foreach (var invalid in new[] { 0, -1, 19, 4001 })
    {
        try { settings.SaveAutoSend(true, invalid, 500); }
        catch (ArgumentOutOfRangeException)
        {
            try { settings.SaveAutoSend(true, 100, invalid); }
            catch (ArgumentOutOfRangeException) { continue; }
            throw new Exception($"Auto-send accepted {invalid} characters for later messages.");
        }
        throw new Exception($"Auto-send accepted {invalid} characters for the first message.");
    }
    settings.SaveAutoSend(enabled: false, firstChars: 100, nextChars: 500);
    if (AppSettings.Load(preferencePath).AutoSendAnswer)
        throw new Exception("Auto-send did not turn back off through JSON preferences.");
    Console.WriteLine("PASS: auto-send toggle and sentence count persistence");
    settings.SaveTheme("Light");
    settings.SaveCaptionMethod(CaptionMethod.WindowsLiveCaptions);
    var displaySettings = AppSettings.Load(preferencePath);
    if (displaySettings.Theme != "Light" || displaySettings.CaptionMethod != CaptionMethod.WindowsLiveCaptions)
        throw new Exception("Theme and caption source did not round-trip through JSON preferences.");
    Console.WriteLine("PASS: AI provider and destination preference persistence");
    Console.WriteLine("PASS: per-editor font and color preference persistence");
    Console.WriteLine("PASS: configurable send shortcut preference persistence");
    Console.WriteLine("PASS: configurable tray restore shortcut preference persistence");
    Console.WriteLine("PASS: theme and Windows caption source persistence");
    settings.SaveStartWithWindows(false);
    settings.SaveCollapsedSections(translationCollapsed: true, answerCollapsed: false);
    var layout = AppSettings.Load(preferencePath);
    if (layout.StartWithWindows || !layout.TranslationCollapsed || layout.AnswerCollapsed)
        throw new Exception("Startup and collapsed-section settings did not round-trip through JSON preferences.");
    Console.WriteLine("PASS: Windows startup and collapsed section persistence");
    settings.SaveCollapsedSections(translationCollapsed: false, answerCollapsed: false,
        aiPanelCollapsed: true, chatPanelCollapsed: false);
    var panels = AppSettings.Load(preferencePath);
    if (!panels.AiPanelCollapsed || panels.ChatPanelCollapsed)
        throw new Exception("Panel 05 and 06 collapse states did not round-trip through JSON preferences.");
    settings.SaveCollapsedSections(false, false);
    Console.WriteLine("PASS: browser panel collapse persistence");
}
finally
{
    if (oldPreferences is null) File.Delete(preferencePath);
    else File.WriteAllBytes(preferencePath, oldPreferences);
}
