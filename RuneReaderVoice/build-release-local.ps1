# build-release-local.ps1
# RuneReader Voice -- Local/Staging Release Build
#
# Builds installer packages pointing to the personal file server for updates,
# then optionally uploads them via SCP/SFTP.
#
# Usage:
#   .\build-release-local.ps1                  # build only
#   .\build-release-local.ps1 -Upload          # build + upload via SCP
#   .\build-release-local.ps1 -Full -Upload    # full installer only + upload
#   .\build-release-local.ps1 -SkipSign        # skip signing (default for local)
#
# The app built by this script checks https://www.mkfam.com/filedump/ for updates
# instead of GitHub releases.
#  .\build-release-local.ps1 -Version "1.5.4" -SkipSign -Upload

[CmdletBinding()]
param(
    [switch]$Full,
    [switch]$Slim,
    [switch]$Upload,
    [switch]$SkipSign,
    [string]$Version          = "1.5.1.1",
    [string]$UpdateFeedUrl    = "https://www.mkfam.com/filedump/",
    [string]$LocalCopyPath    = "X:\webhost\filedump",
    [string]$ScpTarget        = "",   # e.g. "user@mkfam.com:/var/www/filedump/"
    [string]$AppId            = "RuneReaderVoice",
    [string]$AppFriendlyName  = "RuneReader Voice (Test)",
    [string]$CertSubject      = "Michael Sutton",
    [string]$PfxPath          = "",
    [string]$PfxPassword      = "",
    [string]$ScpUser = "",
    [string]$ScpPassword = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if (-not $Full -and -not $Slim) {
    $Full = $true
    $Slim = $true
}

$ProjectFile  = Join-Path $PSScriptRoot "RuneReaderVoice.csproj"
$PublishBase  = Join-Path $PSScriptRoot "bin\publish-local"
$ReleaseBase  = Join-Path $PSScriptRoot "bin\release-local"
$ModelPath    = Join-Path $PSScriptRoot "Models\kokoro-quant.onnx"

function Find-SignTool {
    $sdkBin = "C:\Program Files (x86)\Windows Kits\10\bin"
    if (Test-Path $sdkBin) {
        $found = Get-ChildItem $sdkBin -Directory | Sort-Object Name -Descending |
                 ForEach-Object { Join-Path $_.FullName "x64\signtool.exe" } |
                 Where-Object { Test-Path $_ } |
                 Select-Object -First 1
        if ($found) { return $found }
    }
    return $null
}

function Sign-File([string]$FilePath) {
    if ($SkipSign) {
        Write-Host "  [SKIP SIGN] $FilePath" -ForegroundColor Yellow
        return
    }
    $st = Find-SignTool
    if (-not $st) { Write-Warning "signtool not found -- skipping"; return }
    Write-Host "  Signing: $FilePath" -ForegroundColor Cyan
    if ($PfxPath -and (Test-Path $PfxPath)) {
        $args = @("sign", "/fd", "sha256", "/tr", "http://ts.ssl.com", "/td", "sha256",
                  "/f", $PfxPath, "/p", $PfxPassword, "/d", $AppFriendlyName, $FilePath)
    } else {
        $args = @("sign", "/fd", "sha256", "/tr", "http://ts.ssl.com", "/td", "sha256",
                  "/n", $CertSubject, "/d", $AppFriendlyName, $FilePath)
    }
    & $st @args
    if ($LASTEXITCODE -ne 0) { throw "signtool failed for: $FilePath" }
}

function Invoke-Publish([string]$OutputDir, [bool]$IncludeModel) {
    Write-Host ""
    Write-Host "Publishing to $OutputDir (model=$IncludeModel)..." -ForegroundColor Green
    if (Test-Path $OutputDir) { Remove-Item $OutputDir -Recurse -Force }

    $publishArgs = @(
        "publish", $ProjectFile,
        "--configuration", "Release",
        "--runtime", "win-x64",
        "--self-contained", "true",
        "--output", $OutputDir,
        "-p:PublishSingleFile=false",
        "-p:PublishTrimmed=false",
        "-p:PublishReadyToRun=false",
        "-p:DebugType=none",
        "-p:DebugSymbols=false",
        "-p:Version=$Version",
        "-p:AssemblyVersion=$Version.0",
        "-p:FileVersion=$Version.0",
        "-p:UpdateFeedUrl=$UpdateFeedUrl"
    )
    if (-not $IncludeModel) { $publishArgs += "-p:ExcludeKokoroModel=true" }

    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

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
        "--channel",     $Channel,
        "--exclude", ".*\.(so(\.\d+)*|dylib)$"
    )
    & vpk @vpkArgs
    if ($LASTEXITCODE -ne 0) { throw "vpk pack failed" }

    $setup = Get-ChildItem $ReleaseDir -Filter "*Setup*.exe" | Select-Object -First 1
    if ($setup) {
        Sign-File $setup.FullName
        $newName = "$AppId-$Version-$Channel-local-Setup.exe"
        Rename-Item $setup.FullName (Join-Path $ReleaseDir $newName)
        Write-Host "  Installer: $newName" -ForegroundColor Green
    }
}

