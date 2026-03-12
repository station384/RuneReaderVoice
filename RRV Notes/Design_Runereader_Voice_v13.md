# RuneReader — Quest Text-to-Speech (TTS)

*Feature Design Document | Roadmap Item*

Status: Phases 1–4 Complete, Phase 5 In Progress | Target: Retail WoW

Last Updated: March 2026 | Version 13

---

## What's New in v13

- **PCM-first audio architecture:** providers now synthesize directly to in-memory `PcmAudio` (interleaved float32). No temporary WAV files are created anywhere in the synthesis path. The cache layer is the first and only on-disk write.

- **OGG-only cache:** `StoreAsync` transcodes `PcmAudio` directly to OGG and writes it as the sole cached artifact. The play-first WAV strategy (WAV written immediately, OGG transcoded in background) has been removed. Cache reads decode OGG back to `PcmAudio` via NVorbis before returning to the coordinator.

- **WinRT MediaPlaybackList replaced:** `WasapiStreamAudioPlayer` (NAudio/WASAPI) is now the Windows audio backend. The player consumes decoded `PcmAudio` directly from the coordinator rather than file paths. WinRT `MediaPlaybackList` is no longer used.

- **`IAudioPlayer` interface updated:** `PlayAsync` and `PlaylistPlayAsync` now accept `PcmAudio` and `IAsyncEnumerable<PcmAudio>` respectively. File path APIs removed.

- **`ITtsProvider` interface updated:** `SynthesizePhraseStreamAsync` yields `(PcmAudio audio, int phraseIndex, int phraseCount)`. `SynthesizeToFileAsync` replaced by `SynthesizeAsync` returning `Task<PcmAudio>`. Providers must not create temporary files.

- **New `ITtsProvider` capability flag:** `SupportsInlinePronunciationHints`. Providers that cannot use Kokoro/Misaki inline markup (e.g. WinRT) receive plain text. Kokoro receives the processed text with pronunciation hints embedded.

- **NPC Race Override system:** per-NPC voice accent assignment. Users can override the race-to-accent-group mapping for any NPC ID. Overrides are stored in a local SQLite database (`sqlite-net-pcl`, no WinRT dependency). Three-tier source hierarchy: Local > CrowdSourced > Confirmed.

- **NPC Voices tab added:** CRUD grid for managing local overrides. Last NPC panel shown after each NPC segment fires, allowing the user to assign or correct the accent for the last-heard NPC.

- **UI restructure:** Volume slider and Start/Stop button promoted to always-visible top chrome. All settings tabs wrapped in a collapsing Settings expander. Expander collapsed states persisted in `VoiceUserSettings.ExpanderStates`.

- **`NpcVoiceSlotCatalog` labels updated:** shared accent group slots now list all races that share them. Example: BritishHaughty is labeled "Blood Elf / Void Elf".

- **Phase 5 server backend confirmed:** Kokoro-82M ONNX (`kokoro-onnx`) is the v1 production backend. XTTS v2 dropped as v1 candidate (Python 3.13 incompatible). XTTS v2 preserved as Phase 5b (GPU-gated in `deploy.sh`).

- **SQLite dependency change:** `Microsoft.Data.Sqlite` incompatible with the non-packaged app identity model (WinRT packaging APIs activate on `net8.0-windows10.0.x` TFM). Replaced with `sqlite-net-pcl` + `SQLitePCLRaw.bundle_green`.

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
- Splits text into ~10-word chunks and pads each chunk to a fixed byte target.
- Encodes each chunk as a Base64 ASCII QR payload with a structured header.
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

### 4.3 TTS Provider (Pluggable)

The TTS backend is abstracted behind `ITtsProvider`. All providers synthesize to `PcmAudio`. No provider creates temporary files. The cache layer owns all on-disk writes.

### 4.4 TTS HTTP Server (Phase 5 — Separate Project)

An optional standalone server the desktop client can call instead of synthesizing locally. Supports multiple simultaneous users and higher-quality or GPU-accelerated synthesis. v1 backend is Kokoro-82M ONNX. XTTS v2 and GPU-backed models are Phase 5b. See Section 20.

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

- **Local:** user-entered on this machine. Full CRUD. Stored in SQLite.
- **CrowdSourced:** received from server aggregation (Phase 5+). Read-only on the client; shadowed by a local entry for the same NPC ID.
- **Confirmed:** hand-verified by server admin. Read-only on the client; shadowed by a local entry.

