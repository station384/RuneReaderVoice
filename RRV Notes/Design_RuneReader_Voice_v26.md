# RuneReader — Quest Text-to-Speech (TTS)

*Feature Design Document | Roadmap Item*

Status: Phases 1–6 Complete | Target: Retail WoW

Last Updated: May 2026 | Version 26

---

## What's New in v26

- **Chatterbox-family batch prosodic continuity corrected:** cross-request token continuation now works correctly when the client submits explicit same-speaker chains via `prime_from_segment`. Two bugs were fixed: (1) the v2 batch route dispatched all segments as independent background tasks immediately, so a chained segment's `continue_from_cache_key` disk load always missed because the prior segment's `.tokens.pt` sidecar had not been written yet; (2) the `total == 1` guard on sidecar writes prevented multi-chunk segments from ever persisting tail tokens at all. The fixes: `JobState` now carries an `asyncio.Event` (`_synthesis_done`) set in the `finally` block of `_run_synthesis`; the batch loop awaits the prior job's event before dispatching any chained segment (cache-hit jobs are exempt — the sidecar already exists). The sidecar write guard `if total == 1` is removed across all three Chatterbox backends — the last chunk's tail tokens are now always written regardless of how many internal chunks the request produced.

- **`torch.backends.cuda.sdp_kernel()` FutureWarning suppressed:** the Chatterbox worker subprocess entry point (`rrv-chatterbox/run_worker.py`) now installs a `warnings.filterwarnings` rule at process startup to suppress the `sdp_kernel deprecated` `FutureWarning` emitted by Chatterbox internals on every inference call. The filter is added before any torch imports and cannot be patched in the library itself.

- **LuxTTS defaults updated from benchmarks:** `RRV_LUX_NUM_STEPS` default changed from `10` to `32` (quality ceiling; the previous default had audible frame artifacts). `RRV_LUX_T_SHIFT` default changed from `0.7` to `0.5` (more natural comma/pause handling observed in Provider_Tests.md 2026-04-06 benchmark).

- **`VoiceSlot` is now a string-keyed record struct:** the client's `VoiceSlot` type is now `readonly record struct VoiceSlot(string SlotKey, Gender Gender)` replacing the previous `AccentGroup` enum-keyed design. `SlotKey` is the runtime identity string (e.g. `"Narrator"`, `"BloodElf"`, `"Tauren"`). The `AccentGroup` enum is retained as a legacy compatibility shim; `Group` property derives `AccentGroup` from `SlotKey` at read time. `NpcRaceOverride` stores `CatalogId` as the runtime source of truth for slot resolution; `RaceId` is legacy only.

- **`NpcPeopleCatalog` used for slot resolution:** non-narrator NPC override resolution now calls `AppServices.NpcPeopleCatalog?.ResolveCatalogSlot(catalogId, gender)` rather than using a fixed race→slot mapping. This allows server-defined catalog entries to drive slot assignment at runtime.

- **`UseNpcIdAsSeed` per-NPC flag:** `NpcRaceOverride` carries a `UseNpcIdAsSeed` bool. When set, the NPC's numeric ID is used as the synthesis seed so the same NPC always renders with the same voice characteristics. Seed suppression applies when not set and the NPC is non-narrator.

- **Cache identity documentation corrected again:** server OGG cache key now correctly reflects all fields present in `compute_cache_key()` including `cb_temperature`, `cb_top_p`, `cb_repetition_penalty`, `cosy_instruct`, `voice_instruct`, `synthesis_seed`, `lux_*`, and `longcat_*` — all previously described as being "folded into `voice_context`" but now documented as explicit named fields in the cache key.

- **`RemoteBatchSegmentRequest.PrimeFromSegment` was already wired on the client** (`PrimeFromSegmentId` in `BatchSegmentPlan`, submitted as `prime_from_segment` in batch requests) — confirmed working end-to-end with the v26 server fix.

---

## What's New in v25

- **Client/player-name replacement is now a normal cache-preserving path:** player-name handling is no longer treated as an experimental split mode. Actual player names, cache-friendly titles, class replacements, and optional realm suffixes now all use the same sentence-shaped split flow so the variable portion can be isolated while most of the text remains cacheable. The default mode is **Use cache-friendly title**, the default replacement is **Champion**, and appending realm remains off by default.

- **Wait-for-full-text was corrected after post-split expansion:** playback now waits on the final audible segment count after player-name expansion rather than the pre-split count. The remaining-range wait logic was also corrected so playback waits only on the segments that are still outstanding.

- **Remote batch chaining now carries explicit continuity links:** when the client expands a segment into multiple remote batch pieces, it submits an explicit same-stream chain (`seg_0` → `seg_1` → `seg_2` ...) so the server can resolve each piece against the prior segment's cache identity. This is especially important for name/title isolation splits that can occur mid-sentence.

- **Chatterbox-family large-text handling is now transparent on the server:** `chatterbox`, `chatterbox_full`, and `chatterbox_multilingual` no longer require the client to pre-split every long request to avoid hard failure. Oversized input is split server-side at sentence boundaries, falls back to clause boundaries when needed, synthesizes piece-by-piece, and is rejoined before the OGG is returned.

- **Batch join tail trim is now a first-class server behavior:** client-requested batch segments for Chatterbox-family providers are rejoined with an optional non-final tail trim (`RRV_CB_BATCH_JOIN_TAIL_TRIM_MS`, default 100 ms) applied at the batch handoff layer only. This leaves the backend's internal sentence-based splitting behavior alone while reducing audible seams when the client had to split mid-sentence.

- **Cache identity documentation corrected:** the client local audio cache no longer includes DSP in the key, and the server OGG cache no longer includes `model_version`.

---

## What's New in v24

- **Provider benchmark harness added (Phase 6):** A standalone Python benchmark runner (`rrv_benchmark.py`) for repeatable remote-provider characterization. Fixed embedded corpus, per-provider OGG artifact output, v1/v2 response metrics capture, structured JSON/CSV/Markdown/LLM outputs.

---

## What's New in v23

- **CosyVoice3 backend added:** `cosyvoice` and `cosyvoice_vllm` worker subprocess backends. Zero-shot voice cloning, optional `cosy_instruct` style instructions.

- **LuxTTS backend added:** `lux` flow-matching voice cloning. Controls: `lux_num_steps`, `lux_t_shift`, `lux_return_smooth`.

- **Chatterbox Multilingual backend added:** `chatterbox_multilingual`, 22 languages.

- **Qwen3-TTS backends added:** `qwen_natural`, `qwen_custom`, `qwen_design`.

- **ResourceManager added:** GPU contention coordinator for all backends and ASR. Evicts all eligible idle workers (biggest VRAM first) before loading a new backend.

- **ASR migrated to Whisper subprocess worker.**

- **CosyVoice `prompt_text` bug fixed, `speech_rate` now forwarded.**

- **CosyVoice added to client chunking policy:** 380/480 char limits.

- **Settings checkbox init bug fixed:** `_uiInitializing` guard prevents Click handlers from firing during `InitializeComponent()`.

- **Synthesis response metrics headers added** to v1 and v2 endpoints.

---

## 1. Overview

RuneReader Voice adds spoken voice narration to World of Warcraft text by creating a data pipeline between a WoW addon (RuneReaderVoice) and the RuneReader desktop application. The addon encodes text selected by the Lua addon as QR barcodes displayed on-screen; RuneReader captures, decodes, and passes the text to a TTS engine which synthesizes and plays audio in real time. This is not limited to quests: the same pipeline can be used for quest dialog, NPC greetings, readable books, flight map text, and other text the addon chooses to orchestrate for narration.

Blizzard's in-game TTS is limited to legacy Windows XP/Vista era voices. This feature bypasses that limitation by routing voice generation through the host OS or a configurable AI voice engine, enabling natural-sounding narration for a broad set of in-game text surfaces under addon control.

---

## 1.1 Licensing

- **Desktop client license:** GNU General Public License, version 3 (**GPL v3**).
- **Desktop client source headers:** Use `SPDX-License-Identifier: GPL-3.0-only`.
- **Repository license file:** `LICENSE` contains the standard GNU GPL v3 text.
- **Bundled third-party code:** Third-party files retain their original license and attribution.
- **Server project:** Licensed separately under GPL v3; source headers use `GPL-3.0-or-later`.

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
- NPC override resolution uses `CatalogId` as runtime identity. Resolution order: bespoke sample profile → `NpcPeopleCatalog`-resolved slot → narrator fallback. Unknown or generic humanoid NPCs do not silently fall back to Human.
- Persistent state is split between portable JSON settings (`settings.json`) and the SQLite database `runereader-voice.db`. SQLite stores NPC overrides, pronunciation rules, text shaping rules, and the cache manifest. JSON settings store provider selection, slot profiles, per-provider sample defaults, and capture/playback preferences.

### 4.3 TTS Provider (Pluggable)

The TTS backend is abstracted behind `ITtsProvider`. All providers synthesize to `PcmAudio`. No provider creates temporary files. The cache layer owns all on-disk writes.

### 4.4 TTS HTTP Server (Phase 5 — Separate Project)

