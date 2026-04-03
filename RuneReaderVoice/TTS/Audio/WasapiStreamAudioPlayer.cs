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

#if WINDOWS
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using RuneReaderVoice.TTS.Providers;

namespace RuneReaderVoice.TTS.Audio;

[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class WasapiStreamAudioPlayer : IAudioPlayer
{
    private const int OutputSampleRate = 48000;
    private const int OutputChannels = 2;
    private const int OutputBitsPerSample = 16;
    private const float DefaultSampleGain = 1.0f;
    private static readonly WaveFormat OutputWaveFormat =
        new(OutputSampleRate, OutputBitsPerSample, OutputChannels);

    private readonly object _sync = new();

    private WasapiOut? _output;
    private BufferedWaveProvider? _buffer;
    private MMDevice? _outputDevice;
    private SimpleAudioVolume? _sessionVolume;
    private CancellationTokenSource? _playbackCts;
    private Task? _feedTask;
    private TaskCompletionSource<bool>? _playbackTcs;
    private string? _deviceId;
    private bool _disposed;
    private float _appVolume = 1.0f;
    private float _speed = 1.0f;

    public bool IsPlaying
    {
        get
        {
            lock (_sync)
            {
                return _output?.PlaybackState == PlaybackState.Playing;
            }
        }
    }

    public float Volume
    {
        get => _appVolume;
        set
        {
            _appVolume = Math.Clamp(value, 0f, 1f);
            lock (_sync)
            {
                ApplySessionVolumeLocked();
            }
        }
    }

    public float Speed
    {
        get => _speed;
        set => _speed = Math.Clamp(value, 0.75f, 1.5f);
    }

    public async Task PlayAsync(PcmAudio audio, CancellationToken ct)
    {
        await PlaylistPlayAsync(OneItem(audio), ct);

        static async IAsyncEnumerable<PcmAudio> OneItem(PcmAudio item)
        {
            yield return item;
            await Task.CompletedTask;
        }
    }

    public async Task PlaylistPlayAsync(IAsyncEnumerable<PcmAudio> audioChunks, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Stop();

        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        BufferedWaveProvider localBuffer;
        WasapiOut localOutput;
        Task? localFeedTask;
        CancellationTokenSource localPlaybackCts;

        lock (_sync)
        {
            EnsureOutputLocked();

            _playbackCts = linkedCts;
            _playbackTcs = tcs;

            _buffer!.ClearBuffer();
            _output!.Stop();
            ApplySessionVolumeLocked();

            localBuffer = _buffer;
            localOutput = _output;
            localPlaybackCts = linkedCts;
        }

        void OnLocalPlaybackStopped(object? sender, StoppedEventArgs e)
        {
            if (e.Exception != null)
                tcs.TrySetException(e.Exception);
        }

        localOutput.PlaybackStopped += OnLocalPlaybackStopped;
        localOutput.Play();

        _feedTask = FeedStreamAsync(audioChunks, localBuffer, localOutput, tcs, linkedCts.Token);
        localFeedTask = _feedTask;

        using var reg = linkedCts.Token.Register(() =>
        {
            try
            {
                lock (_sync)
                {
                    if (ReferenceEquals(_output, localOutput))
                        _output.Stop();
                }
            }
            catch
            {
                // ignore shutdown races
            }

            tcs.TrySetCanceled();
        });

        try
        {
            await tcs.Task.ConfigureAwait(false);
            if (localFeedTask != null)
                await localFeedTask.ConfigureAwait(false);
        }
        finally
        {
            localOutput.PlaybackStopped -= OnLocalPlaybackStopped;
            CleanupPlaybackRun(localPlaybackCts, localFeedTask, tcs);
        }
    }

    public void Stop()
    {
        CancellationTokenSource? cts;
        WasapiOut? output;
        BufferedWaveProvider? buffer;

        lock (_sync)
        {
            cts = _playbackCts;
            output = _output;
            buffer = _buffer;
        }

        try { cts?.Cancel(); } catch { }
        try { output?.Stop(); } catch { }
        try { buffer?.ClearBuffer(); } catch { }

        lock (_sync)
        {
            _playbackTcs?.TrySetResult(false);
        }
    }

    public void SetOutputDevice(string? deviceId)
    {
        deviceId = string.IsNullOrWhiteSpace(deviceId) ? null : deviceId;

        bool changed;
        lock (_sync)
        {
            changed = !string.Equals(_deviceId, deviceId, StringComparison.OrdinalIgnoreCase);
            _deviceId = deviceId;
        }

        if (changed)
        {
            Stop();
            lock (_sync)
            {
                DisposeOutputLocked();
            }
        }
    }

    public IReadOnlyList<AudioDeviceInfo> GetOutputDevices()
    {
        var result = new List<AudioDeviceInfo>();
        using var enumerator = new MMDeviceEnumerator();
        using var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using (device)
            {
                result.Add(new AudioDeviceInfo
                {
                    DeviceId   = device.ID,
                    DeviceName = device.FriendlyName,
                    IsDefault  = string.Equals(device.ID, defaultDevice.ID, StringComparison.OrdinalIgnoreCase),
                });
            }
        }

        return result;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Stop();

        CancellationTokenSource? cts;
        Task? feedTask;
        TaskCompletionSource<bool>? tcs;

        lock (_sync)
        {
            cts = _playbackCts;
            feedTask = _feedTask;
            tcs = _playbackTcs;
            _playbackCts = null;
            _feedTask = null;
            _playbackTcs = null;
        }

        CleanupPlaybackRun(cts, feedTask, tcs);

        lock (_sync)
        {
            DisposeOutputLocked();
        }
    }

    private void EnsureOutputLocked()
    {
        if (_output != null && _buffer != null)
        {
            ApplySessionVolumeLocked();
            return;
        }

        _buffer = new BufferedWaveProvider(OutputWaveFormat)
        {
            DiscardOnBufferOverflow = false,
            ReadFully = true,
            BufferDuration = TimeSpan.FromSeconds(3),
        };

        _output = CreateOutputLocked(_deviceId);
        _output.Init(_buffer);

        BindSessionVolumeLocked();
        ApplySessionVolumeLocked();
    }

    private WasapiOut CreateOutputLocked(string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            _outputDevice = null;
            return new WasapiOut(AudioClientShareMode.Shared, true, 200);
        }

        using var enumerator = new MMDeviceEnumerator();
        _outputDevice = enumerator.GetDevice(deviceId);
        return new WasapiOut(_outputDevice, AudioClientShareMode.Shared, true, 200);
    }

    private void BindSessionVolumeLocked()
    {
        try
        {
            _sessionVolume?.Dispose();
            _sessionVolume = null;

            if (_outputDevice != null)
            {
                _sessionVolume = _outputDevice.AudioSessionManager.SimpleAudioVolume;
                return;
            }

            using var enumerator = new MMDeviceEnumerator();
            using var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            _sessionVolume = defaultDevice.AudioSessionManager.SimpleAudioVolume;
        }
        catch
        {
            _sessionVolume = null;
        }
    }

    private void ApplySessionVolumeLocked()
    {
        try
        {
            if (_sessionVolume == null)
                BindSessionVolumeLocked();

            if (_sessionVolume != null)
                _sessionVolume.Volume = _appVolume;
        }
        catch
        {
            _sessionVolume = null;
        }
    }

    private async Task FeedStreamAsync(
        IAsyncEnumerable<PcmAudio> audioChunks,
        BufferedWaveProvider buffer,
        WasapiOut output,
        TaskCompletionSource<bool> playbackTcs,
        CancellationToken ct)
    {
        Exception? failure = null;

        try
        {
            await foreach (var audio in audioChunks.WithCancellation(ct).ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();
                await QueueAudioAsync(audio, buffer, ct).ConfigureAwait(false);
            }

            while (!ct.IsCancellationRequested)
            {
                if (buffer.BufferedBytes <= 0)
                    break;

                await Task.Delay(200, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            playbackTcs.TrySetCanceled();
            return;
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        if (failure != null)
            playbackTcs.TrySetException(failure);
        else
            playbackTcs.TrySetResult(true);
    }

    private async Task QueueAudioAsync(PcmAudio audio, BufferedWaveProvider buffer, CancellationToken ct)
    {
        var sampleProvider = new PcmAudioSampleProvider(audio);
        ISampleProvider working = sampleProvider;

        if (audio.Channels == 1 && OutputChannels == 2)
            working = new MonoToStereoSampleProvider(working);
        else if (audio.Channels == 2 && OutputChannels == 1)
            working = new StereoToMonoSampleProvider(working);
        else if (audio.Channels != OutputChannels)
            throw new NotSupportedException($"Unsupported channel count conversion: {audio.Channels} -> {OutputChannels}");

        if (audio.SampleRate != OutputSampleRate)
            working = new WdlResamplingSampleProvider(working, OutputSampleRate);

        await PumpSampleProviderAsync(working, buffer, ct).ConfigureAwait(false);
    }

    private async Task PumpSampleProviderAsync(ISampleProvider source, BufferedWaveProvider buffer, CancellationToken ct)
    {
        var sampleRent = ArrayPool<float>.Shared.Rent(OutputSampleRate / 4 * OutputChannels);
        var byteRent = ArrayPool<byte>.Shared.Rent(sampleRent.Length * 2);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var readSamples = source.Read(sampleRent, 0, sampleRent.Length);
                if (readSamples <= 0)
                    break;

                var bytes = ConvertFloatToPcm16(sampleRent, readSamples, byteRent, DefaultSampleGain);
                await WaitForBufferSpaceAsync(buffer, bytes, ct).ConfigureAwait(false);
                buffer.AddSamples(byteRent, 0, bytes);
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(sampleRent);
            ArrayPool<byte>.Shared.Return(byteRent);
        }
    }

    private static int ConvertFloatToPcm16(float[] samples, int sampleCount, byte[] target, float volume)
    {
        var outIndex = 0;
        for (var i = 0; i < sampleCount; i++)
        {
            var scaled = samples[i] * volume;
            scaled = Math.Clamp(scaled, -1.0f, 1.0f);
            short pcm = (short)Math.Round(scaled * short.MaxValue);

            target[outIndex++] = (byte)(pcm & 0xFF);
            target[outIndex++] = (byte)((pcm >> 8) & 0xFF);
        }

        return outIndex;
    }

    private static async Task WaitForBufferSpaceAsync(BufferedWaveProvider buffer, int nextBytes, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var available = buffer.BufferLength - buffer.BufferedBytes;
            if (available >= nextBytes)
                return;

            await Task.Delay(10, ct).ConfigureAwait(false);
        }
    }

    private void CleanupPlaybackRun(
        CancellationTokenSource? cts,
        Task? feedTask,
        TaskCompletionSource<bool>? tcs)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_playbackCts, cts))
                _playbackCts = null;

            if (ReferenceEquals(_feedTask, feedTask))
                _feedTask = null;

            if (ReferenceEquals(_playbackTcs, tcs))
                _playbackTcs = null;
        }

        try { cts?.Cancel(); } catch { }

        try
        {
            if (feedTask != null && !feedTask.IsCompleted)
                feedTask.Wait(TimeSpan.FromMilliseconds(250));
        }
        catch
        {
            // ignore teardown races
        }

        try { cts?.Dispose(); } catch { }
    }

    private void DisposeOutputLocked()
    {
        var output = _output;
        var buffer = _buffer;
        var sessionVolume = _sessionVolume;
        var outputDevice = _outputDevice;

        _output = null;
        _buffer = null;
        _sessionVolume = null;
        _outputDevice = null;

        try { output?.Stop(); } catch { }
        try { output?.Dispose(); } catch { }
        try { sessionVolume?.Dispose(); } catch { }
        try { outputDevice?.Dispose(); } catch { }
    }

    private sealed class PcmAudioSampleProvider : ISampleProvider
    {
        private readonly PcmAudio _audio;
        private int _position;
        private readonly WaveFormat _waveFormat;

        public PcmAudioSampleProvider(PcmAudio audio)
        {
            _audio = audio;
            _waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(audio.SampleRate, audio.Channels);
        }

        public WaveFormat WaveFormat => _waveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            var remaining = _audio.Samples.Length - _position;
            if (remaining <= 0)
                return 0;

            var toCopy = Math.Min(count, remaining);
            Array.Copy(_audio.Samples, _position, buffer, offset, toCopy);
            _position += toCopy;
            return toCopy;
        }
    }
}
#endif