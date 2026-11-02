using System.Runtime.InteropServices;

namespace HelpMe.Services.Mac;

// The audio devices macOS knows about.
//
// This exists for one reason: on macOS nothing captures what the computer is
// playing unless a virtual device is in the path. BlackHole is that device, and
// it appears here as an ordinary input, so routing playback through it turns
// "what I am listening to" into something an input capture can read.
public static class CoreAudio
{
    private const string Framework = "/System/Library/Frameworks/CoreAudio.framework/CoreAudio";

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyAddress
    {
        public uint Selector;
        public uint Scope;
        public uint Element;
    }

    [DllImport(Framework)]
    private static extern int AudioObjectGetPropertyDataSize(uint objectId, ref PropertyAddress address,
        uint qualifierSize, IntPtr qualifier, out uint dataSize);

    [DllImport(Framework)]
    private static extern int AudioObjectGetPropertyData(uint objectId, ref PropertyAddress address,
        uint qualifierSize, IntPtr qualifier, ref uint dataSize, IntPtr data);

    private const uint SystemObject = 1;
    private const uint ScopeGlobal = 0x676C6F62;  // 'glob'
    private const uint ScopeInput = 0x696E7074;   // 'inpt'
    private const uint Devices = 0x64657623;      // 'dev#'
    private const uint DefaultInput = 0x64496E20; // 'dIn '
    private const uint DeviceUid = 0x75696420;    // 'uid '
    private const uint ObjectName = 0x6C6E616D;   // 'lnam'
    private const uint StreamConfiguration = 0x736C6179; // 'slay'

    public sealed record InputDevice(uint Id, string Uid, string Name);

    // Every device with at least one input channel. An aggregate or virtual
    // device such as BlackHole is indistinguishable from a sound card here,
    // which is exactly what makes the loopback route work.
    public static IReadOnlyList<InputDevice> InputDevices()
    {
        if (!OperatingSystem.IsMacOS()) return Array.Empty<InputDevice>();
        var address = new PropertyAddress { Selector = Devices, Scope = ScopeGlobal, Element = 0 };
        if (AudioObjectGetPropertyDataSize(SystemObject, ref address, 0, IntPtr.Zero, out var size) != 0 || size == 0)
            return Array.Empty<InputDevice>();
        var count = (int)(size / sizeof(uint));
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            var ioSize = size;
            if (AudioObjectGetPropertyData(SystemObject, ref address, 0, IntPtr.Zero, ref ioSize, buffer) != 0)
                return Array.Empty<InputDevice>();
            var result = new List<InputDevice>();
            for (var i = 0; i < count; i++)
            {
                var id = (uint)Marshal.ReadInt32(buffer, i * sizeof(uint));
                if (!HasInput(id)) continue;
                var uid = StringProperty(id, DeviceUid, ScopeGlobal);
                var name = StringProperty(id, ObjectName, ScopeGlobal);
                if (uid.Length == 0) continue;
                result.Add(new InputDevice(id, uid, name.Length > 0 ? name : uid));
            }
            return result;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    public static InputDevice? FindByName(string fragment)
        => InputDevices().FirstOrDefault(device =>
            device.Name.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    public static string? DefaultInputUid()
    {
        if (!OperatingSystem.IsMacOS()) return null;
        var address = new PropertyAddress { Selector = DefaultInput, Scope = ScopeGlobal, Element = 0 };
        uint size = sizeof(uint);
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (AudioObjectGetPropertyData(SystemObject, ref address, 0, IntPtr.Zero, ref size, buffer) != 0)
                return null;
            var id = (uint)Marshal.ReadInt32(buffer);
            var uid = StringProperty(id, DeviceUid, ScopeGlobal);
            return uid.Length > 0 ? uid : null;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static bool HasInput(uint deviceId)
    {
        var address = new PropertyAddress { Selector = StreamConfiguration, Scope = ScopeInput, Element = 0 };
        if (AudioObjectGetPropertyDataSize(deviceId, ref address, 0, IntPtr.Zero, out var size) != 0 || size == 0)
            return false;
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            var ioSize = size;
            if (AudioObjectGetPropertyData(deviceId, ref address, 0, IntPtr.Zero, ref ioSize, buffer) != 0)
                return false;
            // AudioBufferList: a count, then that many AudioBuffer records of
            // {channels, byteSize, data}. Any channel at all makes it an input.
            var buffers = Marshal.ReadInt32(buffer);
            var cursor = IntPtr.Add(buffer, IntPtr.Size);
            for (var i = 0; i < buffers; i++)
            {
                if (Marshal.ReadInt32(cursor) > 0) return true;
                cursor = IntPtr.Add(cursor, sizeof(uint) + sizeof(uint) + IntPtr.Size);
            }
            return false;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    // CFString is toll-free bridged to NSString, so the Objective-C helper can
    // read it without a second set of CoreFoundation declarations.
    private static string StringProperty(uint deviceId, uint selector, uint scope)
    {
        var address = new PropertyAddress { Selector = selector, Scope = scope, Element = 0 };
        uint size = (uint)IntPtr.Size;
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (AudioObjectGetPropertyData(deviceId, ref address, 0, IntPtr.Zero, ref size, buffer) != 0)
                return "";
            var handle = Marshal.ReadIntPtr(buffer);
            return handle == IntPtr.Zero ? "" : ObjC.FromNSString(handle);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
}
