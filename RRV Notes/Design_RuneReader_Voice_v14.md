# RuneReader — Quest Text-to-Speech (TTS)

*Feature Design Document | Roadmap Item*

Status: Phases 1–4 Complete, Phase 5 In Progress | Target: Retail WoW

Last Updated: March 2026 | Version 14

---

## What's New in v14

- **Unified SQLite back-end (`runereader-voice.db`):** all four persistent stores — NPC race overrides, pronunciation rules, text swap rules, and the audio cache manifest — have been consolidated into a single SQLite database managed by `RvrDb`. The legacy `npc-overrides.db`, `pronunciation-rules.json`, `text-swap-rules.json`, and `cache_manifest.json` files are no longer used. Migration runs automatically on first launch and deletes the legacy files.

- **DB-backed audio cache manifest:** `TtsAudioCache` no longer maintains an in-memory `Dictionary<string, CacheEntry>` backed by a JSON file. Manifest reads and writes go directly to the `AudioCacheManifest` table via `RvrDb`. LRU eviction queries the table by `LastAccessedUtcTicks`. Measured result: ~5× faster cache lookup on warm cache due to indexed SQLite reads replacing full JSON deserialize + dictionary scan.

- **Instance-based rule stores:** `PronunciationRuleStore` and `TextSwapRuleStore` are now instance classes injected via `AppServices` rather than static classes operating on JSON files. Both expose async DB-backed CRUD (`UpsertRuleAsync`, `DeleteRuleAsync`, `LoadUserRulesAsync`, `GetAllEntriesAsync`).

- **`NpcRaceOverrideDb` unified:** the standalone `NpcRaceOverrideDb` is now a thin wrapper over `RvrDb` exposing a domain-model API (`NpcRaceOverride`, `NpcOverrideSource`). The DB row uses `int` primary keys throughout.

- **Two-column tab layout:** Settings, Advanced, Pronunciation, and Text Shaping tabs restructured into two-column `Grid` layouts. Pronunciation tab now mirrors Text Shaping: the right column has a live rule list (select-to-edit), Delete Rule button, and all action buttons moved under the list.

- **`RaceAccentMapping.ResolveAccentGroup` added:** public static method returning `AccentGroup?` for a race byte. Used by `NpcRaceOverrideDb.ToModel` to derive display labels without string conversion.

- **Import / Export on all rule tabs:** Pronunciation rules, Text Swap rules, and Voice Profiles all support JSON export (save file picker) and import (merge into DB). The old "Open Rules File" buttons have been removed.

- **`TtsAudioCache` constructor updated:** now accepts `RvrDb` as second argument. `_manifest` dict and `_manifestLock` removed. `Dispose()` is a no-op — DB lifetime is owned by `RvrDb`, disposed via `AppServices`.

- **Phase 5 server design revised:** server is a shared L2 render cache for up to ~5 LAN clients. Client remains authoritative over all settings and voice choices. Server is a "render-on-demand" backend. Voice matching (reference-based synthesis) is provider-specific and capability-flagged. DSP processing stays client-side. See Section 20.

---

## 1. Overview

RuneReader Quest TTS adds spoken voice narration to World of Warcraft quest dialog by creating a data pipeline between a WoW addon (RuneReaderVoice) and the RuneReader desktop application. The addon encodes quest text as QR barcodes displayed on-screen; RuneReader captures, decodes, and passes the text to a TTS engine which synthesizes and plays audio in real time.

Blizzard's in-game TTS is limited to legacy Windows XP/Vista era voices. This feature bypasses that limitation by routing voice generation through the host OS or a configurable AI voice engine, enabling natural-sounding narration for all quest dialog, NPC greetings, and readable in-game books.

---

## 2. Goals

- Narrate quest dialog (greeting, detail, progress, completion) using natural-sounding voices.
- Narrate readable in-game books and item text.
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
- Encodes each chunk as a Base64 ASCII QR payload with a 26-character structured header (protocol v05).
- Quest title and objective text are prefixed with ASCII SOH (`\x01`) sentinel characters by `core.lua` before concatenation; `SplitSegments` detects these and emits them as narrator segments.
- Pre-encodes all QR matrices at dialog-open time (not in the render loop).
- Cycles chunks in a timed display loop; RuneReader reads each one at ~5ms latency.
- Window close detected via `HookScript(OnHide)` on GossipFrame, QuestFrame, and ItemTextFrame.

### 4.2 RuneReader Voice Desktop Application (C# / Avalonia) — Standalone

