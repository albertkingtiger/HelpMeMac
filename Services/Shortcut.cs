using Avalonia.Input;

namespace HelpMe.Services;

// A keyboard shortcut, parsed and written back in one spelling.
//
// WPF had KeyGestureConverter for this. Avalonia's parser is close but differs
// over the Command key, which is the modifier a Mac shortcut is most likely to
// use, so the handful of rules Help Me! needs live here instead: Settings
// records a shortcut with them, the window matches key presses against them,
// and the global one is handed to Carbon through them.
public readonly record struct Shortcut(KeyModifiers Modifiers, Key Key)
{
    public static bool TryParse(string? text, out Shortcut shortcut)
    {
        shortcut = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var modifiers = KeyModifiers.None;
        var key = Key.None;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "cmd" or "command" or "meta" or "win" or "super":
                    modifiers |= KeyModifiers.Meta; continue;
                case "ctrl" or "control":
                    modifiers |= KeyModifiers.Control; continue;
                case "alt" or "option" or "opt":
                    modifiers |= KeyModifiers.Alt; continue;
                case "shift":
                    modifiers |= KeyModifiers.Shift; continue;
            }
            if (!TryParseKey(raw, out key)) return false;
        }
        if (key == Key.None) return false;
        shortcut = new Shortcut(modifiers, key);
        return true;
    }

    private static bool TryParseKey(string text, out Key key)
    {
        // A bare digit is D1..D0 in the key enum, which is not what anyone
        // types into a shortcut box.
        if (text.Length == 1 && char.IsAsciiDigit(text[0])) return Enum.TryParse("D" + text, true, out key);
        return Enum.TryParse(text, ignoreCase: true, out key) && key != Key.None;
    }

    public int ModifierCount
    {
        get
        {
            var count = 0;
            if (Modifiers.HasFlag(KeyModifiers.Meta)) count++;
            if (Modifiers.HasFlag(KeyModifiers.Control)) count++;
            if (Modifiers.HasFlag(KeyModifiers.Alt)) count++;
            if (Modifiers.HasFlag(KeyModifiers.Shift)) count++;
            return count;
        }
    }

    public bool Matches(Key pressed, KeyModifiers pressedModifiers)
        => pressed == Key && pressedModifiers == Modifiers;

    public override string ToString()
    {
        var parts = new List<string>(5);
        if (Modifiers.HasFlag(KeyModifiers.Meta)) parts.Add("Cmd");
        if (Modifiers.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        parts.Add(Name(Key));
        return string.Join("+", parts);
    }

    private static string Name(Key key)
    {
        var text = key.ToString();
        return text.Length == 2 && text[0] == 'D' && char.IsAsciiDigit(text[1]) ? text[1..] : text;
    }

    // The spelling to store and show, or the fallback when what was saved can
    // no longer be understood.
    public static string Normalize(string? text, string fallback)
        => TryParse(text, out var shortcut) ? shortcut.ToString() : fallback;

    // Modifier keys on their own are not a shortcut; a capture box ignores them
    // and waits for the key they are held with.
    public static bool IsModifierKey(Key key) => key is
        Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or
        Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin;
}
