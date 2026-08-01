<#
.SYNOPSIS
    Builds the Magnetoskop Capture release package.

.DESCRIPTION
    Runs the test suite, publishes a self-contained win-x64 build of
    Magnetoskop.App, copies the documentation, and produces a versioned zip
    under artifacts/.

.PARAMETER Version
    Overrides the package version (default: the <Version> from Magnetoskop.App.csproj).

.PARAMETER SkipTests
    Skips the test run (not recommended for release builds).

.PARAMETER NoZip
    Leaves the publish folder unzipped.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File build/publish.ps1
    powershell -ExecutionPolicy Bypass -File build/publish.ps1 -Version 1.1.0-rc1 -SkipTests
#>
[CmdletBinding()]
param(
    [string]$Version,
    [switch]$SkipTests,
    [switch]$NoZip
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$appProject = Join-Path $repoRoot 'src/Magnetoskop.App/Magnetoskop.App.csproj'
$solution = Join-Path $repoRoot 'MagnetoskopCapture.slnx'
$artifacts = Join-Path $repoRoot 'artifacts'

if (-not $Version) {
    $csproj = [xml](Get-Content $appProject)
    $Version = ($csproj.Project.PropertyGroup.Version | Where-Object { $_ }) | Select-Object -First 1
    if (-not $Version) { $Version = '0.0.0' }
}

$publishDir = Join-Path $artifacts "MagnetoskopCapture-$Version-win-x64"
Write-Host "==> Packaging Magnetoskop Capture $Version" -ForegroundColor Cyan

if (-not $SkipTests) {
    Write-Host '==> Running tests' -ForegroundColor Cyan
    dotnet test $solution --configuration Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Test run failed; aborting the release build.' }
}

Write-Host '==> Publishing self-contained win-x64 build' -ForegroundColor Cyan
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
dotnet publish $appProject `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --output $publishDir `
    -p:Version=$Version `
    -p:PublishSingleFile=false `
    --nologo
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

Write-Host '==> Copying documentation' -ForegroundColor Cyan
$docsOut = Join-Path $publishDir 'docs'
New-Item -ItemType Directory -Path $docsOut -Force | Out-Null
Copy-Item (Join-Path $repoRoot 'README.md') $publishDir
Copy-Item (Join-Path $repoRoot 'docs/USER_GUIDE.md') $docsOut
Copy-Item (Join-Path $repoRoot 'docs/HARDWARE_TESTING.md') $docsOut
Copy-Item (Join-Path $repoRoot 'docs/ARCHITECTURE.md') $docsOut

# FFmpeg is not redistributed with the package; the app finds it on PATH, next to
# the exe, or in an ffmpeg/ subfolder. Leave a note where to put it.
@"
Place ffmpeg.exe in this folder (or on PATH) to enable recording.
Use a full/GPL build: https://www.gyan.dev/ffmpeg/builds/ or https://ffmpeg.org/download.html
It must include the libx264 and prores_ks encoders.
"@ | Set-Content (Join-Path $publishDir 'PUT_FFMPEG_HERE.txt')

if (-not $NoZip) {
    Write-Host '==> Creating zip' -ForegroundColor Cyan
    $zipPath = "$publishDir.zip"
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Compress-Archive -Path "$publishDir\*" -DestinationPath $zipPath
    Write-Host "==> Done: $zipPath" -ForegroundColor Green
}
else {
    Write-Host "==> Done: $publishDir" -ForegroundColor Green
}