- Continuously captures screen frames and scans for QR barcodes.
- Detects a TTS payload by inspecting the decoded header magic bytes.
- Discards packets with FLAG_PREVIEW set (settings panel live preview).
- Assembles multi-chunk payloads per segment; routes assembled text and speaker metadata to the active `ITtsProvider`.
- Providers synthesize to in-memory `PcmAudio`. The cache transcodes to OGG and decodes back to `PcmAudio` for playback. No file paths pass between the provider and the player.
- Plays `PcmAudio` via `WasapiStreamAudioPlayer` (Windows/NAudio) or `GstAudioPlayer` (Linux). ESC hotkey aborts playback if audio is playing; passes through to game if idle.
- NPC Race Override system resolves per-NPC accent before voice slot selection.
- All persistent state (NPC overrides, pronunciation rules, text swap rules, cache manifest) stored in a single SQLite database `runereader-voice.db`.

### 4.3 TTS Provider (Pluggable)

The TTS backend is abstracted behind `ITtsProvider`. All providers synthesize to `PcmAudio`. No provider creates temporary files. The cache layer owns all on-disk writes.

### 4.4 TTS HTTP Server (Phase 5 — Separate Project)

An optional standalone server the desktop client can call instead of synthesizing locally. Supports multiple simultaneous LAN clients and higher-quality or GPU-accelerated synthesis. Shared L2 render cache across all clients. v1 backend is Kokoro-82M ONNX. XTTS v2, F5-TTS, and other GPU-backed models are Phase 5b+. See Section 20.

---

## 5. Data Pipeline

### 5.1 Pipeline Overview

| Stage | Description |
|---|---|
| RvBarcodeMonitor | Scans screen frames, decodes QR codes, fires `OnPacketDecoded` per valid RV packet. |
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

Splits full segment text into pronounceable phrases before synthesis:

- Sentence endings (`. ! ? ...`): punctuation stays with the left chunk.
- Clause breaks (`, ; :`): punctuation moves to the start of the right chunk — preserves prosody at the split point.
- **MinFragmentWords = 3**: short trailing fragments are merged forward.
- Abbreviation protection: Mr. Mrs. Dr. St. etc. do not trigger sentence splits.
- Decimal protection: 1.5, 3.14 do not trigger splits.

### 7.2 SynthesizePhraseStreamAsync

Returns: `IAsyncEnumerable<(PcmAudio audio, int phraseIndex, int phraseCount)>`

KokoroTtsProvider implementation:

- Splits text via TextSplitter into N phrases.
- Enqueues all phrase ONNX inference jobs immediately (parallel encoding begins for all phrases at once).
- Uses `Channel<(int index, PcmAudio audio)>` to collect completions as they arrive (any order).
- Out-of-order arrivals buffered in a pre-allocated array keyed by index; yielded to caller in strict phrase order.

> *NOTE: ORT_PARALLEL vs ORT_SEQUENTIAL has not been benchmarked for Kokoro 82M. ORT_SEQUENTIAL may be faster for small models due to lower threading overhead.*

### 7.3 PlaybackCoordinator Integration

- On cache miss: calls `SynthesizePhraseStreamAsync`, stores each `PcmAudio` phrase to cache (OGG encode in `StoreAsync`) as it arrives, feeds `PcmAudio` immediately to `PlaylistPlayAsync`.
- On cache hit: `TryGetDecodedAsync` returns `PcmAudio` decoded from OGG. Fed directly to `PlaylistPlayAsync`.
- Phrase 0 plays while phrase 1 is still encoding.

> *NOTE: KNOWN ISSUE: If phrase 0 is very short and phrase 1 is not ready before phrase 0 ends, `PlaylistPlayAsync` may resolve early and drop later phrases. Fix belongs in the player, not the splitter.*

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

Uses NAudio `WasapiOut` with a `BufferedWaveProvider`. Replaces `WinRtAudioPlayer` / `MediaPlaybackList`.

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

The RV protocol provides a RACE byte per NPC, but it reflects the NPC's base race and may not accurately capture the intended accent (e.g. a custom creature, a cross-faction NPC, or an NPC whose voice clearly doesn't match their race). The NPC Race Override system allows the user to explicitly assign an accent group to any NPC by NPC ID.

### 9.2 Source Hierarchy

Three tiers, highest priority wins:

- **Local:** user-entered on this machine. Full CRUD. Stored in `runereader-voice.db`.
- **CrowdSourced:** received from server aggregation (Phase 5+). Read-only on the client; shadowed by a local entry for the same NPC ID.
- **Confirmed:** hand-verified by server admin. Read-only on the client; shadowed by a local entry.

### 9.3 Data Model — NpcRaceOverride