### 9.3 Data Model — NpcRaceOverride

| Field | Type | Description |
|---|---|---|
| NpcId | int | NPC ID from the RV packet NPC field (unit GUID segment 6). |
| RaceId | int | Race ID that maps into RaceAccentMapping (player races 1–37, creature types 0x50–0x58). |
| AccentGroup | AccentGroup | Derived from RaceId on load. Not persisted separately. |
| Notes | string? | Optional user label (e.g. "Thrall", "Rexxar"). |
| Source | NpcOverrideSource | Local / CrowdSourced / Confirmed. |
| Confidence | int? | Server vote score. Null for local entries. |
| CreatedAt / UpdatedAt | DateTime | UTC timestamps. |

### 9.4 Storage — NpcRaceOverrideDb

- SQLite database: `config/npc-overrides.db` alongside `settings.json`.
- ORM: `sqlite-net-pcl` + `SQLitePCLRaw.bundle_green`. No WinRT dependency.
- WAL journal mode, NORMAL synchronous. Table: `npc_race_overrides` with PK on `npc_id`.
- `UpsertAsync`: inserts or updates local entries only. Non-local rows are never overwritten by the client.
- `DeleteAsync`: deletes local entries only. Non-local rows are never deleted by the client.
- `MergeServerEntriesAsync`: merges crowd-sourced / confirmed entries from server sync. Skips NPC IDs that already have a local entry.
- `InitializeAsync`: creates table and sets PRAGMA options. Must be called once after construction.

> *NOTE: `Microsoft.Data.Sqlite` was incompatible — it triggers WinRT packaging APIs (`ApplicationData.Current`) even in a non-packaged app when the TFM is `net8.0-windows10.0.x`. `sqlite-net-pcl` has no such dependency.*

### 9.5 TtsSessionAssembler Integration

- `LoadOverridesAsync()` called at startup: pre-loads all DB entries into the in-memory `_npcRaceStore` dict.
- On segment complete: if NpcId != 0, checks `_npcRaceStore` for an override before calling `RaceAccentMapping.Resolve()`.
- `ApplyRaceOverride(npcId, raceId)` / `RemoveRaceOverride(npcId)`: runtime update from UI, updates both DB and in-memory store atomically.
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
- Export JSON / Import JSON buttons: stubbed, deferred.
- Refresh button reloads grid from DB.

---

## 10. Audio Cache (Desktop Client)

### 10.1 OGG-Only Strategy

`StoreAsync` receives `PcmAudio`, applies optional silence trimming (pass-through stub), transcodes to OGG synchronously via `Task.Run`, and writes OGG as the sole manifest entry. There is no intermediate WAV and no background compression task.

`TryGetDecodedAsync` checks the manifest, validates the file is `.ogg`, decodes via NVorbis, and returns `PcmAudio`. Non-OGG entries are invalidated and deleted (migration guard for pre-v13 WAV entries).

### 10.2 Cache Key

SHA-256 hash of: normalized text + voice slot ID + TTS provider ID. Truncated to 16 hex characters. Per-phrase cache keys use reconstructed phrase text (`GetPhraseText`), not the full segment text.

### 10.3 Storage Locations

| Path | Contents |
|---|---|
| `<AppDir>/tts_cache/` | OGG audio files + manifest JSON |
| `<AppDir>/models/` | Kokoro ONNX model files (shared with Python server) |
| `<AppDir>/config/settings.json` | User settings |
| `<AppDir>/config/npc-overrides.db` | NPC race override SQLite database |

### 10.4 Eviction

- No TTL — quest dialog is static content. A cached line is valid indefinitely.
- Configurable max size (default 500 MB). LRU eviction on startup when limit exceeded.
- Manual clear button in settings.

---

## 11. Addon File Structure

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

## 12. WoW Events and Dialog Sources

All confirmed working on Retail WoW (Interface 120001).

| Event | Text API | Dialog Type | Notes |
|---|---|---|---|
| GOSSIP_SHOW | C_GossipInfo.GetText() | NPC greeting / gossip | Deferred 1 frame via C_Timer.After(0). Falls back to GetGossipText(). |
| QUEST_GREETING | GetGreetingText() | Multi-quest NPC greeting | Fires for NPCs with multiple available quests. |
| QUEST_DETAIL | GetTitleText() + GetQuestText() + GetObjectiveText() | Quest accept dialog | All three concatenated with double-space separator. |
| QUEST_PROGRESS | GetProgressText() | Quest in-progress check-in | |
| QUEST_COMPLETE | GetRewardText() | Quest completion / reward | |
| ITEM_TEXT_READY | ItemTextGetText() + ItemTextGetItem() | In-game books / readable items | EXPERIMENTAL. Multi-page books require player to click through pages. |

