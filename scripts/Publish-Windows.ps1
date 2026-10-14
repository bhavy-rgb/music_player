# Build, validate and package an unsigned, self-contained Windows x64 release.
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'Run this packaging script on Windows.' }
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'Install the .NET 8 SDK, then open a new PowerShell window.' }

function Invoke-Dotnet {
    param([string[]]$Arguments)
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($Arguments -join ' ') failed (exit $LASTEXITCODE)." }
}

$root = Split-Path $PSScriptRoot -Parent
$artifacts = Join-Path $root 'artifacts'
$publish = Join-Path $artifacts 'publish/win-x64'
$archive = Join-Path $artifacts 'Resonance-win-x64.zip'
Push-Location $root
try {
    New-Item -ItemType Directory -Path $artifacts -Force | Out-Null
    Invoke-Dotnet -Arguments @('restore', 'Resonance.sln')
    Invoke-Dotnet -Arguments @('build', 'Resonance.sln', '-c', 'Release', '--no-restore', '-warnaserror')
    Invoke-Dotnet -Arguments @('test', 'Resonance.Tests/Resonance.Tests.csproj', '-c', 'Release', '--no-build', '--logger', 'trx;LogFileName=regression.trx', '--results-directory', 'artifacts/TestResults')
    # Delete only the known generated folder so removed assemblies cannot leak into a new release.
    if (Test-Path -LiteralPath $publish) { Remove-Item -LiteralPath $publish -Recurse -Force }
    Invoke-Dotnet -Arguments @('publish', 'Resonance.App/Resonance.App.csproj', '-c', 'Release', '-r', 'win-x64', '-p:PublishProfile=Release', '-warnaserror')
    if (-not (Test-Path -LiteralPath (Join-Path $publish 'Resonance.exe'))) { throw 'Publish produced no Resonance.exe.' }
    Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination $publish
    Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination $publish
    Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $archive -Force
    $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  Resonance-win-x64.zip" | Set-Content -LiteralPath "$archive.sha256" -Encoding Ascii
    Write-Host "Created $archive"
    Write-Host "Launch: & `"$(Join-Path $publish 'Resonance.exe')`""
}
finally { Pop-Location }
