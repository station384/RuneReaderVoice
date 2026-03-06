// SPDX-License-Identifier: GPL-3.0-or-later
//
// This file is part of RuneReaderVoice.
// Copyright (C) 2026 Michael Sutton
//
// RuneReaderVoice is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// RuneReaderVoice is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with RuneReaderVoice. If not, see <https://www.gnu.org/licenses/>.

#if LINUX
// GstAudioPlayer.cs
// Audio playback via GStreamer on Linux.
// Reuses the existing GstNative P/Invoke layer from the screen capture pipeline.
//
// Pipeline: filesrc location=<path> ! decodebin ! audioconvert ! audioresample ! autoaudiosink
// decodebin handles WAV and OGG automatically.
// autoaudiosink routes to PulseAudio or PipeWire automatically.
//
// Speed and device selection require additional GStreamer elements:
//   Speed: insert scaletempo or pitch element between decodebin and sink.
//   Device: replace autoaudiosink with pulsesink device=<name>.

using System.Runtime.InteropServices;
using System.Diagnostics;

namespace RuneReaderVoice.TTS.Audio;

public sealed class GstAudioPlayer : IAudioPlayer
{
    private nint _pipeline = nint.Zero;
    private TaskCompletionSource<bool>? _playbackTcs;
    private CancellationTokenRegistration _ctReg;
    private float _volume = 1.0f;
    private float _speed  = 1.0f;
    private string? _deviceId;
    private bool _disposed;

    // Bus polling task
    private CancellationTokenSource? _busCts;
    private Task? _busTask;

    public bool IsPlaying { get; private set; }

    public float Volume
    {
        get => _volume;
        set => _volume = Math.Clamp(value, 0f, 1f);
        // TODO: apply to GStreamer volume element dynamically
    }

    public float Speed
    {
        get => _speed;
        set => _speed = Math.Clamp(value, 0.75f, 1.5f);
        // TODO: apply via scaletempo seek event
    }

    public void SetOutputDevice(string? deviceId)
    {
        _deviceId = deviceId;
        // Applied on next PlayAsync call via pulsesink device= parameter
    }

    public IReadOnlyList<AudioDeviceInfo> GetOutputDevices()
    {
        // TODO: enumerate PulseAudio/PipeWire sinks via pactl or GStreamer device monitor
        return new[]
        {
            new AudioDeviceInfo { DeviceId = "", DeviceName = "System Default (autoaudiosink)", IsDefault = true }
        };
    }

    public async Task PlayAsync(string filePath, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Stop();

        // Escape path for GStreamer pipeline string
        var escapedPath = filePath.Replace("\\", "/").Replace("\"", "\\\"");

        // Build sink element: pulsesink with device if specified, else autoaudiosink
        var sinkElement = string.IsNullOrEmpty(_deviceId)
            ? "autoaudiosink"
            : $"pulsesink device=\"{_deviceId}\"";

        // Speed: insert scaletempo if speed != 1.0
        var speedElement = Math.Abs(_speed - 1.0f) > 0.01f
            ? $"scaletempo rate={_speed:F2} ! "
            : string.Empty;

        // Volume: insert volume element
        var volumeElement = $"volume volume={_volume:F2} ! ";

        var pipelineStr =
            $"filesrc location=\"{escapedPath}\" ! decodebin ! audioconvert ! audioresample ! " +
            $"{speedElement}{volumeElement}{sinkElement}";

        nint error = nint.Zero;
        _pipeline = GstNative.gst_parse_launch(pipelineStr, out error);

        if (error != nint.Zero)
        {
            var msg = GstNative.GErrorToStringAndFree(error);
            throw new InvalidOperationException($"GStreamer pipeline error: {msg}");
        }

        if (_pipeline == nint.Zero)
            throw new InvalidOperationException("Failed to create GStreamer pipeline.");

        _playbackTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ctReg = ct.Register(Stop);

        var ret = GstNative.gst_element_set_state(_pipeline, GstNative.GstState.GST_STATE_PLAYING);
        if (ret == GstNative.GstStateChangeReturn.GST_STATE_CHANGE_FAILURE)
        {
            CleanupPipeline();
            throw new InvalidOperationException("GStreamer failed to enter PLAYING state.");
        }

        IsPlaying = true;

        // Poll the bus for EOS or ERROR on a background task
        _busCts  = new CancellationTokenSource();
        _busTask = PollBusAsync(_busCts.Token);

        await _playbackTcs.Task;
    }

    public void Stop()
    {
        _busCts?.Cancel();
        _playbackTcs?.TrySetResult(false);
        _playbackTcs = null;
        _ctReg.Dispose();
        CleanupPipeline();
        IsPlaying = false;
    }

    private async Task PollBusAsync(CancellationToken ct)
    {
        // GStreamer bus polling: gst_bus_timed_pop_filtered to check for EOS/ERROR.
        // We poll every 50ms rather than blocking indefinitely.
        // Full implementation requires gst_element_get_bus + gst_bus_timed_pop_filtered.
        // Stubbed here: sleep until playback completes via cancellation.
        //
        // TODO: add gst_element_get_bus / gst_bus_timed_pop_filtered to GstNative.

        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(50, ct);
                // Check pipeline state — if NULL or paused unexpectedly, signal done.
                // For now rely on cancellation token from Stop().
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _playbackTcs?.TrySetResult(true);
            IsPlaying = false;
        }
    }

    private void CleanupPipeline()
    {
        if (_pipeline == nint.Zero) return;
        GstNative.gst_element_set_state(_pipeline, GstNative.GstState.GST_STATE_NULL);
        GstNative.gst_object_unref(_pipeline);
        _pipeline = nint.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _busCts?.Dispose();
    }
}
#endif
