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

Write-Host "Packaging the browser extension for the Chrome Web Store / Edge Add-ons..." -ForegroundColor Cyan
$pack = Join-Path ([IO.Path]::GetTempPath()) "swiftget-extension-pack"
if (Test-Path $pack) { Remove-Item $pack -Recurse -Force }
Copy-Item (Join-Path $root "extension") $pack -Recurse
$manifestPath = Join-Path $pack "manifest.json"
$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
$manifest.PSObject.Properties.Remove("key")      # stores assign their own ID and reject "key"
$manifest.version = $Version
[IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 10), (New-Object System.Text.UTF8Encoding $false))
$extZip = Join-Path $root "dist\SwiftGet-Extension-$Version.zip"
if (Test-Path $extZip) { Remove-Item $extZip }
Compress-Archive -Path "$pack\*" -DestinationPath $extZip
Remove-Item $pack -Recurse -Force

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
