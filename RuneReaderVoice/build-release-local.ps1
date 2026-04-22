# build-release-local.ps1
# RuneReader Voice -- Local/Staging Release Build
#
# Builds installer packages pointing to the personal file server for updates.
# Supports automatic delta package generation via persistent local feed directories.
#
# Directory layout:
#   bin\publish-local\{full|slim}\   -- dotnet publish output (wiped each build)
#   bin\feed\{full|slim}\            -- PERSISTENT vpk feed (accumulates releases)
#                                       vpk reads previous nupkg from here to
#                                       generate deltas, then writes new packages
#                                       back here. Upload from here directly.
#
# vpk output per channel (in bin\feed\{channel}\):
#   RuneReaderVoice-{ver}-{channel}.nupkg          full package
#   RuneReaderVoice-{ver}-{channel}-delta.nupkg    delta from previous version
#   RuneReaderVoice-{ver}-{channel}-Setup.exe      full installer (renamed below)
#   releases.{channel}.json                        update manifest (keep deployed)
#   RELEASES                                       legacy manifest
#
# Usage:
#   .\build-release-local.ps1                      # build only (both channels)
#   .\build-release-local.ps1 -Upload              # build + upload
#   .\build-release-local.ps1 -Full -Upload        # full channel only + upload
#   .\build-release-local.ps1 -Slim -Upload        # slim channel only + upload
#   .\build-release-local.ps1 -Version "1.5.16" -SkipSign -Upload

[CmdletBinding()]
param(
    [switch]$Full,
    [switch]$Slim,
    [switch]$Upload,
    [switch]$SkipSign,
    [string]$Version          = "1.5.1.1",
    [string]$FeedBaseUrl      = "https://www.mkfam.com/filedump/",  # channel subdir appended automatically
    [string]$LocalCopyPath    = "X:\webhost\filedump",
    [string]$ScpTarget        = "",        # e.g. "192.168.45.13:/path/to/filedump"
    [string]$ScpUser          = "",
    [string]$ScpPassword      = "",
    [string]$AppId            = "RuneReaderVoice",
    [string]$AppFriendlyName  = "RuneReader Voice",
    [string]$CertSubject      = "Michael Sutton",
    [string]$PfxPath          = "",
    [string]$PfxPassword      = "",
    # Maximum number of old release versions to keep in the feed.
    # Keep at least 2 so users one version behind can still delta-update.
    [int]$KeepMaxReleases     = 3
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if (-not $Full -and -not $Slim) {
    $Full = $true
    $Slim = $true
}

$ProjectFile  = Join-Path $PSScriptRoot "RuneReaderVoice.csproj"
$PublishBase  = Join-Path $PSScriptRoot "bin\publish-local"
$FeedBase     = Join-Path $PSScriptRoot "bin\feed"
$ModelPath    = Join-Path $PSScriptRoot "Models\kokoro-quant.onnx"

# ── Code signing ──────────────────────────────────────────────────────────────

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
        $signArgs = @("sign", "/fd", "sha256", "/tr", "http://ts.ssl.com", "/td", "sha256",
                      "/f", $PfxPath, "/p", $PfxPassword, "/d", $AppFriendlyName, $FilePath)
    } else {
        $signArgs = @("sign", "/fd", "sha256", "/tr", "http://ts.ssl.com", "/td", "sha256",
                      "/n", $CertSubject, "/d", $AppFriendlyName, $FilePath)
    }
    & $st @signArgs
    if ($LASTEXITCODE -ne 0) { throw "signtool failed for: $FilePath" }
}

# ── dotnet publish ─────────────────────────────────────────────────────────────

function Invoke-Publish([string]$OutputDir, [bool]$IncludeModel, [string]$Channel) {
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
        "-p:UpdateFeedUrl=$($FeedBaseUrl.TrimEnd('/') + '/' + $Channel + '/')"
    )
    if (-not $IncludeModel) { $publishArgs += "-p:ExcludeKokoroModel=true" }

    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

    $exe = Join-Path $OutputDir "RuneReaderVoice.exe"
    if (Test-Path $exe) { Sign-File $exe }
}

# ── vpk pack ──────────────────────────────────────────────────────────────────