An optional standalone server the desktop client can call instead of synthesizing locally. Supports multiple simultaneous LAN clients and higher-quality or GPU-accelerated synthesis. Shared L2 render cache across all clients. The implemented server backend set currently includes Kokoro-82M ONNX, F5-TTS, Chatterbox Turbo, Chatterbox Full, Chatterbox Multilingual, CosyVoice3, CosyVoice3-vLLM, LuxTTS, LongCat-AudioDiT, and three Qwen3-TTS backends. The client remains authoritative over text, provider selection, voice choice, and DSP. See Section 21.

---

## 5. Data Pipeline

### 5.1 Pipeline Overview

| Stage | Description |
|---|---|
| RvBarcodeMonitor | Runs continuous region polling plus periodic full-screen relocation scans, decodes valid RV packets, and fires `OnPacketDecoded` per non-preview packet. |
| TtsSessionAssembler | Collects chunks by DIALOG ID. Fires `OnSegmentComplete` when all chunks arrive. Resolves NPC voice slot via `CatalogId` from `NpcPeopleCatalog`. Carries player-name metadata through assembled segments so the playback path can perform cache-preserving replacement and remote batch planning. Fires `OnSessionReset` on DIALOG ID change. Sets `AppServices.LastSegment` after each completion. |
| PlaybackCoordinator | Dequeues segments, checks cache (`TryGetDecodedAsync` returns `PcmAudio`), resolves remote batch submissions when a segment was expanded into client-planned subsegments (including player-name splits), and feeds `PcmAudio` to `PlaylistPlayAsync`. In `WaitForFullText` mode it waits on the final post-split audible segment count, not just the assembler's original count. |
| ITtsProvider / TtsAudioCache | Provider yields `PcmAudio` phrases. Cache encodes each phrase to OGG in `StoreAsync`. Cache reads decode OGG back to `PcmAudio` via NVorbis. Client cache identity is based on text + resolved voice identity + provider only; DSP is applied after cache retrieval and is not part of the local audio cache key. |

### 5.2 Segment Assembly Rules

- All segments in a dialog must assemble before any fire (holds until `_completedSegments.Count == _seqTotal`).
- Re-loop detection: completed segment keys are stored in `_completedKeys`; duplicate SUB=0 arrivals are silently ignored.
- Early sub-chunks (non-zero sub arriving before SUB=0) are stashed in `_earlyChunks` keyed by `(subTotal, flags, race, seqIndex)` and replayed when SUB=0 arrives.
- Non-zero sub matching requires `Subs[0] != null` to prevent stale subs from contaminating a new accumulator.
- Utterance deduplication: `_completedUtteranceKeys` keyed by `(dialogId, seqIndex, slot, npcId, text)` prevents the same utterance from firing twice even if the segment key changes.
- Segment text passes through: HTML extraction or text stripping → metadata token extraction → player-name replacement → paragraph period injection → narrator-marker expansion (for NPC segments with embedded `<...>` or `[...]` narrator blocks).

### 5.3 Narrator Marker Expansion

If a non-narrator NPC segment contains text in angle brackets `<...>` or square brackets `[...]`, `ExpandNarratorForcedSegments` splits the text into runs and emits NPC and narrator sub-segments alternately. Narrator runs use the gender-matched narrator slot. NPC runs inherit all bespoke fields from the parent segment. This expansion happens after all segments are assembled, before `OnSegmentComplete` fires.

### 5.4 Player Name Handling

The assembler extracts `RRV:PLAYER=`, `RRV:REALM=`, `RRV:CLASS=`, and `RRV:TITLE=` metadata tokens from the text stream (embedded between `\x02` and `\x03` delimiters by the addon). These are stored in assembler state and applied as player-name replacement in `ApplyPlayerReplacement`.

Player name mode (`AppServices.Settings.PlayerNameMode`):
- `"generic"` / `"champion"` / `"class"` / `"title"` — replaces the actual name with a preset string.
- `"actual"` — uses the actual player name.
- `"split"` — splits the segment into a batch with the variable portion isolated for cache purposes.

Replacement preset options: `"hero"`, `"champion"`, `"class"`, `"title"`. Title replacement supports `%s` substitution with the player's actual name.

### 5.5 Remote Batch Submission

When a segment has `BatchSegments` populated (player-name split, long-text split, etc.), `PlaybackCoordinator` calls `RemoteTtsProvider.SubmitSplitBatchAsync` to submit all segments as a single v2 batch request. Each segment in the batch carries:
- `segment_id` — client-generated UUID for the segment
- `prime_from_segment` — the `segment_id` of the prior same-speaker segment (for T3 token continuation)
- `voice_context` — the slot identity string (prevents cross-slot cache collisions)
- `cache_key` — client-computed server cache key

All segments in the batch share a `BatchId`. The server resolves `prime_from_segment` to the prior segment's server-side cache key and gates synthesis of the chained segment on completion of its predecessor (see Section 21.10c).

---

## 6. Text Processing

### 6.1 TextChunkingPolicy

Provider-aware chunking. `BuildChunks(text, providerId, profile, enabled)` splits text into `TextChunk` objects. Splitting hierarchy: paragraph → single line → sentence → clause → comma → forced length split. Tiny trailing chunks are merged backward or forward.

Per-provider profiles (from 2026-04 benchmark runs):

| Provider family | TargetChars | HardCapChars | ListItemLimit | RepeatedSentenceLimit | Notes |
|---|---|---|---|---|---|
| Kokoro | 850 | 1050 | 12 | 5 | |
| F5-TTS | 575 | 725 | 10 | 4 | |
| Chatterbox Turbo | 600 | 720 | 8 | 3 | |
| Chatterbox Full | 800 | 1042 | 6 | 2 | Server-side internal chunking handles oversized requests transparently. `SplitOnSingleLines=false`. |
| CosyVoice / CosyVoice-vLLM | 380 | 480 | 6 | 2 | Hyphenated compound number words (`twenty-one` etc.) trigger early split at ≥4 occurrences. |
| LongCat | 100 | 150 | 6 | 2 | ~20-word safe ceiling per chunk. |
| Generic | 700 | 850 | 10 | 4 | |

High-exaggeration tightening: when `profile.Exaggeration >= 1.0` for Chatterbox-family providers, TargetChars is reduced by ~30%, HardCapChars by ~25%, ListItemLimit reduced by 2, RepeatedSentenceLimit reduced by 1.

Pattern-based early splits fire before the char limit is reached:
- Number-heavy text (spelled-out number words) with ≥ ListItemLimit comma-separated items
- Repeated-frame sentences (same 3-word prefix ≥ RepeatedSentenceLimit times)
- Chatterbox: rumor-frame clusters ("some say"/"folk say") and pivot sentence clusters ("well"/"listen"/"truth be told")
- CosyVoice: hyphenated compound number words (≥4 in text)

### 6.2 ChatterboxPreprocess

Applied before sending to Chatterbox-family and CosyVoice backends:
- Strips inline angle-bracket annotations `<...>` (preserves inner text)
- Strips inline square-bracket annotations `[...]` (preserves inner text)
- Reconstructs dash-interrupted sentences across paragraph breaks (`word-... -word` → `word. word`)
- Removes orphaned leading/trailing dashes
- Collapses multiple spaces

### 6.3 Text Normalization (Server Side)

`text_normalize.normalize()` applies WoW-specific normalization plus wetext English text normalization layer 2 before synthesis and before cache key computation. Toggleable via `RRV_WETEXT=false` for diagnostics.

---

## 7. VoiceSlot and Voice Resolution

### 7.1 VoiceSlot

`VoiceSlot` is `readonly record struct VoiceSlot(string SlotKey, Gender Gender)`.

- `SlotKey`: the runtime identity string, e.g. `"Narrator"`, `"BloodElf"`, `"Tauren"`, `"Dragonkin"`.
- `Gender`: `Gender.Male`, `Gender.Female`, or `Gender.Unknown`.
- `IsNarrator`: true when `SlotKey == "Narrator"` (case-insensitive).
- `ToString()` returns `"Narrator/Male"` / `"Narrator/Female"` for narrator, or `"{SlotKey}/{Gender}"` for others (used as `voice_context` in server requests).
- Static constants: `VoiceSlot.Narrator`, `VoiceSlot.MaleNarrator`, `VoiceSlot.FemaleNarrator`.
- `AccentGroup Group` property: derives legacy `AccentGroup` enum value from `SlotKey` at read time (compatibility shim; not stored).

`Gender` enum: `Unknown = 0`, `Male = 1`, `Female = 2`.

`VoiceSlot.CreateCatalog(string catalogId, Gender gender)` constructs a slot from a catalog-defined slot key at runtime.

### 7.2 NpcRaceOverride

| Field | Type | Description |
|---|---|---|
| NpcId | int | NPC ID from unit GUID segment 6. |
| CatalogId | string | Runtime slot identity. Used as primary key for `NpcPeopleCatalog.ResolveCatalogSlot()`. |
| RaceId | int | Legacy field. Used only when CatalogId is absent (backward compat). |
| Notes | string? | User label. |
| BespokeSampleId | string? | Provider-neutral logical sample ID. Narrator segments are never affected. |
| BespokeExaggeration | float? | NPC-local exaggeration override. |
| BespokeCfgWeight | float? | NPC-local `cfg_weight` override. |
| UseNpcIdAsSeed | bool | When true, NPC ID is used as synthesis seed for consistent voice per-NPC. |
| Source | NpcOverrideSource | Local / CrowdSourced / Confirmed. |
| Confidence | int? | Server-assigned vote count (null for local entries). |
| UpdatedAt | double | Unix timestamp of last update. |