| Field | Type | Description |
|---|---|---|
| NpcId | int | NPC ID from the RV packet NPC field (unit GUID segment 6). |
| RaceId | int | Race ID that maps into RaceAccentMapping (player races 1–37, creature types 0x50–0x58). |
| AccentGroup | AccentGroup | Derived from RaceId via `RaceAccentMapping.ResolveAccentGroup`. Not persisted separately. |
| Notes | string? | Optional user label (e.g. "Thrall", "Rexxar"). |
| SampleId | string? | Optional reference audio sample ID for voice-matching providers (XTTS v2, F5-TTS). When set and the active provider supports voice matching, this sample is included in the synthesis request alongside the slot's normal voice settings. The slot voice profile (voice ID, speech rate, DSP) remains authoritative — the sample adds tonal flavor only. Null when not set; ignored by non-voice-matching providers. |
| Source | NpcOverrideSource | Local / CrowdSourced / Confirmed. |

### 9.4 Storage — NpcRaceOverrideDb

- Thin wrapper over `RvrDb` exposing domain-model API.
- Table: `NpcRaceOverrides` with `int` PK on `NpcId`.
- Columns: `NpcId` (int PK), `RaceId` (int), `Notes` (text nullable), `SampleId` (text nullable).
- `UpsertAsync(int npcId, int raceId, string? notes, string? sampleId)`: inserts or replaces row.
- `DeleteAsync(int npcId)`: deletes by primary key.
- `GetAllAsync(CancellationToken)`: returns `IReadOnlyList<NpcRaceOverride>`.
- `GetOverrideAsync(int npcId)`: returns single entry or null.
- `InitializeAsync()`: no-op — table created by `RvrDb.InitializeAsync()`.

### 9.5 TtsSessionAssembler Integration

- `LoadOverridesAsync()` called at startup: pre-loads all DB entries into the in-memory `_npcRaceStore` dict.
- On segment complete: if NpcId != 0, checks `_npcRaceStore` for an override before calling `RaceAccentMapping.Resolve()`.
- `ApplyRaceOverride(npcId, raceId)` / `RemoveRaceOverride(npcId)`: runtime update from UI, updates both DB and in-memory store.
- `AppServices.LastSegment` is set after each completed segment, enabling the Last NPC panel to pre-fill the current NPC.

### 9.6 UI — Last NPC Panel

Appears below the status panel after any NPC segment completes (NpcId != 0). Hidden for narrator/book segments and on session reset.

- Shows NPC ID. Pre-fills any existing local override into the notes box and race dropdown.
- Race dropdown: full list of WoW race IDs with friendly labels.
- **Save**: calls `UpsertAsync` and `ApplyRaceOverride`. **Clear**: calls `DeleteAsync` and `RemoveRaceOverride`. Clear button disabled when no local override exists.

### 9.7 UI — NPC Voices Tab

Full CRUD grid for all stored overrides (local + server entries). Columns: NPC ID, Notes, Race/Accent, Source, Edit, Delete.

- Local rows: Edit and Delete buttons active.
- Server rows (CrowdSourced / Confirmed): read-only. "Override locally" button creates a local shadow entry.
- Export JSON / Import JSON: stubbed, deferred.
- Refresh button reloads grid from DB.

**Sample picker (deferred — added with HttpTtsProvider client implementation):** When the active provider is `HttpTtsProvider` and the server reports `supports_voice_matching: true` for the active backend, an additional sample picker dropdown appears in the Last NPC panel and NPC Voices grid. The user selects a reference clip from the server's sample list; the selection is stored as `SampleId` against the NPC ID. On synthesis the client includes the `sample_id` in the request alongside the slot's normal voice settings. The slot voice profile remains authoritative — the sample colors the output toward that NPC's character without replacing the base voice configuration.

---

## 10. Audio Cache (Desktop Client)

### 10.1 OGG-Only Strategy

`StoreAsync` receives `PcmAudio`, applies optional silence trimming (pass-through stub), transcodes to OGG synchronously via `Task.Run`, and writes OGG as the sole manifest entry. There is no intermediate WAV and no background compression task.

`TryGetDecodedAsync` checks the DB manifest, validates the file exists and is `.ogg`, decodes via NVorbis, and returns `PcmAudio`. If the DB row exists but the file is missing, the row is deleted and null is returned — the caller re-synthesizes and re-stores. Non-OGG entries are invalidated and deleted.

### 10.2 Cache Key

SHA-256 hash of fields joined by null bytes (`\x00`): `normalized_text + "\x00" + voiceId + "\x00" + providerId + "\x00" + dspKey`. Truncated to 16 hex characters. `voiceId` is the resolved voice identity string (e.g. `"af_sarah"` or a `mix:` spec), not the slot name. `dspKey` is produced by `DspProfile.BuildCacheKey()` — a compact deterministic string of all non-neutral DSP field values; empty string when DSP is neutral or disabled. Per-phrase cache keys use reconstructed phrase text (`GetPhraseText`), not the full segment text. The null-byte separator prevents collisions from adjacent field concatenation and must be replicated exactly by the server's cache key algorithm.

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
| `<ConfigDir>/settings.json` | User settings |
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

