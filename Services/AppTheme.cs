using System.Windows;
using System.Windows.Media;

namespace HelpMe.Services;

public static class AppTheme
{
    private static readonly Dictionary<string, string> Dark = new()
    {
        ["WindowBackground"] = "#181818", ["EditorBackground"] = "#111213",
        ["Surface"] = "#1F1F1F", ["InputBackground"] = "#252526",
        ["Divider"] = "#303031", ["Foreground"] = "#CCCCCC",
        ["Muted"] = "#9D9D9D", ["Accent"] = "#0078D4",
        ["Hover"] = "#2A2D2E", ["Selection"] = "#264F78",
        ["Error"] = "#F48771", ["ScrollThumb"] = "#505050",
        ["ScrollThumbHover"] = "#686868", ["ScrollThumbDrag"] = "#808080",
        ["ToggleChecked"] = "#203A50", ["Pressed"] = "#37373D",
        ["SelectionText"] = "#FFFFFF"
    };

    private static readonly Dictionary<string, string> Light = new()
    {
        ["WindowBackground"] = "#F3F3F3", ["EditorBackground"] = "#FFFFFF",
        ["Surface"] = "#FAFAFA", ["InputBackground"] = "#FFFFFF",
        ["Divider"] = "#D4D4D4", ["Foreground"] = "#242424",
        ["Muted"] = "#616161", ["Accent"] = "#0067B8",
        ["Hover"] = "#E9E9E9", ["Selection"] = "#ADD6FF",
        ["Error"] = "#B42318", ["ScrollThumb"] = "#B8B8B8",
        ["ScrollThumbHover"] = "#999999", ["ScrollThumbDrag"] = "#777777",
        ["ToggleChecked"] = "#DCEEFF", ["Pressed"] = "#DCDCDC",
        ["SelectionText"] = "#171717"
    };

    public static void Apply(string theme)
    {
        foreach (var (key, value) in theme == "Light" ? Light : Dark)
            System.Windows.Application.Current.Resources[key] = new SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(value));
    }
}
