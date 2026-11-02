using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using HelpMe.Services.Audio;

namespace HelpMe.Services.Mac;

// System audio through ScreenCaptureKit: the other way to hear what the Mac is
// playing, and the one that needs no driver installed.
//
// It is the alternative rather than the default. ScreenCaptureKit audio is part
// of screen recording as far as macOS is concerned, so it asks for Screen
// Recording permission, and Sequoia asks again periodically; when that prompt is
// declined or lapses, capture stops. BlackHole has no such lapse, which is why
// Settings offers both and starts on BlackHole.
//
// Everything here is Objective-C held at arm's length: SCStream delivers audio
// to a delegate object, so a class implementing SCStreamOutput is built at
// runtime and its one method points back into managed code.
public sealed unsafe class ScreenCaptureAudio : IAudioCapture, IAudioSourceCheck
{
    private const string CoreMedia = "/System/Library/Frameworks/CoreMedia.framework/CoreMedia";
    private const string System = "/usr/lib/libSystem.B.dylib";

    [DllImport(CoreMedia)]
    private static extern int CMSampleBufferGetAudioBufferListWithRetainedBlockBuffer(
        IntPtr sampleBuffer, out nuint sizeNeeded, IntPtr bufferList, nuint bufferListSize,
        IntPtr structureAllocator, IntPtr blockAllocator, uint flags, out IntPtr blockBuffer);

    [DllImport(CoreMedia)]
    private static extern nint CMSampleBufferGetNumSamples(IntPtr sampleBuffer);

    [DllImport(System)]
    private static extern IntPtr dispatch_queue_create(string label, IntPtr attributes);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern void CFRelease(IntPtr handle);

    // The sample rate SCStream is asked for. 48 kHz is its native rate; asking
    // for 16 kHz directly is not reliable across releases, so the conversion is
    // done here where it can be checked.
    private const int StreamRate = 48000;
    private const int StreamChannels = 2;
    private const int AudioOutputType = 1; // SCStreamOutputTypeAudio
    private const uint Assure16ByteAlignment = 1;

    private static readonly ConcurrentDictionary<IntPtr, ScreenCaptureAudio> Instances = new();
    private static readonly Lazy<IntPtr> OutputClass = new(RegisterOutputClass, LazyThreadSafetyMode.ExecutionAndPublication);

    private IntPtr stream;
    private IntPtr output;
    private IntPtr queue;
    private bool running;
    private bool disposed;
    private readonly object sync = new();

    public int SampleRate => StreamRate;
    public int Channels => StreamChannels;
    public event Action<float[], int>? DataAvailable;
    public event Action<Exception?>? Stopped;

    public string? Unavailable()
    {
        if (!OperatingSystem.IsMacOS()) return "System audio capture needs macOS.";
        // The framework arrived in Ventura. The Mac mini this was written for
        // runs Sequoia, but a copy of the app on an older machine should say so
        // rather than fail inside a native call.
        if (!OperatingSystem.IsMacOSVersionAtLeast(13))
            return "Capturing system audio without BlackHole needs macOS 13 Ventura or newer.";
        return ObjC.Class("SCStream") == IntPtr.Zero
            ? "ScreenCaptureKit is unavailable on this Mac. Choose BlackHole in Settings."
            : null;
    }

