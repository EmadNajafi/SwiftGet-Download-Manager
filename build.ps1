# Builds SwiftGet and everything that goes on the release page.
#   .\build.ps1 -Version 1.2.0
# Output:
#   dist\SwiftGet-<version>.zip          everything in one file: installer + extensions + read-me (for people)
#   dist\SwiftGet-Setup-<version>.exe    the installer on its own (used by the in-app updater)
#   dist\store\...                       extension packages for the Chrome / Edge / Firefox stores (for the developer)
# Needs the .NET 10 SDK and Inno Setup 6 (winget install JRSoftware.InnoSetup).
param([string]$Version = "1.0.0")
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

# Standard zip (forward slashes, UTF-8 names); Compress-Archive in Windows PowerShell 5 writes backslashes,
# which the Chrome Web Store rejects.
function New-Zip($sourceDir, $zipPath) {
    if (Test-Path $zipPath) { Remove-Item $zipPath }
    $base = (Resolve-Path $sourceDir).Path.TrimEnd('\') + '\'
    $stream = [IO.File]::Open($zipPath, [IO.FileMode]::CreateNew)
    $archive = New-Object IO.Compression.ZipArchive($stream, [IO.Compression.ZipArchiveMode]::Create, $false, [Text.Encoding]::UTF8)
    try {
        Get-ChildItem $sourceDir -Recurse -File | ForEach-Object {
            $name = $_.FullName.Substring($base.Length).Replace('\', '/')
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $_.FullName, $name, [IO.Compression.CompressionLevel]::Optimal)
        }
    }
    finally { $archive.Dispose(); $stream.Dispose() }
}
$root = $PSScriptRoot
Set-Location $root

$app = Join-Path $root "publish\app"
$dist = Join-Path $root "dist"
$store = Join-Path $dist "store"
if (Test-Path $app) { Remove-Item $app -Recurse -Force }
New-Item -ItemType Directory -Force $dist, $store | Out-Null

Write-Host "Publishing SwiftGet $Version..." -ForegroundColor Cyan
dotnet publish SwiftGet\SwiftGet.csproj -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none `
    -p:Version=$Version -o $app
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

# ---------------------------------------------------------------- extension packages

function Save-Json($obj, $path) {
    [IO.File]::WriteAllText($path, ($obj | ConvertTo-Json -Depth 10), (New-Object System.Text.UTF8Encoding $false))
}

Write-Host "Packaging the browser extension..." -ForegroundColor Cyan
$pack = Join-Path ([IO.Path]::GetTempPath()) "swiftget-extension-pack"
if (Test-Path $pack) { Remove-Item $pack -Recurse -Force }
Copy-Item (Join-Path $root "extension") $pack -Recurse
$manifestPath = Join-Path $pack "manifest.json"

# installed copy (Chrome/Edge): keeps the "key", so the extension has the same ID on every computer
$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
$manifest.version = $Version
Save-Json $manifest (Join-Path $app "extension\manifest.json")

# store package: the stores assign their own ID and reject "key"
$manifest.PSObject.Properties.Remove("key")
Save-Json $manifest $manifestPath
$extZip = Join-Path $store "SwiftGet-Extension-$Version.zip"
New-Zip $pack $extZip


# Firefox runs the background as a script (not a service worker) and needs its own add-on ID
$manifest.background = [pscustomobject]@{ scripts = @("background.js") }
$manifest.PSObject.Properties.Remove("minimum_chrome_version")
$manifest | Add-Member -NotePropertyName browser_specific_settings -NotePropertyValue ([pscustomobject]@{
    gecko = [pscustomobject]@{ id = "swiftget@emadnajafi.github.io"; strict_min_version = "121.0" } }) -Force
Save-Json $manifest $manifestPath
$ffZip = Join-Path $store "SwiftGet-Extension-Firefox-$Version.zip"
New-Zip $pack $ffZip

$ffDir = Join-Path $app "extension-firefox"
if (Test-Path $ffDir) { Remove-Item $ffDir -Recurse -Force }
Copy-Item $pack $ffDir -Recurse
Remove-Item $pack -Recurse -Force

# ---------------------------------------------------------------- installer

$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 not found. Install it with: winget install JRSoftware.InnoSetup" }

Write-Host "Building installer..." -ForegroundColor Cyan
$setup = Join-Path $dist "SwiftGet-Setup-$Version.exe"
for ($attempt = 1; $attempt -le 3; $attempt++) {
    & $iscc "/DAppVersion=$Version" "installer\SwiftGet.iss"
    if ($LASTEXITCODE -eq 0) { break }
    # antivirus sometimes holds the new file for a moment
    if ($attempt -eq 3) { throw "Inno Setup failed" }
    Start-Sleep -Seconds 5
}

# ---------------------------------------------------------------- one zip with everything

Write-Host "Creating SwiftGet-$Version.zip..." -ForegroundColor Cyan
$bundle = Join-Path ([IO.Path]::GetTempPath()) "swiftget-bundle"
if (Test-Path $bundle) { Remove-Item $bundle -Recurse -Force }
$top = Join-Path $bundle "SwiftGet $Version"
New-Item -ItemType Directory -Force $top | Out-Null
Copy-Item $setup $top
Copy-Item (Join-Path $app "extension") (Join-Path $top "Extension - Chrome, Edge, Brave, Opera") -Recurse
Copy-Item $ffDir (Join-Path $top "Extension - Firefox") -Recurse
$readme = @"
SwiftGet $Version
=================

1. Run SwiftGet-Setup-$Version.exe to install SwiftGet.
   If Windows shows "Windows protected your PC", click "More info" and then "Run anyway".

2. Browser extension (optional, recommended):
   In SwiftGet open  Options > Browser  and click "Add to Chrome" (or Edge, Brave, Opera, Firefox).
   It uses the extension that the installer puts next to the app, which is updated together with SwiftGet.

   The "Extension - ..." folders in this zip are a copy for manual installation:
   Chrome/Edge: chrome://extensions > turn on Developer mode > drag the folder onto the page.
   Firefox: about:debugging#/runtime/this-firefox > Load Temporary Add-on > choose manifest.json.
   Keep the folder where it is afterwards: the browser loads the extension from it.

Project page: https://github.com/EmadNajafi/SwiftGet-Download-Manager


راهنمای فارسی
=============

۱. فایل SwiftGet-Setup-$Version.exe را اجرا کنید تا SwiftGet نصب شود.
   اگر ویندوز پیام «Windows protected your PC» نشان داد، روی More info و سپس Run anyway بزنید.

۲. افزونهٔ مرورگر (اختیاری، پیشنهادی):
   در SwiftGet به «تنظیمات ← مرورگر» بروید و روی «افزودن به Chrome» (یا Edge، Brave، Opera، Firefox) بزنید.
   این دکمه از افزونه‌ای استفاده می‌کند که نصب‌کننده کنار برنامه گذاشته و همراه SwiftGet به‌روز می‌شود.

   پوشه‌های «Extension - ...» در این فایل zip یک نسخهٔ اضافه برای نصب دستی هستند:
   Chrome/Edge: آدرس chrome://extensions ← روشن کردن Developer mode ← کشیدن پوشه روی صفحه.
   Firefox: آدرس about:debugging#/runtime/this-firefox ← Load Temporary Add-on ← انتخاب manifest.json.
   بعد از آن پوشه را جابه‌جا یا پاک نکنید، چون مرورگر افزونه را از همان پوشه اجرا می‌کند.
"@
[IO.File]::WriteAllText((Join-Path $top "Read me.txt"), $readme.Replace("`r`n", "`n").Replace("`n", "`r`n"), (New-Object System.Text.UTF8Encoding $true))
$zip = Join-Path $dist "SwiftGet-$Version.zip"
New-Zip $bundle $zip

Remove-Item $bundle -Recurse -Force

Write-Host "Done. Files for the release page:" -ForegroundColor Green
Get-Item $zip, $setup | ForEach-Object { "  $($_.Name)  ($([math]::Round($_.Length / 1MB, 1)) MB)" }
Write-Host "Store packages (not for the release page):" -ForegroundColor Green
Get-Item $extZip, $ffZip | ForEach-Object { "  store\$($_.Name)" }
