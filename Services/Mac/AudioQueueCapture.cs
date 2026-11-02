using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using HelpMe.Services.Audio;

namespace HelpMe.Services.Mac;

// Capture from an ordinary macOS input device, through AudioToolbox's audio
// queues.
//
// Both macOS sources that are not ScreenCaptureKit come through here: the
// microphone for panel 03, and BlackHole for panel 01 once playback has been
// routed into it. A virtual device is an input like any other, so one code path
// serves both.
//
// The queue converts the device's own format to what is asked for, so 16 kHz
// mono float is requested directly and Whisper is fed without a resampling step
// on our side. A device that refuses that conversion falls back to 48 kHz and
// AudioMath does the work instead.
public sealed unsafe class AudioQueueCapture : IAudioCapture, IAudioSourceCheck
{
    private const string Framework = "/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox";

    [StructLayout(LayoutKind.Sequential)]
    private struct StreamDescription
    {
        public double SampleRate;
        public uint FormatId;
        public uint FormatFlags;
        public uint BytesPerPacket;
        public uint FramesPerPacket;
        public uint BytesPerFrame;
        public uint ChannelsPerFrame;
        public uint BitsPerChannel;
        public uint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct QueueBuffer
    {
        public uint AudioDataBytesCapacity;
        public IntPtr AudioData;
        public uint AudioDataByteSize;
        public IntPtr UserData;
        public uint PacketDescriptionCapacity;
        public IntPtr PacketDescriptions;
        public uint PacketDescriptionCount;
    }

    [DllImport(Framework)]
    private static extern int AudioQueueNewInput(ref StreamDescription format, IntPtr callback, IntPtr userData,
        IntPtr runLoop, IntPtr runLoopMode, uint flags, out IntPtr queue);
    [DllImport(Framework)]
    private static extern int AudioQueueAllocateBuffer(IntPtr queue, uint byteSize, out IntPtr buffer);
    [DllImport(Framework)]
    private static extern int AudioQueueEnqueueBuffer(IntPtr queue, IntPtr buffer, uint packetCount, IntPtr packets);
    [DllImport(Framework)]
    private static extern int AudioQueueStart(IntPtr queue, IntPtr startTime);
    [DllImport(Framework)]
    private static extern int AudioQueueStop(IntPtr queue, bool immediate);
    [DllImport(Framework)]
    private static extern int AudioQueueDispose(IntPtr queue, bool immediate);
    [DllImport(Framework)]
    private static extern int AudioQueueSetProperty(IntPtr queue, uint propertyId, IntPtr data, uint dataSize);

    private const uint LinearPcm = 0x6C70636D;      // 'lpcm'
    private const uint FlagIsFloat = 1;
    private const uint FlagIsPacked = 8;
    private const uint CurrentDevice = 0x61716364;  // 'aqcd'
    private const int PreferredRate = 16000;
    private const int FallbackRate = 48000;
    private const int BufferCount = 3;

    private readonly string? deviceUid;
    private readonly string description;
    private IntPtr queue;
    private GCHandle self;
    private readonly IntPtr[] buffers = new IntPtr[BufferCount];
    private bool running;
    private bool disposed;
    private readonly object sync = new();

    public int SampleRate { get; private set; } = PreferredRate;
    public int Channels => 1;
    public event Action<float[], int>? DataAvailable;
    public event Action<Exception?>? Stopped;

    // A null device UID means "whatever macOS calls the default input", which
    // is what the microphone panel wants. Panel 01 names its device instead.
    public AudioQueueCapture(string? deviceUid, string description)
    {
        this.deviceUid = deviceUid;
        this.description = description;
    }

    public string? Unavailable()
    {
        if (!OperatingSystem.IsMacOS()) return "Audio capture needs macOS.";
        if (deviceUid is null) return null;
        return CoreAudio.InputDevices().Any(device => device.Uid == deviceUid)
            ? null
            : $"{description} is not available. Check it in Audio MIDI Setup and try again.";
    }

