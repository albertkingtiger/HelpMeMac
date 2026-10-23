$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location $projectRoot
try {
    $localDotnet = Join-Path $projectRoot ".tools/dotnet/dotnet.exe"
    if (Test-Path -LiteralPath $localDotnet) {
        $dotnet = $localDotnet
        $env:DOTNET_ROOT = Join-Path $projectRoot ".tools/dotnet"
        $env:DOTNET_CLI_HOME = Join-Path $projectRoot ".tools/cli"
        $env:NUGET_PACKAGES = Join-Path $projectRoot ".tools/packages"
        $env:APPDATA = Join-Path $projectRoot ".tools/appdata"
    } else {
        $dotnet = (Get-Command dotnet -ErrorAction Stop).Source
    }
    $application = Join-Path $projectRoot "bin/Release/net8.0-windows/win-x64/HelpMe.dll"
    if (!(Test-Path -LiteralPath $application)) {
        & $dotnet build HelpMe.csproj -c Release
        if ($LASTEXITCODE -ne 0) { throw "Build failed; see output above." }
    }
    & $dotnet $application
    if ($LASTEXITCODE -ne 0) { throw "Help Me! exited with code $LASTEXITCODE" }
} catch {
    Write-Host $_ -ForegroundColor Red
    Read-Host "Press Enter to close"
} finally { Pop-Location }
