# RuneReader — Quest Text-to-Speech (TTS)

*Feature Design Document | Roadmap Item*

Status: Phases 1–6 Complete | Target: Retail WoW

Last Updated: April 2026 | Version 24

---

## What's New in v24

- **Provider benchmark harness added (Phase 6):** A standalone Python benchmark runner (`rrv_benchmark.py`) is now defined for repeatable remote-provider characterization. The script takes the target server URL and provider ID as explicit parameters, uses a built-in fixed corpus so test text does not drift across runs, saves every returned clip as an `.ogg` file with human-meaningful names, records v1/v2 response metrics, and emits machine-readable summaries that can be given back to an LLM to consolidate updates into the provider test notes.

- **Benchmark result intent documented:** Phase 6 is specifically for empirically characterizing provider limits and behavior — safe chunk size, truncation boundary, repetition drift, ordered-list stability, punctuation sensitivity, cache-hit vs cache-miss performance, and real-time throughput — so client chunking policy and provider-specific defaults can be tuned from measured data rather than guesses.

## What's New in v23

- **CosyVoice3 backend added:** `cosyvoice` and `cosyvoice_vllm` are now implemented server backends. CosyVoice3 is a hybrid LLM+flow-matching model with zero-shot voice cloning, multilingual support, and optional natural-language style instructions via `cosy_instruct` / `inference_instruct2()`. Both backends run as worker subprocesses under the ResourceManager.

- **LuxTTS backend added:** `lux` is a new high-speed flow-matching voice cloning backend. Client-configurable controls: `lux_num_steps` (ODE solver steps, default 10), `lux_t_shift` (time shift, default 0.7), `lux_return_smooth` (artifact reduction, default true). Emits 48 kHz mono clips by default.

- **Chatterbox Multilingual backend added:** `chatterbox_multilingual` extends Chatterbox voice cloning to 22 languages (ar, da, de, el, en, es, fi, fr, he, hi, it, ja, ko, ms, nl, no, pl, pt, ru, sv, sw, tr, zh). Uses the same `cfg_weight` and `exaggeration` controls as the other Chatterbox backends. 10-step diffusion decoder; no paralinguistic tags.

- **Qwen3-TTS backends added:** Three Qwen backends are implemented — `qwen_natural` (reference-based voice cloning with optional `voice_instruct` style control), `qwen_custom` (same), and `qwen_design` (voice persona design via `voice.type = "description"` and a `voice_description` text prompt). Model size (`large` / `small`) configurable per-backend via env var.

- **ResourceManager added:** A new `server/manager.py` component manages GPU contention across all TTS backends and the ASR provider. Workers register at startup; before loading, a worker calls `request_load()` which evicts all eligible idle workers (not just one) ordered by VRAM usage then LRU. The eviction window is configurable via `RRV_BACKEND_RECENT_USE_WINDOW` (default 60 s). Backends and ASR reload on demand when a synthesis request arrives.

- **ASR migrated to Whisper subprocess worker:** Whisper now runs as a proper worker subprocess (`rrv-whisper/run_asr_worker.py`) using the same Unix socket protocol as all TTS backends. Load/transcribe/unload per batch. Whisper unloads itself after each transcription batch to release VRAM for TTS backends. `RRV_ASR_PROVIDER=whisper` is the only supported ASR path; Qwen-ASR, CrisperWhisper, and Cohere Transcribe providers have been removed.

- **CosyVoice `prompt_text` bug fixed:** `inference_zero_shot()` was incorrectly receiving the instruct system-prompt prefix as `prompt_text`. The system-prompt format is only valid for `inference_instruct2()`. Zero-shot now receives the raw reference transcript only, eliminating mid-text truncation caused by LLM token budget confusion.

- **`speech_rate` now passed to CosyVoice:** Both `inference_zero_shot()` and `inference_instruct2()` now receive `speed=clamp(speech_rate, 0.5, 2.0)`. Previously `speech_rate` was silently ignored for both CosyVoice backends.

- **CosyVoice added to client chunking policy:** `cosyvoice` and `cosyvoice_vllm` now have a dedicated `TextChunkingPolicy` profile (380 target / 480 hard cap, ListItemLimit=6, RepeatedSentenceLimit=2) matching Chatterbox Full conservatism. Previously these providers fell through to the generic 700/850 defaults.

- **`ChatterboxPreprocess` extended to CosyVoice:** Angle-bracket annotation stripping and dash-reconstruction cleanup now also run for CosyVoice providers in `RemoteTtsProvider.SubmitOggCoreAsync()`.

- **Settings checkbox init bug fixed:** Four checkboxes (`PhraseChunking`, `RepeatSuppressionEnabled`, `CompressionEnabled`, `SilenceTrim`) had hardcoded `IsChecked="True"` in AXAML, causing their `Click` handlers to fire during `InitializeComponent()` and overwrite saved settings with `true` before `LoadSettingsIntoUI()` ran. The AXAML defaults are removed and a `_uiInitializing` guard is added to all four handlers.

- **Synthesis response metrics headers added:** Both the v1 (`/api/v1/synthesize`) and v2 (`/api/v1/synthesize/v2`) endpoints now return `X-Input-Chars`, `X-Input-Words`, `X-Synth-Time`, `X-Duration`, and `X-Realtime-Factor` headers. The v2 submit response body also includes `input_chars` and `input_words`. The v2 SSE complete event includes all five metrics. These enable benchmark scripts to detect model truncation and measure throughput without audio transcription.

## What's New in v22

- **Project licensing finalized for public release:** The desktop client source is now documented as **GNU GPL v3**. File headers, repository license text, and release-facing documentation are aligned to GPL v3 rather than AGPL.

- **Voice Defaults tab added:** The client now supports **per-provider voice sample defaults**. These defaults are edited with the same profile editor used by the Voices tab, but they apply to a specific `sample_id` / voice rather than to a race slot.

- **Voice sample defaults are portable and server-syncable:** Voice sample defaults can be **exported/imported as JSON** and **pushed to / pulled from the server** as a full all-provider payload instead of only the currently selected provider.

- **Bespoke NPC voice resolution now has a real base layer:** When an NPC has a bespoke sample assigned, playback starts from the **sample default profile** for that sample. If no bespoke sample is set, playback falls back to the NPC race/dialect slot; if no race override exists, it falls back to the **Narrator** slot rather than a generic Human fallback.

- **Quick-assignment selectors now keep recency favorites:** Voice/sample and race/accent selectors maintain a small last-used ranking so the most recently saved items float to the top while older items naturally decay back to alphabetical order.

- **Voice Defaults editor trimmed for bespoke sample editing:** The sample-default editor hides slot-only concepts such as voice mode, single-voice selection, presets, and blend controls that do not apply when tuning a specific bespoke sample baseline.

- **Voice editor preview text presets added:** The profile editor now offers **Short**, **Medium**, and **Long** WoW-themed preview text presets, defaulting to the short sample for faster iteration.

- **Text shaping defaults are now visible and removable:** Built-in text shaping defaults are seeded as normal user-editable rules instead of being silently applied as hardcoded hidden transforms.

- **Barcode scan pacing is now dynamic:** Region scanning stays alive once a region is known, starts with a short high-frequency burst after successful reads, and then decays toward the user-configured idle scan ceiling rather than switching between a few rigid timing buckets.

- **Shutdown and capture coordination hardened:** Full-screen rescans no longer suppress region polling, and application shutdown now performs a stronger monitor stop so capture services do not keep the process resident after the main window closes.

## 1. Overview

RuneReader Voice adds spoken voice narration to World of Warcraft text by creating a data pipeline between a WoW addon (RuneReaderVoice) and the RuneReader desktop application. The addon encodes text selected by the Lua addon as QR barcodes displayed on-screen; RuneReader captures, decodes, and passes the text to a TTS engine which synthesizes and plays audio in real time. This is not limited to quests: the same pipeline can be used for quest dialog, NPC greetings, readable books, flight map text, and other text the addon chooses to orchestrate for narration.

Blizzard's in-game TTS is limited to legacy Windows XP/Vista era voices. This feature bypasses that limitation by routing voice generation through the host OS or a configurable AI voice engine, enabling natural-sounding narration for a broad set of in-game text surfaces under addon control.

---

## 1.1 Licensing

- **Desktop client license:** GNU General Public License, version 3 (**GPL v3**).
- **Desktop client source headers:** Use `SPDX-License-Identifier: GPL-3.0-only`.
- **Repository license file:** `LICENSE` contains the standard GNU GPL v3 text.
- **Bundled third-party code:** Third-party files must retain their original license and attribution. They should not be relicensed by blanket project-header changes.
- **Server project:** The server is documented separately and may use a different license if desired; the client design document does not rely on AGPL-specific network-use terms.

---

## 2. Goals

- Narrate quest dialog (greeting, detail, progress, completion) using natural-sounding voices.
- Narrate readable in-game books, flight map text, item text, and other addon-selected text surfaces.
- Assign distinct voice profiles by WoW speaker slot/theme, with per-NPC accent override capability.
- Operate entirely client-side in the local Kokoro path, while preserving a clean remote rendering path for higher-quality or GPU-backed providers.
- Cache generated audio using synthesis-aware identity so redundant renders are avoided locally and, when applicable, remotely.
- Provide clean extension points for multiple local and remote AI voice backends without redesigning the pipeline.
- Support both Windows and Linux platforms.
- Stream phrase-level audio as soon as the first phrase is synthesized, minimizing perceived latency.

---

## 3. Non-Goals (v1)

- Classic / Era / Season of Discovery support (may be backported later).
- Additional heavyweight local AI voice models beyond Kokoro. Higher-quality or GPU-intensive synthesis is expected from the Phase 5 HTTP server path.
- Gapless playback on Linux. GstAudioPlayer uses sequential PCM playback. True gapless requires a GStreamer playlist pipeline (deferred).
- Windows Piper integration. The new `piper1-gpl` (OHF-Voice fork) is Python-only and removes the standalone binary that `LinuxPiperTtsProvider` relied on. Windows Piper deferred until the libpiper C API ships.

---

## 4. System Architecture

### 4.1 WoW Addon (RuneReaderVoice — Lua)

- Captures quest dialog text via WoW frame events.
- Classifies each text segment by speaker role (NPC Male, NPC Female, Narrator).
- Splits text into word-boundary chunks sized to a configurable pad target (Small=50 bytes, Medium=135 bytes, Large=250 bytes, Custom up to 500 bytes; default Medium). All chunks in a segment pad to the same length so QR matrix version never changes mid-segment.
- Encodes each chunk as a Base45 QR payload with a 26-character structured header (protocol v05).
- Quest title and objective text are prefixed with ASCII SOH (`\x01`) sentinel characters by `core.lua` before concatenation; `SplitSegments` detects these and emits them as narrator segments.
- Pre-encodes all QR matrices at dialog-open time (not in the render loop).
- Cycles chunks in a timed display loop; RuneReader reads each one through an always-on region scan with a fast post-hit burst.
- Window close detected via `HookScript(OnHide)` on GossipFrame, QuestFrame, and ItemTextFrame. Close handling is intentionally delayed so the last QR remains visible briefly while the desktop reader catches up; opening a new dialog cancels any pending delayed close first.

### 4.2 RuneReader Voice Desktop Application (C# / Avalonia) — Standalone

