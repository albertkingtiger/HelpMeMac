using System.Diagnostics;
using System.IO;

namespace HelpMe.Services;

// Starting Help Me! when the user logs in.
//
// Windows used a per-user Run key. The macOS equivalent is a LaunchAgent: a
// property list in the user's own LaunchAgents folder, which needs no
// administrator rights and is removed again by deleting the file.
public static class LoginItem
{
    private const string Label = "com.helpme.app";

    private static string AgentDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents");

    private static string AgentPath => Path.Combine(AgentDirectory, Label + ".plist");

    // The executable inside the bundle. Running from `dotnet run` during
    // development gives a path too, and registering it works, but the normal
    // case is HelpMe.app/Contents/MacOS/HelpMe.
    private static string? ExecutablePath
    {
        get
        {
            var path = Environment.ProcessPath;
            return string.IsNullOrWhiteSpace(path) || !File.Exists(path) ? null : path;
        }
    }

    public static bool IsEnabled()
    {
        try { return File.Exists(AgentPath); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // Returns null on success, or a message explaining why the preference could
    // not be applied. A failure here never prevents the app from running.
    public static string? Apply(bool enabled)
    {
        if (!OperatingSystem.IsMacOS())
            return enabled ? "Starting at login is only set up on macOS." : null;
        try
        {
            if (!enabled)
            {
                if (File.Exists(AgentPath))
                {
                    RunLaunchctl("unload", AgentPath);
                    File.Delete(AgentPath);
                }
                return null;
            }
            var executable = ExecutablePath;
            if (executable is null)
                return "Help Me! can only start at login when it is run from its built application.";
            Directory.CreateDirectory(AgentDirectory);
            File.WriteAllText(AgentPath, Plist(executable));
            // Loading it now means the setting takes effect without a logout.
            // launchctl complains if it is already loaded, which is not a
            // failure worth reporting.
            RunLaunchctl("load", AgentPath);
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return "Could not update the start-at-login setting: " + error.Message;
        }
    }

    private static string Plist(string executable) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
          <key>Label</key>
          <string>{Label}</string>
          <key>ProgramArguments</key>
          <array>
            <string>{System.Security.SecurityElement.Escape(executable)}</string>
          </array>
          <key>RunAtLoad</key>
          <true/>
          <!-- Help Me! is a background app that puts itself in the menu bar, so
               launchd must not restart it when the user chooses Exit. -->
          <key>KeepAlive</key>
          <false/>
          <key>ProcessType</key>
          <string>Interactive</string>
        </dict>
        </plist>
        """;

    private static void RunLaunchctl(string verb, string path)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("/bin/launchctl")
            {
                ArgumentList = { verb, "-w", path },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            process?.WaitForExit(5000);
        }
        // launchctl is advisory here: the plist on disk is what decides the
        // next login, and that has already been written.
        catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception) { }
    }
}
