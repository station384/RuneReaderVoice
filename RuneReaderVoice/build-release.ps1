# build-release.ps1
# RuneReader Voice — Release Build Pipeline
#
# Usage:
#   .\build-release.ps1                      # full installer (with Kokoro model)
#   .\build-release.ps1 -Slim                # slim installer (no model)
#   .\build-release.ps1 -Full -Slim          # both
#   .\build-release.ps1 -SkipSign            # skip code signing (testing only)
#
# Requirements:
#   - .NET 8 SDK
#   - Velopack CLI:  dotnet tool install -g vpk  (keep in sync with NuGet package version)
#   - Windows SDK (for signtool.exe) — or set $SignToolPath manually
#   - OV code signing certificate installed in Windows cert store
#     (or set $PfxPath + $PfxPassword for file-based signing)
#
# Output:
#   release\full\   — full installer assets
#   release\slim\   — slim installer assets

[CmdletBinding()]
param(
    [switch]$Full,
    [switch]$Slim,
    [switch]$SkipSign,
    [string]$Version         = "1.5.0",
    [string]$CertSubject     = "Michael Sutton",        # CN in your OV cert
    [string]$PfxPath         = "",                      # path to .pfx (if not store-based)
    [string]$PfxPassword     = "",
    [string]$SignToolPath    = "",                       # leave blank to auto-detect
    [string]$GitHubOwner     = "station384",
    [string]$GitHubRepo      = "RuneReaderVoice",
    [string]$AppId           = "RuneReaderVoice",
    [string]$AppFriendlyName = "RuneReader Voice",
    [string]$DefaultInstallDir = "C:\RuneReaderVoice"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# ── Defaults ─────────────────────────────────────────────────────────────────

# If neither -Full nor -Slim specified, build both
if (-not $Full -and -not $Slim) {
    $Full = $true
    $Slim = $true
}

$ProjectFile = Join-Path $PSScriptRoot "RuneReaderVoice.csproj"
$PublishBase  = Join-Path $PSScriptRoot "publish"
$ReleaseBase  = Join-Path $PSScriptRoot "release"
$ModelPath    = Join-Path $PSScriptRoot "Models\kokoro-quant.onnx"

# ── Locate signtool ───────────────────────────────────────────────────────────

function Find-SignTool {
    if ($SignToolPath -and (Test-Path $SignToolPath)) { return $SignToolPath }

    $candidates = @(
        "C:\Program Files (x86)\Windows Kits\10\bin\10.0.22621.0\x64\signtool.exe",
        "C:\Program Files (x86)\Windows Kits\10\bin\10.0.19041.0\x64\signtool.exe"
    )
    # Also search installed SDK versions
    $sdkBin = "C:\Program Files (x86)\Windows Kits\10\bin"
    if (Test-Path $sdkBin) {
        Get-ChildItem $sdkBin -Directory | Sort-Object Name -Descending | ForEach-Object {
            $candidate = Join-Path $_.FullName "x64\signtool.exe"
            if (Test-Path $candidate) { $candidates = @($candidate) + $candidates }
        }
    }
    foreach ($c in $candidates) {
        if (Test-Path $c) { return $c }
    }
    return $null
}

function Sign-File([string]$FilePath) {
    if ($SkipSign) {
        Write-Host "  [SKIP SIGN] $FilePath" -ForegroundColor Yellow
        return
    }

    $st = Find-SignTool
    if (-not $st) {
        Write-Warning "signtool.exe not found -- skipping signing for $FilePath"
        return
    }

    Write-Host "  Signing: $FilePath" -ForegroundColor Cyan

    if ($PfxPath -and (Test-Path $PfxPath)) {
        $signArgs = @("sign", "/fd", "sha256", "/tr", "http://ts.ssl.com", "/td", "sha256",
                      "/f", $PfxPath, "/p", $PfxPassword, "/d", $AppFriendlyName, $FilePath)
    } else {
        $signArgs = @("sign", "/fd", "sha256", "/tr", "http://ts.ssl.com", "/td", "sha256",
                      "/n", $CertSubject, "/d", $AppFriendlyName, $FilePath)
    }

    & $st @signArgs

    if ($LASTEXITCODE -ne 0) {
        throw "signtool failed with exit code $LASTEXITCODE for: $FilePath"
    }
}

# ── Build functions ───────────────────────────────────────────────────────────

function Invoke-Publish([string]$OutputDir, [bool]$IncludeModel) {
    Write-Host ""
    Write-Host "Publishing to $OutputDir (model=$IncludeModel)..." -ForegroundColor Green

    # Remove old output
    if (Test-Path $OutputDir) { Remove-Item $OutputDir -Recurse -Force }

    $publishArgs = @(
        "publish",
        $ProjectFile,
        "--configuration", "Release",
        "--runtime", "win-x64",
        "--self-contained", "true",
        "--output", $OutputDir,
        "-p:PublishSingleFile=false",       # Velopack needs loose files, not single-file
        "-p:PublishTrimmed=false",           # trimming + reflection-heavy libs = pain
        "-p:DebugType=none",
        "-p:DebugSymbols=false",
        "-p:Version=$Version",
        "-p:AssemblyVersion=$Version.0",
        "-p:FileVersion=$Version.0"
    )

    # Exclude model from slim build at publish time by overriding the Content item
    if (-not $IncludeModel) {
        $publishArgs += "-p:ExcludeKokoroModel=true"
    }

    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

    # Sign the main exe
    $exe = Join-Path $OutputDir "RuneReaderVoice.exe"
    if (Test-Path $exe) { Sign-File $exe }
}

function Invoke-VpkPack([string]$PublishDir, [string]$ReleaseDir, [string]$Channel) {
    Write-Host ""
    Write-Host "Packaging with vpk (channel=$Channel)..." -ForegroundColor Green

    if (Test-Path $ReleaseDir) { Remove-Item $ReleaseDir -Recurse -Force }
    New-Item -ItemType Directory -Path $ReleaseDir | Out-Null

    $vpkArgs = @(
        "pack",
        "--packId",      $AppId,
        "--packVersion", $Version,
        "--packDir",     $PublishDir,
        "--outputDir",   $ReleaseDir,
        "--mainExe",     "RuneReaderVoice.exe",
        "--packTitle",   $AppFriendlyName,
        "--channel",     $Channel
    )

    & vpk @vpkArgs
    if ($LASTEXITCODE -ne 0) { throw "vpk pack failed" }

    # Sign Setup.exe produced by vpk
    $setup = Get-ChildItem $ReleaseDir -Filter "*Setup*.exe" | Select-Object -First 1
    if ($setup) {
        Sign-File $setup.FullName
        # Rename to include channel in filename for clarity on GitHub releases
        $newName = "$AppId-$Version-$Channel-Setup.exe"
        Rename-Item $setup.FullName (Join-Path $ReleaseDir $newName)
        Write-Host "  Installer: $newName" -ForegroundColor Green
    }
}

# ── Main ──────────────────────────────────────────────────────────────────────

Write-Host "RuneReader Voice Release Build v$Version" -ForegroundColor Magenta
Write-Host "  Full: $Full  |  Slim: $Slim  |  SkipSign: $SkipSign"
Write-Host ""

# Verify vpk is available
if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) {
    Write-Error "vpk not found. Install with: dotnet tool install -g vpk"
    exit 1
}