`TextSwapRuleRowExtensions` provides `ToRow()` / `ToEntry()` mapping.

### 11.3 Import / Export

Both rule stores and voice profiles support JSON import/export via Avalonia `StorageProvider` file pickers. Import is additive (upsert per entry — does not clear existing rules). Export serializes to the same JSON format used by the legacy file-based stores for compatibility.

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
| NPC Voices | Last NPC panel, NPC override grid | — |
| Advanced | Capture, Playback, Cache | Audio, Piper, Diagnostics, Hotkey |
| Pronunciation | Workbench, Sound Picker, Preview | Saved rules list, rule authoring controls |
| Text Shaping | Workbench + preview | Saved rules list + rule authoring controls |

### 12.3 Voice Assignment Grid (Voices Tab)

63 voice slots total: 1 Narrator + 31 accent groups × Male + Female. Every playable race has its own dedicated slot pair — accent groups are not shared between races in the catalog. Creature-type slots (Dragonkin, Elemental, Giant, Mechanical) each have Male/Female pairs. Sort order groups: 0=Narrator, 10s=Alliance, 100s=Horde, 200s=Neutral/Cross-faction, 300s+=Creature types. The grid is populated entirely in code-behind by `PopulateVoiceGrid()` iterating `NpcVoiceSlotCatalog.All`.

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

Export serializes the active provider's `Dictionary<string, VoiceProfile>` with the provider ID as an envelope field. Import always targets the currently active provider regardless of the provider ID in the file, allowing profile transfer between machines. `ApplyVoiceProfile` is a shared helper used by both the Edit dialog and the import path.

---

## 13. Addon File Structure

RuneReaderVoice is a standalone WoW addon (separate from RuneReaderRecast).

| File | Purpose |
|---|---|
| RuneReaderVoice.toc | Addon manifest; Interface 120001 (Retail) |
| core.lua | Event registration, dialog dispatch, window close hooks |
| payload.lua | Text chunking, padding, Base64 encoding, QR payload building |
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
| QUEST_FINISHED | — | Quest accept / decline / auto-accept | Fires on accept OR decline; may fire twice. 3-second timer before stopping; cancelled if dialog ID changed. Marks `_questDetailOpen = false` immediately. |
| ITEM_TEXT_BEGIN | — | Book / readable item opened | Sets `_bookActive = true`. |
| ITEM_TEXT_READY | ItemTextGetText() + ItemTextGetItem() | In-game books / readable items | EXPERIMENTAL. Multi-page books require player to click through pages. Source prepended to text on page 1 only. |
| ITEM_TEXT_CLOSED | — | Book / readable item closed | Clears `_bookActive`, calls `StopDisplay`. |
| GOSSIP_CLOSED | — | Gossip frame closed | No-op handler; cleanup via `GossipFrame:OnHide` hook. |

### 14.2 Stop-on-Close Events

The following events call `StopDisplay` immediately when fired:

| Event | Trigger |
|---|---|
| TAXIMAP_CLOSED | Flight master map closed |
| ADVENTURE_MAP_CLOSE | Adventure / Chromie Time map closed |
| MERCHANT_CLOSED | Merchant window closed |
| TRAINER_CLOSED | Trainer window closed |

### 14.3 Window Close Detection

Close detection uses `HookScript("OnHide")` on Blizzard dialog frames rather than events. Events do not fire on all close paths (Escape key, clicking away, programmatic close). Frame hooks fire unconditionally.

- `GossipFrame:OnHide` → `StopDisplay` immediately (skipped during preview).
- `QuestFrame:OnHide` → `StopDisplay`, unless `QUEST_FINISHED` timer is already pending for the current dialog.
- `ItemTextFrame:OnHide` → `StopDisplay`, clears `_bookActive`.

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
| 0x54 | Demon |
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
| Phase 5 | IN PROGRESS | TTS HTTP server: Kokoro-82M ONNX v1 backend, shared L2 render cache, provider capability discovery, reference sample API. XTTS v2 and F5-TTS in Phase 5b (GPU-gated). |
| Phase 6 | PLANNED | Platform polish: Windows Piper (pending libpiper C API), NPC override crowd-source sync, full GstAudioPlayer EOS, GstAudioPlayer PcmAudio interface update, silence trimming implementation. |

---

## 19. Known Limitations / TODO