function Invoke-Upload([string]$ReleaseDir, [string]$Channel) {
    if (-not $Upload) { return }

    if ($LocalCopyPath) {
        if (-not (Test-Path $LocalCopyPath)) {
            Write-Warning "LocalCopyPath '$LocalCopyPath' not found -- is the drive mapped?"
            return
        }

        # Archive any existing versioned files that don't match current version
        $archiveDir = Join-Path $LocalCopyPath "archive"
        New-Item -ItemType Directory -Path $archiveDir -Force | Out-Null
#         Get-ChildItem $LocalCopyPath -File | Where-Object {
#             $_.Name -notlike "*-$Version-*" -and
#             $_.Name -match "\d+\.\d+\.\d+"
#         } | ForEach-Object {
#             attrib -R $_.FullName
#             Move-Item $_.FullName -Destination (Join-Path $archiveDir $_.Name) -Force
#             Write-Host "  Archived: $($_.Name)" -ForegroundColor DarkGray
#         }

Get-ChildItem $ReleaseDir -File | ForEach-Object {
    $dest = Join-Path $LocalCopyPath $_.Name

    if (Test-Path $dest) {
        $item = Get-Item $dest -Force
        if ($item.IsReadOnly) {
            $item.IsReadOnly = $false
        }
        Remove-Item $dest -Force
    }

    Copy-Item $_.FullName -Destination $LocalCopyPath -Force
    Write-Host "  Copied: $($_.Name)"
}

        Write-Host "  Done." -ForegroundColor Green
        return
    }
    
    # SCP fallback
  # SFTP fallback via WinSCP .NET
      # SCP fallback
      if ($ScpTarget) {
          if (-not $ScpUser -or -not $ScpPassword) {
              Write-Warning "ScpUser and ScpPassword required for SFTP upload"
              return
          }
          Write-Host ""
          Write-Host "Uploading $Channel release via SFTP to $ScpTarget..." -ForegroundColor Cyan
          $scpHost = $ScpTarget.Split(':')[0]
          $scpPath = $ScpTarget.Split(':')[1]
          Get-ChildItem $ReleaseDir | Select-Object -ExpandProperty FullName | ForEach-Object {
              $fname = [System.IO.Path]::GetFileName($_)
              $cmd = "put `"$_`" `"$scpPath/$fname`""
              echo $cmd | & psftp $scpHost -P 21 -pw $ScpPassword -l "$ScpUser"
              #@
              if ($LASTEXITCODE -ne 0) { Write-Warning "SFTP failed for: $_" }
          }
          return
      }
}

# ── Main ──────────────────────────────────────────────────────────────────────

Write-Host "RuneReader Voice LOCAL Build v$Version" -ForegroundColor Magenta
Write-Host "  Update feed: $UpdateFeedUrl"
Write-Host "  Full: $Full  |  Slim: $Slim  |  SkipSign: $SkipSign  |  Upload: $Upload"

if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) {
    Write-Error "vpk not found. Install with: dotnet tool install -g vpk"
    exit 1
}

if ($Full) {
    if (-not (Test-Path $ModelPath)) {
        Write-Error "Kokoro model not found at: $ModelPath"
        exit 1
    }
    $fullPublish = Join-Path $PublishBase "full"
    $fullRelease = Join-Path $ReleaseBase "full"
    Invoke-Publish $fullPublish $true
    Invoke-VpkPack $fullPublish $fullRelease "full"
    Invoke-Upload  $fullRelease "full"
}

if ($Slim) {
    $slimPublish = Join-Path $PublishBase "slim"
    $slimRelease = Join-Path $ReleaseBase "slim"
    Invoke-Publish $slimPublish $false
    Invoke-VpkPack $slimPublish $slimRelease "slim"
    Invoke-Upload  $slimRelease "slim"
}

Write-Host ""
Write-Host "Local build complete." -ForegroundColor Green
if ($Full) { Write-Host "  Full: bin\release-local\full\" }
if ($Slim) { Write-Host "  Slim: bin\release-local\slim\" }
Write-Host ""
if (-not $Upload) {
    Write-Host "To deploy, run with -Upload:" -ForegroundColor Cyan
    Write-Host "  .\build-release-local.ps1 -SkipSign -Upload"
    Write-Host ""
    Write-Host "Files will be copied to: $LocalCopyPath"
}
Write-Host "Update feed URL baked into this build: $UpdateFeedUrl" -ForegroundColor Gray
Write-Host "RELEASES + *.nupkg must be accessible at that URL for updates to work." -ForegroundColor Gray