function Invoke-VpkPack([string]$PublishDir, [string]$FeedDir, [string]$Channel) {
    Write-Host ""
    Write-Host "Packaging with vpk (channel=$Channel)..." -ForegroundColor Green

    # Feed dir is PERSISTENT -- do NOT wipe it.
    # vpk reads any existing nupkg here to generate a delta, then writes new
    # packages alongside them.
    New-Item -ItemType Directory -Path $FeedDir -Force | Out-Null

    $prevPkgs = Get-ChildItem $FeedDir -Filter "*.nupkg" -ErrorAction SilentlyContinue
    if ($prevPkgs) {
        Write-Host "  Previous packages found -- delta will be generated:" -ForegroundColor Cyan
        $prevPkgs | ForEach-Object { Write-Host "    $($_.Name)" -ForegroundColor DarkGray }
    } else {
        Write-Host "  No previous packages -- first release, no delta." -ForegroundColor Yellow
    }

    $vpkArgs = @(
        "pack",
        "--packId",      $AppId,
        "--packVersion", $Version,
        "--packDir",     $PublishDir,
        "--outputDir",   $FeedDir,
        "--mainExe",     "RuneReaderVoice.exe",
        "--packTitle",   $AppFriendlyName,
        "--channel",     $Channel,
        "--delta",       "BestSpeed",
        "--exclude",     ".*\.(so(\.\d+)*|dylib)$"
    )
    & vpk "-y" @vpkArgs
    if ($LASTEXITCODE -ne 0) { throw "vpk pack failed" }

    # Rename the Setup installer to include version and channel
    $setup = Get-ChildItem $FeedDir -Filter "*Setup*.exe" |
             Where-Object { $_.Name -notlike "*-$Version-*" } |
             Select-Object -First 1
    if ($setup) {
        Sign-File $setup.FullName
        $newName = "$AppId-$Version-$Channel-Setup.exe"
        $newPath = Join-Path $FeedDir $newName
        if (Test-Path $newPath) { Remove-Item $newPath -Force }
        Rename-Item $setup.FullName $newPath
        Write-Host "  Installer: $newName" -ForegroundColor Green
    }

    # Report feed contents
    Write-Host "  Feed contents after pack:" -ForegroundColor Cyan
    Get-ChildItem $FeedDir | Sort-Object Name | ForEach-Object {
        $tag = if ($_.Name -like "*delta*")    { " [delta]"     }
               elseif ($_.Name -like "*.nupkg") { " [full pkg]"  }
               elseif ($_.Name -like "*Setup*") { " [installer]" }
               elseif ($_.Name -like "*.json")  { " [manifest]"  }
               else                             { "" }
        Write-Host "    $($_.Name)$tag" -ForegroundColor DarkGray
    }
}

# ── Prune old releases ────────────────────────────────────────────────────────

function Invoke-PruneFeed([string]$FeedDir, [string]$Channel) {
    Write-Host ""
    Write-Host "Pruning $Channel feed to last $KeepMaxReleases releases..." -ForegroundColor Green

    # vpk upload local is designed to sync to a remote — using it for in-place
    # pruning causes file-not-found errors when the manifest references the
    # pre-rename Setup filename. Instead: prune nupkgs directly in PowerShell
    # (keeping the N most recent full packages), then let vpk pack regenerate
    # the manifest on the next build naturally.
    #
    # Find all full nupkgs (not delta), sort by version descending, delete older ones.
    $fullPkgs = @(Get-ChildItem $FeedDir -Filter "*-full.nupkg" -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -notlike "*delta*" } |
                Sort-Object Name -Descending)

    if ($fullPkgs.Count -gt $KeepMaxReleases) {
        $toDelete = $fullPkgs | Select-Object -Skip $KeepMaxReleases
        foreach ($pkg in $toDelete) {
            # Also delete any delta that corresponds to this version
            $ver = if ($pkg.Name -match '(\d+\.\d+[\d.]*)') { $matches[1] } else { $null }
            Remove-Item $pkg.FullName -Force
            Write-Host "  Pruned: $($pkg.Name)" -ForegroundColor DarkGray
            if ($ver) {
                $delta = Join-Path $FeedDir "$($pkg.BaseName -replace '-full$', '')-delta.nupkg"
                if (Test-Path $delta) {
                    Remove-Item $delta -Force
                    Write-Host "  Pruned: $(Split-Path $delta -Leaf)" -ForegroundColor DarkGray
                }
            }
        }
    } else {
        Write-Host "  Nothing to prune ($($fullPkgs.Count) release(s) present)." -ForegroundColor DarkGray
    }
}

