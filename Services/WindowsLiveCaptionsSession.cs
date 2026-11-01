using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.Windows.Automation;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Text;

namespace HelpMe.Services;

// Windows does not publish a caption stream API. This optional source reads the
// accessibility text exposed by the visible Live Captions window.
public sealed class WindowsLiveCaptionsSession(
    Action<string> caption, Action<string> status, Action<string> preview) : ICaptionSession
{
    private readonly CancellationTokenSource stop = new();
    private Task? reader;
    private bool startedHere;
    private string confirmedText = "";
    private string previousLive = "";
    private string currentLive = "";
    private bool windowVisible;
    private IntPtr captionHandle;
    private IntPtr visibilityAppliedHandle;
    private int visibleLeft;
    private int visibleTop;
    private bool savedVisiblePosition;
    private static (int Left, int Top)? lastVisiblePosition;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr handle, int command);
    [StructLayout(LayoutKind.Sequential)]
    private struct WindowRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr handle, out WindowRect rect);
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr handle, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    private delegate bool WindowVisitor(IntPtr handle, IntPtr data);
    [DllImport("user32.dll")]
    private static extern bool EnumWindows(WindowVisitor visitor, IntPtr data);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr handle, StringBuilder className, int capacity);
    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr handle);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr(IntPtr handle, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(IntPtr handle, int index, nint value);

    public bool ToggleVisibility()
    {
        SetVisible(!windowVisible);
        return windowVisible;
    }

    public void SetVisible(bool visible)
    {
        windowVisible = visible;
        var handle = FindWindowHandle();
        if (handle == IntPtr.Zero) return;
        RemoveTaskbarButton(handle);
        const uint keepSizeAndOrder = 0x0001 | 0x0004 | 0x0010;
        if (visible)
        {
            var position = savedVisiblePosition ? (visibleLeft, visibleTop) : lastVisiblePosition;
            if (position is null && GetWindowRect(handle, out var hidden) && hidden.Left <= -20000)
                position = (100, 100);
            if (position is not null)
                SetWindowPos(handle, IntPtr.Zero, position.Value.Item1, position.Value.Item2, 0, 0, keepSizeAndOrder);
            ShowWindow(handle, 8); // Show without activating.
        }
        else
        {
            if (GetWindowRect(handle, out var rect) && rect.Left > -20000 && rect.Top > -20000)
            {
                visibleLeft = rect.Left;
                visibleTop = rect.Top;
                savedVisiblePosition = true;
                lastVisiblePosition = (rect.Left, rect.Top);
            }
            // Keeping the window shown but offscreen preserves its UI Automation
            // caption text. SW_HIDE removes the caption controls from that tree.
            ShowWindow(handle, 8);
            SetWindowPos(handle, IntPtr.Zero, -32000, -32000, 0, 0, keepSizeAndOrder);
        }
        visibilityAppliedHandle = handle;
    }

    private static bool IsOnScreen(IntPtr handle)
        => GetWindowRect(handle, out var rect) && rect.Left > -20000 && rect.Top > -20000;

    private static void RemoveTaskbarButton(IntPtr handle)
    {
        const int extendedStyle = -20;
        const long toolWindow = 0x00000080;
        const long appWindow = 0x00040000;
        var oldStyle = GetWindowLongPtr(handle, extendedStyle).ToInt64();
        var newStyle = (oldStyle | toolWindow) & ~appWindow;
        if (newStyle == oldStyle) return;
        SetWindowLongPtr(handle, extendedStyle, (nint)newStyle);
        const uint refreshFrameWithoutMoving = 0x0001 | 0x0002 | 0x0004 | 0x0010 | 0x0020;
        SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0, refreshFrameWithoutMoving);
    }

    public async Task StartAsync()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621))
            throw new PlatformNotSupportedException("Windows 11 Live Captions requires Windows 11 22H2 or later.");
        // This session owns the Live Captions process lifecycle: Start starts
        // it, Stop closes it, and the next Start creates it again.
        startedHere = true;
        if (Process.GetProcessesByName("LiveCaptions").Length == 0)
        {
            var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "LiveCaptions.exe");
            if (!File.Exists(executable)) throw new FileNotFoundException("Windows 11 Live Captions is not installed.", executable);
            using var launched = Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true });
        }
        status("Waiting for Windows Live Captions. Complete its first-run setup if prompted.");
        reader = Task.Run(ReadLoopAsync);
        for (var attempt = 0; attempt < 100 && FindWindowHandle() == IntPtr.Zero; attempt++)
            await Task.Delay(100).ConfigureAwait(false);
        SetVisible(false);
        return;
    }

    private async Task ReadLoopAsync()
    {
        var hadText = false;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var handle = FindWindowHandle();
                // Live Captions restores its own position after first-run setup and
                // when Windows re-creates the window, so hiding once at start is not
                // enough; re-apply whenever it comes back on screen uninvited.
                if (handle != IntPtr.Zero && (handle != visibilityAppliedHandle || IsOnScreen(handle) != windowVisible))
                    SetVisible(windowVisible);
                var visible = ReadCaptionText();
                if (visible.Length > 0)
                {
                    if (!hadText) { status("Windows Live Captions connected."); hadText = true; }
                    ProcessSnapshot(visible);
                }
                else if (hadText) { status("Waiting for Windows Live Captions window."); hadText = false; }
            }
            catch (ElementNotAvailableException) { }
            catch (InvalidOperationException) { }
            catch (System.Runtime.InteropServices.COMException) { }
            try { await Task.Delay(250, stop.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
        if (previousLive.Length > 0) caption(previousLive);
    }

    private string ReadCaptionText()
    {
        var handle = FindWindowHandle();
        if (handle == IntPtr.Zero) return "";
        var window = AutomationElement.FromHandle(handle);
        var text = window.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "CaptionsTextBlock"));
        if (text is null) return "";
        if (text.TryGetCurrentPattern(TextPattern.Pattern, out var pattern))
            return ((TextPattern)pattern).DocumentRange.GetText(-1).Trim();
        return text.Current.Name.Trim();
    }

    private IntPtr FindWindowHandle()
    {
        if (captionHandle != IntPtr.Zero && IsWindow(captionHandle)) return captionHandle;
        captionHandle = IntPtr.Zero;
        var processIds = new HashSet<uint>();
        foreach (var process in Process.GetProcessesByName("LiveCaptions"))
        {
            using (process)
                processIds.Add((uint)process.Id);
        }
        if (processIds.Count == 0) return IntPtr.Zero;
        EnumWindows((handle, _) =>
        {
            GetWindowThreadProcessId(handle, out var processId);
            if (!processIds.Contains(processId)) return true;
            var className = new StringBuilder(128);
            GetClassName(handle, className, className.Capacity);
            if (className.ToString() != "LiveCaptionsDesktopWindow") return true;
            captionHandle = handle;
            return false;
        }, IntPtr.Zero);
        return captionHandle;
    }

    private void ProcessSnapshot(string visible)
    {
        currentLive = CaptionTextCleaner.Clean(Regex.Replace(visible, @"\s+", " "));
        if (currentLive.Length == 0 || currentLive == previousLive) return;
        if (previousLive.Length == 0)
        {
            previousLive = currentLive;
            preview(previousLive);
            return;
        }

        if (IsRevision(previousLive, currentLive))
        {
            previousLive = currentLive;
            preview(previousLive);
            return;
        }

        var overlapStart = SuffixPrefixStart(previousLive, currentLive);
        var settled = overlapStart >= 0 ? previousLive[..overlapStart].Trim() : previousLive;
        if (settled.Length > 0)
        {
            confirmedText += (confirmedText.Length > 0 ? " " : "") + settled;
            caption(settled);
        }
        previousLive = currentLive;
        preview(previousLive);
    }

    private static bool IsRevision(string previous, string current)
    {
        if (current.StartsWith(previous, StringComparison.OrdinalIgnoreCase) ||
            previous.StartsWith(current, StringComparison.OrdinalIgnoreCase)) return true;
        var oldWords = Words(previous);
        var newWords = Words(current);
        var common = 0;
        while (common < Math.Min(oldWords.Length, newWords.Length) &&
               oldWords[common].Equals(newWords[common], StringComparison.OrdinalIgnoreCase)) common++;
        return common > 0 && (common >= 2 && common * 2 >= Math.Min(oldWords.Length, newWords.Length) ||
                              Math.Min(oldWords.Length, newWords.Length) <= 2);
    }

    private static string[] Words(string text) => Regex.Matches(text, @"[\p{L}\p{N}']+")
        .Select(match => match.Value).ToArray();

    private static int SuffixPrefixStart(string previous, string current)
    {
        var oldWords = Regex.Matches(previous, @"[\p{L}\p{N}']+").ToArray();
        var newWords = Regex.Matches(current, @"[\p{L}\p{N}']+").ToArray();
        for (var count = Math.Min(oldWords.Length, newWords.Length); count >= 1; count--)
        {
            var first = oldWords.Length - count;
            if (count == 1 && oldWords[first].Length < 5) continue;
            var matches = true;
            for (var index = 0; index < count; index++)
                if (!oldWords[first + index].Value.Equals(newWords[index].Value, StringComparison.OrdinalIgnoreCase))
                    { matches = false; break; }
            if (matches) return oldWords[first].Index;
        }
        return -1;
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        if (reader is not null) await reader.ConfigureAwait(false);
        stop.Dispose();
        if (startedHere)
        {
            foreach (var process in Process.GetProcessesByName("LiveCaptions"))
            {
                using (process)
                {
                    try
                    {
                        if (process.HasExited) continue;
                        process.CloseMainWindow();
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                        try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
                        catch (OperationCanceledException)
                        {
                            if (!process.HasExited) process.Kill();
                            await process.WaitForExitAsync().ConfigureAwait(false);
                        }
                    }
                    catch (InvalidOperationException) { }
                    catch (Win32Exception error) { status($"Windows Live Captions could not close: {error.Message}"); }
                }
            }
        }
    }
}