    public void Start()
    {
        lock (sync) { if (running || disposed) return; }
        if (Unavailable() is { } problem) throw new IOException(problem);

        // Everything below is built into locals and only published at the end.
        // Asking for shareable content is where the Screen Recording permission
        // is decided, and someone can take half a minute to answer that prompt;
        // holding the lock across it would freeze Stop and Dispose behind it.
        var content = GetShareableContent(TimeSpan.FromSeconds(30));
        var displays = ObjC.Send(content, ObjC.Sel("displays"));
        if (displays == IntPtr.Zero || ObjC.SendInt(displays, ObjC.Sel("count")) == 0)
            throw new IOException("ScreenCaptureKit reported no displays to capture audio from.");
        var display = ObjC.Send(displays, ObjC.Sel("firstObject"));

        var empty = ObjC.Send(ObjC.Class("NSArray"), ObjC.Sel("array"));
        var filter = ObjC.Send(ObjC.Alloc("SCContentFilter"),
            ObjC.Sel("initWithDisplay:excludingWindows:"), display, empty);

        var configuration = ObjC.New("SCStreamConfiguration");
        ObjC.SendVoid(configuration, ObjC.Sel("setCapturesAudio:"), true);
        ObjC.SendVoid(configuration, ObjC.Sel("setSampleRate:"), (nuint)StreamRate);
        ObjC.SendVoid(configuration, ObjC.Sel("setChannelCount:"), (nuint)StreamChannels);
        // Help Me! must not hear itself: it has no sound of its own today, but a
        // notification sound played by the app would otherwise be transcribed as
        // if someone had said it.
        ObjC.SendVoid(configuration, ObjC.Sel("setExcludesCurrentProcessAudio:"), true);
        // A stream must carry video as well as audio; the smallest frame
        // accepted keeps that side as close to free as it can be.
        ObjC.SendVoid(configuration, ObjC.Sel("setWidth:"), (nuint)2);
        ObjC.SendVoid(configuration, ObjC.Sel("setHeight:"), (nuint)2);

        var newOutput = ObjC.Send(ObjC.Send(OutputClass.Value, ObjC.Sel("alloc")), ObjC.Sel("init"));
        Instances[newOutput] = this;
        var newStream = IntPtr.Zero;
        try
        {
            newStream = ObjC.Send(ObjC.Alloc("SCStream"),
                ObjC.Sel("initWithFilter:configuration:delegate:"), filter, configuration, IntPtr.Zero);
            if (newStream == IntPtr.Zero) throw new IOException("ScreenCaptureKit refused to create the audio stream.");

            var newQueue = dispatch_queue_create("com.helpme.screencapture.audio", IntPtr.Zero);
            var error = IntPtr.Zero;
            if (!AddStreamOutput(newStream, newOutput, AudioOutputType, newQueue, ref error))
                throw new IOException("ScreenCaptureKit refused the audio output. " + ObjC.ErrorMessage(error));

            StartCapture(newStream, TimeSpan.FromSeconds(20));

            lock (sync)
            {
                if (disposed)
                {
                    // Disposed while the permission prompt was open: stop the
                    // stream that was just started rather than leaving it running.
                    ObjC.Send(newStream, ObjC.Sel("stopCaptureWithCompletionHandler:"),
                        ObjCBlock.CreateErrorHandler(_ => { }));
                    throw new ObjectDisposedException(nameof(ScreenCaptureAudio));
                }
                stream = newStream;
                output = newOutput;
                queue = newQueue;
                running = true;
            }
        }
        catch
        {
            Instances.TryRemove(newOutput, out _);
            ObjC.Release(newStream);
            ObjC.Release(newOutput);
            throw;
        }
        finally
        {
            ObjC.Release(filter);
            ObjC.Release(configuration);
            ObjC.Release(content);
        }
    }

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern bool SendAddOutput(IntPtr self, IntPtr op, IntPtr output, nint type, IntPtr queue, ref IntPtr error);

    private static bool AddStreamOutput(IntPtr stream, IntPtr output, int type, IntPtr queue, ref IntPtr error)
        => SendAddOutput(stream, ObjC.Sel("addStreamOutput:type:sampleHandlerQueue:error:"), output, type, queue, ref error);

    private static IntPtr GetShareableContent(TimeSpan timeout)
    {
        var completion = new TaskCompletionSource<IntPtr>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = ObjCBlock.CreateValueErrorHandler((content, error) =>
        {
            if (error != IntPtr.Zero || content == IntPtr.Zero)
            {
                completion.TrySetException(new IOException(
                    "macOS did not allow Help Me! to capture system audio. Give it Screen Recording permission in " +
                    "System Settings > Privacy & Security, or choose BlackHole in Settings. " + ObjC.ErrorMessage(error)));
                return;
            }
            completion.TrySetResult(ObjC.Retain(content));
        });
        ObjC.Send(ObjC.Class("SCShareableContent"), ObjC.Sel("getShareableContentWithCompletionHandler:"), handler);
        if (!completion.Task.Wait(timeout))
            throw new IOException("ScreenCaptureKit did not answer in time. Choose BlackHole in Settings if this continues.");
        return completion.Task.GetAwaiter().GetResult();
    }