### 7.3 Effective Voice Resolution Order

For a non-narrator NPC segment:
1. Look up `NpcRaceOverride` by NpcId.
2. If found: resolve slot via `NpcPeopleCatalog.ResolveCatalogSlot(CatalogId, gender)`. Apply bespoke sample and parameters if set.
3. If no override or no valid CatalogId: use packet race → narrator fallback.
4. Bespoke sample resolution: look up `PerProviderSampleProfiles[providerId][sampleId]` as the base profile, then apply NPC-local tweaks on top.

Narrator segments always use the narrator slot regardless of NpcId.

---

## 8. Audio Playback

### 8.1 IAudioPlayer Interface

```csharp
interface IAudioPlayer
{
    bool IsPlaying { get; }
    Task PlayAsync(PcmAudio audio, CancellationToken ct);
    void Stop();
}
```

### 8.2 WasapiStreamAudioPlayer (Windows)

Uses NAudio `WasapiOut` (latency: 100ms shared mode) with a `BufferedWaveProvider` (2-second buffer duration).

- Output format: 48000 Hz, 16-bit, stereo. Input `PcmAudio` resampled/converted as needed.
- `BufferedWaveProvider` fed by a background `_feedTask` that pulls `PcmAudio` chunks from the playlist enumerator.
- Speed control applied via pitch-corrected tempo (NAudio sample providers).
- Volume applied via `VolumeSampleProvider`.
- `SetOutputDevice(deviceId)` supported; null = system default.
- `CancellationToken` cancels the feed task; `WasapiOut.Stop()` called on cancellation or completion.

### 8.3 GstAudioPlayer (Linux)

Sequential fallback: iterates the `PcmAudio` stream and calls `PlayAsync` for each chunk in order. GStreamer EOS detection is still a stub. True gapless playback deferred.

---

## 9. NPC Race Override System

### 9.1 Purpose

The RV protocol provides a RACE byte per NPC, but it reflects the NPC's base race and may not accurately capture the intended accent. The NPC Race Override system allows the user to explicitly assign a voice slot to any NPC by NPC ID, and optionally assign a bespoke reference voice sample for voice-matching providers.

### 9.2 Source Hierarchy

Three tiers, highest priority wins:
- **Local:** user-entered on this machine. Full CRUD. Stored in `runereader-voice.db`.
- **CrowdSourced:** received from server poll. Read-only on the client; shadowed by a local entry for the same NPC ID.
- **Confirmed:** hand-verified by server admin. Read-only on the client; shadowed by a local entry.

### 9.3 Storage — NpcRaceOverrideDb

- Thin wrapper over `RvrDb` exposing domain-model API.
- Table: `NpcRaceOverrides` with `int` PK on `NpcId`.
- `UpsertAsync(npcId, raceId, catalogId, notes, bespokeSampleId?, bespokeExaggeration?, bespokeCfgWeight?, useNpcIdAsSeed, source, confidence)`.
- `MergeFromServerAsync(IEnumerable<NpcRaceOverride>)`: merges server records with Local-wins logic.
- `LegacyRaceIdToCatalogId(int raceId)` converts old integer race IDs to catalog slot key strings for backward compat.

### 9.4 TtsSessionAssembler Integration

- `TryCompleteSegment` reads `NpcRaceOverride` synchronously via `Task.Run(...).GetAwaiter().GetResult()` at segment completion time.
- Resolves `CatalogId` → `VoiceSlot` via `AppServices.NpcPeopleCatalog?.ResolveCatalogSlot(catalogId, gender)`.
- Falls back to `new VoiceSlot(catalogId, gender)` if catalog is unavailable.
- Bespoke fields (`BespokeSampleId`, `BespokeExaggeration`, `BespokeCfgWeight`, `UseNpcIdAsSeed`) are embedded into `AssembledSegment` at fire time.

### 9.5 UI — Last NPC Panel

Appears below the status panel after any NPC segment completes (NpcId != 0). Hidden for narrator/book segments.

- **Row 0:** NPC ID label, notes box, Save, Clear.
- **Row 1:** Voice accent dropdown (full race/creature-type list from `NpcVoiceSlotCatalog.All`). Recently saved choices float to the top through a small decaying recency score.
- **Row 2:** Two-level voice sample picker — base sample ID dropdown + variant suffix dropdown. Combined selection produces the full logical sample ID. Populated from `GetAvailableVoices()` filtered by provider duration limits (F5-TTS ≤11s, Chatterbox ≤40s). First item is "(race default)". Voice list warmed at startup via background `RefreshVoiceSourcesAsync`. Bespoke sample applies only to NPC voice slots — never narrator.

### 9.6 Community Sync

The server exposes NPC override endpoints for crowd-sourcing. `NpcSyncService` on the client polls every 5 minutes. On first load (`FirstLoadComplete = false`), pulls all four default types from server before setting `FirstLoadComplete = true`. `ContributeByDefault = true` enables silent background contribution on every local save.

---

## 10. Audio Cache (Desktop Client)

### 10.1 OGG-Only Strategy

`StoreAsync` receives `PcmAudio`, transcodes to OGG synchronously via `Task.Run`, and writes OGG as the sole manifest entry. No intermediate WAV.

`TryGetDecodedAsync` checks the DB manifest, validates the file exists and is `.ogg`, decodes via NVorbis, and returns `PcmAudio`. If the DB row exists but the file is missing, the row is deleted and null is returned.

### 10.2 Client Local Audio Cache Key

SHA-256 hash of fields joined by null bytes (`\x00`), truncated to 16 lowercase hex characters:

```
text \x00 voiceId \x00 providerId
```

The method signature accepts a `dspKey` parameter but DSP is intentionally excluded from the hash. DSP is a client-side post-process applied after cache retrieval.

`voiceId` is the resolved voice identity string, slot-namespaced (e.g. `"Narrator:<identity>"` or `"Amani/Female:<identity>"`).

### 10.3 Cache Identity Rules (Client and Server)

#### Client local audio cache (L1)

Key inputs:
- normalized segment text (CRLF→LF, trimmed)
- resolved voice identity string (from `BuildIdentityKey()`)
- provider ID

`BuildIdentityKey()` includes: `VoiceId`, `LangCode`, `SpeechRate`, `CfgWeight`, `Exaggeration`, `CfgStrength`, `NfeStep`, `SwaysamplingCoef`, `VoiceInstruct`, `CosyInstruct`, `SynthesisSeed`, `ChatterboxTemperature`, `ChatterboxTopP`, `ChatterboxRepetitionPenalty`, `LongcatSteps`, `LongcatCfgStrength`, `LongcatGuidance`.

Text normalization for cache: `NormalizeSubmittedTextForCache()` normalizes line endings (CRLF→LF, CR→LF) and trims outer whitespace. Internal line breaks are preserved as part of cache identity.

Client cache key schema version: `CK1` + integer version field (`RemoteCacheVersion`).

#### Server OGG render cache (L2)

Cache key schema version: `CACHE_KEY_SCHEMA_VERSION = "L2V1"`. The full key is a SHA-256 hash of null-byte-joined fields, first 32 hex chars:

```
L2V1 \x00 normalized_text \x00 provider_id \x00 voice_identity \x00 lang_code \x00
speech_rate \x00 cfg_weight \x00 exaggeration \x00 cfg_strength \x00 nfe_step \x00
cross_fade_duration \x00 sway_sampling_coef \x00 voice_context \x00 voice_instruct \x00
cosy_instruct \x00 synthesis_seed \x00 cb_temperature \x00 cb_top_p \x00
cb_repetition_penalty \x00 longcat_steps \x00 longcat_cfg_strength \x00 longcat_guidance \x00
lux_num_steps \x00 lux_t_shift \x00 lux_return_smooth
```

`voice_identity` by voice type:
- `base` → raw `voice_id` string
- `reference` → content hash of the reference sample file
- `description` → SHA-256 of the description text (first 16 chars)
- `blend` → canonical sorted `"id:weight|id:weight"` string

`voice_context` carries the slot identity string (e.g. `"NightElf/Female"`) supplied by the client. This prevents two slots sharing the same sample from colliding in the server cache.

`model_version` is **not** part of the server OGG cache key. Provider subdirectories isolate the cache by backend; replacing model artifacts requires manual provider cache clearing.

Server-side client cache key composition: `compose_server_cache_key(client_cache_key, asset_fingerprint)` → `"{client_cache_key}.{asset_hash_suffix}"`. Used when the client provides its own pre-computed key.

#### Tail-token sidecars

`.tokens.pt` sidecar files are stored alongside OGG files at `{cache_dir}/{provider_id}/{key}.tokens.pt`. These are conceptually separate from the conditioning cache and should not be conflated with it. See Section 21.10c.

### 10.4 DB Schema — RvrDb

All four tables live in `runereader-voice.db` managed by `RvrDb`:

| Table | Row Type | Purpose |
|---|---|---|
| `NpcRaceOverrides` | `NpcRaceOverrideRow` | NPC accent/slot assignments + bespoke sample ID + UseNpcIdAsSeed flag |
| `PronunciationRules` | `PronunciationRuleRow` | User pronunciation rules |
| `TextSwapRules` | `TextSwapRuleRow` | User text swap rules |
| `AudioCacheManifest` | `AudioCacheManifestRow` | Cache file registry |

