$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$binDir = Join-Path $root "bin"
$adbDir = Join-Path $binDir "adb"
$scrcpyDir = Join-Path $binDir "scrcpy"

Write-Host "==> Ensuring directories exist..." -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $adbDir | Out-Null
New-Item -ItemType Directory -Force -Path $scrcpyDir | Out-Null

# 1. Download Android Platform Tools (adb)
$adbExe = Join-Path $adbDir "adb.exe"
if (-not (Test-Path $adbExe)) {
    Write-Host "==> Downloading official Android Platform Tools (Google)..." -ForegroundColor Cyan
    $platformToolsZip = Join-Path $env:TEMP "platform-tools-windows.zip"
    $platformToolsUrl = "https://dl.google.com/android/repository/platform-tools-latest-windows.zip"

    Invoke-WebRequest -Uri $platformToolsUrl -OutFile $platformToolsZip
    Write-Host "==> Extracting platform-tools..." -ForegroundColor Cyan
    $extractTemp = Join-Path $env:TEMP "platform-tools-temp"
    if (Test-Path $extractTemp) { Remove-Item -Recurse -Force $extractTemp }
    Expand-Archive -Path $platformToolsZip -DestinationPath $extractTemp -Force
    
    Copy-Item -Path "$extractTemp\platform-tools\*" -Destination $adbDir -Recurse -Force
    Remove-Item -Force $platformToolsZip
    Remove-Item -Recurse -Force $extractTemp
    Write-Host "==> ADB installed to $adbDir" -ForegroundColor Green
} else {
    Write-Host "==> ADB already exists at $adbExe" -ForegroundColor Yellow
}

# 2. Download official scrcpy-server.jar
$scrcpyJar = Join-Path $scrcpyDir "scrcpy-server.jar"
if (-not (Test-Path $scrcpyJar)) {
    Write-Host "==> Downloading scrcpy-server.jar (v2.4)..." -ForegroundColor Cyan
    $scrcpyUrl = "https://github.com/Genymobile/scrcpy/releases/download/v2.4/scrcpy-server-v2.4"
    Invoke-WebRequest -Uri $scrcpyUrl -OutFile $scrcpyJar
    Write-Host "==> scrcpy-server.jar downloaded to $scrcpyDir" -ForegroundColor Green
} else {
    Write-Host "==> scrcpy-server.jar already exists at $scrcpyJar" -ForegroundColor Yellow
}

Write-Host "`n[SUCCESS] Setup completed! JRemote is ready to run." -ForegroundColor Green