### 12.1 Window Close Detection

Close detection uses `HookScript("OnHide")` on Blizzard dialog frames rather than events. Events do not fire on all close paths (Escape key, clicking away, programmatic close). Frame hooks fire unconditionally.

- `GossipFrame:OnHide` → StopDisplay immediately.
- `QuestFrame:OnHide` → StopDisplay immediately, unless QUEST_FINISHED timer already scheduled.
- `ItemTextFrame:OnHide` → StopDisplay immediately.

### 12.2 NPC Gender Detection

- Primary: `UnitSex("target")` — player must target the NPC to open dialog.
- Fallback: `UnitSex("questnpc")`, then `UnitSex("npc")`.
- Returns: 1=unknown, 2=male, 3=female. Unknown maps to neutral voice.

---

## 13. QR Payload Protocol (v04)

### 13.1 Payload Structure

```
[ MAGIC(2) | VER(2) | DIALOG(4) | IDX(2) | TOTAL(2) | FLAGS(2) | RACE(2) | NPC(6) | BASE64_PAYLOAD ]
```

| Field | Chars | Format | Description |
|---|---:|---|---|
| MAGIC | 2 | "RV" | Identifies a RuneReaderVoice TTS packet. |
| VER | 2 | "04" | Protocol version. |
| DIALOG | 4 | Hex 0000–FFFF | Dialog block ID. Increments once per NPC interaction. |
| IDX | 2 | Hex 00–FF | 0-based chunk index within this segment. |
| TOTAL | 2 | Hex 01–FF | Total chunk count for this segment. |
| FLAGS | 2 | Hex bitmask | Speaker / control flags. See Section 13.2. |
| RACE | 2 | Hex 00–FF | NPC race or creature type ID. |
| NPC | 6 | Hex 000000–FFFFFF | NPC ID from unit GUID segment 6. |
| BASE64_PAYLOAD | Variable | Base64 | Base64-encoded text chunk, space-padded to fixed size. |

### 13.2 FLAGS Byte

| Bit | Mask | Name | Meaning |
|---:|---:|---|---|
| 0 | 0x01 | FLAG_NARRATOR | Narrator voice. RuneReader assigns Narrator slot regardless of gender/race. |
| 1 | 0x02 | GENDER_MALE | NPC is male. Bits 1+2 encode gender: 00=unknown, 01=male, 10=female. |
| 2 | 0x04 | GENDER_FEMALE | NPC is female. |
| 3 | 0x08 | FLAG_PREVIEW | Settings panel live preview. RuneReader MUST discard and never synthesize. |
| 4–7 | 0xF0 | (reserved) | Reserved for future use. Must be zero. |

### 13.3 Race → Accent Group Mapping

| Accent Group | Races (RACE byte values) | Character |
|---|---|---|
| Neutral American | Human (1), Orc (2), Night Elf (4), Mag'har Orc (36) | Clean, no accent — default fallback |
| American Raspy | Undead / Forsaken (5) | Hollow, gravelly |
| Scottish | Dwarf (3), Dark Iron Dwarf (30) | |
| British Haughty | Blood Elf (10), Void Elf (29) | Posh, refined |
| British Rugged | Worgen (22), Kul Tiran (32) | Gruff, weathered |
| Playful / Squeaky | Gnome (7), Mechagnome (37) | High energy, fast-talking |
| Eastern European | Draenei (11), Lightforged Draenei (28) | Slavic inflection |
| Caribbean | Troll (8) | Jamaican / Caribbean lilt |
| Regal Tribal | Zandalari Troll (31) | Slower, more formal than Troll |
| Deep Resonant | Tauren (6), Highmountain Tauren (27) | Slow, gravelly, deep |
| New York | Goblin (9) | Thick NY accent, fast-talking |
| East Asian | Pandaren (13) | Chinese-inflected |
| French | Nightborne (24) | French-inflected |
| Scrappy | Vulpera (35) | Quick, street-smart energy |
| Narrator | RACE=0x00 or FLAG_NARRATOR set, or any unmapped value | Neutral fallback |