# ── Generate download page ─────────────────────────────────────────────────────

function Invoke-GenerateDownloadPage {
    Write-Host ""
    Write-Host "Generating download page..." -ForegroundColor Green

    $templatePath = Join-Path $PSScriptRoot "index.template.html"
    $iconPath     = Join-Path $PSScriptRoot "Assets\RuneReaderVoice_512x512.png"

    if (-not (Test-Path $templatePath)) {
        Write-Warning "index.template.html not found -- skipping page generation"
        return
    }

    $year = (Get-Date).Year.ToString()
    $html = Get-Content $templatePath -Raw
    $html = $html -replace '<!--VERSION-->', $Version
    $html = $html -replace '<!--YEAR-->',    $year

    # index.html lives at the feed root — links point into full/ and slim/ subdirs
    New-Item -ItemType Directory -Path $FeedBase -Force | Out-Null
    $outPath = Join-Path $FeedBase "index.html"
    Set-Content -Path $outPath -Value $html -Encoding UTF8
    Write-Host "  Written: $outPath"

    if (Test-Path $iconPath) {
        Copy-Item $iconPath -Destination $FeedBase -Force
        Write-Host "  Copied icon to: $FeedBase"
    }
}

# ── Upload root files (index.html + icon → filedump root) ───────────────────────

function Invoke-UploadRoot {
    if (-not $Upload) { return }

    $rootFiles = @(
        (Join-Path $FeedBase "index.html"),
        (Join-Path $PSScriptRoot "Assets\RuneReaderVoice_512x512.png")
    )

    if ($LocalCopyPath) {
        if (-not (Test-Path $LocalCopyPath)) { return }
        Write-Host ""
        Write-Host "Copying root files to $LocalCopyPath..." -ForegroundColor Cyan
        foreach ($f in $rootFiles) {
            if (Test-Path $f) {
                $dest = Join-Path $LocalCopyPath (Split-Path $f -Leaf)
                if (Test-Path $dest) {
                    $item = Get-Item $dest -Force
                    if ($item.IsReadOnly) { $item.IsReadOnly = $false }
                    Remove-Item $dest -Force
                }
                Copy-Item $f -Destination $LocalCopyPath -Force
                Write-Host "  Copied: $(Split-Path $f -Leaf)"
            }
        }
        return
    }

    if ($ScpTarget) {
        if (-not $ScpUser -or -not $ScpPassword) { return }
        $colonIdx = $ScpTarget.IndexOf(':')
        if ($colonIdx -lt 0) { return }
        $scpHost = $ScpTarget.Substring(0, $colonIdx)
        $scpPath = $ScpTarget.Substring($colonIdx + 1).TrimEnd('/')
        $batchLines = @()
        foreach ($f in $rootFiles) {
            if (Test-Path $f) {
                $batchLines += "put `"$f`" `"$scpPath/$(Split-Path $f -Leaf)`""
            }
        }
        $batchLines += "exit"
        $batchLines -join "`n" | & psftp $scpHost -P 22 -pw $ScpPassword -l $ScpUser -batch
    }
}

# ── Upload ─────────────────────────────────────────────────────────────────────

