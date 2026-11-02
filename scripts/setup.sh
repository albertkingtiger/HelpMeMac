#!/usr/bin/env bash
# First setup on the Mac: the Whisper model, and optionally the Python
# environment and the Korean translation model. Needs the internet once; the
# app runs offline afterwards.
set -euo pipefail

translation=0
convert=0
python_bin="python3"
blackhole=0

usage() {
    cat <<'USAGE'
Usage: scripts/setup.sh [--translation] [--convert-translation] [--blackhole] [--python PATH]

  --translation          also prepare the Korean NLLB model and its Python environment
  --convert-translation  convert Meta's checkpoint instead of downloading the pinned
                         CTranslate2 build (several GB, installs PyTorch)
  --blackhole            install the BlackHole 2ch virtual audio device with Homebrew
  --python PATH          use a particular python3 (default: the one on PATH)
USAGE
}

while [ $# -gt 0 ]; do
    case "$1" in
        --translation) translation=1 ;;
        --convert-translation) translation=1; convert=1 ;;
        --blackhole) blackhole=1 ;;
        --python) shift; python_bin="${1:?--python needs a path}" ;;
        -h|--help) usage; exit 0 ;;
        *) echo "Unknown option: $1" >&2; usage >&2; exit 2 ;;
    esac
    shift
done

cd "$(dirname "$0")/.."
project_root="$(pwd)"

command -v dotnet >/dev/null 2>&1 || {
    echo "The .NET 8 SDK is not on PATH. Install it from https://dotnet.microsoft.com/download/dotnet/8.0 (x64 build for this Intel Mac)." >&2
    exit 1
}
command -v "$python_bin" >/dev/null 2>&1 || {
    echo "No python3 found. Install Python 3.11 or newer, or pass --python PATH." >&2
    exit 1
}

# Keep every download inside the project rather than in the user's home.
export HF_HOME="$project_root/.tools/huggingface"
export PIP_CACHE_DIR="$project_root/.tools/pip-cache"
export HF_HUB_DISABLE_XET=1

mkdir -p models

model_file="$project_root/models/ggml-base.en-q8_0.bin"
if [ ! -f "$model_file" ]; then
    echo "Downloading English Whisper Base Q8 for improved caption accuracy..."
    "$python_bin" scripts/download_whisper.py "$model_file"
fi

if [ "$translation" -eq 1 ]; then
    echo "Preparing NLLB INT8 (approximately 647 MB including tokenizer)."
    echo "Model license: CC-BY-NC-4.0 (noncommercial); see README.md."
    if [ ! -x .venv/bin/python3 ]; then
        "$python_bin" -m venv .venv
    fi
    ./.venv/bin/python3 -m pip install --upgrade pip
    ./.venv/bin/python3 -m pip install -r translation/requirements.txt
    if [ "$convert" -eq 1 ]; then
        ./.venv/bin/python3 -m pip install torch==2.6.0 --index-url https://download.pytorch.org/whl/cpu
        ./.venv/bin/python3 scripts/prepare_translation.py --output models/nllb-200-distilled-600M-int8
    else
        ./.venv/bin/python3 scripts/download_translation.py --output models/nllb-200-distilled-600M-int8
    fi
fi

if [ "$blackhole" -eq 1 ]; then
    # The same script the application bundle carries, so the instructions are
    # written once and say the same thing either way it is reached.
    scripts/install-blackhole.sh
fi

echo "Setup complete. Build and run: scripts/run.sh"