> *NOTE: Race IDs above are approximate. Verify against live UnitRace() raceID values in-game using `/rrv race`.*

---

## 14. ITtsProvider Interface

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

### 14.1 Provider Implementations

| Provider | Platform | SynthesizePhraseStreamAsync | Notes |
|---|---|---|---|
| KokoroTtsProvider | Windows / Linux (CPU) | Full — parallel phrase encoding via Channel | Primary AI voice provider. 54 voices. `SupportsInlinePronunciationHints = true`. |
| WinRtTtsProvider | Windows only | Stub — single result from SynthesizeAsync | WinRT SpeechSynthesizer. `SupportsInlinePronunciationHints = false`. |
| LinuxPiperTtsProvider | Linux only | Stub — single result from SynthesizeAsync | Piper subprocess. Windows Piper deferred. |
| HttpTtsProvider | All platforms | Stub — single result from SynthesizeAsync | Calls Phase 5 HTTP server. `RequiresFullText = true`. Returns OGG decoded to PcmAudio. |
| NotImplementedTtsProvider | All | Throws NotImplementedException | Placeholder for unimplemented backends. |

### 14.2 Kokoro Voice Profiles and Recommended Presets

Each voice slot resolves to a `VoiceProfile` containing `VoiceId`, `LangCode`, and `SpeechRate`. `VoiceId` may be a single built-in Kokoro voice or a `mix:` specification. Mix strings are internal storage format; the primary UI presents friendly names and percentages.

`PlaybackSpeed` is intentionally separate from `SpeechRate`. `SpeechRate` affects synthesis. `PlaybackSpeed` affects already-rendered audio during playback only.

`ResolveVoiceId(slot)` returns `VoiceProfile.BuildIdentityKey()` — the full synthesis identity used for cache keying.

| Voice Slot | Default Voice / Mix |
|---|---|
| Narrator | mix:am_adam:0.2\|bm_lewis:0.8 |
| NeutralAmerican Male | am_michael |
| NeutralAmerican Female | af_sarah |
| All other Male slots | am_echo |
| All other Female slots | af_nova |

---

## 15. Settings UI

### 15.1 Top Chrome (Always Visible)

The following controls are always visible regardless of the Settings expander state:

- Title bar: "RuneReader Voice" + status badge (provider state).
- Live status panel: Capture / Session / Playback / Cache status indicators.
- Volume row: Vol label + Volume slider + percentage label + Start/Stop button. All in a single horizontal row.

### 15.2 Settings Expander

All tabs are wrapped in a single collapsible `Expander` labelled "Settings". Collapsed by default on first launch. Expanded/collapsed state persisted in `VoiceUserSettings.ExpanderStates` keyed by expander name. All expanders use `Expander.IsExpandedProperty` / `PropertyChanged` for state change detection (not `IsExpandedChanged` event, which does not exist in Avalonia 11).

Settings tab contents (each in its own collapsed sub-expander):

- **TTS Provider** (`ExpanderProvider`): provider selector ComboBox.
- **Playback** (`ExpanderPlayback`): speed slider, playback mode selector, phrase chunking checkbox, audio device selector.
- **Dialog Sources** (`ExpanderDialogSources`): per-source enable/disable checkboxes (Greeting, Detail, Progress, Reward, Books).

Other tabs: Voices, NPC Voices, Advanced, Pronunciation, Text Shaping.

### 15.3 Advanced Tab

All expanders start collapsed. Named: `ExpanderCapture`, `ExpanderAdvPlayback`, `ExpanderCache`, `ExpanderAudio`, `ExpanderDiagnostics`, `ExpanderHotkey`. Each saves its state to `ExpanderStates` on toggle.

### 15.4 Voice Assignment Grid (Voices Tab)

27 voice slots (13 accent groups × Male + Female + Narrator). Rows = accent group, columns = Male / Female. Race labels list all races sharing the slot. Example: "Blood Elf / Void Elf / Male NPC" for BritishHaughty/Male.

### 15.5 NPC Voices Tab

See Section 9.7.

### 15.6 VoiceMixDialog

- Available voices list (filters already-selected voices).
- Selected voices grid with weight slider per voice (0–100%) + voice display name + gender markers.
- Remaining % indicator; Save disabled unless total ≈ 100%.
- Blend output format stored in settings: `mix:voiceA:0.6000|voiceB:0.4000`

---

## 16. Performance Characteristics

### 16.1 Addon (Phase 1 Measurements)

