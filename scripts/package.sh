#!/usr/bin/env bash
# Build HelpMe.app, and a disk image to move it with.
#
# The bundle is self-contained for .NET: it carries its own runtime, the
# Whisper native libraries, the models and the app itself. It does not carry a
# Python interpreter. A virtual environment is not relocatable on macOS, so the
# one that is copied in still points at the python3 it was made from, which
# matters only if the bundle is moved to a Mac without that interpreter.
set -euo pipefail

version="1.0.0"
include_python=1
make_dmg=1

while [ $# -gt 0 ]; do
    case "$1" in
        --version) shift; version="${1:?--version needs a value}" ;;
        --no-python) include_python=0 ;;
        --no-dmg) make_dmg=0 ;;
        -h|--help)
            echo "Usage: scripts/package.sh [--version X.Y.Z] [--no-python] [--no-dmg]"
            exit 0 ;;
        *) echo "Unknown option: $1" >&2; exit 2 ;;
    esac
    shift
done

cd "$(dirname "$0")/.."
project_root="$(pwd)"
app="$project_root/dist/HelpMe.app"
contents="$app/Contents"
macos="$contents/MacOS"
resources="$contents/Resources"

[ "$(uname -s)" = "Darwin" ] || {
    echo "The bundle has to be built on the Mac: it uses sips, iconutil, codesign and hdiutil." >&2
    exit 1
}

echo "Publishing a self-contained osx-x64 build..."
rm -rf "$project_root/dist/publish" "$app"
dotnet publish HelpMe.csproj -c Release -r osx-x64 --self-contained true \
    -p:Version="$version" -o "$project_root/dist/publish"

mkdir -p "$macos" "$resources"
cp -R "$project_root/dist/publish/." "$macos/"
chmod +x "$macos/HelpMe"

# Relative asset paths resolve beside the executable, so the models and the
# Python environment live in MacOS/ rather than in Resources/.
echo "Copying models..."
mkdir -p "$macos/models"
for model in "$project_root"/models/*.bin "$project_root"/models/nllb-200-distilled-600M-int8; do
    [ -e "$model" ] || continue
    cp -R "$model" "$macos/models/"
done

if [ "$include_python" -eq 1 ] && [ -d "$project_root/.venv" ]; then
    echo "Copying the Python environment for translation..."
    cp -R "$project_root/.venv" "$macos/.venv"
fi

echo "Building the icon..."
iconset="$project_root/dist/helpme.iconset"
rm -rf "$iconset"; mkdir -p "$iconset"
for size in 16 32 64 128 256 512; do
    sips -z $size $size "$project_root/Assets/helpme.png" --out "$iconset/icon_${size}x${size}.png" >/dev/null
    double=$((size * 2))
    sips -z $double $double "$project_root/Assets/helpme.png" --out "$iconset/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$iconset" -o "$resources/helpme.icns"
rm -rf "$iconset"

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

# An ad-hoc signature is enough for a Mac running an app it built itself, and
# it keeps Gatekeeper from calling the bundle damaged after it is copied.
echo "Signing (ad-hoc)..."
codesign --force --deep --sign - "$app"

if [ "$make_dmg" -eq 1 ]; then
    echo "Building the disk image..."
    dmg="$project_root/dist/HelpMe-$version.dmg"
    rm -f "$dmg"
    staging="$project_root/dist/dmg"
    rm -rf "$staging"; mkdir -p "$staging"
    cp -R "$app" "$staging/"
    ln -s /Applications "$staging/Applications"
    hdiutil create -volname "Help Me! $version" -srcfolder "$staging" -ov -format UDZO "$dmg" >/dev/null
    rm -rf "$staging"
    shasum -a 256 "$dmg" > "$dmg.sha256"
    echo "Wrote $dmg"
    cat "$dmg.sha256"
fi

rm -rf "$project_root/dist/publish"
echo "Wrote $app"
echo "Drag it to /Applications, then open it once from there so macOS asks for microphone access."
