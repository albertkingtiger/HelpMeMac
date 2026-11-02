using HelpMe.Services.Mac;

namespace HelpMe.Services.Audio;

// Builds the capture for each panel. Everything platform-specific about picking
// a source is decided here, so AudioSession never learns which Mac API it is
// reading from.
public static class CaptureFactory
{
    public const string BlackHoleName = "BlackHole";

    public static string BlackHoleSetup =>
        "BlackHole is not installed. Install it with `brew install blackhole-2ch`, then open Audio MIDI Setup, " +
        "create a Multi-Output Device containing your speakers and BlackHole 2ch, and select that as the system " +
        "output. Or choose ScreenCaptureKit as the PC audio source in Settings.";

    public static IAudioCapture CreateMicrophone()
    {
        RequireMac();
        // Null means whatever macOS currently calls the default input, which is
        // what someone changing microphones in System Settings expects.
        return new AudioQueueCapture(null, "the microphone");
    }

    public static IAudioCapture CreateSystemAudio(AppSettings settings)
    {
        RequireMac();
        if (settings.SystemAudioSource == SystemAudioSource.ScreenCaptureKit)
        {
            var capture = new ScreenCaptureAudio();
            if (capture.Unavailable() is { } problem)
            {
                capture.Dispose();
                throw new IOException(problem);
            }
            return capture;
        }
        var device = CoreAudio.FindByName(BlackHoleName)
            ?? throw new IOException(BlackHoleSetup);
        return new AudioQueueCapture(device.Uid, device.Name);
    }

    // A description of the current PC audio route for the status line, so the
    // window says which of the two is in use without opening Settings.
    public static string Describe(AppSettings settings)
        => settings.SystemAudioSource == SystemAudioSource.ScreenCaptureKit
            ? "ScreenCaptureKit  •  Offline captions"
            : "BlackHole  •  Offline captions";

    private static void RequireMac()
    {
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException(
                "Audio capture in this build is macOS-only. The window and the panels still work for looking at the layout.");
    }
}