| Metric | Value | Notes |
|---|---|---|
| CPU while display active | ~0.3% | Measured in-game during quest dialog cycling. |
| CPU burst on dialog open | ~5% | Pre-encoding all QR matrices. Imperceptible — occurs during UI interaction. |
| CPU while idle | ~0% | OnUpdate unregistered when no display active. |
| Chunk display time (default) | 100ms | 20× read margin vs. RuneReader 5ms capture interval. |

### 16.2 Kokoro Synthesis Performance (Observed)

| Metric | Value | Notes |
|---|---|---|
| First-phrase latency | ~200–400ms | Time from segment complete to first audio playing. |
| Parallel phrase encoding | Yes | All phrase jobs enqueued to ONNX immediately. |
| ONNX executor | ORT_SEQUENTIAL (default) | ORT_PARALLEL not benchmarked. May be slower for 82M model. |
| Token limit per phrase | 510 tokens | ONNX input backstop. Multi-segment phrases concatenated before write. |
| Cache miss OGG encode | Synchronous in StoreAsync | Runs on thread-pool via Task.Run. No background task. |

---

## 17. Development Phases

| Phase | Status | Summary |
|---|---|---|
| Phase 1 | COMPLETE | Addon QR protocol, dialog capture, segmented payloads, preview flags, race/NPC metadata, pre-encoding, frame hooks. |
| Phase 2 | COMPLETE | Local TTS integration, Kokoro support, settings UI, slot assignment, profile-aware voice refinement, dialect/language support, speech rate, presets, cache identity updates, voice editor UX. |
| Phase 3 | COMPLETE | Phrase-level streaming, TextSplitter, Channel-based parallel phrase encoding, PCM-first architecture, WasapiStreamAudioPlayer, OGG-only cache. |
| Phase 4 | COMPLETE | Linux support: GStreamer playback stub, Piper provider stub, cross-platform abstraction. |
| Phase 5 | IN PROGRESS | TTS HTTP server: Kokoro-82M ONNX v1 backend, remote renderer, shared cache, provider discovery. XTTS v2 and GPU models in Phase 5b. |
| Phase 6 | PLANNED | Platform polish: Windows Piper (pending libpiper C API), NPC override crowd-source sync, export/import, full GstAudioPlayer EOS, GstAudioPlayer PcmAudio interface update. |

---

## 18. Known Limitations / TODO

- **Windows playback race (phrase streaming):** if phrase 0 ends before phrase 1 is ready, `PlaylistPlayAsync` may resolve early and drop later phrases. Fix in player.
- **GstAudioPlayer:** EOS detection is a stub. True gapless playback not implemented. PcmAudio interface not yet updated.
- **HttpTtsProvider:** still a stub in the local codebase. Server parity is design direction, not current implementation.
- **Kokoro cache identity normalization:** logically identical mixes with different ordering/weight formatting still hash differently.
- **NPC override Export/Import JSON:** buttons exist in the UI but are stubbed.
- **Silence trimming:** `TrimSilence` is a pass-through stub.
- **Windows Piper:** deferred until libpiper C API ships.
- **Speed control:** WasapiStreamAudioPlayer pitch-corrected tempo not yet verified end-to-end.

---

## 19. Security / Privacy

- No network transmission in Phases 1–4 unless the user explicitly selects a remote provider.
- Local providers keep text on the user's machine.
- HTTP/cloud providers are opt-in. The UI should make it clear when text leaves the machine.
- Server auth/TLS requirements defined by the Phase 5 deployment model.

---

## 20. TTS HTTP Server (Phase 5)

### 20.1 Summary

The TTS HTTP server is a separate Python/FastAPI project. The client remains authoritative over voice choice. The server renders exactly what the client requests, provides shared caching across clients, and exposes provider capability discovery.

### 20.2 v1 Backend — Kokoro-82M ONNX

The confirmed v1 production backend is Kokoro-82M ONNX via the `kokoro-onnx` Python package.

- Python 3.13 compatible. Supports Python 3.10–3.13.
- XTTS v2 dropped as v1 backend due to Python 3.13 incompatibility. Preserved as Phase 5b (GPU-gated in `deploy.sh`).
- Model files: `kokoro-v1.0.onnx` and `voices-v1.0.bin`. Shared between the C# local client and the Python server (`./models/`).
- All 29 WoW accent group slots mapped to Kokoro voice names in `kokoro_backend.py`.
- `"kokoro"` is the default backend across `server.py`, `preset_manager.py`, `settings.py`, config files, `requirements.txt`, `deploy.sh`, `SETUP.md`, and `backends/__init__.py`.
- ONNX Runtime releases the GIL during C++ inference — the asyncio event loop stays responsive during synthesis. No GIL-related concurrency issues.

