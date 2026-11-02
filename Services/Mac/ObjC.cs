using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace HelpMe.Services.Mac;

// The Objective-C runtime, reached directly rather than through a binding
// library. Everything macOS-native in Help Me! (the web panels, the audio
// capture and the global shortcut) is built on these few entry points, so they
// are kept deliberately small and explicit.
//
// Only the message signatures actually used are declared. objc_msgSend is
// variadic in C and must be called through a declaration matching the real
// argument types, so adding a new call means adding its signature here rather
// than reusing one that nearly fits.
internal static unsafe class ObjC
{
    private const string Runtime = "/usr/lib/libobjc.A.dylib";

    [DllImport(Runtime)] private static extern IntPtr objc_getClass(string name);
    [DllImport(Runtime)] private static extern IntPtr sel_registerName(string name);
    [DllImport(Runtime)] internal static extern IntPtr objc_allocateClassPair(IntPtr super, string name, nuint extraBytes);
    [DllImport(Runtime)] internal static extern void objc_registerClassPair(IntPtr cls);
    [DllImport(Runtime)] internal static extern bool class_addMethod(IntPtr cls, IntPtr name, IntPtr imp, string types);
    [DllImport(Runtime)] internal static extern bool class_addProtocol(IntPtr cls, IntPtr protocol);
    [DllImport(Runtime)] internal static extern IntPtr objc_getProtocol(string name);

    [DllImport(Runtime, EntryPoint = "objc_msgSend")] internal static extern IntPtr Send(IntPtr self, IntPtr op);
    [DllImport(Runtime, EntryPoint = "objc_msgSend")] internal static extern IntPtr Send(IntPtr self, IntPtr op, IntPtr a);
    [DllImport(Runtime, EntryPoint = "objc_msgSend")] internal static extern IntPtr Send(IntPtr self, IntPtr op, IntPtr a, IntPtr b);
    [DllImport(Runtime, EntryPoint = "objc_msgSend")] internal static extern IntPtr Send(IntPtr self, IntPtr op, IntPtr a, IntPtr b, IntPtr c);
    [DllImport(Runtime, EntryPoint = "objc_msgSend")] internal static extern IntPtr Send(IntPtr self, IntPtr op, IntPtr a, IntPtr b, IntPtr c, IntPtr d);
    [DllImport(Runtime, EntryPoint = "objc_msgSend")] internal static extern IntPtr Send(IntPtr self, IntPtr op, IntPtr a, IntPtr b, IntPtr c, IntPtr d, IntPtr e);
    [DllImport(Runtime, EntryPoint = "objc_msgSend")] internal static extern IntPtr SendRect(IntPtr self, IntPtr op, CGRect a);
    [DllImport(Runtime, EntryPoint = "objc_msgSend")] internal static extern IntPtr SendRect(IntPtr self, IntPtr op, CGRect a, IntPtr b);
    [DllImport(Runtime, EntryPoint = "objc_msgSend")] internal static extern void SendVoid(IntPtr self, IntPtr op);
    [DllImport(Runtime, EntryPoint = "objc_msgSend")] internal static extern void SendVoid(IntPtr self, IntPtr op, IntPtr a);
    [DllImport(Runtime, EntryPoint = "objc_msgSend")] internal static extern void SendVoid(IntPtr self, IntPtr op, bool a);
    [DllImport(Runtime, EntryPoint = "objc_msgSend")] internal static extern void SendVoid(IntPtr self, IntPtr op, nuint a);
    [DllImport(Runtime, EntryPoint = "objc_msgSend")] internal static extern void SendVoid(IntPtr self, IntPtr op, CGRect a);
    [DllImport(Runtime, EntryPoint = "objc_msgSend")] internal static extern bool SendBool(IntPtr self, IntPtr op);
    [DllImport(Runtime, EntryPoint = "objc_msgSend")] internal static extern bool SendBool(IntPtr self, IntPtr op, IntPtr a);
    [DllImport(Runtime, EntryPoint = "objc_msgSend")] internal static extern nint SendInt(IntPtr self, IntPtr op);
    [DllImport(Runtime, EntryPoint = "objc_msgSend")] internal static extern uint SendUInt32(IntPtr self, IntPtr op);