    private static void StartCapture(IntPtr stream, TimeSpan timeout)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = ObjCBlock.CreateErrorHandler(error =>
        {
            if (error != IntPtr.Zero)
                completion.TrySetException(new IOException("ScreenCaptureKit could not start: " + ObjC.ErrorMessage(error)));
            else completion.TrySetResult(true);
        });
        ObjC.Send(stream, ObjC.Sel("startCaptureWithCompletionHandler:"), handler);
        if (!completion.Task.Wait(timeout))
            throw new IOException("ScreenCaptureKit did not start in time.");
        completion.Task.GetAwaiter().GetResult();
    }

    // The SCStreamOutput implementation, built once and reused. A class pair
    // cannot be registered twice under the same name, and the instance the
    // callback belongs to is found from the object pointer macOS passes back.
    private static IntPtr RegisterOutputClass()
    {
        var cls = ObjC.objc_allocateClassPair(ObjC.Class("NSObject"), "HelpMeScreenCaptureOutput", 0);
        if (cls == IntPtr.Zero) throw new InvalidOperationException("Could not create the capture delegate class.");
        var implementation = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, nint, void>)&OnSampleBuffer;
        ObjC.class_addMethod(cls, ObjC.Sel("stream:didOutputSampleBuffer:ofType:"), implementation, "v@:@^vq");
        var protocol = ObjC.objc_getProtocol("SCStreamOutput");
        if (protocol != IntPtr.Zero) ObjC.class_addProtocol(cls, protocol);
        ObjC.objc_registerClassPair(cls);
        return cls;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnSampleBuffer(IntPtr self, IntPtr selector, IntPtr stream, IntPtr sampleBuffer, nint type)
    {
        try
        {
            if (type != AudioOutputType) return;
            if (!Instances.TryGetValue(self, out var capture)) return;
            capture.Deliver(sampleBuffer);
        }
        // Never let a managed fault unwind into the capture queue.
        catch (Exception) { }
    }

    private void Deliver(IntPtr sampleBuffer)
    {
        var frames = (int)CMSampleBufferGetNumSamples(sampleBuffer);
        if (frames <= 0) return;

        // Room for a list of up to eight planes; system audio is stereo, but a
        // fixed upper bound avoids a second call just to learn the size.
        const int maxBuffers = 8;
        var listSize = sizeof(uint) + maxBuffers * (sizeof(uint) + sizeof(uint) + IntPtr.Size);
        var list = Marshal.AllocHGlobal(listSize);
        var blockBuffer = IntPtr.Zero;
        try
        {
            var status = CMSampleBufferGetAudioBufferListWithRetainedBlockBuffer(
                sampleBuffer, out _, list, (nuint)listSize, IntPtr.Zero, IntPtr.Zero,
                Assure16ByteAlignment, out blockBuffer);
            if (status != 0) return;

            var planes = Marshal.ReadInt32(list);
            if (planes <= 0) return;
            var cursor = IntPtr.Add(list, IntPtr.Size);
            var interleaved = new float[frames * StreamChannels];

            for (var plane = 0; plane < planes && plane < maxBuffers; plane++)
            {
                var channels = Marshal.ReadInt32(cursor);
                var byteSize = (uint)Marshal.ReadInt32(cursor, sizeof(uint));
                var data = Marshal.ReadIntPtr(cursor, sizeof(uint) + sizeof(uint));
                cursor = IntPtr.Add(cursor, sizeof(uint) + sizeof(uint) + IntPtr.Size);
                if (data == IntPtr.Zero || byteSize == 0) continue;
                var available = (int)(byteSize / sizeof(float));

                if (channels > 1)
                {
                    // Already interleaved: one buffer carrying every channel.
                    var take = Math.Min(available, interleaved.Length);
                    Marshal.Copy(data, interleaved, 0, take);
                    break;
                }
                // Planar: one buffer per channel, which is how SCStream
                // normally delivers, so the planes are woven back together.
                var samples = new float[available];
                Marshal.Copy(data, samples, 0, available);
                for (var frame = 0; frame < frames && frame < available; frame++)
                {
                    var index = frame * StreamChannels + Math.Min(plane, StreamChannels - 1);
                    if (index < interleaved.Length) interleaved[index] = samples[frame];
                }
            }
            DataAvailable?.Invoke(interleaved, interleaved.Length);
        }
        finally
        {
            if (blockBuffer != IntPtr.Zero) CFRelease(blockBuffer);
            Marshal.FreeHGlobal(list);
        }
    }

    public void Stop()
    {
        lock (sync)
        {
            if (!running) return;
            running = false;
            if (stream != IntPtr.Zero)
            {
                var handler = ObjCBlock.CreateErrorHandler(_ => { });
                ObjC.Send(stream, ObjC.Sel("stopCaptureWithCompletionHandler:"), handler);
            }
        }
        Stopped?.Invoke(null);
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            if (running)
            {
                running = false;
                if (stream != IntPtr.Zero)
                {
                    var handler = ObjCBlock.CreateErrorHandler(_ => { });
                    ObjC.Send(stream, ObjC.Sel("stopCaptureWithCompletionHandler:"), handler);
                }
            }
            if (output != IntPtr.Zero) Instances.TryRemove(output, out _);
            ObjC.Release(stream);
            ObjC.Release(output);
            stream = IntPtr.Zero;
            output = IntPtr.Zero;
            queue = IntPtr.Zero;
        }
    }
}
