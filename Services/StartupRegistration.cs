using System.IO;
using Microsoft.Win32;

namespace HelpMe.Services;

// Help Me! starts hidden in the tray, so launching it at sign-in is the normal
// way to have captions ready. The per-user Run key needs no elevation.
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "HelpMe";

    private static string? ExecutablePath
    {
        get
        {
            var path = Environment.ProcessPath;
            return string.IsNullOrWhiteSpace(path) || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? null : path;
        }
    }

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string value && value.Length > 0;
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // Returns null on success, or a message explaining why the preference could
    // not be applied. A failure here never prevents the app from running.
    public static string? Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true)
                ?? throw new InvalidOperationException("The Windows startup key is unavailable.");
            if (!enabled) { key.DeleteValue(ValueName, throwOnMissingValue: false); return null; }
            var executable = ExecutablePath;
            if (executable is null)
                return "Help Me! can only start with Windows when it is run from its built executable.";
            key.SetValue(ValueName, "\"" + executable + "\"", RegistryValueKind.String);
            return null;
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException
            or InvalidOperationException or IOException)
        {
            return "Could not update the Windows startup setting: " + error.Message;
        }
    }
}
