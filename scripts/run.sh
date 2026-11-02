#!/usr/bin/env bash
# Build Release if needed and start Help Me!. It comes up hidden in the menu
# bar, so there is nothing to see until the restore shortcut or the menu bar
# icon asks for the window.
set -euo pipefail
cd "$(dirname "$0")/.."

output="bin/Release/net8.0/osx-x64/HelpMe"
if [ ! -x "$output" ] || [ "${1:-}" = "--rebuild" ]; then
    dotnet build HelpMe.csproj -c Release -r osx-x64
fi
exec "$output"
