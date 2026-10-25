using System.Text.RegularExpressions;

namespace HelpMe.Services;

public static class CaptionTextCleaner
{
    // Whisper writes non-speech cues as stage directions in brackets. Strip
    // them before previews or final captions reach the UI and translation.
    private static readonly Regex StageDirection = new(@"\[[^\[\]\r\n]{1,120}\]|\([^()\r\n]{1,120}\)|♪[^♪\r\n]*♪",
        RegexOptions.Compiled);
    private static readonly Regex ForeignLanguage = new(@"\b(?:speaking\s+in\s+(?:a\s+)?foreign\s+language|BLANK_AUDIO)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    public static string Clean(string? transcript)
    {
        if (string.IsNullOrWhiteSpace(transcript)) return "";
        var text = StageDirection.Replace(transcript, " ");
        var removedCue = text != transcript;
        text = ForeignLanguage.Replace(text, " ");
        text = Whitespace.Replace(text, " ").Trim();
        text = Regex.Replace(text, @"\s+([,.;:!?])", "$1");
        // A removed cue may leave only separators, or punctuation before the
        // first spoken word: "[MUSIC PLAYING], Hello".
        if (removedCue) text = text.TrimStart(',', '.', ';', ':', '!', '?', ' ');
        if (!text.Any(char.IsLetterOrDigit)) return "";
        return text;
    }
}
