param(
    [string]$Version = '1.0.0'
)

$ErrorActionPreference = 'Stop'
$project = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$dist = Join-Path $project 'dist'
$stage = Join-Path $dist 'HelpMe-Installer-Stage'
$installer = Join-Path $dist "HelpMe-Setup-$Version.exe"
if (Test-Path -LiteralPath $installer) { throw "Installer already exists: $installer" }

# Uses an installed Inno Setup 6 when there is one. Otherwise it fetches the
# Tools.InnoSetup package from NuGet into the git-ignored .tools folder.
$innoVersion = '6.4.3'
$innoRoot = Join-Path $project ".tools\innosetup\$innoVersion"
$compiler = @(
    (Join-Path $innoRoot 'tools\ISCC.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $compiler) {
    New-Item -ItemType Directory -Path $innoRoot -Force | Out-Null
    $download = Join-Path $innoRoot 'package.zip'
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -Uri "https://www.nuget.org/api/v2/package/Tools.InnoSetup/$innoVersion" -OutFile $download -UseBasicParsing
    Expand-Archive -LiteralPath $download -DestinationPath $innoRoot
    Remove-Item -LiteralPath $download
    $compiler = Join-Path $innoRoot 'tools\ISCC.exe'
    if (-not (Test-Path -LiteralPath $compiler)) { throw "Inno Setup compiler not found: $compiler" }
}

# The stage is this script's own scratch output, rebuilt on every run.
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
& (Join-Path $PSScriptRoot 'package.ps1') -OutputDirectory $stage -SkipArchive

& $compiler "/DBundle=$stage" "/DOutputFolder=$dist" "/DAppVersion=$Version" (Join-Path $PSScriptRoot 'HelpMe.iss')
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed: $LASTEXITCODE" }

$hash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash
"$hash  $(Split-Path $installer -Leaf)" | Set-Content -LiteralPath "$installer.sha256" -Encoding ascii
Remove-Item -LiteralPath $stage -Recurse -Force
Write-Host "Installer ready: $installer ($([math]::Round((Get-Item -LiteralPath $installer).Length / 1GB, 2)) GB)"