    public void Start()
    {
        lock (sync)
        {
            if (running || disposed) return;
            if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("Audio capture needs macOS.");
            if (Unavailable() is { } problem) throw new IOException(problem);

            self = GCHandle.Alloc(this);
            var callback = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, uint, IntPtr, void>)&OnInput;
            var status = Open(PreferredRate, callback);
            if (status != 0)
            {
                // Some interfaces refuse a conversion that far from their own
                // rate. 48 kHz is universally accepted; AudioMath takes it down.
                status = Open(FallbackRate, callback);
                if (status != 0)
                {
                    self.Free();
                    throw new IOException($"Could not open {description} for capture (CoreAudio status {status}). " +
                        "Check Privacy & Security > Microphone for Help Me!.");
                }
                SampleRate = FallbackRate;
            }
            else SampleRate = PreferredRate;

            if (deviceUid is not null)
            {
                var uid = ObjC.NSString(deviceUid);
                var holder = Marshal.AllocHGlobal(IntPtr.Size);
                try
                {
                    Marshal.WriteIntPtr(holder, uid);
                    var set = AudioQueueSetProperty(queue, CurrentDevice, holder, (uint)IntPtr.Size);
                    if (set != 0)
                    {
                        Close();
                        throw new IOException($"Could not select {description} for capture (CoreAudio status {set}).");
                    }
                }
                finally { Marshal.FreeHGlobal(holder); }
            }

            // A tenth of a second per buffer: short enough that the speech
            // detector reacts quickly, long enough not to wake the process
            // constantly.
            var bufferBytes = (uint)(SampleRate / 10 * sizeof(float));
            for (var i = 0; i < BufferCount; i++)
            {
                if (AudioQueueAllocateBuffer(queue, bufferBytes, out buffers[i]) != 0 ||
                    AudioQueueEnqueueBuffer(queue, buffers[i], 0, IntPtr.Zero) != 0)
                {
                    Close();
                    throw new IOException($"Could not prepare capture buffers for {description}.");
                }
            }

            var started = AudioQueueStart(queue, IntPtr.Zero);
            if (started != 0)
            {
                Close();
                throw new IOException($"Could not start capturing {description} (CoreAudio status {started}). " +
                    "Check Privacy & Security > Microphone for Help Me!.");
            }
            running = true;
        }
    }

    private int Open(int rate, IntPtr callback)
    {
        var format = new StreamDescription
        {
            SampleRate = rate,
            FormatId = LinearPcm,
            FormatFlags = FlagIsFloat | FlagIsPacked,
            BytesPerPacket = sizeof(float),
            FramesPerPacket = 1,
            BytesPerFrame = sizeof(float),
            ChannelsPerFrame = 1,
            BitsPerChannel = 32,
            Reserved = 0
        };
        return AudioQueueNewInput(ref format, callback, GCHandle.ToIntPtr(self),
            IntPtr.Zero, IntPtr.Zero, 0, out queue);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnInput(IntPtr userData, IntPtr audioQueue, IntPtr buffer,
        IntPtr startTime, uint packetCount, IntPtr packets)
    {
        // This runs on a CoreAudio thread. Nothing here may throw into the
        // native caller, and the work is kept to a copy and one callback.
        try
        {
            if (userData == IntPtr.Zero) return;
            if (GCHandle.FromIntPtr(userData).Target is not AudioQueueCapture capture) return;
            capture.Deliver(buffer);
        }
        catch (Exception) { }
    }

    private void Deliver(IntPtr bufferHandle)
    {
        var buffer = Marshal.PtrToStructure<QueueBuffer>(bufferHandle);
        var count = (int)(buffer.AudioDataByteSize / sizeof(float));
        if (count > 0 && buffer.AudioData != IntPtr.Zero)
        {
            var samples = new float[count];
            Marshal.Copy(buffer.AudioData, samples, 0, count);
            DataAvailable?.Invoke(samples, count);
        }
        lock (sync)
        {
            if (running && queue != IntPtr.Zero) AudioQueueEnqueueBuffer(queue, bufferHandle, 0, IntPtr.Zero);
        }
    }

    public void Stop()
    {
        lock (sync)
        {
            if (!running) return;
            running = false;
            if (queue != IntPtr.Zero) AudioQueueStop(queue, true);
        }
        Stopped?.Invoke(null);
    }

    private void Close()
    {
        if (queue != IntPtr.Zero)
        {
            AudioQueueDispose(queue, true);
            queue = IntPtr.Zero;
        }
        if (self.IsAllocated) self.Free();
        Array.Clear(buffers);
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
                if (queue != IntPtr.Zero) AudioQueueStop(queue, true);
            }
            Close();
        }
    }
}
