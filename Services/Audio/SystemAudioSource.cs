using System.Text.Json.Serialization;

namespace HelpMe.Services.Audio;

// Which route panel 01 takes to hear what the Mac is playing.
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SystemAudioSource
{
    // A virtual output device that playback is routed through, which then
    // appears as an input. Needs a one-time setup in Audio MIDI Setup and no
    // permission of any kind afterwards, so it is the default.
    BlackHole,
    // Apple's own system-audio tap. Nothing to install, but it counts as screen
    // recording and the permission has to be kept alive.
    ScreenCaptureKit
}
