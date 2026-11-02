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

    // What panel 01 ended up listening to. The source asked for is not always
    // the one that can be used, and the window says which it is rather than
    // leaving the difference invisible.
    public sealed record SystemAudio(IAudioCapture Capture, SystemAudioSource Source, bool Substituted);

    public static IAudioCapture CreateMicrophone()
    {
        RequireMac();
        // Null means whatever macOS currently calls the default input, which is
        // what someone changing microphones in System Settings expects.
        return new AudioQueueCapture(null, "the microphone");
    }

    public static SystemAudio CreateSystemAudio(AppSettings settings)
    {
        RequireMac();
        if (settings.SystemAudioSource == SystemAudioSource.ScreenCaptureKit)
            return new SystemAudio(CreateScreenCapture(), SystemAudioSource.ScreenCaptureKit, false);

        var device = CoreAudio.FindByName(BlackHoleName);
        if (device is not null)
            return new SystemAudio(new AudioQueueCapture(device.Uid, device.Name), SystemAudioSource.BlackHole, false);

        // BlackHole is a separate install with an installer of its own, so a
        // Mac that has only ever run the Help Me! bundle will not have it.
        // ScreenCaptureKit needs nothing installed, so rather than refuse to
        // caption at all, use it and say so on the status line.
        var fallback = new ScreenCaptureAudio();
        if (fallback.Unavailable() is not null)
        {
            fallback.Dispose();
            throw new IOException(BlackHoleSetup);
        }
        return new SystemAudio(fallback, SystemAudioSource.ScreenCaptureKit, true);
    }

    private static IAudioCapture CreateScreenCapture()
    {
        var capture = new ScreenCaptureAudio();
        if (capture.Unavailable() is { } problem)
        {
            capture.Dispose();
            throw new IOException(problem);
        }
        return capture;
    }

    // The status line names the route actually in use, so a substituted source
    // is visible without opening Settings.
    public static string Describe(SystemAudioSource source, bool substituted = false)
    {
        if (substituted) return "ScreenCaptureKit (BlackHole not installed)  •  Offline captions";
        return source == SystemAudioSource.ScreenCaptureKit
            ? "ScreenCaptureKit  •  Offline captions"
            : "BlackHole  •  Offline captions";
    }

    public static string Describe(AppSettings settings) => Describe(settings.SystemAudioSource);

    private static void RequireMac()
    {
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException(
                "Audio capture in this build is macOS-only. The window and the panels still work for looking at the layout.");
    }
}
