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

#if WINDOWS
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using NVorbis;

namespace RuneReaderVoice.TTS.Audio;

[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class WasapiStreamAudioPlayer : IAudioPlayer
{
    private const int OutputSampleRate = 48000;
    private const int OutputChannels = 2;
    private const int OutputBitsPerSample = 16;
    private static readonly WaveFormat OutputWaveFormat =
        new(OutputSampleRate, OutputBitsPerSample, OutputChannels);

    private readonly object _sync = new();

    private WasapiOut? _output;
    private BufferedWaveProvider? _buffer;
    private CancellationTokenSource? _playbackCts;
    private Task? _feedTask;
    private TaskCompletionSource<bool>? _playbackTcs;
    private string? _deviceId;
    private bool _disposed;
    private float _volume = 1.0f;
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
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0f, 1f);
            lock (_sync)
            {
                if (_output != null)
                    _output.Volume = _volume;
            }
        }
    }

    public float Speed
    {
        get => _speed;
        set => _speed = Math.Clamp(value, 0.75f, 1.5f);
    }

    public async Task PlayAsync(string filePath, CancellationToken ct)
    {
        await PlaylistPlayAsync(OneItem(filePath), ct);

        static async IAsyncEnumerable<string> OneItem(string path)
        {
            yield return path;
            await Task.CompletedTask;
        }
    }

    public async Task PlaylistPlayAsync(IAsyncEnumerable<string> filePaths, CancellationToken ct)
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
            _playbackCts = linkedCts;
            _playbackTcs = tcs;

            _buffer = new BufferedWaveProvider(OutputWaveFormat)
            {
                DiscardOnBufferOverflow = false,
                ReadFully = true,
                BufferDuration = TimeSpan.FromSeconds(20),
            };

            _output = CreateOutput(_deviceId);
            _output.Init(_buffer);
            _output.Volume = _volume;

            localBuffer = _buffer;
            localOutput = _output;
            localPlaybackCts = _playbackCts;
        }

        void OnLocalPlaybackStopped(object? sender, StoppedEventArgs e)
        {
            if (e.Exception != null)
                tcs.TrySetException(e.Exception);
        }

        localOutput.PlaybackStopped += OnLocalPlaybackStopped;
        localOutput.Play();

        _feedTask = FeedStreamAsync(filePaths, localBuffer, localOutput, tcs, linkedCts.Token);
        localFeedTask = _feedTask;

        using var reg = linkedCts.Token.Register(() =>
        {
            try
            {
                lock (_sync)
                {
                    _output?.Stop();
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
            CleanupPlaybackObjects(localOutput, localBuffer, localPlaybackCts, localFeedTask, tcs);
        }
    }

    public void Stop()
    {
        CancellationTokenSource? cts;
        WasapiOut? output;

        lock (_sync)
        {
            cts = _playbackCts;
            output = _output;
        }

        try { cts?.Cancel(); } catch { }
        try { output?.Stop(); } catch { }

        lock (_sync)
        {
            _playbackTcs?.TrySetResult(false);
        }
    }

    public void SetOutputDevice(string? deviceId)
    {
        _deviceId = string.IsNullOrWhiteSpace(deviceId) ? null : deviceId;
    }

    public IReadOnlyList<AudioDeviceInfo> GetOutputDevices()
    {
        var result = new List<AudioDeviceInfo>();
        var enumerator = new MMDeviceEnumerator();
        var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            result.Add(new AudioDeviceInfo
            {
                DeviceId = device.ID,
                DeviceName = device.FriendlyName,
                IsDefault = string.Equals(device.ID, defaultDevice.ID, StringComparison.OrdinalIgnoreCase),
            });
        }

        return result;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Stop();

        WasapiOut? output;
        BufferedWaveProvider? buffer;
        CancellationTokenSource? cts;
        Task? feedTask;
        TaskCompletionSource<bool>? tcs;

        lock (_sync)
        {
            output = _output; buffer = _buffer; cts = _playbackCts; feedTask = _feedTask; tcs = _playbackTcs;
        }
        CleanupPlaybackObjects(output, buffer, cts, feedTask, tcs);
    }

    private WasapiOut CreateOutput(string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return new WasapiOut(AudioClientShareMode.Shared, true, 20);

        var enumerator = new MMDeviceEnumerator();
        var device = enumerator.GetDevice(deviceId);
        return new WasapiOut(device, AudioClientShareMode.Shared, true, 20);
    }

    private async Task FeedStreamAsync(
        IAsyncEnumerable<string> filePaths,
        BufferedWaveProvider buffer,
        WasapiOut output,
        TaskCompletionSource<bool> playbackTcs,
        CancellationToken ct)
    {
        Exception? failure = null;

        try
        {
            await foreach (var path in filePaths.WithCancellation(ct).ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();
                await DecodeAndQueueFileAsync(path, buffer, ct).ConfigureAwait(false);
            }

            // Wait for the queued audio to drain.
            while (!ct.IsCancellationRequested)
            {
                if (buffer.BufferedBytes <= 0)
                    break;

                await Task.Delay(20, ct).ConfigureAwait(false);
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

    private async Task DecodeAndQueueFileAsync(string filePath, BufferedWaveProvider buffer, CancellationToken ct)
    {
        var ext = Path.GetExtension(filePath);

        if (ext.Equals(".ogg", StringComparison.OrdinalIgnoreCase))
        {
            await DecodeOggAsync(filePath, buffer, ct).ConfigureAwait(false);
            return;
        }

        if (ext.Equals(".wav", StringComparison.OrdinalIgnoreCase))
        {
            await DecodeWavAsync(filePath, buffer, ct).ConfigureAwait(false);
            return;
        }

        throw new NotSupportedException($"Unsupported audio file for WASAPI stream playback: {filePath}");
    }

    private async Task DecodeWavAsync(string filePath, BufferedWaveProvider buffer, CancellationToken ct)
    {
        await using var fs = File.OpenRead(filePath);
        using var reader = new WaveFileReader(fs);
        await PumpWaveProviderAsync(reader, buffer, ct).ConfigureAwait(false);
    }

    private async Task DecodeOggAsync(string filePath, BufferedWaveProvider buffer, CancellationToken ct)
    {
        await using var fs = File.OpenRead(filePath);
        using var vorbis = new VorbisReader(fs, false);

        var sourceChannels = vorbis.Channels;
        var sourceRate = vorbis.SampleRate;

        var sampleProvider = new VorbisSampleProvider(vorbis);
        ISampleProvider working = sampleProvider;

        if (sourceChannels == 1 && OutputChannels == 2)
            working = new MonoToStereoSampleProvider(working);
        else if (sourceChannels == 2 && OutputChannels == 1)
            working = new StereoToMonoSampleProvider(working);
        else if (sourceChannels != OutputChannels)
            throw new NotSupportedException($"Unsupported channel count conversion: {sourceChannels} -> {OutputChannels}");

        if (sourceRate != OutputSampleRate)
            working = new WdlResamplingSampleProvider(working, OutputSampleRate);

        if (Math.Abs(_speed - 1.0f) > 0.001f)
        {
            // Placeholder: speed control for streamed PCM can be added later with
            // a real time-stretch/pitch-safe provider if needed.
            // For the first implementation we preserve correctness and ordering.
        }

        await PumpSampleProviderAsync(working, buffer, ct).ConfigureAwait(false);
    }

    private async Task PumpWaveProviderAsync(IWaveProvider source, BufferedWaveProvider buffer, CancellationToken ct)
    {
        IWaveProvider working = source;

        if (!source.WaveFormat.Equals(OutputWaveFormat))
        {
            var sample = source.ToSampleProvider();

            if (sample.WaveFormat.Channels == 1 && OutputChannels == 2)
                sample = new MonoToStereoSampleProvider(sample);
            else if (sample.WaveFormat.Channels == 2 && OutputChannels == 1)
                sample = new StereoToMonoSampleProvider(sample);
            else if (sample.WaveFormat.Channels != OutputChannels)
                throw new NotSupportedException($"Unsupported channel count conversion: {sample.WaveFormat.Channels} -> {OutputChannels}");

            if (sample.WaveFormat.SampleRate != OutputSampleRate)
                sample = new WdlResamplingSampleProvider(sample, OutputSampleRate);

            working = sample.ToWaveProvider16();
        }

        var rent = ArrayPool<byte>.Shared.Rent(OutputWaveFormat.AverageBytesPerSecond / 4);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var read = working.Read(rent, 0, rent.Length);
                if (read <= 0)
                    break;

                await WaitForBufferSpaceAsync(buffer, read, ct).ConfigureAwait(false);
                buffer.AddSamples(rent, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rent);
        }
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

                var bytes = ConvertFloatToPcm16(sampleRent, readSamples, byteRent, _volume);
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

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null)
        {
            lock (_sync)
            {
                _playbackTcs?.TrySetException(e.Exception);
            }
        }
    }

    private void CleanupPlaybackObjects(
        WasapiOut? output,
        BufferedWaveProvider? buffer,
        CancellationTokenSource? cts,
        Task? feedTask,
        TaskCompletionSource<bool>? tcs)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_output, output))
                _output = null;

            if (ReferenceEquals(_buffer, buffer))
                _buffer = null;

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

        try { output?.Stop(); } catch { }
        try
        {
            if (output != null)
                output.PlaybackStopped -= OnPlaybackStopped;
        }
        catch
        {
        }
        try { output?.Dispose(); } catch { }
        try { cts?.Dispose(); } catch { }
    }

    private sealed class VorbisSampleProvider : ISampleProvider
    {
        private readonly VorbisReader _reader;
        private readonly WaveFormat _waveFormat;

        public VorbisSampleProvider(VorbisReader reader)
        {
            _reader = reader;
            _waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(reader.SampleRate, reader.Channels);
        }

        public WaveFormat WaveFormat => _waveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            if (offset == 0)
                return _reader.ReadSamples(buffer, 0, count);

            var temp = ArrayPool<float>.Shared.Rent(count);
            try
            {
                var read = _reader.ReadSamples(temp, 0, count);
                Array.Copy(temp, 0, buffer, offset, read);
                return read;
            }
            finally
            {
                ArrayPool<float>.Shared.Return(temp);
            }
        }
    }
}
#endif
