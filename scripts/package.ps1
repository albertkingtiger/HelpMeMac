param(
    [string]$OutputDirectory,
    [string]$ArchivePath,
    [switch]$ArchiveOnly,
    [switch]$SkipArchive
)

$ErrorActionPreference = 'Stop'
$project = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $project 'dist\HelpMe-Windows11-Portable' }
if (-not $ArchivePath) { $ArchivePath = Join-Path $project 'dist\HelpMe-Windows11-Portable.zip' }
$bundle = [IO.Path]::GetFullPath($OutputDirectory)
$archive = [IO.Path]::GetFullPath($ArchivePath)
$dist = [IO.Path]::GetFullPath((Join-Path $project 'dist'))
if (-not $bundle.StartsWith($dist + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
    -not $archive.StartsWith($dist + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Package output must stay inside the project dist directory.'
}
if ((Test-Path -LiteralPath $bundle) -and -not $ArchiveOnly) { throw "Package directory already exists: $bundle" }
if ($ArchiveOnly -and -not (Test-Path -LiteralPath $bundle)) { throw "Package directory is missing: $bundle" }
if (-not $SkipArchive -and (Test-Path -LiteralPath $archive)) { throw "Archive already exists: $archive" }

if (-not $ArchiveOnly) {
$venv = Join-Path $project '.venv'
$venvConfig = Join-Path $venv 'pyvenv.cfg'
$sitePackages = Join-Path $venv 'Lib\site-packages'
$pythonHome = ((Get-Content -LiteralPath $venvConfig | Where-Object { $_ -match '^home\s*=' } | Select-Object -First 1) -split '=', 2)[1].Trim()
if (-not (Test-Path -LiteralPath (Join-Path $pythonHome 'python.exe')) -or
    -not (Test-Path -LiteralPath $sitePackages)) {
    throw 'A complete local Python 3.11 environment is required in .venv.'
}
$whisper = Join-Path $project 'models\ggml-base.en-q8_0.bin'
$whisperModels = @(Get-ChildItem -LiteralPath (Join-Path $project 'models') -Filter 'ggml-*.bin' -File)
$nllb = Join-Path $project 'models\nllb-200-distilled-600M-int8'
if (-not (Test-Path -LiteralPath $whisper) -or -not (Test-Path -LiteralPath (Join-Path $nllb 'model.bin'))) {
    throw 'The Whisper and NLLB models must be downloaded before packaging.'
}

New-Item -ItemType Directory -Path $bundle -Force | Out-Null
# A separate build output keeps a running copy of Help Me! from locking the publish.
$buildOutput = Join-Path $project '.tmp\package-build\'
dotnet publish (Join-Path $project 'HelpMe.csproj') -c Release -r win-x64 --self-contained true -p:PublishDir="$bundle\" -p:OutputPath="$buildOutput" --no-restore
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

$pythonDestination = Join-Path $bundle 'python'
New-Item -ItemType Directory -Path $pythonDestination -Force | Out-Null
& robocopy $pythonHome $pythonDestination /E /XD (Join-Path $pythonHome 'Lib\site-packages') (Join-Path $pythonHome 'Scripts') (Join-Path $pythonHome 'Doc') (Join-Path $pythonHome 'Tools') (Join-Path $pythonHome 'include') (Join-Path $pythonHome 'libs') (Join-Path $pythonHome 'tcl') /XF '*.pyc' /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "Python runtime copy failed: $LASTEXITCODE" }
$siteDestination = Join-Path $pythonDestination 'Lib\site-packages'
New-Item -ItemType Directory -Path $siteDestination -Force | Out-Null
& robocopy $sitePackages $siteDestination /E /XD __pycache__ /XF '*.pyc' /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "Python dependency copy failed: $LASTEXITCODE" }
foreach ($dll in 'concrt140.dll', 'msvcp140.dll') {
    $source = Join-Path $env:WINDIR "System32\$dll"
    if (-not (Test-Path -LiteralPath $source)) { throw "Missing required C++ runtime DLL: $source" }
    Copy-Item -LiteralPath $source -Destination (Join-Path $pythonDestination $dll)
}

# Whisper's native libraries import these too, and a clean PC may not have them.
# They go beside the executable and beside whisper.dll, whichever the loader searches.
$whisperNative = @(Get-ChildItem -LiteralPath $bundle -Filter 'whisper.dll' -File -Recurse | Where-Object { $_.FullName -notmatch 'win-(x86|arm64)' })
if ($whisperNative.Count -eq 0) { throw 'Published output has no x64 whisper.dll.' }
$nativeDirectories = @($bundle) + @($whisperNative | ForEach-Object { $_.DirectoryName }) | Select-Object -Unique
foreach ($dll in 'msvcp140.dll', 'vcruntime140.dll', 'vcruntime140_1.dll', 'vcomp140.dll') {
    $source = Join-Path $env:WINDIR "System32\$dll"
    if (-not (Test-Path -LiteralPath $source)) { throw "Missing required C++ runtime DLL: $source" }
    foreach ($directory in $nativeDirectories) { Copy-Item -LiteralPath $source -Destination (Join-Path $directory $dll) }
}

$modelDestination = Join-Path $bundle 'models'
New-Item -ItemType Directory -Path $modelDestination -Force | Out-Null
foreach ($model in $whisperModels) { Copy-Item -LiteralPath $model.FullName -Destination (Join-Path $modelDestination $model.Name) }
Copy-Item -LiteralPath $nllb -Destination $modelDestination -Recurse
$settings = Get-Content -LiteralPath (Join-Path $project 'appsettings.json') -Raw | ConvertFrom-Json
$settings.PythonPath = 'python/python.exe'
$settings | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $bundle 'appsettings.json') -Encoding utf8
Copy-Item -LiteralPath (Join-Path $project 'Package-Start-HelpMe.cmd') -Destination (Join-Path $bundle 'Start-HelpMe.cmd')
Copy-Item -LiteralPath (Join-Path $project 'Package-README.txt') -Destination (Join-Path $bundle 'README.txt')
}

