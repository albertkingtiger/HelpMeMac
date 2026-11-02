#!/usr/bin/env bash
# Build HelpMe.app with everything it needs inside it.
#
# The bundle carries the .NET runtime, the Whisper native libraries, a
# standalone Python with the translation dependencies, and the models. Nothing
# has to be installed on the Mac that runs it: no .NET, no Python, no model
# download. The one thing that cannot be bundled is BlackHole, because a virtual
# audio device is a system installer of its own, so a Mac without it captures
# system audio with ScreenCaptureKit instead and the status line says so.
#
# This runs on the Mac, and also on the machine the code is written on. Away
# from macOS the bundle is assembled and archived as a .tar.gz; the disk image
# and the signature need macOS tools and are skipped with a note.
set -euo pipefail

version="1.0.0"
models="all"
make_dmg=1
skip_python=0

# Pinned so a rebuild gets the same interpreter. Bump both together; the
# matching assets are listed at
# https://github.com/astral-sh/python-build-standalone/releases
python_release="20261026"
python_version="3.11.18"
python_short="3.11"

usage() {
    cat <<'USAGE'
Usage: scripts/package.sh [options]

  --version X.Y.Z   version recorded in the bundle (default 1.0.0)
  --models all|base which Whisper models to include (default all)
  --no-python       leave out the Python runtime and the translation model
  --no-dmg          skip the disk image even on macOS
USAGE
}

while [ $# -gt 0 ]; do
    case "$1" in
        --version) shift; version="${1:?--version needs a value}" ;;
        --models) shift; models="${1:?--models needs a value}" ;;
        --no-python) skip_python=1 ;;
        --no-dmg) make_dmg=0 ;;
        -h|--help) usage; exit 0 ;;
        *) echo "Unknown option: $1" >&2; usage >&2; exit 2 ;;
    esac
    shift
done

cd "$(dirname "$0")/.."
project_root="$(pwd)"
dist="$project_root/dist"
app="$dist/HelpMe.app"
contents="$app/Contents"
macos="$contents/MacOS"
resources="$contents/Resources"
cache="$project_root/.tools/package"

host="$(uname -s)"
on_mac=0
[ "$host" = "Darwin" ] && on_mac=1

note() { printf '\n==> %s\n' "$1"; }

note "Publishing a self-contained osx-x64 build"
rm -rf "$dist/publish" "$app"
mkdir -p "$macos" "$resources" "$cache"
dotnet publish HelpMe.csproj -c Release -r osx-x64 --self-contained true \
    -p:Version="$version" -o "$dist/publish"
cp -R "$dist/publish/." "$macos/"
rm -rf "$dist/publish"
[ -f "$macos/HelpMe" ] || { echo "The published build has no HelpMe executable." >&2; exit 1; }

note "Copying models"
mkdir -p "$macos/models"
if [ "$models" = "base" ]; then
    whisper_models=("$project_root/models/ggml-base.en-q8_0.bin")
else
    whisper_models=("$project_root"/models/ggml-*.bin)
fi
for model in "${whisper_models[@]}"; do
    [ -f "$model" ] || { echo "Missing Whisper model: $model. Run scripts/setup.sh first." >&2; exit 1; }
    cp "$model" "$macos/models/"
done

if [ "$skip_python" -eq 0 ]; then
    translation_model="$project_root/models/nllb-200-distilled-600M-int8"
    [ -f "$translation_model/model.bin" ] || {
        echo "Missing the Korean model: $translation_model. Run scripts/setup.sh --translation first." >&2
        exit 1
    }
    cp -R "$translation_model" "$macos/models/"

    note "Embedding Python $python_version"
    tarball="$cache/cpython-$python_version-$python_release-x86_64-apple-darwin.tar.gz"
    if [ ! -f "$tarball" ]; then
        url="https://github.com/astral-sh/python-build-standalone/releases/download/$python_release/cpython-$python_version+$python_release-x86_64-apple-darwin-install_only.tar.gz"
        echo "Downloading $url"
        curl -fsSL --retry 3 -o "$tarball.partial" "$url"
        mv "$tarball.partial" "$tarball"
    fi
    # The archive unpacks to a single "python" directory, which is exactly
    # where appsettings.json below expects to find the interpreter. It is full
    # of symlinks, which Windows cannot create from a shell, so away from macOS
    # the helper copies the targets in their place instead.
    if [ "$on_mac" -eq 1 ]; then
        tar -xzf "$tarball" -C "$macos"
    else
        extract_python="${PYTHON:-python}"
        command -v "$extract_python" >/dev/null 2>&1 || extract_python=python3
        "$extract_python" scripts/extract_tar.py "$tarball" "$macos"
    fi
    [ -x "$macos/python/bin/python3" ] || [ -f "$macos/python/bin/python3" ] || {
        echo "The Python runtime did not unpack as expected." >&2; exit 1; }

    site="$macos/python/lib/python$python_short/site-packages"
    note "Installing translation dependencies into the bundle"
    if [ "$on_mac" -eq 1 ]; then
        # The bundled interpreter is the right one to install with: it is the
        # same build that will import these packages at runtime.
        "$macos/python/bin/python3" -m pip install --no-cache-dir --upgrade pip
        "$macos/python/bin/python3" -m pip install --no-cache-dir -r translation/requirements.txt
    else
        # Cross-building: fetch the macOS wheels and unpack them into the
        # bundle's own site-packages, without running anything from them.
        build_python="${PYTHON:-python}"
        command -v "$build_python" >/dev/null 2>&1 || build_python=python3
        "$build_python" -m pip install --no-cache-dir --upgrade \
            --target "$site" \
            --only-binary=:all: --python-version "$python_short" \
            --implementation cp --abi "cp${python_short//./}" --abi abi3 --abi none \
            --platform macosx_10_9_x86_64 --platform macosx_11_0_x86_64 \
            --platform macosx_12_0_x86_64 --platform macosx_13_0_x86_64 \
            --platform macosx_14_0_x86_64 --platform any \
            -r translation/requirements.txt
    fi
    [ -d "$site/ctranslate2" ] || { echo "CTranslate2 did not install into the bundle." >&2; exit 1; }
    # Compiled caches belong to the machine that wrote them, not the bundle.
    find "$macos/python" -name '__pycache__' -type d -prune -exec rm -rf {} + 2>/dev/null || true
