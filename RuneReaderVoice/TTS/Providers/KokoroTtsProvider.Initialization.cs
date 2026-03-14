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

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using KokoroSharp;
using Microsoft.ML.OnnxRuntime;

namespace RuneReaderVoice.TTS.Providers;

public sealed partial class KokoroTtsProvider
{
    // ── Events ────────────────────────────────────────────────────────────────

    /// <summary>Fires when the ONNX model begins loading/downloading. Arg = status message.</summary>
    public event Action<string>? OnModelDownloading;

    /// <summary>Fires when the model is fully loaded and ready to synthesize.</summary>
    public event Action? OnModelReady;

    // ── Model init with download feedback ────────────────────────────────────

    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (_initialized) return;

        bool shouldInit;
        lock (_initLock)
        {
            shouldInit = !_initialized && !_initializing;
            if (shouldInit) _initializing = true;
        }

        if (shouldInit)
        {
            OnModelDownloading?.Invoke(
                "Kokoro: loading model — first run downloads ~320 MB, please wait…");

            try
            {
                await Task.Run(() =>
                {
                    var modelDir = VoiceSettingsManager.GetDefaultModelDirectory();
                    var modelPath = Path.Combine(modelDir, "kokoro.onnx");
                    Directory.CreateDirectory(modelDir);

                    using var opts = new SessionOptions();
                    opts.AppendExecutionProvider_CPU();
                    opts.EnableCpuMemArena = true;
                    opts.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
                    opts.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;

                    _tts = KokoroTTS.LoadModel(modelPath, sessionOptions: opts);
                    _tts.NicifyAudio = true;
                }, ct);

                lock (_initLock)
                {
                    _initialized = true;
                    _initializing = false;
                }

                OnModelReady?.Invoke();
            }
            catch
            {
                lock (_initLock)
                    _initializing = false;
                throw;
            }
        }
        else
        {
            // Another thread is initializing — poll until ready
            while (!_initialized)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(100, ct);
            }
        }
    }
}