### 10.5 Storage Locations

| Path | Contents |
|---|---|
| `<ConfigDir>/runereader-voice.db` | Unified SQLite database (all four tables) |
| `<CacheDir>/` | OGG audio files (path configurable via settings or env var) |
| `<ConfigDir>/settings.json` | User settings, slot profiles, and per-provider voice sample defaults |
| `<AppDir>/models/` | Kokoro ONNX model files (shared with Python server) |

### 10.6 Eviction

- No TTL — quest dialog is static content. A cached line is valid indefinitely.
- Configurable max size (default 500 MB). LRU eviction on startup and after each `StoreAsync` when limit is exceeded.
- LRU ordered by `LastAccessedUtcTicks` (server-clock only; no client-supplied timestamps).
- Manual clear button in Advanced > Cache expander.

---

## 11. Rule Stores

### 11.1 PronunciationRuleStore

Instance class injected via `AppServices.PronunciationRules`. Backed by the `PronunciationRules` table in `RvrDb`.

- `GetAllEntriesAsync()` → `List<PronunciationRuleEntry>`.
- `LoadUserRulesAsync()` → `IReadOnlyList<PronunciationRule>` (enabled, valid rows as domain objects).
- `UpsertRuleAsync(entry)`: natural key is `(MatchText, Scope, WholeWord, CaseSensitive)`.
- `DeleteRuleAsync(entry)`, `ClearAllAsync()`.

### 11.2 TextSwapRuleStore

Instance class injected via `AppServices.TextSwapRules`. Backed by the `TextSwapRules` table in `RvrDb`. Natural key is `(FindText, WholeWord, CaseSensitive)`. Built-in text shaping defaults are seeded as normal editable rows on first-run database creation.

### 11.3 Import / Export

Both rule stores, slot voice profiles, and per-provider voice sample defaults support JSON import/export via Avalonia `StorageProvider` file pickers. Import is additive (upsert by natural key).

---

## 12. Settings UI

### 12.1 Top Chrome (Always Visible)

- Title bar: "RuneReader Voice" + status badge.
- Live status panel: Capture / Session / Playback / Cache status indicators.
- Volume row: Vol label + Volume slider + percentage label + Start/Stop button.

### 12.2 Current Settings Defaults

- `Player name handling` default: **Use cache-friendly title**
- `Replacement title` default: **Champion**
- `Append realm as "of <Realm>"` default: **false**
- `Playback Mode` default: **Wait for full text**
- `Phrase chunking` default: **off**

### 12.3 Tab Layout

| Tab | Left Column | Right Column |
|---|---|---|
| Settings | TTS Provider, Playback | Dialog Sources |
| Voices | Voice grid + Export/Import buttons | — |
| Voice Defaults | Per-provider sample list, Edit / Refresh / Import / Export / Push / Pull | — |
| NPC Voices | Last NPC panel, NPC override grid | — |
| Advanced | Capture, Playback, Cache | Audio, Piper, Diagnostics, Hotkey |
| Pronunciation | Workbench, Sound Picker, Preview | Saved rules list, rule authoring controls |
| Text Shaping | Workbench + preview | Saved rules list + rule authoring controls |

### 12.4 Voice Assignment Grid (Voices Tab)

65 voice slots total: 1 Narrator + 32 accent groups × Male + Female (includes Illidari). Every playable race has its own dedicated slot pair. Sort order groups: 0=Narrator, 10s=Alliance, 100s=Horde, 200s=Neutral/Cross-faction, 300s+=Creature types. Populated in code-behind by `PopulateVoiceGrid()` iterating `NpcVoiceSlotCatalog.All`.

### 12.5 Voice Defaults Tab

Presents all available voices/samples for the active provider. Lets the user edit a base profile per sample. Stored in `settings.json` under `PerProviderSampleProfiles`. Exportable/importable/push/pull as all-provider payload. Editor hides slot-only controls (voice mode, preset, blend config) that do not apply to per-sample editing.

### 12.6 Voice Profile Editor

Layout: fixed top area (voice controls, language, speech rate, live preview state) + scrollable lower area (advanced render controls, DSP, summary). Seed help text describes blank as provider-default behavior.

---

## 13. Addon File Structure

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
| GOSSIP_SHOW | C_GossipInfo.GetText() | NPC greeting / gossip | Deferred 1 frame via C_Timer.After(0). |
| QUEST_GREETING | GetGreetingText() | Multi-quest NPC greeting | |
| QUEST_DETAIL | GetTitleText() + GetQuestText() + GetObjectiveText() | Quest accept dialog | Title and objective prefixed with `\x01` SOH sentinel. |
| QUEST_PROGRESS | GetProgressText() | Quest in-progress check-in | |
| QUEST_COMPLETE | GetRewardText() | Quest completion / reward | |
| QUEST_FINISHED | — | Quest accept / decline / auto-accept | Shared delayed-close timer. |
| ITEM_TEXT_BEGIN | — | Book / readable item opened | Sets `_bookActive`. |
| ITEM_TEXT_READY | ItemTextGetText() + ItemTextGetItem() | In-game books / readable items | Scan mode or per-page mode. |
| ITEM_TEXT_CLOSED | — | Book closed | Clears book state, calls `StopDisplay`. |

### 14.2 Stop-on-Close Events

TAXIMAP_CLOSED, ADVENTURE_MAP_CLOSE, MERCHANT_CLOSED, TRAINER_CLOSED, BANKFRAME_CLOSED, MAIL_CLOSED, AUCTION_HOUSE_CLOSED, LOOT_CLOSED. Delayed close set to **5 seconds**.

### 14.3 Window Close Detection

`HookScript("OnHide")` on GossipFrame, QuestFrame, ItemTextFrame. Frame hooks fire unconditionally on every hide regardless of close mechanism (Escape, click-away, programmatic close).

### 14.4 NPC Gender Detection

Primary: `UnitSex("questnpc")`. Fallback: `UnitSex("npc")`, then `UnitSex("target")`. Returns: 1=unknown, 2=male, 3=female.

---

## 15. QR Payload Protocol (v05)

### 15.1 Payload Structure

```
[ MAGIC(2) | VER(2) | DIALOG(4) | SEQ(2) | SEQTOTAL(2) | SUB(2) | SUBTOTAL(2) | FLAGS(2) | RACE(2) | NPC(6) | BASE64_PAYLOAD ]
```

| Field | Chars | Description |
|---|---:|---|
| MAGIC | 2 | "RV" |
| VER | 2 | "05" |
| DIALOG | 4 | Dialog block ID hex. Change signals new dialog. |
| SEQ | 2 | 0-based segment index within dialog. |
| SEQTOTAL | 2 | Total segment count. Known upfront from `SplitSegments`. |
| SUB | 2 | 0-based barcode chunk index within segment. |
| SUBTOTAL | 2 | Total chunk count for segment. |
| FLAGS | 2 | Speaker / control flags (see below). |
| RACE | 2 | NPC race or creature type ID. |
| NPC | 6 | NPC ID hex. "000000" for non-creature units / preview. |
| BASE64_PAYLOAD | Variable | Space-padded Base64 text chunk. |

### 15.2 FLAGS Byte

| Bit | Mask | Name | Meaning |
|---:|---:|---|---|
| 0 | 0x01 | FLAG_NARRATOR | Narrator voice. |
| 1 | 0x02 | GENDER_MALE | NPC is male. |
| 2 | 0x04 | GENDER_FEMALE | NPC is female. |
| 3 | 0x08 | FLAG_PREVIEW | Settings panel preview — client must discard. |
| 4–7 | 0xF0 | (reserved) | Must be zero. |

### 15.3 RACE Byte

| Range | Meaning |
|---|---|
| 0x00 | Unknown → narrator fallback |
| 0x01–0x3F | Player race IDs |
| 0x40–0x4F | Reserved |
| 0x50 | Humanoid (non-playable) |
| 0x51 | Beast |
| 0x52 | Dragonkin |
| 0x53 | Undead (non-Forsaken) |
| 0x54 | Demon / Illidari |
| 0x55 | Elemental |
| 0x56 | Giant |
| 0x57 | Mechanical |
| 0x58 | Aberration |

**Decoding order:** creature types 0x50–0x58 must be checked before the broader 0x01–0x7F player race range in `AccentGroup.ResolveGroup` so Elemental (0x55), Giant (0x56), etc. are reachable.

### 15.4 Chunk Sizing

