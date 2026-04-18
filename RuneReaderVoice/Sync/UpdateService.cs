// SPDX-License-Identifier: GPL-3.0-only
//
// This file is part of RuneReaderVoice.
// Copyright (C) 2026 Michael Sutton
//
// RuneReaderVoice is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, version 3 of the License.
//
// RuneReaderVoice is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with RuneReaderVoice. If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Threading;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace RuneReaderVoice.Sync;

// UpdateService.cs
// User-controlled update pipeline via Velopack + GitHub Releases.
//
// No automatic downloads or installs — the user drives every step:
//   1. Click "Check for Updates"  → CheckAsync()
//   2. Click "Download Update"    → DownloadAndStageAsync()
//   3. Click "Restart and Install" → RestartAndInstall()
//
// State machine:
//   Idle → Checking → UpToDate
//                   → UpdateAvailable → Downloading → ReadyToInstall
//                   → Error
//
// StatusChanged fires on every state transition so the UI can
// update labels and button visibility without polling.

public enum UpdateState
{
    Idle,
    Checking,
    UpToDate,
    UpdateAvailable,
    Downloading,
    ReadyToInstall,
    Error,
    NotInstalled,   // Running from dev build / not managed by Velopack
}

public sealed class UpdateService
{
    private const string GitHubOwner = "station384";
    private const string GitHubRepo  = "RuneReaderVoice";

    // UpdateFeedUrl is defined in UpdateServiceConfig.g.cs, which is generated
    // at build time by the csproj. Release builds use GitHub; test/staging builds
    // use the URL passed via -p:UpdateFeedUrl=https://... to dotnet publish.
    // If empty, the GitHub source is used automatically.
    private static readonly string UpdateFeedUrl = UpdateServiceConfig.UpdateFeedUrl;

    private readonly UpdateManager? _manager;
    private UpdateInfo?             _pendingUpdate;
    private UpdateState             _state;
    private string                  _statusMessage = string.Empty;

    public event Action<UpdateState, string>? StatusChanged;

    public UpdateState State         => _state;
    public string      StatusMessage => _statusMessage;

    public string? AvailableVersion =>
        _pendingUpdate?.TargetFullRelease?.Version?.ToString();

    public string CurrentVersion =>
        _manager?.CurrentVersion?.ToString() ?? GetAssemblyVersion();

    public UpdateService()
    {
        try
        {
            IUpdateSource source;
            if (!string.IsNullOrWhiteSpace(UpdateFeedUrl))
            {
                // Test/staging: plain HTTP source pointing to file dump
                source = new SimpleWebSource(UpdateFeedUrl);
            }
            else
            {
                // Production: GitHub releases
                source = new GithubSource(
                    $"https://github.com/{GitHubOwner}/{GitHubRepo}",
                    accessToken: null,
                    prerelease:  false);
            }

            _manager = new UpdateManager(source);

            // If Velopack isn't managing this install (dev build, xcopy deploy),
            // report NotInstalled so the UI can show appropriate messaging.
            _state = _manager.IsInstalled
                ? UpdateState.Idle
                : UpdateState.NotInstalled;
        }
        catch
        {
            _state   = UpdateState.NotInstalled;
            _manager = null;
        }
    }

    /// <summary>
    /// Check GitHub releases for a newer version. Does not download anything.
    /// No-op if not running as an installed Velopack app.
    /// </summary>
    public async Task CheckAsync(CancellationToken ct = default)
    {
        if (_manager == null || !_manager.IsInstalled) return;
        if (_state is UpdateState.Checking or UpdateState.Downloading) return;

        SetState(UpdateState.Checking, "Checking for updates…");
        try
        {
            var update = await _manager.CheckForUpdatesAsync().WaitAsync(ct);
            if (update == null)
            {
                _pendingUpdate = null;
                SetState(UpdateState.UpToDate, "RuneReader Voice is up to date.");
            }
            else
            {
                _pendingUpdate = update;
                SetState(UpdateState.UpdateAvailable,
                    $"Version {update.TargetFullRelease?.Version} is available.");
            }
        }
        catch (OperationCanceledException)
        {
            SetState(UpdateState.Idle, string.Empty);
        }
        catch (Exception ex)
        {
            SetState(UpdateState.Error, $"Update check failed: {ex.Message}");
        }
    }


    /// <summary>
    /// Background update check used at app startup. Failures are ignored so the
    /// app remains fully functional even if the feed cannot be reached.
    /// </summary>
    public async Task CheckSilentlyAsync(CancellationToken ct = default)
    {
        if (_manager == null || !_manager.IsInstalled) return;
        if (_state is UpdateState.Checking or UpdateState.Downloading or UpdateState.ReadyToInstall) return;

        try
        {
            var update = await _manager.CheckForUpdatesAsync().WaitAsync(ct);
            if (update == null)
            {
                _pendingUpdate = null;
                SetState(UpdateState.UpToDate, "RuneReader Voice is up to date.");
            }
            else
            {
                _pendingUpdate = update;
                SetState(UpdateState.UpdateAvailable,
                    $"Version {update.TargetFullRelease?.Version} is available.");
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            if (_state == UpdateState.Checking)
                SetState(UpdateState.Idle, string.Empty);
        }
    }

    /// <summary>
    /// Download and stage the pending update. Call CheckAsync first.
    /// <paramref name="onProgress"/> receives values 0–100.
    /// </summary>
    public async Task DownloadAndStageAsync(
        Action<int>?      onProgress = null,
        CancellationToken ct         = default)
    {
        if (_manager == null || _pendingUpdate == null) return;
        if (_state != UpdateState.UpdateAvailable) return;

        SetState(UpdateState.Downloading, "Downloading update…");
        try
        {
            await _manager.DownloadUpdatesAsync(_pendingUpdate, onProgress).WaitAsync(ct);
            SetState(UpdateState.ReadyToInstall,
                $"Version {_pendingUpdate.TargetFullRelease?.Version} ready — " +
                "restart to install.");
        }
        catch (OperationCanceledException)
        {
            SetState(UpdateState.UpdateAvailable,
                $"Download cancelled. " +
                $"Version {_pendingUpdate.TargetFullRelease?.Version} is available.");
        }
        catch (Exception ex)
        {
            SetState(UpdateState.Error, $"Download failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Apply the staged update and restart the application immediately.
    /// Only valid when State == ReadyToInstall.
    /// </summary>
    public void RestartAndInstall()
    {
        if (_manager == null || _state != UpdateState.ReadyToInstall) return;
        if (_pendingUpdate != null) 
            _manager.ApplyUpdatesAndRestart(_pendingUpdate);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void SetState(UpdateState state, string message)
    {
        _state         = state;
        _statusMessage = message;
        StatusChanged?.Invoke(state, message);
    }

    private static string GetAssemblyVersion()
    {
        var v = System.Reflection.Assembly
            .GetExecutingAssembly()
            .GetName()
            .Version;
        return v != null ? $"{v.Major}.{v.Minor}.{v.Build}" : "unknown";
    }
}
