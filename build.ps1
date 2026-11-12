# Builds SwiftGet and its installer.
#   .\build.ps1                 -> dist\SwiftGet-Setup-<version>.exe and dist\SwiftGet-<version>-portable.zip
#   .\build.ps1 -Version 1.2.0
# Needs the .NET 10 SDK and Inno Setup 6 (winget install JRSoftware.InnoSetup).
param([string]$Version = "1.0.0")
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
Set-Location $root

$app = Join-Path $root "publish\app"
if (Test-Path $app) { Remove-Item $app -Recurse -Force }
New-Item -ItemType Directory -Force (Join-Path $root "dist") | Out-Null

Write-Host "Publishing SwiftGet $Version..." -ForegroundColor Cyan
dotnet publish SwiftGet\SwiftGet.csproj -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none `
    -p:Version=$Version -o $app
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

Write-Host "Creating portable zip..." -ForegroundColor Cyan
$zip = Join-Path $root "dist\SwiftGet-$Version-portable.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path "$app\*" -DestinationPath $zip

$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 not found. Install it with: winget install JRSoftware.InnoSetup" }

Write-Host "Building installer..." -ForegroundColor Cyan
& $iscc "/DAppVersion=$Version" "installer\SwiftGet.iss"
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed" }

Write-Host "Done:" -ForegroundColor Green
Get-ChildItem (Join-Path $root "dist") | ForEach-Object { "  $($_.Name)  ($([math]::Round($_.Length / 1MB, 1)) MB)" }