| Preset | Pad bytes | Approx. QR payload chars |
|---|---:|---:|
| Small | 50 | ~90 |
| Medium (default) | 135 | ~206 |
| Large | 250 | ~354 |
| Custom | 50–500 | — |

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
    VoiceProfile? ResolveProfile(VoiceSlot slot);
    IReadOnlyList<VoiceInfo> GetAvailableVoices();
}
```

### 16.1 Provider Implementations

| Provider | Platform | Notes |
|---|---|---|
| KokoroTtsProvider | Windows / Linux (CPU) | Primary AI voice provider. 54 voices. `SupportsInlinePronunciationHints = true`. Parallel phrase encoding via Channel. |
| WinRtTtsProvider | Windows only | WinRT SpeechSynthesizer. |
| LinuxPiperTtsProvider | Linux only | Piper subprocess. Windows Piper deferred. |
| RemoteTtsProvider | All platforms | Calls Phase 5 HTTP server via v2 async API. `RequiresFullText = true`. Returns OGG decoded to PcmAudio. Falls back to local rendering on server unavailability. |
| NotImplementedTtsProvider | All | Placeholder. |

### 16.2 RemoteTtsProvider Synthesis Flow

**Single-segment path (SynthesizeOggCoreAsync):**
1. `TextChunkingPolicy.GetChunkTexts` to split if needed.
2. If one chunk: submit single v2 job → await SSE → fetch OGG.
3. If multiple chunks: Phase 1 — submit all chunks simultaneously; Phase 2 — fetch all results in parallel (each awaiting its own SSE stream). Concatenate PCM, re-encode to OGG.

**Split-batch path (SubmitSplitBatchAsync):**
1. Build `RemoteSynthesizeV2BatchRequest` with all `BatchSegmentPlan` items.
2. Each segment carries `CacheKey` (client-computed), `VoiceContext` (slot string), and `PrimeFromSegment` (prior segment ID for T3 continuation).
3. Submit to `POST /api/v1/synthesize/v2/batch`.
4. Each segment result fetched individually via `FetchBatchSegmentResultAsync` → `WaitForJobAsync` → `GetV2ResultAsync`.

**Batch SSE monitoring:** a `_MonitorBatchProgress` background task subscribes to `GET /api/v1/synthesize/v2/batch/{batchId}/progress` and updates `AppServices` generation activity status while synthesis is running. Retries up to 15 times if the batch is not yet registered.

---

## 17. Performance Characteristics

### 17.1 Addon

| Metric | Value | Notes |
|---|---|---|
| CPU while display active | ~0.3% | Measured in-game during quest dialog cycling. |
| CPU burst on dialog open | ~5% | Pre-encoding all QR matrices. |
| Chunk display time (default) | 100ms | 20× read margin vs. RuneReader 5ms capture interval. |

### 17.2 Kokoro Synthesis Performance (Observed)

| Metric | Value | Notes |
|---|---|---|
| First-phrase latency | ~200–400ms | Time from segment complete to first audio playing. |
| Parallel phrase encoding | Yes | All phrase jobs enqueued to ONNX immediately. |

---

## 18. Development Phases

| Phase | Status | Summary |
|---|---|---|
| Phase 1 | COMPLETE | Addon QR protocol, dialog capture, segmented payloads, preview flags, race/NPC metadata, pre-encoding, frame hooks. |
| Phase 2 | COMPLETE | Local TTS integration, Kokoro support, settings UI, slot assignment, profile-aware voice refinement, dialect/language support, speech rate, presets, cache identity updates, voice editor UX. |
| Phase 3 | COMPLETE | Phrase-level streaming, TextSplitter, Channel-based parallel phrase encoding, PCM-first architecture, WasapiStreamAudioPlayer, OGG-only cache. |
| Phase 4 | COMPLETE | Linux support: GStreamer playback stub, Piper provider stub, cross-platform abstraction. |
| Phase 4b | COMPLETE | Unified SQLite back-end (RvrDb), DB-backed cache manifest, instance-based rule stores, two-column tab layout, import/export on all rule tabs. |
| Phase 5 | COMPLETE | TTS HTTP server: shared L2 render cache, provider capability discovery, reference sample API, worker subprocess architecture, ResourceManager GPU contention management, Whisper subprocess ASR, all backends. Synthesis response metrics headers. Client RemoteTtsProvider integration, v2 async API, batch submit. |
| Phase 6 | COMPLETE | Provider benchmark harness. |
| Phase 7 | PLANNED | Platform polish: Windows Piper, full GstAudioPlayer EOS, silence trimming, GstAudioSlayer PcmAudio interface update. |

---

## 19. Known Limitations / TODO

- **GstAudioPlayer:** EOS detection is a stub. True gapless playback not implemented. PcmAudio interface not yet updated.
- **Silence trimming:** `TrimSilence` is a pass-through stub.
- **Windows Piper:** deferred until libpiper C API ships.
- **Kokoro cache identity normalization:** logically identical mixes with different ordering/weight formatting still hash differently.
- **Default/recommended voice mappings:** `GetPreferredSampleStem` / `SpeakerPresetCatalog` presets need updating for each remote provider once the voice library is assembled.
- **CosyVoice chunking limits uncharacterized:** the 380/480 char limits are conservative estimates. Empirical benchmarking needed.
- **Cache identity hardening:** client and server do not normalize text identically; numeric precision rules differ across subsystems. Planned future work.
- **Sample intake job-state durability:** the watcher infers progress from files/sidecars rather than a persisted per-sample job ledger.

---

## 20. Security / Privacy

- No network transmission in Phases 1–4b unless the user explicitly selects a remote provider.
- Local providers keep text on the user's machine.
- HTTP/cloud providers are opt-in.
- Phase 5 server is LAN-oriented and offline-first. Optional API key for auth. Model files are staged manually; the server does not reach the public internet.
- Three separate auth keys: `RRV_API_KEY` (synthesis endpoints), `RRV_CONTRIBUTE_KEY` (crowd-source contributions), `RRV_ADMIN_KEY` (admin confirm/edit).

---

## 21. TTS HTTP Server (Phase 5)

### 21.1 Summary

The TTS HTTP server is a separate Python/FastAPI project. It is a shared **L2 render cache** for LAN clients. The client remains fully authoritative over text shaping, pronunciation processing, provider choice, voice choice, speech rate, and downstream DSP. The server renders exactly what the client requests, caches the result, and serves it to subsequent clients with the same request.

**L1 cache:** local client `TtsAudioCache` (SQLite manifest + OGG files on the client machine).  
**L2 cache:** server SQLite manifest + OGG files on the server machine, shared across all clients.

DSP processing stays entirely client-side.

### 21.2 Implemented Backends

| Backend | Provider ID | GPU Support | Notes |
|---|---|---|---|
| Kokoro-82M ONNX | `kokoro` | CPU / ORT-CUDA / ORT-ROCm | Base voices + inline pronunciation markup. |
| F5-TTS | `f5tts` | CUDA / ROCm / CPU (slow) | Reference-based voice matching. Vocoder: `auto` (BigVGAN if staged, else Vocos), `bigvgan`, or `vocos` via `RRV_F5_VOCODER`. |
| Chatterbox Turbo | `chatterbox` | CUDA / CPU | Reference-based. `cfg_weight`, `exaggeration`, `cb_temperature`, `cb_top_p`, `cb_repetition_penalty`. |
| Chatterbox Full | `chatterbox_full` | CUDA / CPU | Original Chatterbox model. Same controls as Turbo. |
| Chatterbox Multilingual | `chatterbox_multilingual` | CUDA / CPU | 22 languages. Same controls. Always max concurrent = 1. |
| CosyVoice3 | `cosyvoice` | CUDA / CPU | Hybrid LLM+flow-matching zero-shot cloning. Optional `cosy_instruct`. |
| CosyVoice3-vLLM | `cosyvoice_vllm` | CUDA | vLLM-accelerated. `RRV_COSYVOICE_VLLM_MAX_CONCURRENT` (default 6). |
| LuxTTS | `lux` | CUDA / CPU | Flow-matching cloning. `lux_num_steps` (default **32**), `lux_t_shift` (default **0.5**), `lux_return_smooth`. |
| LongCat-AudioDiT | `longcat` | CUDA / CPU | Latent diffusion. `longcat_steps` (default 16), `longcat_cfg_strength` (default 4.0), `longcat_guidance` (`apg`/`cfg`). Model variants: `1B`, `3.5B-bf16`, `3.5B`. |
| Qwen3-TTS Natural | `qwen_natural` | CUDA | Reference cloning + `voice_instruct`. Model size: `large`/`small` via `RRV_QWEN_NATURAL_SIZE`. |
| Qwen3-TTS Custom | `qwen_custom` | CUDA | Same as Natural with separate size config (`RRV_QWEN_CUSTOM_SIZE`). |
| Qwen3-TTS Design | `qwen_design` | CUDA | Voice persona design via `voice.type = "description"`. Always large (1.7B). |

### 21.3 Stack and Project Structure

- **Language / framework:** Python 3.11+ + FastAPI + Uvicorn
- **Storage:** SQLite manifest (`server-cache.db`) + filesystem OGG files
- **Transport:** HTTP/JSON control plane; binary OGG audio response body
- **Deployment:** standalone LAN service; container or bare-metal Linux
- **System dependency:** `ffmpeg` — required for sample conversion
- **Model acquisition:** all models must be staged manually; server never downloads at runtime

```
rrv-server/
  server/
    main.py               # FastAPI app, startup, lifespan
    config.py             # env var + CLI arg resolution (Settings singleton)
    gpu_detect.py         # hardware probe, execution provider selection
    manager.py            # ResourceManager — GPU contention and on-demand reload
    cache.py              # SQLite manifest, OGG store/retrieve, tail-token sidecars, LRU eviction
    transcriber.py        # ffmpeg conversion, ASR lazy load/unload, auto .ref.txt
    voice_profiler.py     # librosa signal analysis, auto .txt voice description
    samples.py            # sample directory scanner, sidecar loader
    sync_db.py            # NPC people catalog and provider slot profile SQLite tables
    asr/
      worker_asr.py       # WorkerAsr — subprocess-based ASR (Whisper)
    backends/
      base.py             # AbstractTtsBackend, SynthesisRequest, SynthesisResult
      worker_backend.py   # WorkerBackend — subprocess proxy
      audio.py            # pcm_to_ogg, estimate_duration
      [backend files]
    routes/
      health.py           # GET /api/v1/health
      capabilities.py     # GET /api/v1/capabilities
      providers.py        # GET /api/v1/providers[/{id}[/voices|/samples]]
      synthesize.py       # POST /api/v1/synthesize (v1 synchronous)
      synthesize_v2.py    # POST /api/v1/synthesize/v2, batch, SSE progress
      npc_people.py       # GET/POST /api/v1/npc-people (NPC catalog sync)
      provider_slot_profiles.py  # GET/POST /api/v1/provider-slot-profiles
      npc_overrides.py    # GET/POST/PUT /api/v1/npc-overrides
      defaults.py         # GET/PUT /api/v1/defaults/{type}

