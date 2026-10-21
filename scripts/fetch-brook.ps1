<#
.SYNOPSIS
  Downloads the official brook release binary for Windows into vendor\brook.exe (cached in vendor\dl).
.EXAMPLE
  scripts\fetch-brook.ps1            # x64
  scripts\fetch-brook.ps1 -Arch arm64
#>
param(
    [ValidateSet('x64', 'arm64')] [string] $Arch = 'x64'
)
$ErrorActionPreference = 'Stop'
$BrookVersion = 'v20270101'

$root = Split-Path -Parent $PSScriptRoot
$dl = Join-Path $root 'vendor\dl'
New-Item -ItemType Directory -Force -Path $dl | Out-Null

$asset = if ($Arch -eq 'x64') { 'brook_windows_amd64.exe' } else { 'brook_windows_arm64.exe' }
$cached = Join-Path $dl "$BrookVersion-$asset"
if (-not (Test-Path $cached) -or (Get-Item $cached).Length -eq 0) {
    $url = "https://github.com/txthinking/brook/releases/download/$BrookVersion/$asset"
    Write-Host "Downloading $url"
    Invoke-WebRequest -Uri $url -OutFile "$cached.part" -UseBasicParsing
    Move-Item -Force "$cached.part" $cached
}
Copy-Item -Force $cached (Join-Path $root 'vendor\brook.exe')
Write-Host "vendor\brook.exe <- $asset ($BrookVersion)"