- Continuously captures screen frames and scans for QR barcodes. Once a region is known, region polling stays active continuously while periodic full-screen rescans only relocate the region when the QR moves.
- Detects a TTS payload by inspecting the decoded header magic bytes.
- Discards packets with `FLAG_PREVIEW` set (settings panel live preview).
- Assembles multi-chunk payloads per segment; routes assembled text and speaker metadata to the active `ITtsProvider`. Whitespace-only segments still count toward dialog completion so a blank separator segment cannot stall an entire dialog.
- Providers synthesize to in-memory `PcmAudio`. The cache transcodes to OGG and decodes back to `PcmAudio` for playback. No file paths pass between the provider and the player.
- Plays `PcmAudio` via `WasapiStreamAudioPlayer` (Windows/NAudio) or `GstAudioPlayer` (Linux). ESC hotkey aborts playback if audio is playing; passes through to game if idle.
- NPC override resolution now prefers bespoke sample defaults first, then race/dialect defaults, then Narrator fallback. Unknown or generic humanoid NPCs must not silently fall back to Human.
- Persistent state is split between portable JSON settings (`settings.json`) and the SQLite database `runereader-voice.db`. SQLite stores NPC overrides, pronunciation rules, text shaping rules, and the cache manifest. JSON settings store provider selection, slot profiles, per-provider sample defaults, and capture/playback preferences.

### 4.3 TTS Provider (Pluggable)

The TTS backend is abstracted behind `ITtsProvider`. All providers synthesize to `PcmAudio`. No provider creates temporary files. The cache layer owns all on-disk writes.

### 4.4 TTS HTTP Server (Phase 5 — Separate Project)

An optional standalone server the desktop client can call instead of synthesizing locally. Supports multiple simultaneous LAN clients and higher-quality or GPU-accelerated synthesis. Shared L2 render cache across all clients. The implemented server backend set currently includes Kokoro-82M ONNX, F5-TTS, Chatterbox Turbo, Chatterbox (full), Chatterbox Multilingual, CosyVoice3, CosyVoice3-vLLM, LuxTTS, and three Qwen3-TTS backends. The client remains authoritative over text, provider selection, voice choice, and DSP. See Section 21.

---

## 5. Data Pipeline

### 5.1 Pipeline Overview

| Stage | Description |
|---|---|
| RvBarcodeMonitor | Runs continuous region polling plus periodic full-screen relocation scans, decodes valid RV packets, and fires `OnPacketDecoded` per non-preview packet. |
| TtsSessionAssembler | Collects chunks by DIALOG ID. Fires `OnSegmentComplete` when all chunks arrive. Applies NPC race override before resolving voice slot. Fires `OnSessionReset` on DIALOG ID change. Sets `AppServices.LastSegment` after each completion. |
| PlaybackCoordinator | Dequeues segments, checks cache (`TryGetDecodedAsync` returns `PcmAudio`), calls `SynthesizePhraseStreamAsync` for cache misses, feeds `PcmAudio` to `PlaylistPlayAsync`. |
| ITtsProvider / TtsAudioCache | Provider yields `PcmAudio` phrases. Cache encodes each phrase to OGG in `StoreAsync`. Cache reads decode OGG back to `PcmAudio` via NVorbis. |
| IAudioPlayer | `WasapiStreamAudioPlayer` (Windows, NAudio) streams decoded `PcmAudio` via `BufferedWaveProvider`. `GstAudioPlayer` (Linux) plays PCM sequentially. Speed and volume applied per-session. |

### 5.2 Session Reset vs. Source Gone

- **OnSessionReset** (new DIALOG ID): cancels current playback immediately, clears queue, restarts coordinator loop. This is the only hard interrupt.
- **OnSourceGone** (QR frame disappears): intentional no-op. Queued audio plays to completion.
- **ESC hotkey**: cancels playback via `CancellationToken`. Passes ESC through to game if idle.

---

## 6. PCM-First Audio Architecture

### 6.1 Design Rationale

Prior to v13, providers wrote WAV files to a temp directory, the cache stored WAVs and transcoded to OGG in the background, and the audio player read files from disk. v13 eliminates all temporary files and the two-phase write strategy.

New contract:
- Providers synthesize to `PcmAudio` (interleaved float32, sampleRate, channels). No file I/O in the provider.
- Cache receives `PcmAudio`, encodes to OGG synchronously (on a thread-pool thread), and stores the OGG as the sole cached artifact.
- Cache reads (`TryGetDecodedAsync`) decode the stored OGG back to `PcmAudio` using NVorbis before returning.
- The audio player receives `PcmAudio` and handles resampling, channel conversion, and device output internally.

### 6.2 PcmAudio Type

```csharp
public sealed class PcmAudio
{
    public float[] Samples { get; }   // interleaved, range [-1, 1]
    public int SampleRate  { get; }
    public int Channels    { get; }
}
```

### 6.3 Cache OGG Strategy

`StoreAsync` receives `PcmAudio`, applies optional silence trimming (currently a pass-through stub), transcodes to OGG synchronously via `Task.Run`, and writes the OGG as the manifest entry. There is no intermediate WAV, no play-first strategy, and no background compression task.

- OGG quality: `oggQuality / 10f` (0.0–1.0), default 4 → 0.4 (~64 kbps). Configurable in advanced settings.
- OGG encoder: OggVorbisEncoder NuGet (pure managed, cross-platform).
- OGG decoder for cache reads: NVorbis (pure managed).
- Non-OGG entries found in the manifest are invalidated and deleted on read (migration guard for pre-v13 WAV entries).

> *NOTE: Silence trimming (`TrimSilence`) is present in the post-processing layer but is currently a pass-through stub. Implementation deferred.*

---

## 7. Phrase-Level Streaming Synthesis

### 7.1 TextSplitter

`TextSplitter` breaks long text into phrases for local providers (Kokoro). Sentence boundaries are preferred; clause and comma boundaries are used as fallback. `TextChunkingPolicy` wraps `TextSplitter` and applies per-provider limits:

| Provider | TargetChars | HardCapChars | ListItemLimit | RepeatedSentenceLimit |
|---|---|---|---|---|
| Kokoro | 850 | 1050 | 12 | 5 |
| F5-TTS | 575 | 725 | 10 | 4 |
| Chatterbox Turbo | 600 | 720 | 8 | 3 |
| Chatterbox Full | 380 | 480 | 6 | 2 |
| CosyVoice / CosyVoice-vLLM | 380 | 480 | 6 | 2 |

CosyVoice3 is an LLM-based hybrid AR+flow model with a similar practical context limit to Chatterbox Full. Limits are conservative pending empirical benchmarking. High-exaggeration profiles (≥1.0) apply a further 70%/75% reduction on Chatterbox target/hard cap only.

- Sentence endings (`. ! ? ...`): punctuation stays with the left chunk.
- Clause breaks (`, ; :`): punctuation moves to the start of the right chunk.
- **MinFragmentWords = 3**: short trailing fragments are merged forward.
- Abbreviation and decimal protection: `Mr.`, `1.5` etc. do not trigger splits.

### 7.2 TextPreprocess (Chatterbox and CosyVoice)

Text sent to any Chatterbox or CosyVoice backend passes through `ChatterboxPreprocess()` before synthesis:

1. Inline `<annotation text>` (WoW flavor notes like `<A water stain has blurred the ink>`) → `"$1"` — annotation text preserved but brackets stripped so the model reads it as prose rather than markup.
2. Dash-reconstructed sentence joins (`word-...-word` from paragraph splits) → `word. Word` — clean sentence break.
3. Orphaned leading/trailing dashes → stripped.
4. Multiple spaces collapsed.

### 7.3 SynthesizePhraseStreamAsync

Used by local providers (Kokoro) only. Returns `IAsyncEnumerable<(PcmAudio audio, int phraseIndex, int phraseCount)>`.

- Splits text via TextSplitter into N phrases.
- Enqueues all phrase ONNX inference jobs immediately.
- Uses `Channel<(int index, PcmAudio audio)>` to collect completions as they arrive (any order).
- Out-of-order arrivals buffered; yielded to caller in strict phrase order.

Remote providers receive chunked text directly via the HTTP synthesis endpoint and return a single OGG per chunk. The coordinator decodes each returned OGG to `PcmAudio`, normalizes remote chunks to a common PCM format when sample rate or channel count differ, and then concatenates them into a single `PcmAudio` before playback.

### 7.4 Parallel Synthesis Pipeline (PlaybackCoordinator)

All segments in a dialog synthesize concurrently. When `EnqueueSegment(N)` is called, a `Task<PcmAudio?>` fires immediately and is stored in `_synthTasks[N]`. The playback loop awaits tasks in strict index order:

```
EnqueueSegment(0) → Task[0] fired
EnqueueSegment(1) → Task[1] fired
EnqueueSegment(2) → Task[2] fired

PlaybackLoop: await Task[0] → play → await Task[1] → play → await Task[2] → play
```

While segment 0 plays, segments 1 and 2 are already synthesizing. If segment N+1 finishes before N finishes playing, it waits in the task map. If N+1 is not ready when N finishes, the loop awaits it — natural buffer-underrun handling with no polling.

Session reset (new dialog ID or ESC) cancels the session CTS, calls `TrySetCanceled()` on all pending `_segmentReady` TCS entries to unblock the loop cleanly, stops the player, and clears `_synthTasks` and `_segmentReady`.

### 7.5 Cache Key

The synthesis cache key incorporates the slot string as a namespace prefix:

```
{slot}:{voiceIdentityKey}
e.g. "Narrator:M_Narrator|en-us|0.79|cfg:0.10|ex:0.50"
     "BloodElf/Male:M_Bloodelf_Citizen_3|en-gb|0.81|cfg:0.21|ex:0.50"
```

Without the slot prefix, two slots defaulting to the same sample would share cache entries. Bespoke entries: `{slot}:{voiceId}+bespoke:{sampleId}`.

---

## 8. Audio Playback

### 8.1 IAudioPlayer Interface

```csharp
Task PlayAsync(PcmAudio audio, CancellationToken ct);
Task PlaylistPlayAsync(IAsyncEnumerable<PcmAudio> audioChunks, CancellationToken ct);
void Stop();
float Volume { get; set; }   // 0.0–1.0
float Speed  { get; set; }   // 0.75–1.5
void SetOutputDevice(string? deviceId);
IReadOnlyList<AudioDeviceInfo> GetOutputDevices();
```

`PlaylistPlayAsync` accepts a streaming `PcmAudio` enumerable — it does not require all chunks to be known upfront.

### 8.2 WasapiStreamAudioPlayer (Windows)

Uses NAudio `WasapiOut` (latency: 100ms shared mode) with a `BufferedWaveProvider` (2-second buffer duration). Replaces `WinRtAudioPlayer` / `MediaPlaybackList`. The 100ms WASAPI latency (increased from 20ms in v17) eliminates buffer underrun clicks on synthesis-heavy dialogs.

- Output format: 48000 Hz, 16-bit, stereo. Input `PcmAudio` resampled/converted as needed.
- `BufferedWaveProvider` fed by a background `_feedTask` that pulls `PcmAudio` chunks from the playlist enumerator.
- Speed control applied via pitch-corrected tempo (NAudio sample providers).
- Volume applied via `VolumeSampleProvider`.
- `SetOutputDevice(deviceId)` supported; null = system default.
- `CancellationToken` cancels the feed task; `WasapiOut.Stop()` called on cancellation or completion.

> *NOTE: LIMITATION: Speed adjustment mid-playlist may cause a brief audio glitch as the buffer is flushed and re-initialized.*

### 8.3 GstAudioPlayer (Linux)

Sequential fallback: `PlaylistPlayAsync` iterates the `PcmAudio` stream and calls `PlayAsync` for each chunk in order. GStreamer EOS detection is still a stub. True gapless playback deferred.

> *NOTE: LIMITATION: GstAudioPlayer has not yet been updated to the new `PcmAudio`-based `IAudioPlayer` interface. Update required before Linux playback is functional with the v13 pipeline.*

---

## 9. NPC Race Override System

### 9.1 Purpose

The RV protocol provides a RACE byte per NPC, but it reflects the NPC's base race and may not accurately capture the intended accent (e.g. a custom creature, a cross-faction NPC, or an NPC whose voice clearly doesn't match their race). The NPC Race Override system allows the user to explicitly assign an accent group to any NPC by NPC ID, and optionally assign a bespoke reference voice sample for voice-matching providers.

