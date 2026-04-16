# RuneReader Voice — Release & Installer Process

This document covers everything needed to build, sign, and publish installers
for RuneReader Voice. Read this before making a release.

---

## Overview

RuneReader Voice uses **Velopack** for installation and auto-updates. Velopack
is a modern replacement for Squirrel.Windows. It handles:

- Building a Windows `Setup.exe` installer
- Packaging app files into `.nupkg` update bundles
- Auto-update logic inside the app (check, download, apply on restart)
- Delta updates so users only download what changed between versions

There are **two installer variants**:

| Variant | File | Contains Kokoro model? | Size (approx) |
|---------|------|----------------------|---------------|
| Full    | `RuneReaderVoice-X.X.X-full-Setup.exe` | Yes | ~300 MB |
| Slim    | `RuneReaderVoice-X.X.X-slim-Setup.exe` | No  | ~220 MB |

The slim installer is recommended for most users since they will typically use
the remote server. The full installer is for users who want local Kokoro TTS
without any downloads after install.

---

## How Velopack Works (Conceptual)

### Install
The user runs `Setup.exe`. Velopack installs the app to the chosen directory
(default `C:\RuneReaderVoice\`). Everything lives there — exe, models, settings,
cache, database. Nothing goes to `%APPDATA%` or the registry.

Inside the install directory:
```
C:\RuneReaderVoice\
  current\
    RuneReaderVoice.exe       <- the app
    Models\kokoro-quant.onnx  <- (full installer only)
    config\settings.json      <- created on first run
    config\runereader-voice.db
    tts_cache\
  Update.exe                  <- Velopack updater binary
  packages\                   <- staged update packages live here
```

### Update
When the user clicks "Check for Updates" in the app's Advanced > Updates panel:

1. App calls GitHub (or the configured HTTP feed) to check for a newer version
2. If found, user clicks "Download Update" — the `.nupkg` is downloaded to `packages\`
3. User clicks "Restart and Install" — the app restarts, Velopack swaps in the
   new files from the `.nupkg`, then launches the new version

**User data survives updates.** Velopack only replaces files that were part of
the package. `settings.json`, `runereader-voice.db`, and `tts_cache\` are
created at runtime and never included in the package, so they are never touched.

### What are .nupkg files?
`.nupkg` is a NuGet package format repurposed by Velopack as the update bundle
container. It holds the versioned app files. Users never interact with these
directly — they are downloaded and applied automatically by the updater.

Each release produces:
- `RuneReaderVoice-X.X.X-full.nupkg` — full update bundle for the full channel
- `RuneReaderVoice-X.X.X-slim.nupkg` — full update bundle for the slim channel
- `RuneReaderVoice-X.X.X-full-delta.nupkg` — delta bundle (only what changed)
- `RELEASES` — manifest file the updater reads to find available versions

All of these must be uploaded to the release/update feed alongside the Setup.exe.

### Two update channels
Full and slim are separate Velopack **channels**. A user who installed via the
slim installer only receives slim channel updates. A user who installed via the
full installer only receives full channel updates. They never cross-contaminate.

---

## One-Time Setup

These steps only need to be done once per machine.

### 1. Install .NET 8 SDK
Download from https://dotnet.microsoft.com/download/dotnet/8.0

### 2. Install Velopack CLI
```powershell
dotnet tool install -g vpk
```

The `vpk` version must match the `Velopack` NuGet package version in
`RuneReaderVoice.csproj`. Currently both are `0.0.1298`.

To check installed version:
```powershell
vpk --version
```

To update:
```powershell
dotnet tool update -g vpk
```

### 3. Install Windows SDK (for code signing)
Required for `signtool.exe`. Install via Visual Studio Installer or directly
from https://developer.microsoft.com/en-us/windows/downloads/windows-sdk/

### 4. Get an OV Code Signing Certificate (for public releases)
- Purchase from SSL.com (OV cert ~$75/yr)
- Install the certificate into the Windows certificate store
- The `CertSubject` parameter in `build-release.ps1` must match the CN in
  your certificate (currently set to `"Michael Sutton"`)

For local/test builds, use `-SkipSign` to bypass signing entirely.

---

## Versioning

The version is defined in `RuneReaderVoice.csproj`:

```xml
<Version>1.5.0</Version>
<AssemblyVersion>1.5.0.0</AssemblyVersion>
<FileVersion>1.5.0.0</FileVersion>
```

**Velopack uses semver (3-part: major.minor.patch).** The 4-part Windows
version (`1.5.0.0`) is for the exe file properties only.

When releasing a new version:
1. Update `<Version>` in `RuneReaderVoice.csproj`
2. Update the `$Version` default in `build-release.ps1` and `build-release-local.ps1`
3. Build and publish

---

## Build Scripts

All build scripts live in the project root alongside `RuneReaderVoice.csproj`.

### build-release-local.ps1 — Test/Staging Builds

Use this for testing. Produces installers that check your personal server
(`https://www.mkfam.com/filedump/`) for updates instead of GitHub.

```powershell
# Build both installers (no signing, no upload)
.\build-release-local.ps1 -SkipSign

# Build and copy to local web server path
.\build-release-local.ps1 -SkipSign -Upload

# Specify a different copy path
.\build-release-local.ps1 -SkipSign -Upload -LocalCopyPath "Y:\other\path"
```

Output goes to `release-local\full\` and `release-local\slim\`.

The `-Upload` flag copies all release files to `X:\dataStore\webhost\filedump`
by default. Make sure the X: drive is mapped before running with `-Upload`.

### build-release.ps1 — Production/GitHub Releases

Use this for public releases. Produces installers that check GitHub releases
for updates.

```powershell
# Build both installers with signing
.\build-release.ps1

# Build both, skip signing (testing only — do NOT distribute unsigned builds)
.\build-release.ps1 -SkipSign

# Build full installer only
.\build-release.ps1 -Full

# Build slim installer only
.\build-release.ps1 -Slim
```

Output goes to `release\full\` and `release\slim\`.

---

## How the Update Feed URL Gets Into the App

The update feed URL is **baked into the binary at compile time** via an MSBuild
property. This is how the full and local builds point to different servers
without requiring two separate code paths.

The csproj contains a target that generates `UpdateServiceConfig.g.cs` in the
intermediate output directory:

```xml
<Target Name="GenerateUpdateServiceConfig" BeforeTargets="CoreCompile">
  ...generates: internal const string UpdateFeedUrl = "...";
</Target>
```

- `build-release.ps1` passes no `UpdateFeedUrl` → constant is empty → app uses GitHub
- `build-release-local.ps1` passes `-p:UpdateFeedUrl=https://www.mkfam.com/filedump/` → constant is set → app uses HTTP source

`UpdateService.cs` reads this constant and selects the appropriate Velopack
source (`GithubSource` vs `SimpleWebSource`) at startup.

---

## Publishing a Test Release (Local Server)

### Step 1 — Build
```powershell
.\build-release-local.ps1 -SkipSign
```

### Step 2 — Copy to web server
```powershell
.\build-release-local.ps1 -SkipSign -Upload
```

Or manually copy everything from `release-local\full\` and `release-local\slim\`
to `X:\dataStore\webhost\filedump`.

### Step 3 — Verify the feed is accessible
The update check works by fetching the `RELEASES` file from the feed URL.
Verify it is accessible:
```
https://www.mkfam.com/filedump/RELEASES
```

If that URL returns the RELEASES file content, the update feed is working.

### Step 4 — Install and test
Run `release-local\full\RuneReaderVoice-1.5.0-full-local-Setup.exe` on a test
machine. After install, open the app, go to Advanced > Updates, click
"Check for Updates". It should report up to date (or find a newer version if
one was published).

To test an actual update: bump the version, rebuild, copy to the server, then
check for updates from the older installed version.

---

## Publishing a Public Release (GitHub)

### Step 1 — Update version
In `RuneReaderVoice.csproj`, update:
```xml
<Version>1.5.1</Version>
```

Also update `$Version` in `build-release.ps1`.

### Step 2 — Build with signing
Ensure OV certificate is installed, then:
```powershell
.\build-release.ps1
```

### Step 3 — Create GitHub release
1. Go to https://github.com/station384/RuneReaderVoice/releases/new
2. Tag: `v1.5.1`
3. Title: `RuneReader Voice 1.5.1`
4. Upload ALL of the following files as release assets:

From `release\full\`:
- `RuneReaderVoice-1.5.1-full-Setup.exe`
- `RuneReaderVoice-1.5.1-full.nupkg`
- `RuneReaderVoice-1.5.1-full-delta.nupkg` (if present)
- `RELEASES`

From `release\slim\`:
- `RuneReaderVoice-1.5.1-slim-Setup.exe`
- `RuneReaderVoice-1.5.1-slim.nupkg`
- `RuneReaderVoice-1.5.1-slim-delta.nupkg` (if present)
- `RELEASES`

> **Important:** Both channels have a file named `RELEASES`. GitHub release
> assets must have unique names. Rename them before uploading:
> - `full\RELEASES` → `RELEASES-full`
> - `slim\RELEASES` → `RELEASES-slim`
>
> Then update the `GithubSource` configuration in `UpdateService.cs` if needed,
> or use the Velopack `--channel` support which handles this automatically.
> **(TODO: verify channel-aware RELEASES naming with Velopack docs)**

### Step 4 — Publish the release
Click "Publish release" on GitHub. Users with the app installed will find the
update the next time they click "Check for Updates".

---

## In-App Update UI

The update panel is in **Advanced > Updates** tab.

| Control | Appears when | Action |
|---------|-------------|--------|
| Version label | Always | Shows current installed version |
| Status text | Always | Shows current update state |
| "Check for Updates" button | Idle / UpToDate / Error | Queries the feed |
| "Download Update" button | Update available | Downloads the .nupkg |
| Progress bar | Downloading | Shows download progress |
| "Restart and Install" button | Download complete | Applies update and restarts |

The entire Updates expander is hidden when the app is not running as a Velopack-
managed install (e.g. running directly from the build output directory in Rider).

---

## Troubleshooting

**"vpk not found"**
Run `dotnet tool install -g vpk` and ensure `%USERPROFILE%\.dotnet\tools` is in PATH.

**"Kokoro model not found"**
Place `kokoro-quant.onnx` in the `Models\` directory before running the full build.

**Update check fails with network error**
Verify the `RELEASES` file is accessible at the configured feed URL. For the
local server: `https://www.mkfam.com/filedump/RELEASES`.

**"Check for Updates" button does nothing / Updates expander is hidden**
The app is not running as a Velopack-managed install. Run the Setup.exe to
install it properly, then test from the installed location.

**Velopack NuGet version mismatch warning**
The `Velopack` NuGet package version and `vpk` CLI version should match.
Check `RuneReaderVoice.csproj` for the NuGet version and run `vpk --version`
for the CLI version. Update whichever is older.

**Delta packages not generated**
Delta packages are only generated when there is a previous release in the output
directory to diff against. On a fresh `release\` directory (first release or
after clearing), only the full `.nupkg` is produced. This is normal.

---

## File Reference

| File | Purpose |
|------|---------|
| `build-release.ps1` | Production build — GitHub update feed |
| `build-release-local.ps1` | Test/staging build — personal HTTP server |
| `Sync/UpdateService.cs` | In-app update logic (Velopack wrapper) |
| `Sync/UpdateServiceConfig.g.cs` | Auto-generated at build time — contains feed URL constant |
| `RuneReaderVoice.csproj` | Version number, Velopack NuGet reference, MSBuild config generator |
| `release\full\` | Production full installer output |
| `release\slim\` | Production slim installer output |
| `release-local\full\` | Test full installer output |
| `release-local\slim\` | Test slim installer output |
| `publish\` | Intermediate dotnet publish output (production) |
| `publish-local\` | Intermediate dotnet publish output (local/test) |
