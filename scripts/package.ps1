param (
    [string]$Version = "1.0.0",
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"

$rootDir = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $rootDir "src\JRemote.UI\JRemote.UI.csproj"
$publishDir = Join-Path $rootDir "publish\JRemote"
$releaseDir = Join-Path $rootDir "release"
$zipPath = Join-Path $releaseDir "JRemote-v$Version-$Runtime.zip"

Write-Host "=========================================" -ForegroundColor Cyan
Write-Host "  JRemote Packaging Tool (v$Version - $Runtime)" -ForegroundColor Cyan
Write-Host "=========================================`n" -ForegroundColor Cyan

# 1. Ensure external tools are downloaded
Write-Host "==> Checking external binaries (adb & scrcpy)..." -ForegroundColor Yellow
& "$PSScriptRoot\download-tools.ps1"

# 2. Clean previous publish folder
if (Test-Path $publishDir) {
    Write-Host "==> Cleaning previous publish directory..." -ForegroundColor Yellow
    Remove-Item -Recurse -Force $publishDir
}
New-Item -ItemType Directory -Force -Path $publishDir | Out-Null
New-Item -ItemType Directory -Force -Path $releaseDir | Out-Null

# 3. Publish .NET 8 WPF Application (Self-Contained)
Write-Host "==> Publishing JRemote.UI (Self-Contained $Runtime)..." -ForegroundColor Cyan
dotnet publish $projectPath `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    /p:PublishSingleFile=false `
    /p:Version=$Version `
    /p:AssemblyVersion=$Version.0 `
    /p:FileVersion=$Version.0 `
    -o $publishDir

if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet publish failed with exit code $LASTEXITCODE"
}

# 4. Copy external binaries into the publish folder
Write-Host "==> Bundling adb and scrcpy into publish payload..." -ForegroundColor Cyan
$sourceBinDir = Join-Path $rootDir "bin"
$targetBinDir = Join-Path $publishDir "bin"
New-Item -ItemType Directory -Force -Path $targetBinDir | Out-Null
Copy-Item -Path "$sourceBinDir\*" -Destination $targetBinDir -Recurse -Force

# 5. Copy README and License/Notes
Copy-Item -Path (Join-Path $rootDir "README.md") -Destination $publishDir -Force

# 6. Compress into ZIP package
if (Test-Path $zipPath) {
    Remove-Item -Force $zipPath
}
Write-Host "==> Creating ZIP archive: $zipPath..." -ForegroundColor Cyan
Compress-Archive -Path "$publishDir\*" -DestinationPath $zipPath -CompressionLevel Optimal

$zipItem = Get-Item $zipPath
$zipSizeMb = [math]::Round($zipItem.Length / 1MB, 2)

Write-Host "`n[SUCCESS] Package created successfully!" -ForegroundColor Green
Write-Host "  File: $zipPath" -ForegroundColor Green
Write-Host "  Size: $zipSizeMb MB" -ForegroundColor Green
Write-Host "  Ready for GitHub Release!" -ForegroundColor Green