### 9.2 Source Hierarchy

Three tiers, highest priority wins:

- **Local:** user-entered on this machine. Full CRUD. Stored in `runereader-voice.db`.
- **CrowdSourced:** received from server poll. Read-only on the client; shadowed by a local entry for the same NPC ID.
- **Confirmed:** hand-verified by server admin. Read-only on the client; shadowed by a local entry.

### 9.3 Data Model — NpcRaceOverride

| Field | Type | Description |
|---|---|---|
| NpcId | int | NPC ID from the RV packet NPC field (unit GUID segment 6). |
| RaceId | int | Race ID that maps into RaceAccentMapping (player races 1–37, creature types 0x50–0x58). |
| AccentGroup | AccentGroup | Derived from RaceId via `RaceAccentMapping.ResolveAccentGroup`. Not persisted separately. |
| Notes | string? | Optional user label (e.g. "Thrall", "Rexxar"). |
| BespokeSampleId | string? | Optional provider-neutral logical sample ID used by sample-based remote providers such as Chatterbox-family and F5. Narrator segments are never affected regardless of `NpcId`. Null = use race slot default. |
| BespokeExaggeration | float? | Optional NPC-local exaggeration override. Null = inherit from the sample default profile when a bespoke sample is set, otherwise inherit from the resolved race slot profile. |
| BespokeCfgWeight | float? | Optional NPC-local `cfg_weight` override. Null = inherit from the sample default profile when a bespoke sample is set, otherwise inherit from the resolved race slot profile. |
| Source | NpcOverrideSource | Local / CrowdSourced / Confirmed. |
| Confidence | int? | Server-assigned vote count (null for local entries). |
| UpdatedAt | double | Unix timestamp of last update. Used for delta sync polling. |

### 9.4 Storage — NpcRaceOverrideDb

- Thin wrapper over `RvrDb` exposing domain-model API.
- Table: `NpcRaceOverrides` with `int` PK on `NpcId`.
- `UpsertAsync(npcId, raceId, notes, bespokeSampleId?, bespokeExaggeration?, bespokeCfgWeight?, source, confidence)`.
- `MergeFromServerAsync(IEnumerable<NpcRaceOverride>)`: merges server records with Local-wins logic — Local entries are never overwritten.
- `DeleteAsync(int npcId)`, `GetAllAsync()`, `GetOverrideAsync(int npcId)`.

### 9.5 Effective Resolution Order

Resolution for a non-narrator NPC segment now follows this order:

1. **Bespoke sample assigned** → use the per-provider **sample default profile** for that `sample_id`, then apply any NPC-local bespoke tweaks.
2. **No bespoke sample assigned** → use the resolved **Race/Dialect** slot profile.
3. **No race/dialect override available** → fall back to the **Narrator** slot profile.

This intentionally avoids treating WoW's broad `Humanoid` classification as a meaningful voice choice fallback.

### 9.6 TtsSessionAssembler Integration

- `LoadOverridesAsync()` at startup: pre-loads all DB entries into `_npcVoiceStore` (`Dictionary<int, NpcVoiceOverride>`).
- `NpcVoiceOverride` record struct carries `RaceId`, `BespokeSampleId`, `BespokeExaggeration`, `BespokeCfgWeight`.
- On segment complete: if NpcId != 0, checks `_npcVoiceStore` for an override before calling `RaceAccentMapping.Resolve()`.
- Bespoke fields are embedded into `AssembledSegment` at fire time and carried through to `PlaybackCoordinator`.
- Early-chunk stash key includes `SeqIndex` to prevent stale subs from contaminating same-shaped segments.
- Non-zero sub matching requires `Subs[0] != null` (anchor populated) before accepting into an accumulator.

### 9.7 UI — Last NPC Panel

Appears below the status panel after any NPC segment completes (NpcId != 0). Hidden for narrator/book segments.

- **Row 0:** NPC ID label, notes box, Save, Clear.
- **Row 1:** Voice accent dropdown (full race/creature-type list). Recently saved choices float to the top through a small decaying recency score, then fall back to alphabetical ordering.
- **Row 2:** Two-level voice sample picker — visible for sample-based remote providers such as Chatterbox-family and F5 regardless of whether sub-voices or variants have already been loaded. Dropdown 1: distinct **provider-neutral base sample IDs**. Dropdown 2: variant suffixes for the selected base (`(default)`, `slow`, `fast`, `quiet`, `loud`, `breathy`) — hidden when the base has no variants. Combined selection produces the full logical sample ID (e.g. `M_WWZ_10-slow`). Populated from `GetAvailableVoices()` filtered by provider duration limits (F5-TTS ≤11s, Chatterbox ≤40s). First item is "(race default)". Voice list warmed at startup via background `RefreshVoiceSourcesAsync`. Save stores the full logical sample ID as `BespokeSampleId`, not an internal provider-tagged filename stem. The voice/sample selector uses the same decaying recency ordering so recently saved bespoke voices are easier to reach again.
- Bespoke sample applies only to NPC voice slots — never narrator, even when narrator segments share the same NpcId.

### 9.8 UI — NPC Voices Tab

Full CRUD grid for all stored overrides (local + server entries). Columns: NPC ID, Notes, Race/Accent, Source, Edit, Delete.

- Local rows: Edit and Delete buttons active.
- Server rows (CrowdSourced / Confirmed): read-only. "Override locally" button creates a local shadow entry.
- **Export JSON / Import JSON:** serializes/deserializes all local entries including bespoke fields. JSON format version "1".
- **Push to Server / Pull from Server:** contribute local overrides to the community DB or pull server records (full pull, Local-wins merge).
- Refresh button reloads grid from DB.
- Status label shows result of export/import/push/pull operations.

### 9.9 Community Sync

The server exposes NPC override endpoints for crowd-sourcing:

- `GET /api/v1/npc-overrides/since?t={unix_ts}` — poll for records updated after timestamp. `t=0` returns all. Open, no auth.
- `POST /api/v1/npc-overrides` — contribute a record. Requires `RRV_CONTRIBUTE_KEY` Bearer token if configured.
- `PUT /api/v1/npc-overrides/{npc_id}` — admin confirm/edit. Requires `RRV_ADMIN_KEY`.

`NpcSyncService` on the client polls every 5 minutes. On first load (`FirstLoadComplete = false`), pulls all four default types from server before setting `FirstLoadComplete = true`. `ContributeByDefault = true` enables silent background contribution on every local save.

---

## 10. Audio Cache (Desktop Client)

### 10.1 OGG-Only Strategy

`StoreAsync` receives `PcmAudio`, applies optional silence trimming (pass-through stub), transcodes to OGG synchronously via `Task.Run`, and writes OGG as the sole manifest entry. There is no intermediate WAV and no background compression task.

`TryGetDecodedAsync` checks the DB manifest, validates the file exists and is `.ogg`, decodes via NVorbis, and returns `PcmAudio`. If the DB row exists but the file is missing, the row is deleted and null is returned — the caller re-synthesizes and re-stores. Non-OGG entries are invalidated and deleted.

### 10.2 Cache Key

SHA-256 hash of fields joined by null bytes (`\x00`): `normalized_text + "\x00" + voiceId + "\x00" + providerId + "\x00" + dspKey`. Truncated to 16 hex characters. `voiceId` is the resolved voice identity string (e.g. `"af_sarah"` or a `mix:` spec), not the slot name. `dspKey` is produced by `DspProfile.BuildCacheKey()` — concatenation of `DspEffectItem.BuildCacheKey()` for each enabled effect in list order, joined by `+`. Each item's key is `{kindIndex}|param1=v1|param2=v2...`. Empty string when DSP is neutral, disabled, or the effects list is empty. Per-phrase cache keys use reconstructed phrase text (`GetPhraseText`), not the full segment text. The null-byte separator prevents collisions from adjacent field concatenation and must be replicated exactly by the server's cache key algorithm.

### 10.3 DB Schema — RvrDb

All four tables live in `runereader-voice.db` managed by `RvrDb`:

| Table | Row Type | Purpose |
|---|---|---|
| `NpcRaceOverrides` | `NpcRaceOverrideRow` | NPC accent assignments + optional voice-matching sample ID |
| `PronunciationRules` | `PronunciationRuleRow` | User pronunciation rules |
| `TextSwapRules` | `TextSwapRuleRow` | User text swap rules |
| `AudioCacheManifest` | `AudioCacheManifestRow` | Cache file registry |

`RvrDb` exposes `VacuumAsync()`, `GetDbFileSizeBytes()`, and `ClearTableAsync(RvrTable)` for maintenance. `InitializeAsync()` creates all four tables via `CreateTableAsync<T>()`.

### 10.4 Storage Locations

| Path | Contents |
|---|---|
| `<ConfigDir>/runereader-voice.db` | Unified SQLite database (all four tables) |
| `<CacheDir>/` | OGG audio files (path configurable via settings or env var) |
| `<ConfigDir>/settings.json` | User settings, slot profiles, and per-provider voice sample defaults |
| `<AppDir>/models/` | Kokoro ONNX model files (shared with Python server) |

### 10.5 Eviction

- No TTL — quest dialog is static content. A cached line is valid indefinitely.
- Configurable max size (default 500 MB). LRU eviction on startup and after each `StoreAsync` when limit is exceeded.
- LRU ordered by `LastAccessedUtcTicks` (server-clock only; no client-supplied timestamps).
- Manual clear button in Advanced > Cache expander.

---

## 11. Rule Stores

### 11.1 PronunciationRuleStore

Instance class injected via `AppServices.PronunciationRules`. Backed by the `PronunciationRules` table in `RvrDb`.

- `GetAllEntriesAsync()` → `List<PronunciationRuleEntry>` (all rows, ordered by priority/match text in UI).
- `LoadUserRulesAsync()` → `IReadOnlyList<PronunciationRule>` (enabled, valid rows as domain objects — used to rebuild the processor).
- `UpsertRuleAsync(entry)`: natural key is `(MatchText, Scope, WholeWord, CaseSensitive)`.
- `DeleteRuleAsync(entry)`: deletes by natural key.
- `ClearAllAsync()`: truncates table.

`PronunciationRuleRowExtensions` provides `ToRow()` / `ToEntry()` mapping between `PronunciationRuleEntry` and `PronunciationRuleRow`.

### 11.2 TextSwapRuleStore

Instance class injected via `AppServices.TextSwapRules`. Backed by the `TextSwapRules` table in `RvrDb`.

- Same async API pattern as `PronunciationRuleStore`.
- Natural key is `(FindText, WholeWord, CaseSensitive)`.

`TextSwapRuleRowExtensions` provides `ToRow()` / `ToEntry()` mapping. On first-run database creation, the default text shaping rules are seeded into the table as normal editable rows instead of being applied as invisible hardcoded transforms.

### 11.3 Import / Export

Both rule stores, slot voice profiles, and per-provider voice sample defaults support JSON import/export via Avalonia `StorageProvider` file pickers. Import is additive (upsert/replace by natural key — it does not clear unrelated entries).

---

## 12. Settings UI

### 12.1 Top Chrome (Always Visible)

- Title bar: "RuneReader Voice" + status badge (provider state).
- Live status panel: Capture / Session / Playback / Cache status indicators.
- Volume row: Vol label + Volume slider + percentage label + Start/Stop button.

### 12.2 Tab Layout

All tabs use a two-column `Grid` layout (`ColumnDefinitions="*,*"` or `"1.1*,0.9*"`):

| Tab | Left Column | Right Column |
|---|---|---|
| Settings | TTS Provider, Playback | Dialog Sources |
| Voices | Voice grid (code-behind populated) + Export/Import buttons | — |
| Voice Defaults | Per-provider sample list, Edit / Refresh / Import / Export / Push / Pull | — |
| NPC Voices | Last NPC panel, NPC override grid | — |
| Advanced | Capture, Playback, Cache | Audio, Piper, Diagnostics, Hotkey |
| Pronunciation | Workbench, Sound Picker, Preview | Saved rules list, rule authoring controls |
| Text Shaping | Workbench + preview | Saved rules list + rule authoring controls |