### 20.3 Phase 5b — GPU-Gated Backends

`deploy.sh` gates GPU-only backends behind a `--gpu` flag. XTTS v2 and other powerful models (StyleTTS2, F5-TTS, Coqui VITS) are installed only when `--gpu` is passed. `xtts_backend.py` is preserved intact for Phase 5b activation.

### 20.4 Stack

- Language / framework: Python + FastAPI
- Inference backends: provider-pluggable (Kokoro v1; XTTS v2 Phase 5b; future)
- Storage: SQLite metadata store for cache manifest; filesystem for rendered audio and reference assets
- Transport: HTTP/JSON control plane; binary OGG audio response
- Deployment: standalone LAN service, optionally GPU-backed

### 20.5 Discovery Model

Three layers of discovery:

- `GET /api/v1/capabilities`: server-wide info (API version, output formats, auth required, provider list).
- `GET /api/v1/providers` / `GET /api/v1/providers/{id}`: per-provider capability report (voice list support, mixing, zero-shot cloning, style controls, speech rate, pitch, streaming vs full-response, etc.).
- `GET /api/v1/providers/{id}/voices`: provider-scoped voice list with `id`, `display_name`, `language`, `gender`, `speaker_type`, `mixable`, `cloneable`.

### 20.6 Request Model

Two synthesis styles:

- **Convenience request:** `provider_id` + `preset_id` + `text`. Server resolves preset to synthesis parameters.
- **Explicit request (preferred):** `provider_id` + `voice_profile` + `text`. `voice_profile` includes `voice_id`, `lang_code`, `speech_rate`, optional mix data, optional reference identity.

Presets are convenience shortcuts only. The explicit `voice_profile` path is required for local/remote parity.

### 20.7 Synthesis Identity and Cache Key

Server cache key includes: normalized text + provider/backend identity + model version + resolved synthesis identity (voice/mix, language, speech rate; plus reference clip identity for reference-based providers).

### 20.8 Two-Layer Cache Model

1. Check local client cache (`TryGetDecodedAsync`).
2. If local miss and remote provider selected, call server.
3. Server checks shared cache.
4. If server miss, synthesize, store, return OGG.
5. Later identical requests from any client reuse the server cache.

### 20.9 Example Endpoints

```
GET  /api/v1/health
GET  /api/v1/capabilities
GET  /api/v1/providers
GET  /api/v1/providers/{provider_id}
GET  /api/v1/providers/{provider_id}/voices
POST /api/v1/synthesize
POST /api/v1/reference/upload     (provider-specific / optional)
POST /api/v1/reference/register   (provider-specific / optional)
```

### 20.10 Diagnostics

Useful log fields per request: request ID, provider ID, preset ID if present, resolved profile summary, cache hit/miss, synth duration, output duration, output format, caller/client ID.

---

## 21. Licensing / Packaging Notes

RuneReaderVoice and related code use GPL-3.0-or-later. Source headers include the SPDX identifier (`SPDX-License-Identifier: GPL-3.0-or-later`). Distribution artifacts include the LICENSE text. User-facing metadata (addon TOC, about/license surfaces) points to the included license text.

---

## 22. Final Design Summary

RuneReaderVoice v13 establishes a PCM-first audio architecture: providers synthesize to in-memory `PcmAudio`, the cache stores OGG as the sole on-disk artifact, and the audio player consumes decoded `PcmAudio` directly — eliminating all temporary files from the synthesis path. `WasapiStreamAudioPlayer` (NAudio/WASAPI) replaces `WinRtAudioPlayer`/`MediaPlaybackList` on Windows. The NPC Race Override system adds per-NPC accent assignment with a three-tier source hierarchy (Local > CrowdSourced > Confirmed), SQLite persistence via `sqlite-net-pcl`, and full UI in the Last NPC panel and NPC Voices tab. The UI has been restructured so volume and Start/Stop are always visible, with all settings collapsed behind a persistent expander. The Phase 5 HTTP server uses Kokoro-82M ONNX as the confirmed v1 backend, with XTTS v2 and GPU-backed models gated to Phase 5b.
