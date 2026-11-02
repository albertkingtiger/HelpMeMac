#!/usr/bin/env bash
# Install BlackHole and set up the audio route Help Me! captures from.
#
# BlackHole is a virtual audio device: playback goes through it as well as to
# your speakers, and Help Me! then reads it as an ordinary input. It is the PC
# audio route that needs no permission and never lapses, which is why it is
# worth the one-time setup.
#
# It cannot be bundled inside HelpMe.app, because installing an audio device
# driver needs an installer and an administrator password of its own. This
# script is carried inside the app so it can be run without the source:
#
#   /Applications/HelpMe.app/Contents/Resources/install-blackhole.sh
set -euo pipefail

device="BlackHole"
cask="blackhole-2ch"
open_audio_setup=1

while [ $# -gt 0 ]; do
    case "$1" in
        --no-open) open_audio_setup=0 ;;
        -h|--help)
            echo "Usage: install-blackhole.sh [--no-open]"
            echo "  --no-open   do not open Audio MIDI Setup at the end"
            exit 0 ;;
        *) echo "Unknown option: $1" >&2; exit 2 ;;
    esac
    shift
done

[ "$(uname -s)" = "Darwin" ] || { echo "BlackHole is a macOS audio device; run this on the Mac." >&2; exit 1; }

installed() {
    # system_profiler lists every audio device macOS knows about, which is the
    # same list Help Me! reads through CoreAudio.
    system_profiler SPAudioDataType 2>/dev/null | grep -qi "$device"
}

routing_steps() {
    cat <<'STEPS'

One more step, so you hear the sound AND Help Me! can capture it.

In Audio MIDI Setup:
  1. Click + at the bottom left and choose "Create Multi-Output Device".
  2. Tick BOTH your speakers or headphones AND "BlackHole 2ch".
  3. Make sure your speakers are the first one ticked: the Multi-Output Device
     follows its master device for volume.
  4. Rename it something like "Speakers + Help Me!" if you like.

Then in System Settings > Sound > Output, choose that Multi-Output Device.

Finally, in Help Me! > Settings > PC audio source, choose BlackHole.
The status line at the bottom of the window will say "BlackHole" once it is
capturing through it.

Note: a Multi-Output Device has no volume slider of its own. Use the volume
controls on your speakers, or keep a plain output device around to switch back
to when you are not captioning.
STEPS
}

if installed; then
    echo "BlackHole is already installed."
    routing_steps
    [ "$open_audio_setup" -eq 1 ] && open -a "Audio MIDI Setup" 2>/dev/null || true
    exit 0
fi

if ! command -v brew >/dev/null 2>&1; then
    cat <<'NOHOMEBREW' >&2
BlackHole needs an installer, and the only scriptable source for it is
Homebrew, which is not installed on this Mac.

Either install Homebrew first:
  /bin/bash -c "$(curl -fsSL https://raw.githubusercontent.com/Homebrew/install/HEAD/install.sh)"
then run this script again,

or download the signed installer directly:
  https://existential.audio/blackhole/

You do not have to do either. Without BlackHole, Help Me! captures system
audio with ScreenCaptureKit instead; it only asks for Screen Recording
permission, and macOS asks for it again from time to time.
NOHOMEBREW
    exit 1
fi

echo "Installing $cask with Homebrew. macOS will ask for your password: the"
echo "installer is adding an audio device, which needs administrator rights."
brew install "$cask"

if ! installed; then
    echo "Homebrew finished, but macOS is not listing BlackHole yet." >&2
    echo "Log out and back in, or restart, and run this script again to check." >&2
    exit 1
fi

echo "BlackHole is installed and macOS can see it."
routing_steps
[ "$open_audio_setup" -eq 1 ] && open -a "Audio MIDI Setup" 2>/dev/null || true
