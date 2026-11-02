namespace HelpMe.Services.Audio;

// Where the sound in a panel comes from.
//
// On Windows this was NAudio: WasapiCapture for the microphone and
// WasapiLoopbackCapture for everything the PC was playing. macOS has the first
// but not the second, so the two implementations behind this interface are an
// ordinary input device (the microphone, and BlackHole when playback has been
// routed into it) and ScreenCaptureKit, which taps the system mix directly.
public interface IAudioCapture : IDisposable
{
    // Capture always arrives as 32-bit float samples. Channels are interleaved.
    int SampleRate { get; }
    int Channels { get; }

    // The buffer is reused between calls, so a handler that keeps the samples
    // must copy them. Count is the number of valid floats, not frames.
    event Action<float[], int>? DataAvailable;

    // Null when capture was stopped deliberately, otherwise why it ended: an
    // unplugged device, or a permission withdrawn while it was running.
    event Action<Exception?>? Stopped;

    void Start();
    void Stop();
}

// What a source needs to say about itself before it is chosen, so Settings can
// explain a missing device instead of failing when capture starts.
public interface IAudioSourceCheck
{
    // Null when the source is ready, otherwise what the user has to do first.
    string? Unavailable();
}