- **Windows playback race (phrase streaming):** if phrase 0 ends before phrase 1 is ready, `PlaylistPlayAsync` may resolve early and drop later phrases. Fix in player.
- **GstAudioPlayer:** EOS detection is a stub. True gapless playback not implemented. PcmAudio interface not yet updated.
- **HttpTtsProvider:** still a stub in the local codebase. Full implementation deferred to the Phase 5 client integration pass — will cover L1/L2 cache flow, server timeout/fallback, capability response caching, and the sample picker UI wired to `NpcRaceOverride.SampleId`.
- **NPC voice-matching sample picker:** `NpcRaceOverrideRow.SampleId` column and domain model field are designed but not yet added to the DB schema or UI. Added with the HttpTtsProvider client implementation.
- **NPC override Export/Import JSON:** buttons exist in the UI but are stubbed.
- **Silence trimming:** `TrimSilence` is a pass-through stub.
- **Windows Piper:** deferred until libpiper C API ships.
- **Speed control:** WasapiStreamAudioPlayer pitch-corrected tempo not yet verified end-to-end.
- **Kokoro cache identity normalization:** logically identical mixes with different ordering/weight formatting still hash differently.

---

## 20. Security / Privacy

- No network transmission in Phases 1–4b unless the user explicitly selects a remote provider.
- Local providers keep text on the user's machine.
- HTTP/cloud providers are opt-in. The UI makes it clear when text leaves the machine.
- Phase 5 server is LAN-only, no auth enforced by default. An optional API key stub is defined in the server API (see Section 21.10) so auth can be activated without a protocol change.

---

## 21. TTS HTTP Server (Phase 5)

### 21.1 Summary

The TTS HTTP server is a separate Python/FastAPI project. It is a shared **L2 render cache** for up to approximately 5 LAN clients. The client remains fully authoritative over all settings and voice choices. The server renders exactly what the client requests, caches the result, and serves it to subsequent clients with the same request. The client falls back to local rendering transparently if the server is unavailable or times out.

**L1 cache:** local client `TtsAudioCache` (SQLite manifest + OGG files on the client machine).
**L2 cache:** server SQLite manifest + OGG files on the server machine, shared across all clients.

DSP processing stays entirely client-side. The server returns raw synthesized OGG. The client applies DSP after retrieval, exactly as it does for local cache hits.

### 21.2 Supported Backends

| Backend | Phase | GPU Support | Notes |
|---|---|---|---|
| Kokoro-82M ONNX (`kokoro-onnx`) | v1 | CPU / ORT-CUDA / ORT-ROCm | Python 3.10–3.13 compatible. Shares model files with local client. Fast on CPU — no GPU required. |
| XTTS v2 (Coqui TTS) | 5b | CUDA / ROCm / CPU (slow) | 17-language support. Voice cloning from reference clips. ~4–5 GB VRAM. |
| F5-TTS | 5b | CUDA / ROCm / CPU (slow) | Zero-shot voice cloning. Strong English quality. Faster than XTTS v2. ~3–4 GB VRAM. |
| StyleTTS2 | future | CUDA | Style-reference synthesis. Different cloning workflow — deferred pending evaluation. |

XTTS v2 supports 17 languages: English (`en`), Spanish (`es`), French (`fr`), German (`de`), Italian (`it`), Portuguese (`pt`), Polish (`pl`), Turkish (`tr`), Russian (`ru`), Dutch (`nl`), Czech (`cs`), Arabic (`ar`), Chinese (`zh-cn`), Japanese (`ja`), Hungarian (`hu`), Korean (`ko`), Hindi (`hi`).

F5-TTS supports English primarily; multilingual support is expanding in active development.

**GPU auto-detection** runs once at startup and selects the best available execution provider in priority order: CUDA → ROCm → CPU. The selected provider is logged clearly at startup. Intel Arc on Linux uses CPU for PyTorch-based models (XTTS v2, F5-TTS) — IPEX support is experimental and not relied upon. Kokoro via ONNX Runtime uses ORT-CPU on Arc, which is fast enough given Kokoro's model size. No GPU resource monitoring or backoff — video encode engines (NVENC, VCE, Quick Sync) run on dedicated silicon and do not contend with compute workloads.

### 21.3 Stack and Project Structure

- **Language / framework:** Python 3.11+ + FastAPI + Uvicorn
- **Inference backends:** provider-pluggable (see Section 21.2)
- **Storage:** SQLite manifest (`server-cache.db`) + filesystem OGG files
- **Transport:** HTTP/JSON control plane; binary OGG audio response body
- **Deployment:** standalone LAN service; container or bare-metal Arch Linux

Project is structured as a `pyproject.toml` package with optional dependency groups:

```
rrv-server/
  pyproject.toml          # optional groups: [kokoro], [xtts], [f5tts], [gpu-cuda], [gpu-rocm]
  Dockerfile
  docker-compose.yml
  .env.example
  server/
    main.py               # FastAPI app, startup, lifespan
    config.py             # env var + CLI arg resolution
    gpu_detect.py         # hardware probe, execution provider selection
    cache.py              # SQLite manifest, OGG store/retrieve, LRU eviction
    backends/
      __init__.py         # BackendRegistry, load_backends()
      base.py             # AbstractTtsBackend protocol
      kokoro_backend.py
      xtts_backend.py
      f5tts_backend.py
    routes/
      health.py
      capabilities.py
      providers.py
      synthesize.py
    samples.py            # sample directory scanner, sidecar loader
  models/                 # model files (gitignored, volume-mounted in container)
  samples/                # reference audio clips (gitignored, volume-mounted)
  cache/                  # generated OGG files (gitignored, volume-mounted)
```