### 12.3 Voice Assignment Grid (Voices Tab)

65 voice slots total: 1 Narrator + 32 accent groups × Male + Female (includes Illidari added in v18). Every playable race has its own dedicated slot pair — accent groups are not shared between races in the catalog. Creature-type slots (Dragonkin, Elemental, Giant, Mechanical) each have Male/Female pairs. Sort order groups: 0=Narrator, 10s=Alliance, 100s=Horde, 200s=Neutral/Cross-faction, 300s+=Creature types. The grid is populated entirely in code-behind by `PopulateVoiceGrid()` iterating `NpcVoiceSlotCatalog.All`.

### 12.4 Pronunciation Tab (Right Column)

The right column mirrors the Text Shaping tab layout:

- `PronRuleList` ListBox (Height=180): all saved rules, ordered by priority/match text. Clicking a row loads it into the workbench fields on the left.
- Scope selector, Accent Group selector, Whole word / Case sensitive / Enabled checkboxes.
- Notes field.
- `WrapPanel` with Save Rule, Delete Rule, Reload Rules, Export Rules, Import Rules buttons.
- Status text block.

### 12.5 Speak/Action Button Wrapping

The Speak Original / Speak Processed / Copy Preview / Clear buttons on the Pronunciation left column are in a `WrapPanel` (`ItemWidth=130`) so they wrap rather than overflow into the right column at narrow window widths.

### 12.6 Voice Profiles — Import / Export

Slot voice profiles are serialized as an **all-provider** payload rather than only the currently selected provider. This avoids losing profiles for non-active providers during push/pull or round-trip export/import. `ApplyVoiceProfile` remains the shared helper used by both the edit dialog and the import path.

### 12.7 Voice Defaults Tab

The Voice Defaults tab presents all currently available voices/samples for the active provider and lets the user edit a **base profile per sample**. These defaults are stored in `settings.json` under `PerProviderSampleProfiles` and can be exported, imported, pushed to the server, or pulled back as an all-provider payload.

The Voice Defaults editor intentionally hides slot-only controls such as voice mode, preset selection, and blend configuration when they do not apply to editing a specific bespoke sample baseline.

---

## 13. Addon File Structure

RuneReaderVoice is a standalone WoW addon (separate from RuneReaderRecast).

| File | Purpose |
|---|---|
| RuneReaderVoice.toc | Addon manifest; Interface 120001 (Retail) |
| core.lua | Event registration, dialog dispatch, window close hooks |
| payload.lua | Text chunking, padding, Base45 packet encoding, QR payload building |
| frames_qr.lua | QR frame creation, texture pool, chunk cycling, pre-encoding |
| config.lua | Default settings and RuneReaderVoiceDB initialization |
| config_panel.lua | Settings UI (retail Settings API), live preview management |
| QRcodeEncoder.lua | Pure-Lua QR matrix generator (bundled) |

---

## 14. WoW Events and Dialog Sources

All confirmed working on Retail WoW (Interface 120001).

### 14.1 Dialog Events

| Event | Text API | Dialog Type | Notes |
|---|---|---|---|
| GOSSIP_SHOW | C_GossipInfo.GetText() | NPC greeting / gossip | Deferred 1 frame via C_Timer.After(0). Falls back to GetGossipText(). |
| QUEST_GREETING | GetGreetingText() | Multi-quest NPC greeting | Fires for NPCs with multiple available quests. |
| QUEST_DETAIL | GetTitleText() + GetQuestText() + GetObjectiveText() | Quest accept dialog | Title and objective prefixed with `\x01` SOH sentinel before concatenation; `SplitSegments` emits them as narrator segments. |
| QUEST_PROGRESS | GetProgressText() | Quest in-progress check-in | |
| QUEST_COMPLETE | GetRewardText() | Quest completion / reward | |
| QUEST_FINISHED | — | Quest accept / decline / auto-accept | Fires on accept OR decline; may fire twice. Uses the shared delayed-close timer so the final QR remains visible briefly after the frame closes. Marks `_questDetailOpen = false` immediately. |
| ITEM_TEXT_BEGIN | — | Book / readable item opened | Sets `_bookActive`. If `BookScanMode` enabled, sets `_bookScanning = true` to begin full-scan collection. |
| ITEM_TEXT_READY | ItemTextGetText() + ItemTextGetItem() | In-game books / readable items | In scan mode: collects each page and calls `ItemTextNextPage()` until exhausted, then `ItemTextPrevPage()` back to page 1, then dispatches all pages as one combined dialog. In per-page mode: dispatches each page independently as the player clicks through. |
| ITEM_TEXT_CLOSED | — | Book / readable item closed | Clears `_bookActive`, `_bookScanning`, `_bookPages`. Calls `StopDisplay`. |
| GOSSIP_CLOSED | — | Gossip frame closed | No-op handler; cleanup via `GossipFrame:OnHide` hook. |

### 14.2 Stop-on-Close Events

The following events schedule the shared delayed close used by the QR display cleanup path:

| Event | Trigger |
|---|---|
| TAXIMAP_CLOSED | Flight master map closed |
| ADVENTURE_MAP_CLOSE | Adventure / Chromie Time map closed |
| MERCHANT_CLOSED | Merchant window closed |
| TRAINER_CLOSED | Trainer window closed |
| BANKFRAME_CLOSED | Bank window closed |
| MAIL_CLOSED | Mailbox closed |
| AUCTION_HOUSE_CLOSED | Auction house closed |
| LOOT_CLOSED | Loot window closed |

The delayed close is currently set to **5 seconds**. Opening a new dialog cancels any pending close before the new QR is shown.

**Events explicitly NOT used:**

- `QUEST_LOG_UPDATE` — fires constantly during normal play (quest progress, item pickups, objective completion, and many other game events unrelated to closing a dialog). Using it as a close detector causes false stops mid-dialog. Quest frame close is covered by `QuestFrame:OnHide` in the window close hooks instead.

### 14.3 Window Close Detection

Close detection uses `HookScript("OnHide")` on Blizzard dialog frames rather than relying on events alone. Events do not fire on all close paths (Escape key, clicking away, programmatic close). Frame hooks fire unconditionally on every hide.

- `GossipFrame:OnHide` → schedule the shared delayed close.
- `QuestFrame:OnHide` → schedule the shared delayed close unless a newer dialog has already taken ownership.
- `ItemTextFrame:OnHide` → schedule the shared delayed close and clear `_bookActive`, `_bookScanning`, `_bookPages`.

### 14.4 NPC Gender Detection

- Primary: `UnitSex("questnpc")` — dedicated questnpc token, more reliable than "target" during quest dialog.
- Fallback: `UnitSex("npc")`, then `UnitSex("target")`.
- Returns: 1=unknown, 2=male, 3=female. Unknown maps to neutral/Narrator voice slot.

---

## 15. QR Payload Protocol (v05)

### 15.1 Payload Structure

Protocol was bumped from v04 to v05 when SEQ/SEQTOTAL were added to support multi-segment dialogs (narrator splits, quest title/objective). The header grew from 22 to 26 ASCII characters.

```
[ MAGIC(2) | VER(2) | DIALOG(4) | SEQ(2) | SEQTOTAL(2) | SUB(2) | SUBTOTAL(2) | FLAGS(2) | RACE(2) | NPC(6) | BASE64_PAYLOAD ]
```

| Field | Chars | Format | Description |
|---|---:|---|---|
| MAGIC | 2 | "RV" | Identifies a RuneReaderVoice TTS packet. |
| VER | 2 | "05" | Protocol version. |
| DIALOG | 4 | Hex 0000–FFFF | Dialog block ID. Increments once per NPC interaction. Change signals new dialog — assembler discards in-progress state. |
| SEQ | 2 | Hex 00–FF | 0-based segment index within this dialog (0 = NPC voice; subsequent = narrator splits etc.). |
| SEQTOTAL | 2 | Hex 01–FF | Total segment count for this dialog. Known upfront from `SplitSegments` before any QR encoding begins. |
| SUB | 2 | Hex 00–FF | 0-based barcode chunk index within this segment. |
| SUBTOTAL | 2 | Hex 01–FF | Total barcode chunk count for this segment. SUB == SUBTOTAL-1 is the last chunk. |
| FLAGS | 2 | Hex bitmask | Speaker / control flags. See Section 15.2. |
| RACE | 2 | Hex 00–FF | NPC race or creature type ID. See Section 15.3. |
| NPC | 6 | Hex 000000–FFFFFF | NPC ID from unit GUID segment 6. "000000" for non-creature units, books, and preview packets. |
| BASE64_PAYLOAD | Variable | Base64 | Base64-encoded text chunk, space-padded to the configured pad size before encoding. |

### 15.2 FLAGS Byte

| Bit | Mask | Name | Meaning |
|---:|---:|---|---|
| 0 | 0x01 | FLAG_NARRATOR | Narrator voice. RuneReader assigns Narrator slot regardless of gender/race. |
| 1 | 0x02 | GENDER_MALE | NPC is male. Bits 1+2 encode gender: 00=unknown, 01=male, 10=female. |
| 2 | 0x04 | GENDER_FEMALE | NPC is female. |
| 3 | 0x08 | FLAG_PREVIEW | Settings panel live preview. RuneReader MUST discard and never synthesize. |
| 4–7 | 0xF0 | (reserved) | Reserved for future use. Must be zero. |

### 15.3 RACE Byte

| Range | Meaning |
|---|---|
| 0x00 | Unknown / undetectable → narrator fallback voice |
| 0x01–0x3F | Player race IDs (direct from `UnitRace()` raceID) |
| 0x40–0x4F | Reserved for future player races |
| 0x50 | Humanoid (non-playable NPC) |
| 0x51 | Beast |
| 0x52 | Dragonkin |
| 0x53 | Undead (non-Forsaken) |
| 0x54 | Demon / Illidari — maps to `AccentGroup.Illidari` |
| 0x55 | Elemental |
| 0x56 | Giant |
| 0x57 | Mechanical |
| 0x58 | Aberration |
| 0x59–0xEF | Reserved creature types |
| 0xF0–0xFF | Reserved for future protocol use |
| (unmapped) | Narrator fallback |

### 15.4 Chunk Sizing

Chunks are split on word boundaries to avoid breaking mid-word. The target pad size is configurable per-installation:

| Preset | Pad bytes | Approx. QR payload chars |
|---|---:|---:|
| Small | 50 | ~90 |
| Medium (default) | 135 | ~206 |
| Large | 250 | ~354 |
| Custom | 50–500 | — |

All chunks in a segment pad to the same target length so the QR matrix version never changes mid-segment (stable QR dimensions = faster decode by the reader).

### 15.5 Segment Splitting and SOH Sentinel

`SplitSegments` in `payload.lua` produces multiple segments from a single dialog text, each with its own SEQ index and FLAGS:

1. **`<Angle bracket>` passages** become narrator segments; brackets are stripped. Preceding and following NPC speech become separate segments.
2. **Quest title and objective text** are marked by `core.lua` inserting an ASCII SOH (`\x01`) sentinel before concatenating fields. `SplitSegments` detects `\x01` on the first line (title) and in the final segment (objectives), splitting there and emitting those parts as narrator segments.
3. **SEQTOTAL is known upfront** — all segments are counted by `SplitSegments` before any QR encoding begins, so every chunk in the dialog carries the correct total from the first barcode scanned.

Example: Quest detail with title "Find the Artifact" + NPC body "The threat is real." + objective "Retrieve the Idol" produces segments: narrator (title) → NPC (body) → narrator (objective). SEQTOTAL=3 on all chunks.

