using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia.Input;
using Avalonia.Threading;

namespace HelpMe.Services.Mac;

// The system-wide shortcut that brings Help Me! back from the menu bar.
//
// Windows had RegisterHotKey. The macOS counterpart is Carbon's
// RegisterEventHotKey, which is still the supported way to claim a shortcut
// that works while another application is in front. It is also the one that
// needs no Accessibility permission: an event tap would see every keystroke the
// user types and macOS asks accordingly, while a hot key only ever reports its
// own combination.
public static unsafe class MacHotkey
{
    private const string Carbon = "/System/Library/Frameworks/Carbon.framework/Carbon";

    [StructLayout(LayoutKind.Sequential)]
    private struct EventTypeSpec
    {
        public uint EventClass;
        public uint EventKind;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HotKeyId
    {
        public uint Signature;
        public uint Id;
    }

    [DllImport(Carbon)]
    private static extern IntPtr GetApplicationEventTarget();
    [DllImport(Carbon)]
    private static extern int InstallEventHandler(IntPtr target, IntPtr handler, uint count,
        ref EventTypeSpec types, IntPtr userData, out IntPtr handlerRef);
    [DllImport(Carbon)]
    private static extern int RegisterEventHotKey(uint keyCode, uint modifiers, HotKeyId id,
        IntPtr target, uint options, out IntPtr hotKeyRef);
    [DllImport(Carbon)]
    private static extern int UnregisterEventHotKey(IntPtr hotKeyRef);

    private const uint KeyboardEventClass = 0x6B657962; // 'keyb'
    private const uint HotKeyPressed = 5;
    private const uint Signature = 0x484D4B59;          // 'HMKY'

    // Carbon's own modifier masks, which are not the ones AppKit uses.
    private const uint CmdKey = 0x0100;
    private const uint ShiftKey = 0x0200;
    private const uint OptionKey = 0x0800;
    private const uint ControlKey = 0x1000;

    private static IntPtr handler;
    private static IntPtr registration;
    private static Action? pressed;

    // Registers the shortcut, replacing any previous one. False means macOS
    // refused it, which in practice means another application already owns it.
    public static bool Register(string shortcut, Action onPressed)
    {
        if (!OperatingSystem.IsMacOS()) return false;
        if (!Shortcut.TryParse(shortcut, out var parsed)) return false;
        // A single modifier would take the combination away from every other
        // app for something as ordinary as Cmd+H.
        if (parsed.ModifierCount < 2) return false;
        if (!TryVirtualKey(parsed.Key, out var keyCode)) return false;

        Unregister();
        pressed = onPressed;

        if (handler == IntPtr.Zero)
        {
            var type = new EventTypeSpec { EventClass = KeyboardEventClass, EventKind = HotKeyPressed };
            var callback = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, int>)&OnHotKey;
            if (InstallEventHandler(GetApplicationEventTarget(), callback, 1, ref type, IntPtr.Zero, out handler) != 0)
            {
                handler = IntPtr.Zero;
                return false;
            }
        }

        var id = new HotKeyId { Signature = Signature, Id = 1 };
        var status = RegisterEventHotKey(keyCode, CarbonModifiers(parsed.Modifiers), id,
            GetApplicationEventTarget(), 0, out registration);
        if (status != 0)
        {
            registration = IntPtr.Zero;
            return false;
        }
        return true;
    }

    public static void Unregister()
    {
        if (registration == IntPtr.Zero) return;
        UnregisterEventHotKey(registration);
        registration = IntPtr.Zero;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int OnHotKey(IntPtr callRef, IntPtr eventRef, IntPtr userData)
    {
        // Carbon delivers this on the main thread, but the window work still
        // goes through the dispatcher so it is ordered with everything else.
        try
        {
            var action = pressed;
            if (action is not null) Dispatcher.UIThread.Post(action);
        }
        catch (Exception) { }
        return 0; // noErr: the event is ours and goes no further.
    }

    private static uint CarbonModifiers(KeyModifiers modifiers)
    {
        uint result = 0;
        if (modifiers.HasFlag(KeyModifiers.Meta)) result |= CmdKey;
        if (modifiers.HasFlag(KeyModifiers.Shift)) result |= ShiftKey;
        if (modifiers.HasFlag(KeyModifiers.Alt)) result |= OptionKey;
        if (modifiers.HasFlag(KeyModifiers.Control)) result |= ControlKey;
        return result;
    }

    // Carbon wants the hardware key position, not the character on it. These
    // are the ANSI positions; a Dvorak or non-US layout types a different
    // letter at the same place, which is how every Mac shortcut behaves.
    private static bool TryVirtualKey(Key key, out uint code)
    {
        code = key switch
        {
            Key.A => 0, Key.S => 1, Key.D => 2, Key.F => 3, Key.H => 4, Key.G => 5,
            Key.Z => 6, Key.X => 7, Key.C => 8, Key.V => 9, Key.B => 11, Key.Q => 12,
            Key.W => 13, Key.E => 14, Key.R => 15, Key.Y => 16, Key.T => 17,
            Key.D1 => 18, Key.D2 => 19, Key.D3 => 20, Key.D4 => 21, Key.D6 => 22,
            Key.D5 => 23, Key.D9 => 25, Key.D7 => 26, Key.D8 => 28, Key.D0 => 29,
            Key.O => 31, Key.U => 32, Key.I => 34, Key.P => 35, Key.L => 37,
            Key.J => 38, Key.K => 40, Key.N => 45, Key.M => 46,
            Key.Space => 49,
            Key.F1 => 122, Key.F2 => 120, Key.F3 => 99, Key.F4 => 118, Key.F5 => 96,
            Key.F6 => 97, Key.F7 => 98, Key.F8 => 100, Key.F9 => 101, Key.F10 => 109,
            Key.F11 => 103, Key.F12 => 111,
            _ => uint.MaxValue
        };
        return code != uint.MaxValue;
    }
}