$pythonDestination = Join-Path $bundle 'python'
$modelDestination = Join-Path $bundle 'models'
foreach ($required in 'HelpMe.exe', 'vcruntime140.dll', 'vcomp140.dll', 'WebView2Loader.dll', 'python\python.exe', 'translation\worker.py',
    'models\ggml-base.en-q8_0.bin', 'models\nllb-200-distilled-600M-int8\model.bin',
    'Start-HelpMe.cmd', 'README.txt') {
    if (-not (Test-Path -LiteralPath (Join-Path $bundle $required))) {
        throw "Package file is missing: $required"
    }
}

$env:HF_HUB_OFFLINE = '1'
$env:TRANSFORMERS_OFFLINE = '1'
$env:PYTHONIOENCODING = 'utf-8'
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
& (Join-Path $pythonDestination 'python.exe') -I -c 'import ctranslate2, transformers, sentencepiece'
if ($LASTEXITCODE -ne 0) { throw 'Portable Python dependency check failed.' }
$sample = '{"text":"We can meet at 2 or 3 PM.","source":"eng_Latn"}'
$reply = $sample | & (Join-Path $pythonDestination 'python.exe') -u (Join-Path $bundle 'translation\worker.py') --model (Join-Path $modelDestination 'nllb-200-distilled-600M-int8')
if ($LASTEXITCODE -ne 0 -or $reply.Count -lt 2 -or -not (($reply[-1] | ConvertFrom-Json).text)) {
    throw 'Bundled translation model check failed.'
}
Write-Host "Bundled translation: $($reply[-1])"

if ($SkipArchive) { Write-Host "Package folder ready: $bundle"; return }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($bundle, $archive, [IO.Compression.CompressionLevel]::Optimal, $true)
$hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
"$hash  $(Split-Path $archive -Leaf)" | Set-Content -LiteralPath "$archive.sha256" -Encoding ascii
Write-Host "Package ready: $archive"