function Invoke-Upload([string]$FeedDir, [string]$Channel) {
    if (-not $Upload) { return }

    # Local network copy (mapped drive) -- mirrors feed dir into a channel subdir
    if ($LocalCopyPath) {
        if (-not (Test-Path $LocalCopyPath)) {
            Write-Warning "LocalCopyPath '$LocalCopyPath' not found -- is the drive mapped?"
            return
        }
        $destChannel = Join-Path $LocalCopyPath $Channel
        New-Item -ItemType Directory -Path $destChannel -Force | Out-Null

        Write-Host ""
        Write-Host "Copying $Channel feed to $destChannel..." -ForegroundColor Cyan
        Get-ChildItem $FeedDir -File | ForEach-Object {
            $dest = Join-Path $destChannel $_.Name
            if (Test-Path $dest) {
                $item = Get-Item $dest -Force
                if ($item.IsReadOnly) { $item.IsReadOnly = $false }
                Remove-Item $dest -Force
            }
            Copy-Item $_.FullName -Destination $destChannel -Force
            Write-Host "  Copied: $($_.Name)"
        }
        Write-Host "  Done." -ForegroundColor Green
        return
    }

    # SFTP fallback via psftp
    if ($ScpTarget) {
        if (-not $ScpUser -or -not $ScpPassword) {
            Write-Warning "ScpUser and ScpPassword required for SFTP upload"
            return
        }
        $colonIdx = $ScpTarget.IndexOf(':')
        if ($colonIdx -lt 0) {
            Write-Warning "ScpTarget must be in format host:/remote/path"
            return
        }
        $scpHost = $ScpTarget.Substring(0, $colonIdx)
        $scpPath = $ScpTarget.Substring($colonIdx + 1).TrimEnd('/') + "/$Channel"

        Write-Host ""
        Write-Host "Uploading $Channel feed via SFTP to ${scpHost}:${scpPath}..." -ForegroundColor Cyan

        $batchLines = @("mkdir `"$scpPath`"")
        Get-ChildItem $FeedDir -File | ForEach-Object {
            $batchLines += "put `"$($_.FullName)`" `"$scpPath/$($_.Name)`""
        }
        $batchLines += "exit"
        $batchLines -join "`n" | & psftp $scpHost -P 22 -pw $ScpPassword -l $ScpUser -batch
        if ($LASTEXITCODE -ne 0) { Write-Warning "psftp reported errors for $Channel" }
        return
    }
}

# ── Main ───────────────────────────────────────────────────────────────────────

Write-Host ""
Write-Host "RuneReader Voice LOCAL Build v$Version" -ForegroundColor Magenta
Write-Host "  Update feed     : $FeedBaseUrl{full|slim}/"
Write-Host "  Local feed dir  : $FeedBase"
Write-Host "  Full: $Full  |  Slim: $Slim  |  SkipSign: $SkipSign  |  Upload: $Upload"
Write-Host "  KeepMaxReleases : $KeepMaxReleases"

if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) {
    Write-Error "vpk not found. Install with: dotnet tool install -g vpk"
    exit 1
}

if ($Full) {
    if (-not (Test-Path $ModelPath)) {
        Write-Error "Kokoro model not found at: $ModelPath"
        exit 1
    }
    $fullFeed = Join-Path $FeedBase "full"
    Invoke-Publish   (Join-Path $PublishBase "full") $true "full"
    Invoke-VpkPack   (Join-Path $PublishBase "full") $fullFeed "full"
    Invoke-PruneFeed $fullFeed "full"
}

if ($Slim) {
    $slimFeed = Join-Path $FeedBase "slim"
    Invoke-Publish   (Join-Path $PublishBase "slim") $false "slim"
    Invoke-VpkPack   (Join-Path $PublishBase "slim") $slimFeed "slim"
    Invoke-PruneFeed $slimFeed "slim"
}

Invoke-GenerateDownloadPage

if ($Full) { Invoke-Upload (Join-Path $FeedBase "full") "full" }
if ($Slim) { Invoke-Upload (Join-Path $FeedBase "slim") "slim" }
Invoke-UploadRoot

Write-Host ""
Write-Host "Build complete." -ForegroundColor Green
if ($Full) { Write-Host "  Full feed : bin\feed\full\" }
if ($Slim) { Write-Host "  Slim feed : bin\feed\slim\" }
Write-Host ""
if (-not $Upload) {
    Write-Host "To deploy, run with -Upload:" -ForegroundColor Cyan
    Write-Host "  .\build-release-local.ps1 -Version `"$Version`" -SkipSign -Upload"
    Write-Host ""
    if ($LocalCopyPath) { Write-Host "  Target: $LocalCopyPath\{full|slim}\" }
    if ($ScpTarget)     { Write-Host "  Target: $ScpTarget/{full|slim}/" }
}
Write-Host ""
Write-Host "Update feed base URL  : $FeedBaseUrl" -ForegroundColor DarkGray
Write-Host "Full channel URL      : $($FeedBaseUrl.TrimEnd('/') + '/full/')" -ForegroundColor DarkGray
Write-Host "Slim channel URL      : $($FeedBaseUrl.TrimEnd('/') + '/slim/')" -ForegroundColor DarkGray