# Worker subprocess launchers — one per isolated venv
rrv-chatterbox/run_worker.py    # Handles chatterbox, chatterbox_full, chatterbox_multilingual
rrv-cosyvoice/run_worker.py
rrv-cosyvoice-vllm/run_worker.py
rrv-f5/run_worker.py
rrv-kokoro/run_worker.py
rrv-lux/run_worker.py
rrv-longcat/run_worker.py
rrv-qwen/run_worker.py
rrv-whisper/run_asr_worker.py
```

### 21.3a ResourceManager

`server/manager.py` coordinates GPU resource contention across all TTS backends and ASR.

**Registration:** every `WorkerBackend` and `WorkerAsr` registers at startup.

**Eviction policy:** before loading, a worker calls `manager.request_load(self)`. The manager evicts **all** eligible idle GPU workers (biggest VRAM first, then least recently used). A worker is immune to eviction for `RRV_BACKEND_RECENT_USE_WINDOW` seconds after last use (default 60 s). VRAM usage is self-reported via `torch.cuda.memory_reserved()` — keeps host GPU-vendor agnostic (ROCm compatible).

**On-demand reload:** `WorkerBackend.synthesize()` detects unloaded state, calls `request_load()`, then calls `load()` to respawn the worker subprocess. Transparent to route handlers.

**Chatterbox max concurrent:** `RRV_CHATTERBOX_MAX_CONCURRENT` (default 2). Chatterbox Multilingual is always limited to 1.

### 21.3b Worker Subprocess Architecture

All TTS backends run as isolated worker subprocesses in their own venvs. The host venv is thin (FastAPI + Whisper only). New backends slot in via one env var (`RRV_WORKER_VENV_<backend>`) + one backend file; no host changes needed.

**Wire protocol (Unix domain socket, length-prefixed):**

```
Request  (host → worker):
  [4 bytes big-endian uint32: JSON length]
  [N bytes: UTF-8 JSON body]

Response (worker → host):
  [4 bytes big-endian uint32: JSON header length]
  [N bytes: UTF-8 JSON header]
  On success: header includes "ogg_len"; followed by:
    [4 bytes big-endian uint32: OGG length]
    [N bytes: raw OGG bytes]
  On error: header has "status": "error"