---

## 16. ITtsProvider Interface

```csharp
interface ITtsProvider : IDisposable
{
    string ProviderId    { get; }
    string DisplayName   { get; }
    bool IsAvailable     { get; }
    bool RequiresFullText { get; }
    bool SupportsInlinePronunciationHints { get; }

    IAsyncEnumerable<(PcmAudio audio, int phraseIndex, int phraseCount)>
        SynthesizePhraseStreamAsync(string text, VoiceSlot slot, string tempDir, CancellationToken ct);

    Task<PcmAudio> SynthesizeAsync(string text, VoiceSlot slot, CancellationToken ct);

    string ResolveVoiceId(VoiceSlot slot);
    IReadOnlyList<VoiceInfo> GetAvailableVoices();
}
```

### 16.1 Provider Implementations

| Provider | Platform | Notes |
|---|---|---|
| KokoroTtsProvider | Windows / Linux (CPU) | Primary AI voice provider. 54 voices. `SupportsInlinePronunciationHints = true`. Parallel phrase encoding via Channel. |
| WinRtTtsProvider | Windows only | WinRT SpeechSynthesizer. `SupportsInlinePronunciationHints = false`. |
| LinuxPiperTtsProvider | Linux only | Piper subprocess. Windows Piper deferred. |
| HttpTtsProvider | All platforms | Calls Phase 5 HTTP server. `RequiresFullText = true`. Returns OGG decoded to PcmAudio. Falls back to local rendering on server unavailability. |
| NotImplementedTtsProvider | All | Placeholder for unimplemented backends. |

---

## 17. Performance Characteristics

### 17.1 Addon (Phase 1 Measurements)

| Metric | Value | Notes |
|---|---|---|
| CPU while display active | ~0.3% | Measured in-game during quest dialog cycling. |
| CPU burst on dialog open | ~5% | Pre-encoding all QR matrices. Imperceptible — occurs during UI interaction. |
| Chunk display time (default) | 100ms | 20× read margin vs. RuneReader 5ms capture interval. |

### 17.2 Kokoro Synthesis Performance (Observed)

| Metric | Value | Notes |
|---|---|---|
| First-phrase latency | ~200–400ms | Time from segment complete to first audio playing. |
| Parallel phrase encoding | Yes | All phrase jobs enqueued to ONNX immediately. |
| Cache lookup | ~5× faster vs. v13 | SQLite indexed read replaces full JSON deserialize + dictionary scan. |

---

## 18. Development Phases

| Phase | Status | Summary |
|---|---|---|
| Phase 1 | COMPLETE | Addon QR protocol, dialog capture, segmented payloads, preview flags, race/NPC metadata, pre-encoding, frame hooks. |
| Phase 2 | COMPLETE | Local TTS integration, Kokoro support, settings UI, slot assignment, profile-aware voice refinement, dialect/language support, speech rate, presets, cache identity updates, voice editor UX. |
| Phase 3 | COMPLETE | Phrase-level streaming, TextSplitter, Channel-based parallel phrase encoding, PCM-first architecture, WasapiStreamAudioPlayer, OGG-only cache. |
| Phase 4 | COMPLETE | Linux support: GStreamer playback stub, Piper provider stub, cross-platform abstraction. |
| Phase 4b | COMPLETE | Unified SQLite back-end (RvrDb), DB-backed cache manifest, instance-based rule stores, two-column tab layout, import/export on all rule tabs. |
| Phase 5 | COMPLETE | TTS HTTP server: shared L2 render cache, provider capability discovery, reference sample API, worker subprocess architecture, ResourceManager GPU contention management, Whisper subprocess ASR, Kokoro / F5-TTS / Chatterbox Turbo / Chatterbox Full / Chatterbox Multilingual / CosyVoice3 / CosyVoice3-vLLM / LuxTTS / Qwen3-TTS backends. Synthesis response metrics headers. Client HttpTtsProvider integration and chunking policy per provider. |
| Phase 6 | COMPLETE | Provider benchmark harness: standalone Python runner for remote-provider characterization, fixed embedded test corpus, progressive raw-length sweeps, cache-aware timing capture, OGG artifact retention for human perception evaluation, and structured JSON/CSV/Markdown/LLM outputs for updating provider test notes and tuning client chunking policy. |
| Phase 7 | PLANNED | Platform polish: Windows Piper (pending libpiper C API), NPC override crowd-source sync, full GstAudioPlayer EOS, GstAudioPlayer PcmAudio interface update, silence trimming implementation. |

---

## 19. Known Limitations / TODO

- **GstAudioPlayer:** EOS detection is a stub. True gapless playback not implemented. PcmAudio interface not yet updated.
- **Silence trimming:** `TrimSilence` is a pass-through stub.
- **Windows Piper:** deferred until libpiper C API ships.
- **Kokoro cache identity normalization:** logically identical mixes with different ordering/weight formatting still hash differently.
- **Server cache identity vs. provider output format:** provider-specific clip sample rate/channel settings should remain part of cache identity assumptions so stale old-format renders are not reused after format-policy changes.
- **Default/recommended voice mappings:** `GetPreferredSampleStem` / `SpeakerPresetCatalog` presets need updating for each remote provider once the voice library is assembled. DSP chain presets also need authoring. CosyVoice and LuxTTS are not yet represented.
- **CosyVoice chunking limits uncharacterized:** The 380/480 char limits are conservative estimates matching Chatterbox Full. Empirical benchmarking against the actual model is needed to tune these limits.
- **Benchmark corpus governance:** The Phase 6 benchmark corpus is intentionally embedded in `rrv_benchmark.py` so regression runs remain comparable over time. Any future corpus change should bump an explicit corpus version and be treated as a benchmark-baseline change.
- **Partial chunk playback:** segments are assembled from all chunks before playback begins. Individual completed chunks could feed `PlaybackCoordinator` directly to start playing before all chunks finish synthesis. Deferred.
- **Player-name replacement:** character name in narrated text causes synthesis cache fragmentation across characters. Deferred — requires Lua protocol to carry current player name.
- **Sample intake job-state durability:** the watcher currently infers progress from files/sidecars rather than a persisted per-sample job ledger. A resumable intake manifest/job table remains a future improvement.
- **Transcript candidate selection:** future "best transcript" logic must choose one untouched transcript candidate rather than rewriting text, because case, spacing, punctuation, and pauses affect TTS cadence.

---

## 20. Security / Privacy

- No network transmission in Phases 1–4b unless the user explicitly selects a remote provider.
- Local providers keep text on the user's machine.
- HTTP/cloud providers are opt-in. The UI makes it clear when text leaves the machine.
- Phase 5 server is LAN-oriented and offline-first. No auth is enforced by default. An optional API key stub is defined in the server API so auth can be activated without a protocol change. Model files are staged manually; the server is not expected to reach the public internet.

---

## 21. TTS HTTP Server (Phase 5)

### 21.1 Summary

The TTS HTTP server is a separate Python/FastAPI project. It is a shared **L2 render cache** for up to approximately 5 LAN clients. The client remains fully authoritative over text shaping, pronunciation processing, provider choice, voice choice, speech rate, and downstream DSP. The server renders exactly what the client requests, caches the result, and serves it to subsequent clients with the same request. The client falls back to local rendering transparently if the server is unavailable or times out.

**L1 cache:** local client `TtsAudioCache` (SQLite manifest + OGG files on the client machine).  
**L2 cache:** server SQLite manifest + OGG files on the server machine, shared across all clients.

DSP processing stays entirely client-side. The server returns synthesized OGG only. The client applies DSP after retrieval, exactly as it does for local cache hits.

The current server implementation is working and has been validated on CPU and on bare-metal Ubuntu, in addition to container-oriented deployment paths.

### 21.2 Implemented Backends

| Backend | Provider ID | Status | GPU Support | Notes |
|---|---|---|---|---|
| Kokoro-82M ONNX | `kokoro` | implemented | CPU / ORT-CUDA / ORT-ROCm | Fast on CPU. Shares model files with the local client. Supports base voices and Kokoro inline pronunciation markup. |
| F5-TTS | `f5tts` | implemented | CUDA / ROCm / CPU (slow) | Reference-based voice matching. Uses server-managed reference samples and transcript sidecars. |
| Chatterbox Turbo | `chatterbox` | implemented | CUDA / CPU | Reference-based voice matching. Exposes `cfg_weight` and `exaggeration` controls. |
| Chatterbox (full) | `chatterbox_full` | implemented | CUDA / CPU | Original non-turbo Chatterbox model. Same controls as Turbo; may terminate early on EOS. |
| Chatterbox Multilingual | `chatterbox_multilingual` | implemented | CUDA / CPU | Extends Chatterbox voice cloning to 22 languages. Same `cfg_weight` / `exaggeration` controls. 10-step diffusion decoder; no paralinguistic tags. |
| CosyVoice3 | `cosyvoice` | implemented | CUDA / CPU | Hybrid LLM+flow-matching zero-shot voice cloning. Native 22050 Hz. Optional `cosy_instruct` for natural-language style control via `inference_instruct2()`. Multilingual. |
| CosyVoice3-vLLM | `cosyvoice_vllm` | implemented | CUDA | vLLM-accelerated CosyVoice3. Same controls as `cosyvoice`. Higher throughput for concurrent requests (`RRV_COSYVOICE_VLLM_MAX_CONCURRENT`, default 6). |
| LuxTTS | `lux` | implemented | CUDA / CPU | High-speed flow-matching voice cloning. Controls: `lux_num_steps` (default 10), `lux_t_shift` (default 0.7), `lux_return_smooth` (default true). |
| Qwen3-TTS Natural | `qwen_natural` | implemented | CUDA | Reference-based voice cloning with optional `voice_instruct` style control. Model size: `large` or `small` via `RRV_QWEN_NATURAL_SIZE`. |
| Qwen3-TTS Custom | `qwen_custom` | implemented | CUDA | Same as `qwen_natural` with separate model-size config (`RRV_QWEN_CUSTOM_SIZE`). |
| Qwen3-TTS Design | `qwen_design` | implemented | CUDA | Voice persona design via `voice.type = "description"` and `voice_description` text. Always uses large (1.7B) checkpoint. |

**GPU auto-detection** runs once at startup and selects the best available execution provider in priority order: CUDA → ROCm → CPU where applicable. The selected provider is logged clearly at startup.

### 21.3 Stack and Project Structure

- **Language / framework:** Python 3.11+ + FastAPI + Uvicorn
- **Inference backends:** provider-pluggable, each running as an isolated worker subprocess
- **Storage:** SQLite manifest (`server-cache.db`) + filesystem OGG files
- **Transport:** HTTP/JSON control plane; binary OGG audio response body
- **Deployment:** standalone LAN service; container or bare-metal Linux
- **System dependency:** `ffmpeg` — required for audio/video sample conversion
- **Operating assumption:** offline / airgapped capable deployment with manually staged model files
- **Model acquisition rule:** the server does not download models at runtime. All required model files must be staged manually into the correct model directory during installation or deployment.

Project is structured as a `pyproject.toml` package with optional dependency groups:

