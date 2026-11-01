using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Text;
using HelpMe.Services;

if (args.Contains("--attach"))
{
    var active = Native.FindCaptionWindow();
    if (active == IntPtr.Zero) throw new Exception("No Live Captions window is running.");
    typeof(WindowsLiveCaptionsSession).GetMethod("RemoveTaskbarButton", BindingFlags.NonPublic | BindingFlags.Static)!
        .Invoke(null, new object[] { active });
    var activeStyle = Native.GetWindowLongPtr(active, -20).ToInt64();
    if ((activeStyle & 0x80) == 0 || (activeStyle & 0x40000) != 0)
        throw new Exception("Live Captions taskbar window style was not removed.");
    Console.WriteLine("PASS: existing Live Captions window has tool-window style");
    return;
}

if (Process.GetProcessesByName("LiveCaptions").Length != 0)
    throw new InvalidOperationException("Run this smoke test only when Live Captions is stopped.");

for (var cycle = 1; cycle <= 2; cycle++)
{
    await using (var session = new WindowsLiveCaptionsSession(_ => { }, Console.WriteLine, _ => { }))
    {
        await session.StartAsync();
        if (Process.GetProcessesByName("LiveCaptions").Length == 0)
            throw new Exception($"Live Captions did not start in cycle {cycle}.");
        var handle = Native.FindCaptionWindow();
        if (handle == IntPtr.Zero || !Native.GetWindowRect(handle, out var hidden) || hidden.Left > -20000)
            throw new Exception($"Live Captions was not hidden by default in cycle {cycle}.");
        var style = Native.GetWindowLongPtr(handle, -20).ToInt64();
        if ((style & 0x80) == 0 || (style & 0x40000) != 0)
            throw new Exception($"Live Captions still has a taskbar window style in cycle {cycle}.");
        if (!session.ToggleVisibility() || !Native.GetWindowRect(handle, out var shown) || shown.Left < -20000)
            throw new Exception($"Live Captions could not be shown in cycle {cycle}.");
        if (session.ToggleVisibility() || !Native.GetWindowRect(handle, out hidden) || hidden.Left > -20000)
            throw new Exception($"Live Captions could not be hidden again in cycle {cycle}.");
    }
    if (Process.GetProcessesByName("LiveCaptions").Length != 0)
        throw new Exception($"Live Captions did not exit in cycle {cycle}.");
    Console.WriteLine($"PASS: Live Captions start/stop cycle {cycle}");
}

static class Native
{
    public static IntPtr FindCaptionWindow()
    {
        var ids = Process.GetProcessesByName("LiveCaptions").Select(process => (uint)process.Id).ToHashSet();
        IntPtr found = IntPtr.Zero;
        EnumWindows((handle, _) =>
        {
            GetWindowThreadProcessId(handle, out var id);
            var name = new StringBuilder(128);
            GetClassName(handle, name, name.Capacity);
            if (ids.Contains(id) && name.ToString() == "LiveCaptionsDesktopWindow")
            {
                found = handle;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private delegate bool WindowVisitor(IntPtr handle, IntPtr data);
    [DllImport("user32.dll")]
    private static extern bool EnumWindows(WindowVisitor visitor, IntPtr data);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr handle, StringBuilder className, int capacity);
    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr handle, out WindowRect rect);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static extern nint GetWindowLongPtr(IntPtr handle, int index);
    [StructLayout(LayoutKind.Sequential)]
    public struct WindowRect { public int Left, Top, Right, Bottom; }
}