fi

note "Writing the bundle's settings"
# The packaged app finds its interpreter and models beside itself, not in a
# .venv left over from development.
python_path=".venv/bin/python3"
[ "$skip_python" -eq 0 ] && python_path="python/bin/python3"
cat > "$macos/appsettings.json" <<SETTINGS
{
  "SystemAudioSource": "BlackHole",
  "WhisperModelPath": "models/ggml-base.en-q8_0.bin",
  "TranslationModelPath": "models/nllb-200-distilled-600M-int8",
  "PythonPath": "$python_path",
  "SpeechThreshold": 0.008,
  "MaxSegmentSeconds": 6,
  "SilenceSeconds": 0.7
}
SETTINGS

note "Building the icon"
icon_python="${PYTHON:-python}"
command -v "$icon_python" >/dev/null 2>&1 || icon_python=python3
if [ "$on_mac" -eq 1 ] && [ "$skip_python" -eq 0 ]; then
    icon_python="$macos/python/bin/python3"
fi
"$icon_python" scripts/make_icon.py --icns "$resources/helpme.icns"

cat > "$contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>Help Me!</string>
  <key>CFBundleDisplayName</key><string>Help Me!</string>
  <key>CFBundleIdentifier</key><string>com.helpme.app</string>
  <key>CFBundleExecutable</key><string>HelpMe</string>
  <key>CFBundleIconFile</key><string>helpme.icns</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$version</string>
  <key>CFBundleVersion</key><string>$version</string>
  <key>LSMinimumSystemVersion</key><string>13.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <!-- Help Me! lives in the menu bar: no Dock icon, and no window at launch.
       Set this to false if you would rather it behaved like an ordinary app. -->
  <key>LSUIElement</key><true/>
  <!-- macOS refuses audio input without this sentence, and shows it in the
       permission prompt. BlackHole counts as a microphone to the system. -->
  <key>NSMicrophoneUsageDescription</key>
  <string>Help Me! transcribes what your Mac is playing, and your microphone, into captions on this computer. No audio leaves the Mac.</string>
</dict>
</plist>
PLIST

size="$(du -sh "$app" | cut -f1)"
note "Bundle assembled: $app ($size)"

if [ "$on_mac" -eq 1 ]; then
    note "Signing (ad-hoc)"
    # Enough for a Mac running an app it built itself, and it keeps Gatekeeper
    # from calling the bundle damaged once it has been copied somewhere else.
    codesign --force --deep --sign - "$app"
    codesign --verify --deep "$app" && echo "Signature verifies."

    if [ "$make_dmg" -eq 1 ]; then
        note "Building the disk image"
        dmg="$dist/HelpMe-$version.dmg"
        rm -f "$dmg"
        staging="$dist/dmg"
        rm -rf "$staging"; mkdir -p "$staging"
        cp -R "$app" "$staging/"
        ln -s /Applications "$staging/Applications"
        hdiutil create -volname "Help Me! $version" -srcfolder "$staging" -ov -format UDZO "$dmg" >/dev/null
        rm -rf "$staging"
        shasum -a 256 "$dmg" | tee "$dmg.sha256"
        note "Wrote $dmg"
    fi
else
    note "Archiving (the disk image and the signature need macOS and were skipped)"
    archive="$dist/HelpMe-$version-macos-x64.tar.gz"
    "$icon_python" scripts/make_archive.py "$app" "$archive" --name "HelpMe.app"
    if command -v shasum >/dev/null 2>&1; then
        shasum -a 256 "$archive" | tee "$archive.sha256"
    elif command -v sha256sum >/dev/null 2>&1; then
        sha256sum "$archive" | tee "$archive.sha256"
    fi
    cat <<INSTRUCTIONS

On the Mac:
  tar -xzf $(basename "$archive")
  xattr -dr com.apple.quarantine HelpMe.app   # only if it was downloaded
  codesign --force --deep --sign - HelpMe.app # optional, silences Gatekeeper
  mv HelpMe.app /Applications

Then open it from /Applications once, so macOS asks for microphone access
with the app's own name attached.
INSTRUCTIONS
fi