Installing only what you need:
```bash
pip install -e ".[kokoro]"              # dev / CPU-only
pip install -e ".[kokoro,xtts,f5tts,gpu-cuda]"  # full CUDA deployment
pip install -e ".[kokoro,xtts,f5tts,gpu-rocm]"  # full ROCm deployment
```

### 21.3b Model Acquisition

Models are not bundled — download separately and place in `RRV_MODELS_DIR`.

| Backend | Source | Files | Size |
|---|---|---|---|
| Kokoro | HuggingFace `hexgrad/Kokoro-82M` or `pip install kokoro-onnx` | `kokoro-v1.0.onnx`, `voices-v1.0.bin` | ~320 MB |
| XTTS v2 | HuggingFace `coqui/XTTS-v2` or auto-download via `TTS` on first use | `model.pth`, `config.json`, `vocab.json`, `speakers_xtts.pth`, `dvae.pth`, `mel_stats.pth` | ~1.8 GB |
| F5-TTS | HuggingFace `SWivid/F5-TTS` or auto-download via `f5-tts` on first use | `F5TTS_Base_vocoder.safetensors`, `F5TTS_Base/model_1200000.safetensors` | ~1.2 GB |

Kokoro model files are shared with the C# local client — the same `models/` directory can be used for both if the server runs on the same machine as the client.

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
| `RRV_BACKENDS` / `--backends` | `kokoro` | Comma-separated list of backends to load: `kokoro`, `xtts`, `f5tts` |
| `RRV_LOG_LEVEL` / `--log-level` | `info` | Logging level: `debug`, `info`, `warning`, `error` |

### 21.5 Provider Capability Model

The server reports what each loaded backend can do. The client uses this to build its UI — it does not hardcode provider capabilities.

```json
{
  "provider_id": "xtts_v2",
  "display_name": "XTTS v2",
  "loaded": true,
  "execution_provider": "cuda",
  "supports_base_voices": true,
  "supports_voice_matching": true,
  "supports_voice_blending": false,
  "supports_inline_pronunciation": false,
  "languages": ["en", "es", "fr", "de", "it", "pt", "pl", "tr", "ru", "nl", "cs", "ar", "zh-cn", "ja", "hu", "ko", "hi"]
}
```

Capability flags:

