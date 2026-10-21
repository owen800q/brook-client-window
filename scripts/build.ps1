<#
.SYNOPSIS
  Builds the portable release: build\BrookClient-<version>-windows-<arch>.zip (+ .sha256).
  The zip holds BrookClient.exe (self-contained, no .NET install needed) and brook.exe. Unzip anywhere and run;
  nothing needs administrator rights.
.EXAMPLE
  scripts\build.ps1
  scripts\build.ps1 -Arch arm64 -Version 2026.1021.7
#>
param(
    [ValidateSet('x64', 'arm64')] [string] $Arch = 'x64',
    [string] $Version = (Get-Date -Format 'yyyy.MMdd.0')
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

& "$PSScriptRoot\fetch-brook.ps1" -Arch $Arch

$name = "BrookClient-$Version-windows-$Arch"
$out = Join-Path $root "build\$name"
if (Test-Path $out) { Remove-Item -Recurse -Force $out }

dotnet publish src\BrookClient\BrookClient.csproj -c Release -r "win-$Arch" "-p:Version=$Version" -o $out
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

Copy-Item vendor\brook.exe, README.md, LICENSE $out
Get-ChildItem $out -Filter *.pdb | Remove-Item

$zip = Join-Path $root "build\$name.zip"
if (Test-Path $zip) { Remove-Item -Force $zip }
Compress-Archive -Path "$out\*" -DestinationPath $zip
$hash = (Get-FileHash -Algorithm SHA256 $zip).Hash.ToLowerInvariant()
"$hash  $name.zip" | Set-Content -Encoding ascii "$zip.sha256"
Write-Host "Built $zip"