```

Supported commands: `ping`, `capabilities`, `synthesize`.

Worker exits cleanly on SIGTERM or stdin EOF.

**Venv conflict note:** do NOT co-install Chatterbox and Qwen in the same venv — `transformers==4.46.3` vs `4.57.3` conflict is irreconcilable. `rrv-chatterbox/` handles all three Chatterbox backends.

### 21.3c ASR Architecture

Whisper runs as a worker subprocess (`rrv-whisper/run_asr_worker.py`) using the same Unix socket protocol. `WorkerAsr` manages subprocess lifecycle. After each transcription batch, Whisper unloads itself to free VRAM via `request_load` / `unload`. `RRV_ASR_PROVIDER=whisper` is the only supported value.

Whisper configuration notes:
- Must set `condition_on_previous_text=False`, `language="en"`, `task="transcribe"`
- Must clear conflicting suppress token lists from `generation_config`
- Chunk format: returns `{"timestamp": [s, e]}` — do not read `.get("start")`/`.get("end")`
- Unsloth model variant requires `torch_dtype=` not `dtype=` in `from_pretrained()`

### 21.3d Airgapped / Offline Deployment

Models are never downloaded at runtime. All required artifacts must be staged manually into `RRV_MODELS_DIR`.

| Backend | Local directory |
|---|---|
| Kokoro | `models/kokoro/` |
| F5-TTS | `models/f5tts/` |
| Chatterbox Turbo | `models/chatterbox/` |
| Chatterbox Full | `models/chatterbox-hf/` |
| Chatterbox Multilingual | `models/chatterbox-multilingual/` |
| CosyVoice3 / CosyVoice3-vLLM | `models/cosyvoice/cosyvoice3/` (shared) |
| LuxTTS | `models/lux/` |
| LongCat | `models/longcat/<variant>/` |
| Qwen (all) | `RRV_QWEN_MODELS_DIR` (default `../data/models/qwen`) |
| Whisper | `RRV_WHISPER_MODEL_DIR` (default `./data/models/whisper`) |

### 21.3e Sample Intake Pipeline

1. **Auto-conversion (ffmpeg):** drop any audio/video file into `data/samples/`. Server converts to 44.1 kHz stereo PCM_16 master WAV with 0.5s lead / 1.0s tail silence padding. Source moved to `data/samples/originals/`. Master file renamed to `<base>-master.wav`.

2. **Auto-transcription (Whisper):** any master WAV without a `.ref.txt` transcript sidecar is transcribed automatically. Transcript written as `<stem>.ref.txt`. Whisper loads lazily and unloads after the transcription pass.

3. **Auto-profiling (librosa):** signal analysis, one-line voice description written as `<stem>.txt`.

4. **Auto-extraction:** provider clips and subvariants sliced from the stereo master PCM. Each provider family emits in its own configured format:

| Family | Disk naming | Applies to |
|---|---|---|
| master | `<base>-master.<ext>` | Source of all downstream extraction |
| F5 | `<base>-f5[-<variant>].<ext>` | `f5tts` |
| Chatterbox | `<base>-chatterbox[-<variant>].<ext>` | `chatterbox`, `chatterbox_full` (aliases) |
| LuxTTS | `<base>-lux[-<variant>].<ext>` | `lux` |
| CosyVoice | `<base>-cosyvoice[-<variant>].<ext>` | `cosyvoice`, `cosyvoice_vllm` (aliases) |

Chatterbox clips enforce a 10-second minimum clip length. Polling interval: `RRV_SAMPLE_SCAN_INTERVAL` (default 30 s). Pipeline only activates when at least one voice-matching backend is loaded.

### 21.4 Configuration

| Variable / Option | Default | Description |
|---|---|---|
| `RRV_HOST` / `--host` | `0.0.0.0` | Bind address |
| `RRV_PORT` / `--port` | `8765` | Listen port |
| `RRV_CACHE_DIR` / `--cache-dir` | `./data/cache` | Directory for cached OGG files |
| `RRV_DB_PATH` / `--db-path` | `./data/server-cache.db` | SQLite manifest database path |
| `RRV_MODELS_DIR` / `--models-dir` | `./data/models` | Model files directory |
| `RRV_SAMPLES_DIR` / `--samples-dir` | `./data/samples` | Reference audio clips |
| `RRV_COND_CACHE_DIR` | `../data/cond_cache` | Conditioning cache directory |
| `RRV_API_KEY` | *(empty)* | Optional API key for synthesis endpoints |
| `RRV_CONTRIBUTE_KEY` | *(empty)* | Crowd-source contribution gate |
| `RRV_ADMIN_KEY` | *(empty)* | Admin confirm/edit gate |
| `RRV_GPU` / `--gpu` | `auto` | `auto`, `cuda`, `rocm`, `cpu` |
| `RRV_BACKENDS` / `--backends` | `kokoro` | Comma-separated list: `kokoro`, `f5tts`, `chatterbox`, `chatterbox_full`, `chatterbox_multilingual`, `cosyvoice`, `cosyvoice_vllm`, `lux`, `longcat`, `qwen_natural`, `qwen_custom`, `qwen_design` |
| `RRV_LOG_LEVEL` | `info` | `debug`, `info`, `warning`, `error` |
| `RRV_CACHE_MAX_MB` | `2048` | Server OGG cache size limit in MB |
| `RRV_DEFAULT_SEED` | *(empty — random)* | Server-wide synthesis seed. Integer = deterministic. |
| `RRV_WETEXT` | `true` | Set `false` to disable wetext layer-2 text normalization |
| `RRV_TRUSTED_PROXY_IPS` | `127.0.0.1` | Trusted proxy IPs/CIDRs for X-Forwarded-For |
| `RRV_BACKEND_RECENT_USE_WINDOW` | `60` | Seconds a backend is immune to eviction after last use |
| `RRV_CHATTERBOX_MAX_CONCURRENT` | `2` | Max concurrent for Chatterbox Turbo and Full |
| `RRV_CB_CHUNK_TARGET_CHARS` | `380` | Chatterbox internal sentence-split target chars |
| `RRV_CB_CHUNK_HARD_CHARS` | `480` | Chatterbox internal sentence-split hard cap |
| `RRV_CB_PRIOR_TOKEN_WORDS` | `3` | Word threshold for automatic in-memory prior token priming |
| `RRV_CB_PRIOR_TOKEN_LEN` | `75` | Number of tail tokens to persist for T3 continuation |
| `RRV_CB_BATCH_JOIN_TAIL_TRIM_MS` | `100` | Tail trim on non-final client-batch segments at rejoin (Chatterbox family only) |
| `RRV_SAMPLE_SCAN_INTERVAL` | `30` | Seconds between sample-library polling passes |
| `RRV_ASR_PROVIDER` | `whisper` | ASR provider (only `whisper` supported) |
| `RRV_WHISPER_MODEL_DIR` | `./data/models/whisper` | Whisper model directory |
| `RRV_F5_SAMPLE_CHANNELS` | `1` | F5 reference clip channel count |
| `RRV_F5_SAMPLE_RATE` | `22050` | F5 reference clip sample rate |
| `RRV_F5_VOCODER` | `auto` | F5 vocoder: `auto`, `bigvgan`, `vocos` |
| `RRV_CHATTERBOX_SAMPLE_CHANNELS` | `2` | Chatterbox reference clip channel count |
| `RRV_CHATTERBOX_SAMPLE_RATE` | `44100` | Chatterbox reference clip sample rate |
| `RRV_LUX_SAMPLE_CHANNELS` | `1` | LuxTTS reference clip channel count |
| `RRV_LUX_SAMPLE_RATE` | `48000` | LuxTTS reference clip sample rate |
| `RRV_LUX_NUM_STEPS` | `32` | LuxTTS default ODE solver steps |
| `RRV_LUX_T_SHIFT` | `0.5` | LuxTTS default time shift |
| `RRV_COSYVOICE_VLLM_MAX_CONCURRENT` | `6` | Max concurrent for `cosyvoice_vllm` |
| `RRV_QWEN_NATURAL_SIZE` | `large` | `qwen_natural` model size: `large` or `small` |
| `RRV_QWEN_CUSTOM_SIZE` | `large` | `qwen_custom` model size: `large` or `small` |
| `RRV_QWEN_MODELS_DIR` | `../data/models/qwen` | Qwen model files directory |
| `RRV_LONGCAT_MODEL_VARIANT` | `1B` | LongCat variant: `1B`, `3.5B-bf16`, `3.5B` |
| `RRV_LONGCAT_STEPS` | `16` | LongCat default ODE steps |
| `RRV_LONGCAT_CFG_STRENGTH` | `4.0` | LongCat default guidance strength |
| `RRV_LONGCAT_GUIDANCE` | `apg` | LongCat default guidance mode |
| `RRV_LONGCAT_SAMPLE_RATE` | `22050` | LongCat reference clip sample rate |
| `RRV_LONGCAT_SAMPLE_CHANNELS` | `1` | LongCat reference clip channel count |
| `RRV_WORKER_VENV_<backend>` | — | Path to isolated venv for a backend subprocess |

### 21.5 Provider Capability Model

`GET /api/v1/providers` and `GET /api/v1/providers/{provider_id}` are the authoritative provider-discovery surfaces. The client does not hardcode provider capabilities.

| Flag | Meaning |
|---|---|
| `supports_base_voices` | Built-in named voices (Kokoro, Qwen-Natural/Custom) |
| `supports_voice_matching` | Synthesizes against a reference audio sample |
| `supports_voice_blending` | Weighted mix of multiple voices |
| `supports_voice_design` | Free-text voice persona description (`qwen_design`) |
| `supports_voice_instruct` | Natural-language style instruction alongside reference audio |
| `supports_inline_pronunciation` | Kokoro/Misaki inline IPA phoneme markup |
| `supports_synthesis_seed` | Backend honours `synthesis_seed` (false for deterministic backends like Kokoro) |
| `execution_provider` | Resolved GPU/CPU provider: `cuda`, `rocm`, `cpu` |
| `controls` | Provider-specific optional synthesis controls |

### 21.6 Voice List

`GET /api/v1/providers/{id}/voices` returns voices currently loaded and available — not a theoretical catalog.

### 21.7 Reference Sample List

`GET /api/v1/providers/{id}/samples` returns available reference clips. Sample management is admin-only (filesystem, no upload API). `sample_id` is provider-neutral; provider-tagged filenames are internal.

Provider-family enumeration rules:
- `f5tts` → `-f5` family only
- `chatterbox`, `chatterbox_full` → `-chatterbox` family only
- `cosyvoice`, `cosyvoice_vllm` → `-cosyvoice` family only
- `lux` → `-lux` family only
- `longcat` → `-longcat` family only (when implemented)

### 21.8 Unified Request Model

All providers accept the same core request shape.

Core fields: `provider_id`, `text`, `voice` (type + voice_id/sample_id/blend/voice_description), `lang_code`, `speech_rate`, `voice_context`, `cache_key`, `synthesis_seed`.

Optional controls by backend:
- Chatterbox-family: `cfg_weight`, `exaggeration`, `cb_temperature`, `cb_top_p`, `cb_repetition_penalty`
- F5-TTS: `cfg_strength`, `nfe_step`, `cross_fade_duration`, `sway_sampling_coef`
- CosyVoice: `cosy_instruct`
- LuxTTS: `lux_num_steps`, `lux_t_shift`, `lux_return_smooth`
- Qwen/instruction: `voice_instruct`
- LongCat: `longcat_steps`, `longcat_cfg_strength`, `longcat_guidance`

`speech_rate` range: `[0.5, 2.0]`. Client must clamp before sending.

`voice.type` values: `"base"`, `"reference"`, `"blend"`, `"description"`.

#### v2 Batch request model

```json
POST /api/v1/synthesize/v2/batch
{
  "segments": [
    {
      "segment_id": "seg_0",
      "prime_from_segment": "",
      "provider_id": "...",
      "text": "...",
      "voice": {...},
      "voice_context": "Narrator/Male",
      "cache_key": "<client-computed>",
      ... synthesis controls ...
    },
    {
      "segment_id": "seg_1",
      "prime_from_segment": "seg_0",
      ...
    }
  ]
}
```

`prime_from_segment` is the `segment_id` of the prior same-speaker segment. The server uses it to locate that segment's server-side cache key and pass it as `continue_from_cache_key` to the backend for T3 tail-token continuation.

### 21.8a Chatterbox-Family Large-Text Handling

`chatterbox`, `chatterbox_full`, and `chatterbox_multilingual` handle large text transparently on the server. Oversized requests are split server-side at sentence boundaries, fall back to clause boundaries when needed, synthesized piece-by-piece, and concatenated before OGG encoding. The client still receives one OGG result for one request.

Server-side chunk parameters: `RRV_CB_CHUNK_TARGET_CHARS=380`, `RRV_CB_CHUNK_HARD_CHARS=480`.

**Token continuation across internal chunks:** `_prior_speech_tokens[_voice_key]` is updated after every chunk and used as `cond_prompt_speech_tokens` for the next chunk. This gives internal multi-chunk requests natural prosodic flow across sentence boundaries.

This is separate from client-requested v2 batch splitting. The internal large-text splitter exists so long narration can succeed even when the client sends one large request. The client-requested batch path exists for cache-preserving and continuity-aware decisions such as player-name replacement.

### 21.9 Server Cache Key and Identity

See Section 10.3 for the full cache key field list.

**Cache key schema version:** `"L2V1"` — all cache entries from prior schema versions are incompatible.

OGG files are stored under per-provider subdirectories: `{cache_dir}/{provider_id}/{key}.ogg`. Clearing a single provider's cache: `rm -rf data/cache/{provider_id}/` or via `DELETE /api/v1/cache/{provider_id}`.

### 21.10 Cache File Integrity

**On startup:**
1. Delete any `.ogg.tmp` files left by interrupted writes.
2. Remove manifest rows whose OGG file no longer exists (will regenerate on next request).

**On store:** write to `{key}.ogg.tmp` → atomic rename to `{key}.ogg` → insert/replace manifest row.

### 21.10b LRU Eviction (Server)

Server cache evicts least-recently-accessed OGG files when total size exceeds `RRV_CACHE_MAX_MB` (default 2048 MB). `last_accessed` is server-generated only; no client-supplied timestamps. SQLite manifest tracks `last_accessed`, `size_bytes`, `duration_sec`, `created` per entry.

### 21.10c Tail-Token Sidecars and T3 Continuation

Chatterbox-family backends maintain a `.tokens.pt` sidecar alongside cached OGG files. These are conceptually separate from the conditioning cache and must not be conflated with it.

**What is stored:** last `RRV_CB_PRIOR_TOKEN_LEN` (default 75) speech tokens from the final chunk, plus `voice_key` and `voice_context` guards to prevent cross-voice/cross-slot contamination. Written atomically via `.pt.tmp` → rename.

**Sidecar write:** written for **all** requests after synthesis completes (no longer restricted to single-chunk requests). The last chunk's tail tokens are the correct priming state for the next chained segment regardless of how many internal chunks the request had.

**In-memory prior tokens:** `_prior_speech_tokens[_voice_key]` is updated after every chunk (including internal multi-chunk). Persists across requests for as long as the worker process is alive.

**`continue_from_cache_key` resolution:** when explicit continuation is requested, the backend first checks in-memory; if not present (worker restarted, different request ordered), loads from disk sidecar. Voice key must match — mismatched voice key causes the sidecar to be ignored.

**Automatic short-segment continuation:** `use_prior` fires automatically (without explicit `continue_from_cache_key`) for segments with `word_count <= RRV_CB_PRIOR_TOKEN_WORDS` (default 3) when `voice_context` is set. This prevents single-word dialog from sounding isolated. This behavior is unchanged and intentional.

**Explicit continuation race fix (v26):** the v2 batch route now awaits `prior_job._synthesis_done` before dispatching any chained segment (`prime_from_segment` set). This ensures the prior segment's sidecar is on disk before the chained segment tries to load it. Cache-hit segments bypass the wait (sidecar already exists from original synthesis).

### 21.11 Synthesis Stampede Prevention

Per-key `asyncio.Lock` in `AudioCache.key_lock(cache_key)`. The second client waiting on the same key finds a cache hit when the lock is released.

### 21.12 Fallback Behavior (Client Side)

1. Client checks local L1 cache. Hit → play immediately.
2. L1 miss → submit to server (v2 async).
3. Server hit → server returns cached OGG from L2. Client stores in L1, applies DSP, plays.
4. Server miss → server synthesizes. Client awaits SSE completion event, then fetches OGG.
5. Server timeout or unavailable → client falls back to local `KokoroTtsProvider`.

### 21.13 API Endpoints

#### Core

```
GET  /api/v1/health
GET  /api/v1/capabilities
GET  /api/v1/providers
GET  /api/v1/providers/{provider_id}
GET  /api/v1/providers/{provider_id}/voices
GET  /api/v1/providers/{provider_id}/samples
POST /api/v1/synthesize                          — v1 synchronous (legacy)
```

#### Async Synthesis (v2)

```
POST /api/v1/synthesize/v2                       — submit single job → {progress_key, cache_key, cached, input_chars, input_words}
POST /api/v1/synthesize/v2/batch                 — submit multi-segment batch → {batch_id, segments[]}
GET  /api/v1/synthesize/v2/{key}/progress        — SSE: preprocessing → generating (chunk/total) → complete/error
GET  /api/v1/synthesize/v2/{key}/result          — fetch OGG bytes (202 if still pending)
GET  /api/v1/synthesize/v2/batch/{id}/progress   — SSE batch aggregate: {completed, failed, total, status}
```

#### Community Sync — NPC Overrides

```
GET  /api/v1/npc-overrides                       — all records (open)
GET  /api/v1/npc-overrides/since?t={unix_ts}     — delta poll (open; t=0 returns all)
POST /api/v1/npc-overrides                       — contribute (contribute key)
POST /api/v1/npc-overrides/batch                 — batch contribute (contribute key, max 100)
PUT  /api/v1/npc-overrides/{npc_id}             — admin confirm / edit (admin key)
```

#### Community Sync — NPC People Catalog

```
GET  /api/v1/npc-people                          — all records (open)
GET  /api/v1/npc-people/since?t={unix_ts}        — delta poll (open)
POST /api/v1/npc-people/batch                    — upsert batch (admin key, max 100)
```

The NPC People catalog provides `(npc_id, base_name, sex, race_id, creature_type_id)` records that the client uses to drive `NpcPeopleCatalog` slot resolution.

#### Community Sync — Provider Slot Profiles

```
GET  /api/v1/provider-slot-profiles              — all records (open)
GET  /api/v1/provider-slot-profiles/since?t={unix_ts} — delta poll (open)
POST /api/v1/provider-slot-profiles/batch        — upsert batch (admin key)
```

#### Community Sync — Seed Defaults

```
GET  /api/v1/defaults/{type}    — pull seed data (open). type: voice-profiles | pronunciation | text-shaping | npc-overrides
PUT  /api/v1/defaults/{type}    — push seed data (admin key)
```

#### Authentication

| Key | Env var | Scope |
|---|---|---|
| API key | `RRV_API_KEY` | All synthesis endpoints. Empty = open. |
| Contribute key | `RRV_CONTRIBUTE_KEY` | POST npc-overrides. Empty = open. |
| Admin key | `RRV_ADMIN_KEY` | PUT npc-overrides, PUT defaults, POST npc-people/batch, POST provider-slot-profiles/batch. Empty = open. |

All endpoints accept `Authorization: Bearer <key>`.

`RRV_TRUSTED_PROXY_IPS` (default `127.0.0.1`) activates `ProxyHeadersMiddleware` for real client IP logging behind Caddy.

### 21.14 Synthesis Response Metrics

#### v1 Response Headers

| Header | HIT | MISS | Description |
|---|---|---|---|
| `X-Cache` | `HIT` | `MISS` | Cache state |
| `X-Cache-Key` | ✓ | ✓ | 32-char truncated SHA-256 cache key |
| `X-Input-Chars` | ✓ | ✓ | Character count of normalized text |
| `X-Input-Words` | ✓ | ✓ | Word count of normalized text |
| `X-Synth-Time` | — | ✓ | Wall-clock synthesis duration (seconds) |
| `X-Duration` | — | ✓ | Audio duration (seconds) |
| `X-Realtime-Factor` | — | ✓ | `duration / synth_time` |

#### v2 Submit Response Body

```json
{
  "progress_key": "...",
  "cache_key": "...",
  "cached": false,
  "input_chars": 183,
  "input_words": 34
}
```

#### v2 SSE Complete Event

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

#### Truncation Detection Heuristic

Expected narration duration ≈ `(X-Input-Words / 130) * 60` seconds at ~130 WPM. If `X-Duration` is materially shorter, the model likely truncated.

### 21.15 Provider Benchmark Harness (Phase 6)

Standalone Python script `rrv_benchmark.py`. Fixed embedded corpus covering raw length stability, repeated structure, ordered sequences, punctuation sensitivity, paragraph boundary behavior. Always takes `--server` and `--provider` as explicit CLI parameters.

Output artifacts: `audio/*.ogg`, `benchmark_results.json`, `benchmark_results.csv`, `benchmark_summary.md`, `benchmark_llm_packet.md`.

Metrics captured: `cache_hit`, `cache_key`, `input_chars`, `input_words`, `synth_time`, `duration_sec`, `realtime_factor`.

```bash
python rrv_benchmark.py --server http://127.0.0.1:8000 --provider chatterbox_full --sample-id M_Narrator
```

---

## 22. Client — HTTP Provider Configuration

| Setting | Description |
|---|---|
| Server URL | Base URL, e.g. `https://rrv.example.com` |
| Remote API Key | Sent as `Authorization: Bearer <key>`. Leave blank if server auth is disabled. |
| Contribute Key | Bearer token for `POST /api/v1/npc-overrides`. |
| Admin Key | Bearer token for `PUT` admin endpoints. |
| Contribute NPC voice assignments automatically | When checked, saves contribute each local NPC override to the server silently in the background. |
| First load complete | When unchecked, pulls all four default data types from server on next startup. |

`RemoteTtsClient` uses `SocketsHttpHandler` with:
- `PooledConnectionLifetime = 90s`
- `PooledConnectionIdleTimeout = 60s`
- `ConnectTimeout = 10s`
- `HttpClient.Timeout = 300s`

A global `TaskScheduler.UnobservedTaskException` handler suppresses pool-internal background exceptions.

---

## 23. Licensing / Packaging Notes

RuneReaderVoice desktop client code is licensed under **GPL v3**. Source headers use the SPDX identifier `GPL-3.0-only`. The server uses `GPL-3.0-or-later`.

---

## 24. Final Design Summary

RuneReaderVoice v26 reflects the current client/server baseline after:

- **v26:** Chatterbox-family batch prosodic continuity fully corrected (sidecar race fix + multi-chunk sidecar write guard removed); LuxTTS defaults updated; `VoiceSlot` migrated to string-keyed record struct; `NpcPeopleCatalog` and `CatalogId` used for NPC slot resolution; `UseNpcIdAsSeed` per-NPC flag; `torch sdp_kernel` FutureWarning suppressed in Chatterbox worker.
- **v25:** player-name replacement as normal cache-preserving path; v2 batch explicit continuity chains; Chatterbox-family transparent large-text handling; batch join tail trim.
- **v23–24:** CosyVoice3, LuxTTS, Chatterbox Multilingual, Qwen3-TTS, LongCat backends; ResourceManager; Whisper subprocess ASR; benchmark harness.

The server backend set now covers eleven provider IDs across three worker venvs (chatterbox handles three backends; cosyvoice and cosyvoice-vllm have their own; qwen handles three). All backends run as isolated worker subprocesses communicating over Unix domain sockets. The `ResourceManager` coordinates GPU contention across all workers and ASR, evicting idle workers (biggest VRAM first) before loading new ones.

The Chatterbox T3 token continuation system provides prosodic coherence across both internal sentence splits (within one request) and explicit client-requested batch chains (across separate requests linked by `prime_from_segment`). Sidecars are now always written — the prior `total == 1` restriction is removed. Chained segments await their predecessor's `_synthesis_done` event so sidecars are guaranteed present at load time.

**TODO: Default/Recommended Voice Mappings per Provider.** Once the voice library is assembled, `GetPreferredSampleStem` / `GetDefaultSampleProfile` mappings will be built for each provider. Recommended presets in `SpeakerPresetCatalog` will be updated with per-provider defaults and DSP chain settings.

**TODO: CosyVoice chunking limits.** The 380/480 char limits are conservative placeholders. Run the benchmark against `cosyvoice` with progressively longer text and characterize the actual truncation boundary before loosening.