if ($Full) {
    if (-not (Test-Path $ModelPath)) {
        Write-Error "Kokoro model not found at: $ModelPath`nPlace kokoro-quant.onnx in the Models\ directory."
        exit 1
    }
    $fullPublish = Join-Path $PublishBase "full"
    $fullRelease = Join-Path $ReleaseBase "full"
    Invoke-Publish $fullPublish $true
    Invoke-VpkPack $fullPublish $fullRelease "full"
}

if ($Slim) {
    $slimPublish = Join-Path $PublishBase "slim"
    $slimRelease = Join-Path $ReleaseBase "slim"
    Invoke-Publish $slimPublish $false
    Invoke-VpkPack $slimPublish $slimRelease "slim"
}

Write-Host ""
Write-Host "Build complete." -ForegroundColor Green
if ($Full) { Write-Host "  Full installer: release\full\" }
if ($Slim) { Write-Host "  Slim installer: release\slim\" }
Write-Host ""
Write-Host "Upload to GitHub Release:" -ForegroundColor Cyan
Write-Host "  - release\full\$AppId-$Version-full-Setup.exe"
Write-Host "  - release\full\*.nupkg   (delta packages)"
Write-Host "  - release\slim\$AppId-$Version-slim-Setup.exe"
Write-Host "  - release\slim\*.nupkg"
Write-Host ""
Write-Host "Velopack update feed expects RELEASES file + nupkg assets in the release."