| Flag | Meaning |
|---|---|
| `supports_base_voices` | Provider has built-in named voices (e.g. Kokoro's 54 voices, XTTS base voices) |
| `supports_voice_matching` | Provider can clone a voice from a reference audio sample |
| `supports_voice_blending` | Provider supports weighted mix of multiple voices (Kokoro only in v1) |
| `supports_inline_pronunciation` | Provider supports Kokoro/Misaki inline IPA phoneme markup |
| `execution_provider` | Resolved GPU/CPU provider in use: `cuda`, `rocm`, `cpu` |

### 21.6 Voice List

`GET /api/v1/providers/{id}/voices` returns the voices the server has **loaded and available right now** — not a theoretical catalog. For Kokoro this is the full 54-voice list from `voices-v1.0.bin`. For XTTS it is the 17 built-in base voices. This is an operational health check, not a discovery catalog.

```json
[
  {
    "voice_id": "af_sarah",
    "display_name": "Sarah",
    "language": "en-us",
    "gender": "female",
    "type": "base"
  }
]
```

### 21.7 Reference Sample List (Voice Matching)

`GET /api/v1/providers/{id}/samples` returns the reference audio clips available on the server for voice-matching-capable providers (XTTS v2, F5-TTS).

**Sample management is admin-only — filesystem only, no upload endpoint.** The admin places files directly in `RRV_SAMPLES_DIR`. The server scans on startup and on each `GET /samples` request — no restart required to pick up new files.

#### Naming Convention

- Underscores and hyphens only. No spaces. No special characters.
- Valid extensions: `.wav`, `.mp3`, `.flac`, `.ogg`
- The `sample_id` is the filename stem exactly as written: `thrall_deep.wav` → `sample_id = "thrall_deep"`
- Files that violate the naming convention are skipped with a warning logged. No silent acceptance of bad names.
- Recommended clip length: 6–15 seconds. Single speaker, minimal background noise, consistent volume.

#### Optional Sidecar

A `<stem>.txt` file alongside the audio file provides a one-line description. If absent, `description` is an empty string. If present but empty, same result.

#### Example Directory

```
samples/
  thrall.wav
  thrall.txt                     ← "Deep orc male, slow deliberate cadence, 8s"
  sylvanas_cold.wav
  sylvanas_cold.txt              ← "Sharp commanding female, clipped delivery"
  generic_orc_male.wav           ← no sidecar, description will be empty
  zandalari_female_calm.wav
  zandalari_female_calm.txt
```

#### Sample Response

```json
[
  {
    "sample_id": "thrall",
    "filename": "thrall.wav",
    "duration_seconds": 8.2,
    "description": "Deep orc male, slow deliberate cadence"
  },
  {
    "sample_id": "generic_orc_male",
    "filename": "generic_orc_male.wav",
    "duration_seconds": 6.4,
    "description": ""
  }
]
```

Duration is read from the file header at scan time. The server does not modify sample files.

#### Cache Key for Reference Synthesis

The server cache key for reference-based synthesis uses the **SHA-256 hash of the sample file contents**, not the `sample_id` name. Replacing `thrall.wav` with a better recording changes the content hash, causing a natural cache miss and fresh render. No manual cache invalidation is needed.

### 21.8 Unified Request Model

All providers accept the same request shape. The server validates that the requested `voice.type` is supported by the named provider and returns a clean error if not.

```json
{
  "provider_id": "xtts_v2",
  "text": "Stay away from Atal'zul, mon.",
  "voice": {
    "type": "base | reference | blend",
    "voice_id": "Claribel Daw",
    "sample_id": "thrall_ref",
    "blend": [
      { "voice_id": "am_adam", "weight": 0.4 },
      { "voice_id": "bm_lewis", "weight": 0.6 }
    ]
  },
  "lang_code": "en",
  "speech_rate": 1.0
}
```

Only the relevant `voice` fields are required for the chosen type. `voice_id` for `"base"`, `sample_id` for `"reference"`, `blend` array for `"blend"`.

`speech_rate` is a hint. Providers that degrade in quality at extreme rates may clamp internally. The server documents any clamping behavior in the capability response.

### 21.9 Cache Key and Identity

Server cache key: `SHA-256` of fields joined by null bytes (`\x00`):

```
normalized_text \x00 provider_id \x00 model_version \x00 resolved_voice_identity \x00 lang_code \x00 speech_rate
```

Truncated to 32 hex characters. The null-byte separator prevents collisions from adjacent field concatenation — the same algorithm the client uses for its L1 cache key, extended with `model_version`.

`model_version` is the SHA-256 of the primary model file (e.g. `kokoro-v1.0.onnx`, `model.pth`) truncated to 8 hex characters. Computed once at backend load time and cached. If the model file is replaced, the version hash changes and all cached entries for that backend naturally miss.

`resolved_voice_identity` is:
- For `"base"`: the `voice_id` string.
- For `"reference"`: SHA-256 of the sample file contents (8 hex chars). Replacing the sample file automatically invalidates its cache entries.
- For `"blend"`: canonical sorted `voice_id:weight` pairs joined by `|`, e.g. `am_adam:0.40|bm_lewis:0.60`. Weights normalized to 2 decimal places before hashing.

**Cache timestamps are server-generated only.** No client-supplied timestamps are accepted or trusted. LRU eviction uses server-clock timestamps exclusively.

### 21.10 Cache File Integrity

On server startup:

1. Scan for any `.tmp` files in `RRV_CACHE_DIR` left by a previously interrupted write. Delete them.
2. Verify that every DB manifest row has a corresponding file. Rows without files are deleted from the manifest — the next request regenerates the audio.

On `StoreAsync`:

1. Synthesize to memory.
2. Write to `<key>.ogg.tmp`.
3. Rename to `<key>.ogg` atomically.
4. Insert/update DB manifest row.

This ensures the cache directory never contains a partially-written OGG that could be served to a client.

### 21.10b Cache Manifest — Text Storage

The server cache manifest stores the synthesized text alongside each entry. This enables:

- **Organic pre-population:** as users play normally and request synthesis, the server populates its cache. Any subsequent client requesting the same text + voice combination gets a cache hit instantly — including users who join later or switch providers. No explicit pre-cache step is needed for typical usage.
- **Future re-generation:** when a new backend is added (e.g. upgrading from Kokoro to XTTS v2), the stored text can be used to pre-generate entries for the new backend against the existing corpus of known dialog text. The server can be set to work through the text backlog at low priority while idle.
- **Debugging:** the synthesized text is visible directly in the manifest rather than requiring a hash lookup.

The text stored is always the text **as received from the client** — after the client's text swap and pronunciation processing. Users with custom text rules produce different cache entries from users with default settings; this is expected and acceptable. The shared cache benefit applies fully to users running default voices, which is the common case.

The `text` column is added to the `cache_manifest` table as `TEXT NOT NULL`. The cache key already ensures uniqueness; the text column is informational and not part of any index.

### 21.11 Synthesis Stampede Prevention

When two clients request the same key simultaneously and both get a cache miss, without coordination both would synthesize, both would write, and one would overwrite the other mid-stream.

The server maintains an in-process `asyncio.Lock` per cache key. The second client waits on the lock while the first synthesizes and stores. When the lock is released, the second client finds a cache hit and returns immediately without synthesizing. Key locks are held only for the duration of synthesis + write, not for the lifetime of the request.

### 21.12 Fallback Behavior (Client Side)

When `HttpTtsProvider` is selected:

1. Client checks local L1 cache (`TryGetDecodedAsync`). Hit → play immediately.
2. L1 miss → send request to server with configured timeout (default 8 seconds).
3. Server hit → server returns cached OGG. Client stores in L1, applies DSP, plays.
4. Server miss → server synthesizes (may take several seconds for XTTS). Client waits up to timeout.
5. Server timeout or unavailable → client falls back to local `KokoroTtsProvider` silently. A status indicator in the UI shows the fallback state.
6. If XTTS or a GPU-only provider was selected and the server is unavailable, fallback is to Kokoro locally (different voice, same slot assignment). This is a known quality degradation and is documented in the UI.

### 21.13 API Endpoints

```
GET  /api/v1/health
GET  /api/v1/capabilities
GET  /api/v1/providers
GET  /api/v1/providers/{provider_id}
GET  /api/v1/providers/{provider_id}/voices
GET  /api/v1/providers/{provider_id}/samples
POST /api/v1/synthesize
```

#### Authentication Stub

All endpoints accept an optional `Authorization: Bearer <key>` header. When `RRV_API_KEY` is set (non-empty), the server validates the header and returns `401 Unauthorized` if absent or incorrect. When `RRV_API_KEY` is empty (default), authentication is bypassed entirely. This allows auth to be activated for a deployment without any protocol or client changes.

#### Diagnostics

Log fields per request: request ID, provider ID, voice identity summary, cache hit/miss, synthesis duration (if miss), output duration, OGG size bytes, client IP.

---

## 22. Client — HTTP Provider Configuration

The client Settings tab includes a "Server" expander (hidden when `HttpTtsProvider` is not selected as active provider):

| Setting | Description |
|---|---|
| Server URL | Base URL, e.g. `http://192.168.1.10:8765` |
| Request timeout | Seconds before fallback to local render. Default 8. |
| API key | Sent as `Authorization: Bearer <key>`. Leave blank if server auth is disabled. |
| Fallback provider | Which local provider to use if server is unavailable. Default: Kokoro. |

When the server URL is set and the provider is `HttpTtsProvider`, the client calls `GET /api/v1/capabilities` on startup and caches the response. The Voices tab and voice editor use the returned capability flags to show or hide voice matching options, blend controls, and the reference sample picker.

The reference sample picker is only shown when:
- Active provider is `HttpTtsProvider`, and
- The selected backend reports `supports_voice_matching: true`, and
- `GET /api/v1/providers/{id}/samples` returns at least one sample.

---

## 23. Licensing / Packaging Notes

RuneReaderVoice and related code use GPL-3.0-or-later. Source headers include the SPDX identifier (`SPDX-License-Identifier: GPL-3.0-or-later`). Distribution artifacts include the LICENSE text. User-facing metadata (addon TOC, about/license surfaces) points to the included license text.

---

## 24. Final Design Summary

RuneReaderVoice v14 builds on the PCM-first audio architecture of v13 with a unified SQLite back-end (`runereader-voice.db`) consolidating all four persistent stores — NPC overrides, pronunciation rules, text swap rules, and the audio cache manifest — into a single database managed by `RvrDb`. Cache lookup is approximately 5× faster than the v13 JSON-manifest approach. The settings UI has been restructured into two-column layouts across all tabs, and all rule tabs now support JSON import/export. The Pronunciation tab gained a live rule list with select-to-edit behavior mirroring the Text Shaping tab.

The Phase 5 HTTP server is designed as a shared L2 render cache for up to ~5 LAN clients. The client remains authoritative over all voice and settings choices. The server renders on demand, caches results in its own SQLite-backed manifest, and serves cached OGG directly on subsequent identical requests from any client. DSP processing stays client-side. A unified request model accommodates all provider types — Kokoro base voices, XTTS reference-based voice matching, and future blend-capable providers — via capability flags that drive client UI rather than hardcoded provider knowledge. Reference sample cache keys are derived from file content hashes, making cache invalidation automatic when sample files are replaced. A per-key asyncio lock prevents synthesis stampedes under concurrent identical requests. API key auth is stubbed in the protocol and activatable via environment variable without client changes.
