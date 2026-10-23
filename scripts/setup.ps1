param([switch]$Translation, [switch]$ConvertTranslation, [string]$Python = "python")
$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location $projectRoot
try {
    $env:HF_HOME = Join-Path $projectRoot ".tools/huggingface"
    $env:PIP_CACHE_DIR = Join-Path $projectRoot ".tools/pip-cache"
    $env:HF_HUB_DISABLE_XET = "1"
    New-Item -ItemType Directory -Force -Path models | Out-Null
    $modelFile = Join-Path $projectRoot "models/ggml-base.en-q8_0.bin"
    if (!(Test-Path -LiteralPath $modelFile)) {
        Write-Host "Downloading English Whisper Base Q8 for improved caption accuracy..."
        # Python's TLS stack also works on hosts where PowerShell's legacy TLS stack fails.
        & $Python scripts/download_whisper.py $modelFile
        if ($LASTEXITCODE -ne 0) { throw "Whisper download failed." }
    }
    if ($Translation) {
        Write-Host "Preparing NLLB INT8 (approximately 647 MB including tokenizer)."
        Write-Host "Model license: CC-BY-NC-4.0 (noncommercial); see README.md."
        if (!(Test-Path -LiteralPath .venv/Scripts/python.exe)) {
            & $Python -m venv .venv
            if ($LASTEXITCODE -ne 0) { throw "Could not create Python environment." }
        }
        & ./.venv/Scripts/python.exe -m pip install -r translation/requirements.txt
        if ($LASTEXITCODE -ne 0) { throw "Translation dependencies failed to install." }
        if ($ConvertTranslation) {
            & ./.venv/Scripts/python.exe -m pip install torch==2.6.0 --index-url https://download.pytorch.org/whl/cpu
            if ($LASTEXITCODE -ne 0) { throw "Model conversion dependency failed to install." }
            & ./.venv/Scripts/python.exe scripts/prepare_translation.py --output models/nllb-200-distilled-600M-int8
        } else {
            & ./.venv/Scripts/python.exe scripts/download_translation.py --output models/nllb-200-distilled-600M-int8
        }
        if ($LASTEXITCODE -ne 0) { throw "Translation model preparation failed." }
    }
    Write-Host "Setup complete. Build/run: dotnet run --project HelpMe.csproj"
} finally { Pop-Location }