```
rrv-server/
  pyproject.toml
  Dockerfile
  docker-compose.yml
  .env.example
  SETUP.md
  INSTALL_UBUNTU.md
  server/
    main.py               # FastAPI app, startup, lifespan, background polling loop
    config.py             # env var + CLI arg resolution
    gpu_detect.py         # hardware probe, execution provider selection
    manager.py            # ResourceManager — GPU contention and on-demand reload
    cache.py              # SQLite manifest, OGG store/retrieve, LRU eviction
    transcriber.py        # ffmpeg conversion, ASR lazy load/unload, auto .ref.txt
    voice_profiler.py     # librosa signal analysis, auto .txt voice description
    samples.py            # sample directory scanner, sidecar loader
    asr/
      base.py             # AsrProvider protocol, AsrRegistry
      worker_asr.py       # WorkerAsr — subprocess-based ASR (Whisper)
      whisper_asr.py      # in-process Whisper (legacy, not used by default)
    backends/
      __init__.py         # BackendRegistry, load_backends()
      base.py             # AbstractTtsBackend protocol, SynthesisRequest, SynthesisResult
      worker_backend.py   # WorkerBackend — subprocess proxy implementing AbstractTtsBackend
      audio.py            # shared pcm_to_ogg, estimate_duration helpers
      kokoro_backend.py
      f5tts_backend.py
      chatterbox_backend.py
      chatterbox_full_backend.py
      chatterbox_multilingual_backend.py
      cosyvoice_backend.py
      cosyvoice_vllm_backend.py
      lux_backend.py
      qwen_backend.py     # QwenNaturalBackend, QwenCustomBackend, QwenDesignBackend
    routes/
      health.py
      capabilities.py
      providers.py
      synthesize.py       # v1 synchronous
      synthesize_v2.py    # v2 async with SSE progress + batch tracking

# Worker subprocess launchers — one per venv
rrv-chatterbox/run_worker.py
rrv-cosyvoice/run_worker.py
rrv-cosyvoice-vllm/run_worker.py
rrv-f5/run_worker.py
rrv-kokoro/run_worker.py
rrv-lux/run_worker.py
rrv-qwen/run_worker.py
rrv-whisper/run_asr_worker.py   # Whisper ASR subprocess

  models/                 # model files (gitignored, volume-mounted)
  samples/                # reference audio clips (gitignored, volume-mounted)
  cache/                  # generated OGG files (gitignored, volume-mounted)
```

### 21.3a ResourceManager

`server/manager.py` coordinates GPU resource contention across all TTS backends and the ASR provider.

**Registration:** every `WorkerBackend` and `WorkerAsr` calls `manager.register(self)` at startup. Workers report `requires_gpu`, `vram_used_mib` (via `torch.cuda.memory_reserved()`), `_last_used`, and `_is_loaded`.

**Eviction policy:** before loading, a worker calls `manager.request_load(self)`. The manager evicts **all** eligible idle workers (not just one) in order: highest VRAM consumer first, then least recently used. A worker is immune to eviction for `RRV_BACKEND_RECENT_USE_WINDOW` seconds after its last use (default 60 s).

**On-demand reload:** when a synthesis request arrives for an evicted backend, `WorkerBackend.synthesize()` detects the unloaded state, calls `request_load()` to free GPU memory, then calls `load()` to respawn the worker subprocess. This is transparent to the route handler.

**ASR eviction:** `WorkerAsr.transcribe()` applies the same pattern. After a transcription batch completes, Whisper unloads itself via `run_coroutine_threadsafe` to release VRAM for TTS backends.

### 21.3b ASR Architecture

Whisper runs as a worker subprocess (`rrv-whisper/run_asr_worker.py`) using the same Unix socket protocol as all TTS worker backends. The `WorkerAsr` class in `server/asr/worker_asr.py` manages the subprocess lifecycle and communicates via the shared worker protocol.

**Chunk format:** Whisper returns per-segment timestamps as `{"timestamp": [start, end], "text": "..."}`. The `WorkerAsr` correctly parses both the `timestamp` array format and the legacy `start`/`end` key format.

**Post-batch unload:** after each transcription batch, the ASR worker unloads to free VRAM. The next transcription request triggers an on-demand reload via `ResourceManager`.

**Configuration:** `RRV_ASR_PROVIDER=whisper` (default and only supported value). The former Qwen-ASR, CrisperWhisper, and Cohere Transcribe providers have been removed.

### 21.3a Airgapped / Offline Deployment

The server is designed to operate in an airgapped or offline environment. **Models are never downloaded at runtime.** Required model artifacts are obtained manually during installation and copied into `RRV_MODELS_DIR` before the corresponding backend is enabled. Missing model files are a deployment / configuration error, not a runtime recovery path.

This applies to all supported providers, including Kokoro, F5-TTS, Chatterbox Turbo, and Chatterbox (full).

### 21.3c Model Acquisition

Models are not bundled. They are staged manually into `RRV_MODELS_DIR`.

| Backend | Local directory | Notes |
|---|---|---|
| Kokoro | `models/kokoro/` | ONNX model + voice bin files. Can be shared with the C# local client. |
| F5-TTS | `models/f5tts/` | Model and vocoder artifacts placed manually during install. |
| Chatterbox Turbo | `models/chatterbox/` | Turbo model files placed manually during install. |
| Chatterbox (full) | `models/chatterbox-hf/` | Original `ResembleAI/chatterbox-hf` model files. |
| Chatterbox Multilingual | `models/chatterbox-multilingual/` | Multilingual checkpoint placed manually during install. |
| CosyVoice3 | `models/cosyvoice/cosyvoice3/` | `FunAudioLLM/Fun-CosyVoice3-0.5B-2512` model files. |
| CosyVoice3-vLLM | `models/cosyvoice/cosyvoice3/` | Same model directory as `cosyvoice`. |
| LuxTTS | `models/lux/` | `YatharthS/LuxTTS` model files. |
| Qwen (all) | `RRV_QWEN_MODELS_DIR` (default `../data/models/qwen`) | Qwen3-TTS large and/or small checkpoints. |

Public origin URLs or package sources may still be documented in installer notes, but the running server does not rely on internet access.

### 21.3c Sample Intake Pipeline

Reference audio samples are the input for voice-matching synthesis (F5-TTS, Chatterbox Turbo, Chatterbox full). The server manages the full lifecycle automatically:

**1. Auto-conversion (ffmpeg)**  
Drop any audio or video file into `data/samples/` and the server converts it on the next polling pass into a **44.1 kHz stereo PCM_16 master WAV**. Supported formats: `.mp3`, `.aac`, `.m4a`, `.flac`, `.ogg`, `.mp4`, `.mkv`, `.webm`, `.avi`. Video files have their audio track extracted and video discarded. The server pads lead/tail silence during import, moves the original source into `data/samples/originals/`, and treats the normalized stereo master as the authoritative source for all downstream extraction and analysis.

**2. Auto-transcription (Whisper)**  
Any master WAV without a `.ref.txt` transcript sidecar is transcribed automatically using a locally pre-placed Whisper model. The transcript is written as `<stem>.ref.txt` alongside the master audio. Whisper is loaded lazily and unloaded after the transcription pass to release VRAM / RAM.

**3. Auto-profiling (librosa signal analysis)**  
After master transcription, the audio is analysed using librosa signal processing and a one-line voice description is written as `<stem>.txt`.

**4. Auto-extraction of provider clips and variants**  
The sample extractor slices provider clips and subvariants from the preserved stereo master PCM. Generated clips are no longer derived from an analysis-only mono buffer. Each provider family can emit clips in its own configured format (sample rate and channel count) while still sharing the same stereo master source.

**Polling interval:** configurable via `RRV_SAMPLE_SCAN_INTERVAL` (default 30 seconds). The pipeline only activates when at least one voice-matching backend is loaded. Bulk backfill behavior can be tuned further through optional batching and profiling env settings.

Generated provider artifacts use a strict on-disk naming contract:
- master family: `<base>-master.<ext>`
- F5 family: `<base>-f5[-<variant>].<ext>`
- Chatterbox family: `<base>-chatterbox[-<variant>].<ext>`
- LuxTTS family: `<base>-lux[-<variant>].<ext>`
- CosyVoice family: `<base>-cosyvoice[-<variant>].<ext>`

`chatterbox` and `chatterbox_full` are aliases for the same sample family and resolve against `-chatterbox` files. `cosyvoice` and `cosyvoice_vllm` are aliases for the same sample family and resolve against `-cosyvoice` files. The server does not create parallel `-chatterbox_full` or `-cosyvoice_vllm` sample artifacts.

### 21.4 Configuration

All paths and network settings are overridable without code changes. CLI options mirror env vars; CLI takes precedence.

| Variable / Option | Default | Description |
|---|---|---|
| `RRV_CACHE_DIR` / `--cache-dir` | `./cache/` | Directory for cached OGG files |
| `RRV_DB_PATH` / `--db-path` | `./server-cache.db` | SQLite manifest database path |
| `RRV_MODELS_DIR` / `--models-dir` | `./models/` | Model files directory |
| `RRV_SAMPLES_DIR` / `--samples-dir` | `./samples/` | Reference audio clips for voice matching |
| `RRV_HOST` / `--host` | `0.0.0.0` | Bind address |
| `RRV_PORT` / `--port` | `8765` | Listen port |
| `RRV_API_KEY` / `--api-key` | *(empty — disabled)* | Optional API key for stub auth |
| `RRV_GPU` / `--gpu` | `auto` | GPU execution provider: `auto`, `cuda`, `rocm`, `cpu` |
| `RRV_BACKENDS` / `--backends` | `kokoro` | Comma-separated list of backends to load |
| `RRV_LOG_LEVEL` / `--log-level` | `info` | Logging level: `debug`, `info`, `warning`, `error` |
| `RRV_SAMPLE_SCAN_INTERVAL` | `30` | Seconds between sample-library polling passes |
| `RRV_BACKEND_RECENT_USE_WINDOW` | `60` | Seconds a backend is immune to eviction after last use |
| `RRV_ASR_PROVIDER` | `whisper` | ASR provider for sample transcription. Only `whisper` is supported. |
| `RRV_F5_SAMPLE_CHANNELS` | `1` | Channel count for emitted F5 reference clips |
| `RRV_F5_SAMPLE_RATE` | `22050` | Sample rate for emitted F5 reference clips |
| `RRV_CHATTERBOX_SAMPLE_CHANNELS` | `2` | Channel count for emitted Chatterbox-family reference clips |
| `RRV_CHATTERBOX_SAMPLE_RATE` | `44100` | Sample rate for emitted Chatterbox-family reference clips |
| `RRV_LUX_SAMPLE_CHANNELS` | `1` | Channel count for emitted LuxTTS reference clips |
| `RRV_LUX_SAMPLE_RATE` | `48000` | Sample rate for emitted LuxTTS reference clips |
| `RRV_LUX_NUM_STEPS` | `10` | Default ODE solver steps for LuxTTS |
| `RRV_COSYVOICE_VLLM_MAX_CONCURRENT` | `6` | Max concurrent synthesis requests for `cosyvoice_vllm` |
| `RRV_QWEN_NATURAL_SIZE` | `large` | Model size for `qwen_natural`: `large` or `small` |
| `RRV_QWEN_CUSTOM_SIZE` | `large` | Model size for `qwen_custom`: `large` or `small` |
| `RRV_QWEN_MODELS_DIR` | `../data/models/qwen` | Directory for Qwen model files |
| `RRV_SAMPLE_PROFILE_WORKERS` | auto | Process-pool worker count for parallel clip voice profiling |
| `RRV_WHISPER_MASTER_BATCH_SIZE` | `1` | Batched Whisper size for master/raw sample transcription |
| `RRV_WHISPER_GENERATED_BATCH_SIZE` | `1` | Batched Whisper size for generated clip retranscription |

**Rendered output format note:** server-side OGG encoding preserves the synthesized PCM sample rate and channel count after loudness normalization. Format drift during final OGG serialization is treated as a bug, not a supported behavior.

### 21.5 Provider Capability Model

The server reports what each loaded backend can do. The client uses this to build its UI — it does not hardcode provider capabilities. `/api/v1/providers` and `/api/v1/providers/{provider_id}` are the authoritative provider-discovery surfaces.

Capability flags:

| Flag | Meaning |
|---|---|
| `supports_base_voices` | Provider has built-in named voices (Kokoro, Qwen-Natural/Custom) |
| `supports_voice_matching` | Provider can synthesize against a reference audio sample |
| `supports_voice_blending` | Provider supports weighted mix of multiple voices |
| `supports_voice_design` | Provider accepts a free-text voice persona description (`qwen_design`) |
| `supports_voice_instruct` | Provider accepts a natural-language style instruction alongside reference audio |
| `supports_inline_pronunciation` | Provider supports Kokoro/Misaki inline IPA phoneme markup |
| `execution_provider` | Resolved GPU/CPU provider in use: `cuda`, `rocm`, `cpu` |
| `controls` | Provider-specific optional synthesis controls the client may expose dynamically |

Provider-specific controls by backend:

| Backend | Controls |
|---|---|
| Chatterbox (all) | `cfg_weight` (0.0–3.0), `exaggeration` (0.0–3.0) |
| F5-TTS | `cfg_strength` (0.5–3.0), `nfe_step` (8–64), `cross_fade_duration` (0.0–1.0), `sway_sampling_coef` (-1.0–1.0) |
| CosyVoice / CosyVoice-vLLM | `cosy_instruct` (string) — natural-language style instruction; switches to `inference_instruct2()` when set |
| LuxTTS | `lux_num_steps` (4–32), `lux_t_shift` (0.1–1.0), `lux_return_smooth` (bool) |
| Qwen-Custom / Qwen-Natural | `voice_instruct` (string) — style instruction e.g. "speak excitedly" |
| Qwen-Design | `voice_description` (string) via `voice.type = "description"` |

### 21.6 Voice List

`GET /api/v1/providers/{id}/voices` returns the voices the server has **loaded and available right now** — not a theoretical catalog. For Kokoro this is the loaded voice list from the Kokoro voice bin. Voice-matching-only providers may return an empty base voice list or only backend-specific built-ins as applicable.

### 21.7 Reference Sample List (Voice Matching)

`GET /api/v1/providers/{id}/samples` returns the reference audio clips available on the server for voice-matching-capable providers (F5-TTS, Chatterbox family, CosyVoice family, LuxTTS).

**Sample management is admin-only — filesystem only, no upload endpoint.** The admin places files directly in `RRV_SAMPLES_DIR`. The server scans on startup and on subsequent sample refresh passes; no public upload API exists.

`sample_id` is a **provider-neutral logical ID**, not the raw filename stem. The server scans provider-tagged files on disk but strips the provider family token before returning `sample_id` values to the client.

File naming vs. API contract:
- on disk: `<base>-f5[-<variant>].<ext>`, `<base>-chatterbox[-<variant>].<ext>`, `<base>-lux[-<variant>].<ext>`, `<base>-cosyvoice[-<variant>].<ext>`
- returned to clients: `<base>` and `<base>-<variant>`

Examples:
- `M_Narrator-f5.wav` → `sample_id = "M_Narrator"`
- `M_Narrator-chatterbox-slow.wav` → `sample_id = "M_Narrator-slow"`
- `M_Narrator-cosyvoice.wav` → `sample_id = "M_Narrator"`
- `M_Narrator-lux-quiet.wav` → `sample_id = "M_Narrator-quiet"`

Internal provider-tagged identifiers must not be surfaced to clients. Optional `.txt` and `.ref.txt` sidecars provide description and transcript metadata.

Provider-family alias rules:
- `f5tts` requests enumerate only the `-f5` family
- `chatterbox` and `chatterbox_full` requests enumerate only the `-chatterbox` family
- `cosyvoice` and `cosyvoice_vllm` requests enumerate only the `-cosyvoice` family
- `lux` requests enumerate only the `-lux` family

### 21.8 Unified Request Model

All providers accept the same request shape. The server validates that the requested `voice.type` is supported by the named provider and returns a clean error if not.

```json
{
  "provider_id": "cosyvoice",
  "text": "Stay away from Atal'zul, mon.",
  "voice": {
    "type": "reference",
    "sample_id": "M_Narrator"
  },
  "lang_code": "en",
  "speech_rate": 1.0,
  "voice_context": "Narrator",

  "cfg_weight": null,
  "exaggeration": null,

  "cfg_strength": null,
  "nfe_step": null,
  "cross_fade_duration": null,
  "sway_sampling_coef": null,

  "cosy_instruct": null,

  "lux_num_steps": null,
  "lux_t_shift": null,
  "lux_return_smooth": null,

  "voice_instruct": null
}
```

`voice.type` values: `"base"` (voice_id), `"reference"` (sample_id), `"blend"` (blend array), `"description"` (voice_description, Qwen-Design only).

`speech_rate` is a hint in the range `[0.5, 2.0]`. Providers that degrade in quality at extreme rates may clamp internally. CosyVoice clamps to `[0.5, 2.0]` and passes as `speed` to inference. Provider-specific optional controls are advertised through the provider metadata and are ignored or rejected by backends that do not support them. Null fields are omitted by the server.

### 21.9 Cache Key and Identity

Server cache key is a SHA-256-based identity truncated for storage efficiency. It includes at minimum:

```
text \\x00 provider_id \\x00 model_version \\x00 resolved_voice_identity \\x00 lang_code \\x00 speech_rate
```

For providers with optional synthesis controls that materially affect output, those controls must also participate in the cache identity. For current Chatterbox backends this includes `cfg_weight` and `exaggeration`, so requests with different expressive settings do not collide in cache.

`resolved_voice_identity` is:
- For `base`: the `voice_id` string
- For `reference`: a content hash derived from the reference sample
- For `blend`: canonical sorted `voice_id:weight` pairs

`model_version` is derived from the loaded model artifacts at backend load time. If model files are replaced, cache identity changes naturally and old entries stop matching.

### 21.10 Cache File Integrity

On server startup:

1. Scan for any `.tmp` files in `RRV_CACHE_DIR` left by a previously interrupted write and delete them.
2. Verify that every DB manifest row has a corresponding file. Rows without files are deleted from the manifest — the next request regenerates the audio.

On store:

1. Synthesize to memory.
2. Write to `<key>.ogg.tmp`.
3. Rename to `<key>.ogg` atomically.
4. Insert/update DB manifest row.

This ensures the cache directory never contains a partially written OGG that could be served to a client.

### 21.10b Cache Manifest — Text Storage

The current server cache implementation is the accepted baseline design and should be treated as final unless a concrete operational need justifies a change later. Cache behavior is documented from the implementation as it exists now, not as a provisional target. Where source and document differ, the running server implementation is authoritative.

If the cache manifest stores synthesized text for debugging or future regeneration workflows, that is part of the current design. If it does not, the absence of text storage is also an intentional part of the current design until a real requirement emerges. The cache is not considered tentative in either case.

### 21.11 Synthesis Stampede Prevention

When two clients request the same key simultaneously and both get a cache miss, the server maintains an in-process `asyncio.Lock` per cache key. The second client waits while the first synthesizes and stores. When the lock is released, the second client finds a cache hit and returns immediately without synthesizing.

### 21.12 Fallback Behavior (Client Side)

When `HttpTtsProvider` is selected:

1. Client checks local L1 cache (`TryGetDecodedAsync`). Hit → play immediately.
2. L1 miss → send request to server with configured timeout.
3. Server hit → server returns cached OGG. Client stores in L1, applies DSP, plays.
4. Server miss → server synthesizes. Client waits up to timeout.
5. Server timeout or unavailable → client falls back to local `KokoroTtsProvider`. A status indicator in the UI shows the fallback state.

### 21.13 API Endpoints

#### Core

```
GET  /api/v1/health
GET  /api/v1/capabilities
GET  /api/v1/providers
GET  /api/v1/providers/{provider_id}
GET  /api/v1/providers/{provider_id}/voices
GET  /api/v1/providers/{provider_id}/samples
POST /api/v1/synthesize                                    — v1 synchronous (legacy)
```

#### Async Synthesis (v2)

```
POST /api/v1/synthesize/v2                                 — submit job, returns {progress_key, cache_key, cached}
GET  /api/v1/synthesize/v2/{key}/progress                  — SSE stream: fires complete/error event
GET  /api/v1/synthesize/v2/{key}/result                    — fetch OGG bytes (202 if still pending)
GET  /api/v1/synthesize/v2/batch/{id}/progress             — SSE batch aggregate: {completed, total, status}
```

#### Community Sync — NPC Overrides

```
GET  /api/v1/npc-overrides                      — all records (open)
GET  /api/v1/npc-overrides/since?t={unix_ts}    — delta poll since timestamp (open, t=0 returns all)
POST /api/v1/npc-overrides                      — contribute a record (contribute key)
PUT  /api/v1/npc-overrides/{npc_id}             — admin confirm / edit (admin key)
```

#### Community Sync — Seed Defaults

```
GET  /api/v1/defaults/{type}    — pull seed data (open). type: voice-profiles | pronunciation | text-shaping | npc-overrides
PUT  /api/v1/defaults/{type}    — push seed data (admin key)
```

Seed data is stored as JSON files under `data/defaults/`. NPC override records are stored in `data/community.db` (SQLite via aiosqlite).

#### Authentication

Three separate keys, all configured via environment variable:

| Key | Env var | Scope |
|---|---|---|
| API key | `RRV_API_KEY` | Core synthesis endpoints. Empty = open. |
| Contribute key | `RRV_CONTRIBUTE_KEY` | POST npc-overrides. Empty = open. |
| Admin key | `RRV_ADMIN_KEY` | PUT npc-overrides, PUT defaults. Empty = open. |

All endpoints accept `Authorization: Bearer <key>`.

#### Reverse Proxy

`RRV_TRUSTED_PROXY_IPS` (default `127.0.0.1`) activates `ProxyHeadersMiddleware` so real client IPs appear in logs when running behind Caddy or another reverse proxy.

#### Diagnostics

Log fields per request: request ID or cache key summary, provider ID, voice identity summary, cache hit/miss, synthesis duration (if miss), output size bytes, realtime factor, input char/word count, client IP.

## 21.14 Synthesis Response Metrics

Both the v1 and v2 endpoints return performance and input metrics as HTTP response headers. These are designed to support benchmarking and chunking-limit characterization without requiring audio transcription.

### v1 (`POST /api/v1/synthesize`) — Response Headers

| Header | Cache HIT | Cache MISS | Description |
|---|---|---|---|
| `X-Cache` | `HIT` | `MISS` | Cache state |
| `X-Cache-Key` | ✓ | ✓ | Truncated SHA-256 cache key |
| `X-Input-Chars` | ✓ | ✓ | Character count of the normalized (post-TN) text |
| `X-Input-Words` | ✓ | ✓ | Word count of the normalized text |
| `X-Synth-Time` | — | ✓ | Wall-clock synthesis duration in seconds |
| `X-Duration` | — | ✓ | Audio duration in seconds |
| `X-Realtime-Factor` | — | ✓ | `duration / synth_time` — e.g. `3.500` means 3.5× realtime |

### v2 (`POST /api/v1/synthesize/v2`) — Submit Response Body

```json
{
  "progress_key": "...",
  "cache_key": "...",
  "cached": false,
  "input_chars": 183,
  "input_words": 34
}
```

`input_chars` and `input_words` are present on both cache-hit (`"cached": true`) and cache-miss responses.

### v2 (`GET /api/v1/synthesize/v2/{key}/progress`) — SSE Complete Event

```json
{
  "status": "complete",
  "duration_sec": 4.821,
  "synth_time": 2.103,
  "realtime_factor": 2.293,
  "input_chars": 183,
  "input_words": 34,
  "cache_hit": false
}
```

### v2 (`GET /api/v1/synthesize/v2/{key}/result`) — Response Headers

Same header set as v1. `X-Synth-Time` and `X-Realtime-Factor` are empty strings on cache hits.

### Truncation Detection Heuristic

At a normal narration rate of ~130 words/minute, expected audio duration ≈ `(X-Input-Words / 130) * 60` seconds. If `X-Duration` is significantly shorter than this estimate, the model truncated. This ratio is the primary chunking-limit signal for benchmark scripts that cannot perform audio transcription.

