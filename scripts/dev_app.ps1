<#
.SYNOPSIS
  Launches the built app against the simulated cloud (FakeGcp and its scripted worker): no network, no project, no money (issue #69).
.DESCRIPTION
  Sets DEG_FAKE_CLOUD=1 and starts the app on a fresh throwaway data folder (--fresh-profile), so the real history, sign-in and settings
  are never touched. Build first: dotnet build from app\ . Pass -Lane to pick the artifacts folder the build wrote to.
#>
param(
    [string]$Lane = 'main',
    [string]$Exe = ''
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (-not $Exe) {
    $Exe = Join-Path $repo "app\.artifacts\$Lane\bin\DnaEntropyGraph.App\debug_win-x64\DnaEntropyGraph.App.exe"
}
if (-not (Test-Path -LiteralPath $Exe)) {
    Write-Output "ERROR: $Exe does not exist. Build the app first (dotnet build, from app\), or pass -Exe / -Lane."
    exit 1
}

$env:DEG_FAKE_CLOUD = '1'
Write-Output "OK: launching against the simulated cloud on a fresh profile: $Exe"
& $Exe --fresh-profile