    private static readonly Dictionary<string, IntPtr> classes = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, IntPtr> selectors = new(StringComparer.Ordinal);
    private static readonly object sync = new();

    internal static IntPtr Class(string name)
    {
        lock (sync)
        {
            if (classes.TryGetValue(name, out var found)) return found;
            var cls = objc_getClass(name);
            classes[name] = cls;
            return cls;
        }
    }

    internal static IntPtr Sel(string name)
    {
        lock (sync)
        {
            if (selectors.TryGetValue(name, out var found)) return found;
            var sel = sel_registerName(name);
            selectors[name] = sel;
            return sel;
        }
    }

    // alloc followed by init, the two messages that start almost every object.
    internal static IntPtr New(string className)
    {
        var allocated = Send(Class(className), Sel("alloc"));
        return Send(allocated, Sel("init"));
    }

    internal static IntPtr Alloc(string className) => Send(Class(className), Sel("alloc"));

    internal static void Release(IntPtr handle)
    {
        if (handle != IntPtr.Zero) SendVoid(handle, Sel("release"));
    }

    internal static IntPtr Retain(IntPtr handle)
        => handle == IntPtr.Zero ? IntPtr.Zero : Send(handle, Sel("retain"));

    // NSString bridging. Going through UTF-8 keeps every non-ASCII caption,
    // Korean translation and emoji intact in both directions.
    internal static IntPtr NSString(string? value)
    {
        value ??= "";
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        var buffer = Marshal.AllocHGlobal(bytes.Length + 1);
        try
        {
            Marshal.Copy(bytes, 0, buffer, bytes.Length);
            Marshal.WriteByte(buffer, bytes.Length, 0);
            return Send(Class("NSString"), Sel("stringWithUTF8String:"), buffer);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    internal static string FromNSString(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return "";
        var utf8 = Send(handle, Sel("UTF8String"));
        return utf8 == IntPtr.Zero ? "" : Marshal.PtrToStringUTF8(utf8) ?? "";
    }

    // An NSError's own description, so a failed native call becomes a message
    // the user can act on rather than an opaque pointer.
    internal static string ErrorMessage(IntPtr error)
    {
        if (error == IntPtr.Zero) return "";
        return FromNSString(Send(error, Sel("localizedDescription")));
    }

    // NSURL returns nil for a string it cannot parse, which the callers treat
    // as "nothing to navigate to" rather than as an error.
    internal static IntPtr NsUrlOrNull(string url) => Send(Class("NSURL"), Sel("URLWithString:"), NSString(url));

    [StructLayout(LayoutKind.Sequential)]
    internal struct CGRect
    {
        public double X, Y, Width, Height;
        public CGRect(double x, double y, double width, double height)
        { X = x; Y = y; Width = width; Height = height; }
    }
}

// A minimal Objective-C block: enough to hand a C# callback to an AppKit or
// WebKit method that takes a completion handler.
//
// The block is built by hand in unmanaged memory. A global block needs no copy
// or dispose helpers, and the extra field after the descriptor carries the
// handle of the managed callback; Objective-C passes the block itself as the
// first argument of its invoke function, which is how that handle is found
// again when the handler fires.
internal static unsafe class ObjCBlock
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Literal
    {
        public IntPtr Isa;
        public int Flags;
        public int Reserved;
        public IntPtr Invoke;
        public IntPtr Descriptor;
        public IntPtr Context;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Descriptor
    {
        public nuint Reserved;
        public nuint Size;
    }

    private const int BlockIsGlobal = 1 << 28;

    [DllImport("/usr/lib/libSystem.B.dylib")]
    private static extern IntPtr dlsym(IntPtr handle, string symbol);
    [DllImport("/usr/lib/libSystem.B.dylib")]
    private static extern IntPtr dlopen(string? path, int mode);

    private static readonly IntPtr GlobalBlockIsa = ResolveGlobalBlockIsa();

    private static IntPtr ResolveGlobalBlockIsa()
    {
        var self = dlopen(null, 2 /* RTLD_NOW */);
        var symbol = dlsym(self, "_NSConcreteGlobalBlock");
        if (symbol == IntPtr.Zero) throw new InvalidOperationException("The Objective-C block runtime is unavailable.");
        return symbol;
    }

    // A completion handler shaped like void (^)(id value, NSError *error),
    // which is what WebKit's script evaluation and most AppKit callbacks take.
    // Each call allocates its own block and frees it once the handler has run,
    // so nothing is shared between calls and nothing outlives its callback.
    internal static IntPtr CreateValueErrorHandler(Action<IntPtr, IntPtr> callback)
    {
        var handle = GCHandle.Alloc(callback);
        var size = sizeof(Literal);
        var block = (Literal*)Marshal.AllocHGlobal(size);
        var descriptor = (Descriptor*)Marshal.AllocHGlobal(sizeof(Descriptor));
        descriptor->Reserved = 0;
        descriptor->Size = (nuint)size;
        block->Isa = GlobalBlockIsa;
        block->Flags = BlockIsGlobal;
        block->Reserved = 0;
        block->Invoke = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&InvokeValueError;
        block->Descriptor = (IntPtr)descriptor;
        block->Context = GCHandle.ToIntPtr(handle);
        return (IntPtr)block;
    }

    // The other common shape, void (^)(NSError *error), used by anything that
    // reports only whether it succeeded.
    internal static IntPtr CreateErrorHandler(Action<IntPtr> callback)
    {
        var handle = GCHandle.Alloc(callback);
        var size = sizeof(Literal);
        var block = (Literal*)Marshal.AllocHGlobal(size);
        var descriptor = (Descriptor*)Marshal.AllocHGlobal(sizeof(Descriptor));
        descriptor->Reserved = 0;
        descriptor->Size = (nuint)size;
        block->Isa = GlobalBlockIsa;
        block->Flags = BlockIsGlobal;
        block->Reserved = 0;
        block->Invoke = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)&InvokeError;
        block->Descriptor = (IntPtr)descriptor;
        block->Context = GCHandle.ToIntPtr(handle);
        return (IntPtr)block;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void InvokeError(IntPtr block, IntPtr error)
    {
        var literal = (Literal*)block;
        var context = literal->Context;
        var descriptor = literal->Descriptor;
        Action<IntPtr>? callback = null;
        try
        {
            var handle = GCHandle.FromIntPtr(context);
            callback = handle.Target as Action<IntPtr>;
            handle.Free();
        }
        catch (InvalidOperationException) { }
        try { callback?.Invoke(error); }
        catch (Exception) { }
        finally
        {
            Marshal.FreeHGlobal(descriptor);
            Marshal.FreeHGlobal(block);
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void InvokeValueError(IntPtr block, IntPtr value, IntPtr error)
    {
        var literal = (Literal*)block;
        var context = literal->Context;
        var descriptor = literal->Descriptor;
        Action<IntPtr, IntPtr>? callback = null;
        try
        {
            var handle = GCHandle.FromIntPtr(context);
            callback = handle.Target as Action<IntPtr, IntPtr>;
            handle.Free();
        }
        catch (InvalidOperationException) { }
        // A managed exception must never unwind into Objective-C: the native
        // caller has no handler for it and the process would be torn down.
        try { callback?.Invoke(value, error); }
        catch (Exception) { }
        finally
        {
            Marshal.FreeHGlobal(descriptor);
            Marshal.FreeHGlobal(block);
        }
    }
}