## 21.15 Provider Benchmark Harness (Phase 6)

Phase 6 adds a standalone Python benchmark harness, `rrv_benchmark.py`, for repeatable characterization of remote TTS providers exposed by the Phase 5 HTTP server. The harness is not part of the runtime client path. Its purpose is validation, regression detection, provider comparison, and chunking-policy tuning.

### Goals

- Measure how each provider behaves across a fixed, repeatable corpus.
- Find practical chunking limits before audible truncation, repetition drift, or prosody collapse occur.
- Capture timing metrics on cache misses and distinguish them from cache hits.
- Save every returned clip for direct human listening tests.
- Produce structured output that can be reviewed manually or summarized by an LLM back into the provider test notes.

### Fixed Corpus Requirement

The benchmark text corpus is embedded directly in the script. This is intentional. The harness must not depend on an external markdown prompt sheet for its default cases, because changing external text would silently invalidate comparisons across runs. Regression benchmarking requires a stable corpus.

The built-in corpus covers the main failure classes observed during provider evaluation:

- raw length stability
- repeated structure and repetition fatigue
- ordered sequences and number-word drift
- punctuation and clause-boundary sensitivity
- paragraph-boundary behavior
- generated raw-length sweeps used to probe the practical truncation boundary

### Invocation Parameters

The harness is invoked from the command line and always takes the target server and provider as explicit parameters so a single provider can be re-tested independently after backend changes.

| Parameter | Required | Description |
|---|---|---|
| `--server` | yes | Base URL of the RuneReader Voice HTTP server, e.g. `http://127.0.0.1:8000`. |
| `--provider` | yes | Provider ID to benchmark, e.g. `chatterbox_full`, `cosyvoice`, `f5tts`, `kokoro`. |
| `--sample-id` | provider-dependent | Explicit reference/sample ID for sample-based providers. Strongly recommended for repeatability. |
| `--voice` | provider-dependent | Explicit voice ID for fixed-voice providers such as Kokoro. |
| `--speech-rate` | no | Requested synthesis speed when the provider supports it. Benchmarking should normally keep this fixed per test campaign. |
| `--cfg-weight` | no | Chatterbox-family CFG weight override for campaigns that intentionally test CFG sensitivity. |
| `--exaggeration` | no | Chatterbox-family exaggeration override for campaigns that intentionally test exaggeration sensitivity. |
| `--cache-mode` | no | Cache behavior for the run. Cold runs are preferred for timing measurements because cache-hit responses intentionally omit meaningful synth-time and realtime-factor values. |
| `--output-dir` | no | Directory where audio artifacts and structured reports are written. |
| `--list-samples` | no | Provider inspection helper that lists available reference samples instead of running the corpus. |
| `--list-voices` | no | Provider inspection helper that lists available fixed voices instead of running the corpus. |

### Output Artifacts

Each benchmark run emits a complete result bundle.

| Artifact | Purpose |
|---|---|
| `audio/*.ogg` | One synthesized clip per test case for human perception evaluation and spot-checking truncation or drift. Filenames include provider, case ID, and a readable test label. |
| `benchmark_results.json` | Full-fidelity machine-readable record of each request, response, and measured metric. |
| `benchmark_results.csv` | Flat tabular export suitable for sorting and spreadsheet review. |
| `benchmark_summary.md` | Human-readable benchmark report summarizing pass/fail indicators and notable measurements. |
| `benchmark_llm_packet.md` | Consolidated prompt packet designed to be pasted into an LLM so the findings can be merged back into provider test documentation. |

### Metrics Captured

The harness records the metric fields exposed by the HTTP API in Section 21.14. On v2 it also captures the submit/body metadata and the SSE completion event when present.

| Metric | Meaning | Use in benchmarking |
|---|---|---|
| `cache_hit` / `X-Cache` | Whether the render came from cache | Separates throughput measurements from cache effects. |
| `cache_key` / `X-Cache-Key` | Truncated cache identity | Confirms whether two requests resolved to the same synthesis identity. |
| `input_chars` / `X-Input-Chars` | Normalized text character count actually received by the model | Primary raw-length measurement for chunking-limit analysis. |
| `input_words` / `X-Input-Words` | Normalized text word count actually received by the model | Used with duration to detect likely truncation. |
| `synth_time` / `X-Synth-Time` | Wall-clock synthesis time on cache miss | Used to compare provider speed under cold-cache conditions. |
| `duration_sec` / `X-Duration` | Returned audio duration on cache miss | Used to compare expected vs. actual narration length. |
| `realtime_factor` / `X-Realtime-Factor` | `duration / synth_time` | Throughput indicator. Values above 1.0 mean faster than real-time generation. |

### Truncation Detection

The primary non-transcription truncation signal is the duration heuristic from Section 21.14: expected narration duration is approximated as `(input_words / 130) * 60`. If the returned audio duration is materially shorter than that estimate, the provider likely truncated, skipped content, or collapsed the tail of the utterance.

This heuristic is not a replacement for listening tests. It is a first-pass signal used to identify which artifacts deserve manual review. The `.ogg` outputs remain the source of truth for subjective issues such as pronunciation drift, repeated phrases, garbling, flattened prosody, or unnatural breath placement.

### Intended Use of Results

Phase 6 benchmark results are intended to drive engineering decisions, not just generate pass/fail notes. In particular, the results are used to:

- tune `TextChunkingPolicy` target and hard-cap sizes per provider
- validate that backend fixes actually improved truncation or sequence stability
- compare cache-cold throughput across providers and settings
- detect regressions after backend refactors, tokenizer changes, prompt-format changes, or sample-library changes
- determine when provider-specific preprocessing or special-case chunking rules are needed
- update `Provider Tests.md` with fresh empirical findings without rewriting the benchmark corpus itself

### Representative Usage

```bash
python rrv_benchmark.py \
  --server http://127.0.0.1:8000 \
  --provider chatterbox_full \
  --sample-id M_Narrator
```

```bash
python rrv_benchmark.py \
  --server http://127.0.0.1:8000 \
  --provider cosyvoice \
  --sample-id M_Narrator \
  --speech-rate 1.0
```

For comparable provider retests, keep the provider, sample/voice selection, speech rate, and benchmark corpus constant across runs. Change one variable at a time.

## 22. Client — HTTP Provider Configuration

The client Settings tab TTS Provider expander shows the following when a remote provider is selected:

| Setting | Description |
|---|---|
| Server URL | Base URL, e.g. `https://rrv.example.com` |
| Remote API Key | Sent as `Authorization: Bearer <key>`. Leave blank if server auth is disabled. |
| Contribute Key | Bearer token for `POST /api/v1/npc-overrides`. Empty = open. |
| Admin Key | Bearer token for `PUT` admin endpoints. Empty = open. |
| Contribute NPC voice assignments automatically | When checked, saves contribute each local NPC override to the server silently in the background. |
| First load complete | When unchecked, pulls all four default data types from server on next startup. |

On startup the client fires a background `RefreshVoiceSourcesAsync` to warm `_voiceCache`. This ensures the voice sample dropdown in the Last NPC panel is populated even if the user never opens the Voices tab.

`RemoteTtsClient` uses `SocketsHttpHandler` with:
- `PooledConnectionLifetime = 90s` — recycle connections before Caddy's 5-minute idle timeout.
- `PooledConnectionIdleTimeout = 60s` — close truly idle connections promptly.

This eliminates `IOException(SocketException 995)` from idle connection recycling. A global `TaskScheduler.UnobservedTaskException` handler suppresses any remaining pool-internal background exceptions.

The voice sample picker in the Last NPC panel is shown for sample-based remote providers such as Chatterbox-family and F5, even when the current sub-voice list has not yet been populated. Sample list filtering still respects provider duration limits (F5-TTS ≤11s, Chatterbox ≤40s).

---

## 23. Licensing / Packaging Notes

RuneReaderVoice desktop client code is licensed under **GPL v3**. Source headers use the SPDX identifier `GPL-3.0-only`. Distribution artifacts include the standard GNU GPL v3 license text in `LICENSE`. Third-party bundled code keeps its own original license and attribution.

---

## 24. Final Design Summary

RuneReaderVoice v24 expands the Phase 5 server backend roster, hardens the worker subprocess architecture with centralized GPU resource management, fixes a root-cause CosyVoice truncation bug, adds synthesis metrics headers to both API endpoints for structured benchmarking, and formalizes a Phase 6 provider benchmark harness for repeatable regression and chunking-limit characterization.

The server backend set now covers ten provider IDs: Kokoro, F5-TTS, Chatterbox Turbo, Chatterbox Full, Chatterbox Multilingual, CosyVoice3, CosyVoice3-vLLM, LuxTTS, and three Qwen3-TTS backends (Natural, Custom, Design). Each backend runs as an isolated worker subprocess communicating over a Unix socket. The `ResourceManager` coordinates GPU contention across all workers and the Whisper ASR provider, evicting idle workers before loading new ones and reloading evicted backends on demand when synthesis requests arrive.

The CosyVoice `prompt_text` bug — where the instruct system-prompt prefix was incorrectly passed to `inference_zero_shot()` — caused LLM token budget confusion and manifested as whole-sentence truncation at clean word boundaries. The fix (passing the raw reference transcript only) is expected to substantially reduce truncation for both `cosyvoice` and `cosyvoice_vllm`. `speech_rate` is now correctly forwarded as the `speed` parameter to both inference paths.

The client `TextChunkingPolicy` now has a dedicated profile for CosyVoice (380/480 char limits, matching Chatterbox Full conservatism) and `ChatterboxPreprocess` now runs for CosyVoice providers as well. A settings checkbox init bug affecting `PhraseChunking`, `RepeatSuppressionEnabled`, `CompressionEnabled`, and `SilenceTrim` — where AXAML hardcoded `IsChecked="True"` caused Click handlers to fire during `InitializeComponent()` and overwrite saved settings — is fixed by removing the AXAML defaults and adding a `_uiInitializing` guard.

The v1 and v2 synthesis endpoints now return `X-Input-Chars`, `X-Input-Words`, `X-Synth-Time`, `X-Duration`, and `X-Realtime-Factor` on every response. The v2 submit body and SSE complete event carry the same metrics. Combined with a ~130 WPM narration-rate heuristic, `X-Input-Words` vs `X-Duration` gives benchmark scripts a model-truncation signal without audio transcription.

Phase 6 adds a standalone Python benchmark harness, `rrv_benchmark.py`, which targets one provider at a time using a fixed embedded corpus and progressive length sweeps. It saves every synthesized clip as an `.ogg` artifact for human listening review, captures cache-aware timing and input metrics from the HTTP API, and emits JSON/CSV/Markdown/LLM-oriented summaries so empirical findings can be folded back into provider test notes and chunking-policy decisions.

The parallel synthesis pipeline, PCM-first architecture, NPC override resolution order, sample library pipeline, and all v22 features remain unchanged. The sample-library contract now also covers CosyVoice and LuxTTS provider families: provider-tagged filenames (`-cosyvoice`, `-lux`) are internal artifacts; provider-facing sample lists expose only provider-neutral logical IDs. `cosyvoice` and `cosyvoice_vllm` share the `-cosyvoice` family on disk.

**TODO: Default/Recommended Voice Mappings per Provider.** Once the voice library is assembled, `GetPreferredSampleStem` / `GetDefaultSampleProfile` mappings will be built for each provider (F5-TTS, Chatterbox, CosyVoice, LuxTTS, remote Kokoro). Recommended presets in `SpeakerPresetCatalog` will be updated with per-provider defaults and DSP chain settings using the `DspEffectItem` list model.

**TODO: CosyVoice chunking limits.** The 380/480 char limits are conservative placeholders. Run the benchmark script against `cosyvoice` with progressively longer text at 1.0× speed and characterize the actual truncation boundary before loosening these limits.
