**RuneReader**

Quest Text-to-Speech (TTS)

*Feature Design Document | Roadmap Item*

Status: Phases 1–4 Complete, Phase 5 In Progress | Target: Retail WoW

Last Updated: March 2026 | Version 12

What's New in v12

- Phase 2 remains complete, but the document now records the post-completion refinement work: structured voice profiles, dialect/language support, voice speech rate, richer cache identity, presets, and a clearer voice editor UX.

- Kokoro voice selection is now documented as a VoiceProfile model rather than a simple voice-id assignment. The current profile shape is VoiceId + LangCode + SpeechRate.

- Playback speed is now explicitly separated from speech rate. SpeechRate affects synthesis; PlaybackSpeed affects already generated audio playback only.

- Kokoro cache identity is updated to include all synthesis-affecting profile fields via ResolveVoiceId(slot) → VoiceProfile.BuildIdentityKey().

- Voice editing UX is now documented as preset-first and WoW/theme-oriented, with raw mix strings treated as an internal representation rather than the primary user-facing editing surface.

- Recommended speaker presets are now the preferred way to present default voices. Presets are defaults only and remain fully user-overridable.

- Phase 5 server design has been revised: the client is authoritative over voice choice, while the server acts as a remote renderer, shared cache, and provider capability discovery service.

- Server synthesis requests are no longer documented as preset-only. The server may accept preset shorthand, but it must also support explicit provider-aware voice profiles sent by the client.

- Server capability discovery has been expanded to cover multiple providers, provider-level feature reporting, provider-scoped voice lists, and future support for Kokoro, TTSv2, and other renderers.

- The design now explicitly documents the two-layer cache model: client-side local cache first, then server-side shared cache on miss.

**1. Overview**

RuneReader Quest TTS adds spoken voice narration to World of Warcraft quest dialog by creating a data pipeline between a WoW addon (RuneReaderVoice) and the RuneReader desktop application. The addon encodes quest text as QR barcodes displayed on-screen; RuneReader captures, decodes, and passes the text to a TTS engine which synthesizes and plays audio in real time.

Blizzard's in-game TTS is limited to legacy Windows XP/Vista era voices. This feature bypasses that limitation by routing voice generation through the host OS or a configurable AI voice engine, enabling natural-sounding narration for all quest dialog, NPC greetings, and readable in-game books.

**2. Goals**

- Narrate quest dialog (greeting, detail, progress, completion) using natural-sounding voices.

- Narrate readable in-game books and item text.

- Assign distinct voice profiles by WoW speaker slot/theme (for example: race-oriented NPC slots plus narrator/system text), while keeping assignments flexible and user-editable.

- Operate entirely client-side in the local Kokoro path, while preserving a clean remote rendering path for higher-quality or GPU-backed providers.

- Cache generated audio using synthesis-aware identity so redundant renders are avoided locally and, when applicable, remotely.

- Provide clean extension points for multiple local and remote AI voice backends without redesigning the pipeline.

- Support both Windows and Linux platforms.

- Stream phrase-level audio as soon as the first phrase is synthesized, minimizing perceived latency.

**3. Non-Goals (v1)**

- Classic / Era / Season of Discovery support (may be backported later).

- Additional heavyweight local AI voice models beyond Kokoro. Higher-quality or GPU-intensive synthesis is expected to come from the remote HTTP server path.

- Per-NPC hand-authored voices. The current system remains slot/theme oriented rather than individually authored for every NPC.

- Lip-sync or in-game audio integration (audio is played by RuneReader, not WoW).

- Multi-page book reading in a single session — the user must click through pages manually.

- Gapless playback on Linux — GstAudioPlayer uses sequential file playback. True gapless requires GStreamer playlist support (deferred).

**4. System Architecture**

The system consists of three components:

**4.1 WoW Addon (RuneReaderVoice — Lua)**

- Captures quest dialog text via WoW frame events.

- Classifies each text segment by speaker role (NPC Male, NPC Female, Narrator).

- Splits text into ~10-word chunks and pads each chunk to a fixed byte target.

- Encodes each chunk as a Base64 ASCII QR payload with a structured header.

- Pre-encodes all QR matrices at dialog-open time (not in the render loop).

- Cycles chunks in a timed display loop; RuneReader reads each one at ~5ms latency.

- Window close detected via HookScript(OnHide) on GossipFrame, QuestFrame, and ItemTextFrame.

**4.2 RuneReader Voice Desktop Application (C# / Avalonia) — Standalone**

- Continuously captures screen frames and scans for QR barcodes.

- Detects a TTS payload by inspecting the decoded header magic bytes.

- Discards packets with FLAG_PREVIEW set (settings panel live preview).

- Assembles multi-chunk payloads per segment; begins playback on first phrase (stream mode) or waits for full text (batch mode).

- Routes assembled text and speaker metadata to the active ITtsProvider. Playback layer reads synthesized audio files. Cache sits between synthesis and playback, keyed by hash(text + voiceSlotID + providerID).

- Plays synthesized audio via platform audio layer (WinRT MediaPlaybackList on Windows, GStreamer on Linux). ESC hotkey aborts playback if audio is playing; passes through to game if idle.

- Manages the audio cache (see Section 8). Uses ZXing DecodeMultiple on full-screen scans to handle two simultaneous QR codes.

**4.3 TTS Provider (Pluggable)**

The TTS backend is abstracted behind an ITtsProvider interface, allowing local providers (WinRT, Piper, Kokoro) and future remote AI backends to coexist and be selected at runtime. The interface remains intentionally small, but providers may internally resolve richer voice profile data when computing synthesis identity or presenting available voices.

**4.4 TTS HTTP Server (Phase 5 — Separate Project)**

An optional standalone server that the desktop client can call instead of synthesizing locally. Supports multiple simultaneous users and higher-quality or GPU-accelerated synthesis models including voice cloning. Provides the 'cloud/server' AI voice path — Kokoro remains the local AI provider. See Section 19 for full design.

**5. Data Pipeline**

**5.1 Pipeline Overview**

The RuneReader Voice pipeline has five stages:

| Stage | Description |
|---|---|
| RvBarcodeMonitor | Scans screen frames, decodes QR codes, fires OnPacketDecoded per valid RV packet. |
| TtsSessionAssembler | Collects chunks by DIALOG ID, fires OnSegmentComplete when all chunks of a segment arrive. Fires OnSessionReset on DIALOG ID change (cancels playback queue and restarts). OnSourceGone is intentional no-op — queued audio plays to completion. |
| PlaybackCoordinator | Dequeues segments, checks cache, calls SynthesizePhraseStreamAsync for cache misses, feeds phrase paths to PlaylistPlayAsync. Manages cancellation via CancellationToken on session reset or ESC. |
| ITtsProvider / TtsAudioCache | Provider streams phrase WAVs; cache stores each phrase (play-first WAV then background OGG transcode). Cache key = SHA256(text + voiceId + providerId)[0..15]. |
| IAudioPlayer | WinRtAudioPlayer uses MediaPlaybackList for gapless prefetch on Windows. GstAudioPlayer plays files sequentially on Linux. Speed and volume applied per-session. |

**5.2 Session Reset vs. Source Gone**

- **OnSessionReset** (new DIALOG ID): cancels current playback immediately, clears queue, restarts coordinator loop. This is the only hard interrupt — new NPC interaction.

- **OnSourceGone** (QR frame disappears): intentional no-op. Queued audio plays to completion. The player closed the dialog but the content was already received.

- **ESC hotkey**: cancels current playback via CancellationToken. Queue is drained. Passes ESC through to game if no audio is playing.

**6. Phrase-Level Streaming Synthesis**

**6.1 Design Rationale**

Prior to v1.7, PlaybackCoordinator synthesized the full segment text before beginning playback. For a long NPC monologue, this meant the player read the first sentence before hearing any audio. Phrase streaming starts playback on the first synthesized phrase while encoding of subsequent phrases continues in parallel.

**6.2 TextSplitter**

Splits a full segment text into pronounceable phrases before synthesis. Splitting rules:

- Sentence endings (. ! ? ...): punctuation stays with the left chunk.
- Clause breaks (, ; :): punctuation moves to the start of the right chunk — preserves prosody at the split point.
- **MinFragmentWords = 3**: short trailing fragments are merged forward into the next chunk rather than synthesized alone.
- Abbreviation protection: Mr. Mrs. Dr. St. etc. do not trigger sentence splits.
- Decimal protection: 1.5, 3.14 do not trigger splits.

> *NOTE: Comma/semicolon/colon splitting is enabled by default and produces natural phrase pacing. It can be toggled by commenting out the relevant split pass in TextSplitter if a provider handles long text better as a unit.*
>
> *NOTE: KNOWN ISSUE: If the first phrase is very short (e.g. a single short sentence), it may finish playing before the second phrase finishes encoding. In this case PlaylistPlayAsync resolves completion prematurely and subsequent phrases are silently dropped. Two candidate fixes: (1) PlaylistPlayAsync detects stream-not-done at playlist exhaustion and re-arms for new items — preferred, fixes the root cause in the player; (2) TextSplitter ensures the first phrase is always long enough that its speaking time exceeds worst-case encode time for any subsequent phrase — partial mitigation only, does not handle all cases. See Known Limitations Section 17.*

**6.3 SynthesizePhraseStreamAsync**

Returns: IAsyncEnumerable<(string wavPath, int phraseIndex, int phraseCount)>

KokoroTtsProvider implementation:

- Splits text via TextSplitter into N phrases.
- Enqueues ALL phrase ONNX inference jobs immediately (parallel encoding begins for all phrases at once).
- Uses Channel<(int index, float[] pcm)> to collect completions as they arrive (any order).
- Out-of-order arrivals buffered in a pre-allocated array keyed by index; yielded to caller in strict phrase order.
- Multi-segment phrases (>510 ONNX tokens) are concatenated before writing to the channel.

WinRT / Piper / NotImplemented providers: stub implementation — yields a single result from SynthesizeToFileAsync (no splitting).

> *NOTE: ORT_PARALLEL vs ORT_SEQUENTIAL has not been benchmarked for Kokoro 82M. ORT_SEQUENTIAL may be faster for small models due to lower threading overhead. Worth measuring.*

**6.4 PlaybackCoordinator Integration**

On a cache miss, PlaybackCoordinator calls SynthesizePhraseStreamAsync and adapts the result to an IAsyncEnumerable<string> path stream via PhrasePathStream():

- Each phrase WAV is stored to the cache as it arrives (per-phrase cache key).
- Phrase path yielded immediately to PlaylistPlayAsync — playback of phrase 0 begins while phrase 1 is still encoding.
- Per-phrase cache keying: GetPhraseText() reconstructs the phrase text from the full segment text + phrase index for independent cache lookup.

On a cache hit (full segment or individual phrases): plays directly without synthesis.

**7. Audio Playback**

**7.1 IAudioPlayer Interface**

> Task PlayAsync(string filePath, CancellationToken ct);
>
> Task PlaylistPlayAsync(IAsyncEnumerable<string> paths, CancellationToken ct);
>
> float Speed { get; set; }
>
> float Volume { get; set; }

PlaylistPlayAsync accepts a streaming path enumerable — it does not require all files to be known upfront. This is the integration point for phrase streaming.

**7.2 WinRtAudioPlayer (Windows)**

Uses Windows.Media.Playback.MediaPlaybackList for gapless prefetch between phrases:

- MaxPrefetchTime = 5s: WinRT pre-buffers the next item while the current item is playing.
- AutoRepeatEnabled = false.
- Named CurrentItemChanged handler (not lambda) — subscribed before Play(), unsubscribed in finally to prevent leaks.
- Play() called immediately after list is populated with first item. PlaybackRate applied after Play().
- **OnPlayerEnded:** resolves a TaskCompletionSource when streamDone=true AND MediaEnded fires — handles the race between stream exhaustion and final item end.
- COMException guard in OnItemChanged and finally block — MediaPlaybackList throws during shutdown if the player is disposed mid-playback.
- **Speed setter:** writes both a _speed backing field and the live PlaybackRate. The backing field ensures speed is re-applied correctly after Play() on a new session.

> *NOTE: LIMITATION: Speed adjustment mid-playlist (while phrases are still encoding) can cause early playback termination in rare cases. The session self-heals on the next dialog interaction.*

**7.3 GstAudioPlayer (Linux)**

Sequential fallback: PlaylistPlayAsync iterates the path stream and calls PlayAsync for each file in order. No gapless prefetch. GStreamer bus polling for EOS detection is currently a stub — timing is approximated.

> *NOTE: LIMITATION: GstAudioPlayer EOS detection is a stub. True gapless playback on Linux requires a GStreamer playlist pipeline. Deferred.*

**8. Audio Cache (Desktop Client)**

**8.1 Play-First OGG Strategy**

The cache uses a play-first strategy to minimize synthesis-to-playback latency:

- **StoreAsync:** copies the synthesized WAV to the cache immediately and returns the WAV path for instant playback.
- **CompressInBackgroundAsync:** transcodes WAV → OGG on a thread-pool thread with CancellationToken.None (runs to completion regardless of cancellation).
- On completion, atomically swaps the manifest entry (.wav → .ogg) and deletes the WAV.
- **ClearAsync race guard:** if the cache is cleared while compression is in-flight, the orphaned OGG is discarded.
- **TrackCompressionTask:** tracks in-flight compression tasks. Dispose() waits up to 5 seconds for all tasks to complete before shutdown.

> *NOTE: When HttpTtsProvider returns OGG directly, the WAV→OGG pipeline is bypassed. The manifest entry is written as .ogg immediately and marked pre-compressed. CompressInBackgroundAsync is never called for HTTP provider entries.*

**8.2 OGG Compression**

- Uses OggVorbisEncoder NuGet package (v1.2.2).
- OGG quality: _oggQuality / 10f (0.0–1.0), default 4 → 0.4 (~64kbps). Configurable in advanced settings (0–10 scale).
- Chunk size: 1024 samples per TranscodeToOggAsync write pass.
- Concentus and NVorbis removed from csproj — OggVorbisEncoder is the sole transcoder.

**8.3 Cache Key**

SHA-256 hash of: normalized text + voice slot ID + TTS provider ID. Truncated to 16 hex characters. Computed entirely in RuneReader — the addon does not participate in cache key generation. Per-phrase cache keys use the reconstructed phrase text, not the full segment text.

For HttpTtsProvider: ProviderId = "http:<serverUrl>" so cache entries are automatically scoped to the specific server instance. Switching servers invalidates client cache entries for the old server.

**8.4 Storage**

- Default location: AppContext.BaseDirectory + "tts_cache" (app-local, alongside the binary).
- Model files: AppContext.BaseDirectory + "models".
- Settings.json: %APPDATA% (small, appropriate to roam).
- Audio stored as .ogg (post-compression) or .wav (while compression is pending or disabled). Mixed formats supported — manifest tracks extension per entry.

**8.5 Eviction**

- No TTL — quest dialog is static content. A line cached months ago is still valid if the player revisits that NPC.
- Configurable max size (default 500MB). LRU eviction on startup when limit exceeded.
- Manual clear button in settings.

> *NOTE: Silence trimming (strip leading/trailing silence from WAV before OGG transcode) is present in the architecture but currently a pass-through stub. The server implements this properly. Implementation in the desktop client deferred.*

**9. Addon File Structure**

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

**10. Data Flow**

- WoW fires a quest frame event (e.g., QUEST_DETAIL) or the player opens a book.
- core.lua reads dialog text, strips WoW colour codes, collapses whitespace.
- payload.lua splits text on <angle bracket> boundaries into segments (NPC speech and narrator). Each segment split into ~10-word chunks, padded to fixed size.
- Each chunk encoded as Base64 with 22-character ASCII header (MAGIC, VER, DIALOG, IDX, TOTAL, FLAGS, RACE, NPC).
- frames_qr.lua pre-encodes all QR matrices at dialog-open time (not per frame).
- Segments stream continuously: segment 0 cycles its chunks, then segment 1 immediately follows, etc. Final segment loops until dialog closes.
- RuneReader decodes, validates header, discards PREVIEW packets. Assembles complete text from all chunks per segment.
- Cache checked by phrase text hash + voice ID. On hit, plays cached audio. On miss, SynthesizePhraseStreamAsync begins — first phrase plays while subsequent phrases encode.
- Audio plays via MediaPlaybackList (Windows) or sequential GStreamer (Linux). When QR frame hides, RuneReader plays remaining queued audio to completion (OnSourceGone no-op).

**11. WoW Events and Dialog Sources**

All confirmed working on Retail WoW (Interface 120001).

| Event | Text API | Dialog Type | Notes |
|---|---|---|---|
| GOSSIP_SHOW | C_GossipInfo.GetText() | NPC greeting / gossip | Deferred 1 frame via C_Timer.After(0). Falls back to GetGossipText(), then GossipGreetingText FontString. |
| QUEST_GREETING | GetGreetingText() | Multi-quest NPC greeting | Fires for NPCs with multiple available quests. |
| QUEST_DETAIL | GetTitleText() + GetQuestText() + GetObjectiveText() | Quest accept dialog | All three concatenated with double-space separator. |
| QUEST_PROGRESS | GetProgressText() | Quest in-progress check-in |  |
| QUEST_COMPLETE | GetRewardText() | Quest completion / reward |  |
| QUEST_FINISHED | (dialog guard) | Accept or decline | Guarded by 3-second minimum display window + dialog ID check. |
| ITEM_TEXT_READY | ItemTextGetText() + ItemTextGetItem() | In-game books / readable items | EXPERIMENTAL. Working. Multi-page books require the player to click through pages. |

**11.1 Window Close Detection**

Close detection uses HookScript("OnHide") on Blizzard dialog frames rather than events. Events do not fire on all close paths (Escape key, clicking away, programmatic close). Frame hooks fire unconditionally.

- GossipFrame:OnHide → StopDisplay immediately.
- QuestFrame:OnHide → StopDisplay immediately, unless QUEST_FINISHED timer already scheduled.
- ItemTextFrame:OnHide → StopDisplay immediately.

**11.2 NPC Gender Detection**

- Primary: UnitSex("target") — player must target the NPC to open dialog.
- Fallback: UnitSex("questnpc"), then UnitSex("npc").
- Returns: 1=unknown, 2=male, 3=female. Unknown maps to neutral voice.

**12. QR Payload Protocol (v04)**

This section is the authoritative reference for the QR payload format. It reflects the v04 protocol implemented in the addon.

**12.1 Payload Structure**

> [ MAGIC(2) | VER(2) | DIALOG(4) | IDX(2) | TOTAL(2) | FLAGS(2) | RACE(2) | NPC(6) | BASE64_PAYLOAD ]

| Field | Chars | Format | Description |
|---|---:|---|---|
| MAGIC | 2 | "RV" | Identifies a RuneReaderVoice TTS packet. |
| VER | 2 | "04" | Protocol version. |
| DIALOG | 4 | Hex 0000–FFFF | Dialog block ID. Increments once per NPC interaction. Signals context change to RuneReader. |
| IDX | 2 | Hex 00–FF | 0-based chunk index within this segment. |
| TOTAL | 2 | Hex 01–FF | Total chunk count for this segment. |
| FLAGS | 2 | Hex bitmask | Speaker / control flags. See Section 12.2. |
| RACE | 2 | Hex 00–FF | NPC race or creature type ID. |
| NPC | 6 | Hex 000000–FFFFFF | NPC ID from unit GUID segment 6. |
| BASE64_PAYLOAD | Variable | Base64 | Base64-encoded text chunk, space-padded to fixed size. |

**12.2 FLAGS Byte**

| Bit | Mask | Name | Meaning |
|---:|---:|---|---|
| 0 | 0x01 | FLAG_NARRATOR | Narrator voice. RuneReader assigns Narrator slot regardless of gender/race. |
| 1 | 0x02 | GENDER_MALE | NPC is male. Bits 1+2 encode gender: 00=unknown, 01=male, 10=female. |
| 2 | 0x04 | GENDER_FEMALE | NPC is female. |
| 3 | 0x08 | FLAG_PREVIEW | Settings panel live preview. RuneReader MUST discard and never synthesize. |
| 4–7 | 0xF0 | (reserved) | Reserved for future use. Must be zero. |

**12.3 Race → Accent Group Mapping**

| Accent Group | Races (RACE byte values) | Character |
|---|---|---|
| Neutral American | Human (1), Orc (2), Mag'har Orc (36) | Clean, no accent — default fallback |
| American Raspy | Undead / Forsaken (5) | Hollow, gravelly |
| Scottish | Dwarf (3), Dark Iron Dwarf (30) |  |
| British Haughty | Blood Elf (10), Void Elf (29) | Posh, refined |
| British Rugged | Worgen (22), Kul Tiran (32) | Gruff, weathered |
| Playful / Squeaky | Gnome (7), Mechagnome (37) | High energy, fast-talking |
| Eastern European | Draenei (11), Lightforged Draenei (28) | Slavic inflection |
| Caribbean | Troll (8) | Jamaican/Caribbean lilt |
| Regal Tribal | Zandalari Troll (31) | Slower, more formal than Troll |
| Deep Resonant | Tauren (6), Highmountain Tauren (27) | Slow, gravelly, deep |
| New York | Goblin (9) | Thick NY accent, fast-talking |
| East Asian | Pandaren (13) | Chinese-inflected |
| French | Nightborne (27) | French-inflected |
| Scrappy | Vulpera (35) | Quick, street-smart energy |
| Narrator | RACE=0x00 or FLAG_NARRATOR set, or any unmapped value | Neutral fallback |

> *NOTE: Race IDs above are approximate — verify against live UnitRace() raceID values in-game using /rrv race.*
>
> *NOTE: At initial deployment all RACE values map to Narrator/neutral voice. Accent group mapping fully implemented in Phase 2.*

**13. ITtsProvider Interface**

> interface ITtsProvider
>
> {
>
> Task<string> SynthesizeToFileAsync(string text, VoiceSlot slot, string outputPath, CancellationToken ct);
>
> IAsyncEnumerable<(string wavPath, int phraseIndex, int phraseCount)>
>
> SynthesizePhraseStreamAsync(string text, VoiceSlot slot, string tempDir, CancellationToken ct);
>
> string ProviderId { get; }
>
> bool IsAvailable { get; }
>
> bool RequiresFullText { get; }
>
> string ResolveVoiceId(VoiceSlot slot);
>
> IReadOnlyList<VoiceInfo> GetAvailableVoices();
>
> }

**13.1 Provider Implementations**

| Provider | Platform | SynthesizePhraseStreamAsync | Notes |
|---|---|---|---|
| KokoroTtsProvider | Windows / Linux (CPU) | Full implementation — parallel phrase encoding via Channel | Primary AI voice provider. 54 voices. Default: Narrator=mix:am_adam:0.2|bm_lewis:0.8 |
| WinRtTtsProvider | Windows only (#if WINDOWS) | Stub — yields single result from SynthesizeToFileAsync | WinRT SpeechSynthesizer. Natural-sounding OS voices. |
| LinuxPiperTtsProvider | Linux only (#if LINUX) | Stub — yields single result from SynthesizeToFileAsync | Piper subprocess or Speech Dispatcher IPC. |
| HttpTtsProvider | All platforms | Stub — yields single result from SynthesizeToFileAsync | Calls Phase 5 TTS HTTP Server. RequiresFullText=true. Returns OGG directly. See Section 19. |
| NotImplementedTtsProvider | All | Stub — throws NotImplementedException | Placeholder for unimplemented backends. |

13.2 Kokoro Voice Profiles and Recommended Presets

The current Kokoro model is profile-based rather than simple voice-id assignment. Each voice slot resolves to a VoiceProfile containing VoiceId, LangCode, and SpeechRate.

VoiceId may represent a single built-in Kokoro voice or a mix: specification. Mix strings remain an internal storage format; they are not intended to be the primary user-facing editing surface.

PlaybackSpeed is intentionally separate from SpeechRate. SpeechRate affects synthesis. PlaybackSpeed affects already rendered audio during playback only.

ResolveVoiceId(slot) must return the full synthesis identity for cache purposes. For Kokoro this is VoiceProfile.BuildIdentityKey(), which currently includes VoiceId | LangCode | SpeechRate.

Recommended speaker presets provide user-facing defaults. Presets are WoW/theme-oriented starting points, not hardcoded permanent voices, and users remain free to customize them.

Potential future refinement: normalize mix specifications so logically identical blends do not produce separate cache identities due only to ordering or formatting differences.

| Voice Slot | Default Voice / Mix |
|---|---|
| Narrator | mix:am_adam:0.2|bm_lewis:0.8 |
| NeutralAmerican Male | am_michael |
| NeutralAmerican Female | af_sarah |
| All other Male slots | am_echo |
| All other Female slots | af_nova |

**13.3 v2 Providers (Planned)**

- HTTP Server (Phase 5): FastAPI service hosting XTTS v2 and other AI voice models. This is the path for higher-quality synthesis — not local ONNX embedding. See Section 19.
- Cloud TTS: ElevenLabs / OpenAI TTS / Azure — explicit opt-in only, never default.

> *NOTE: Local ONNX embedding of StyleTTS2 / XTTS v2 is not planned. These models are too resource-intensive for local deployment and are better served via the Phase 5 HTTP server.*

**14. Performance Characteristics**

**14.1 Addon (Phase 1 Measurements)**

| Metric | Value | Notes |
|---|---|---|
| CPU while display active | ~0.3% | Measured in-game during quest dialog cycling. |
| CPU burst on dialog open | ~5% | Pre-encoding all QR matrices. Imperceptible — occurs during UI interaction, not combat. |
| CPU while idle | ~0% | OnUpdate unregistered when no display active. |
| QR matrices pre-encoded | Once per dialog | Not per-frame. |
| Chunk display time (default) | 100ms | 20x read margin vs. RuneReader 5ms capture interval. |

**14.2 Kokoro Synthesis Performance (Observed)**

| Metric | Value | Notes |
|---|---|---|
| First-phrase latency | ~200–400ms | Time from segment complete to first audio playing. Includes ONNX inference for first phrase. |
| Parallel phrase encoding | Yes | All phrase jobs enqueued to ONNX immediately. Subsequent phrases overlap with playback of first. |
| ONNX executor | ORT_SEQUENTIAL (default) | ORT_PARALLEL not benchmarked. May be slower for 82M model. Worth measuring. |
| Token limit per phrase | 510 tokens | ONNX input backstop. Multi-segment phrases concatenated before write. |

Voice profiles per WoW/theme slot. User-facing presentation should prefer readable race/theme labels (for example Dwarf / Male NPC, Troll / Female NPC, Narrator) over internal accent-group jargon.

Playback mode: Stream on first phrase / Wait for full text. Default remains stream on first phrase for local Kokoro. Remote HTTP providers may still use full-segment responses.

Volume control (0–100, independent of system volume).

- Playback speed (0.75x – 1.5x). This is a playback-only control and must not be conflated with voice speech rate.
- Active TTS provider selection (WinRT / Kokoro / Piper / HTTP Server / Cloud stub).
- TTS Server URL field (visible when HTTP Server provider selected). Text field + Test button (calls /api/v1/health, displays latency or error). Server status indicator: green/yellow/red dot.
- Voice assignment per accent group slot (13 groups × Male + Female + Narrator = 27 voice slots). Scrollable grid: rows = accent group, columns = Male / Female.
- Playback mode: Stream on first phrase / Wait for full text. Default: stream on first phrase (Kokoro); full text (WinRT / HTTP).
- Volume control (0–100, independent of system volume).
- Playback speed (0.75x – 1.5x). Default: 1.0x.
-- Clear cache button. Shows current cache size. Optional advanced controls: max cache size (MB), OGG quality (0–10).

- Live preview button. Encodes PREVIEW QR packet using the same addon protocol; RuneReader desktop app MUST discard preview packets on the normal capture path. Direct preview in the desktop app uses current unsaved values, bypasses repeat suppression, and should avoid normal cache use by writing a temporary WAV and playing it directly.

- Voice editing uses a preset-first, WoW/theme-oriented editor. The expected presentation is:
  - Applies to: [Race / NPC Slot]
  - Accent flavor / theme
  - Preset picker
  - Use Recommended / Apply Preset
  - Voice Mode: Single Voice or Blend Voices
  - Single voice selector or readable blend summary + Edit Blend
  - Dialect / Language picker (friendly display names preferred over raw codes)
  - Voice Speech Rate control
  - Live Preview
  - Summary
  - Save / Cancel

- Raw mix strings (for example `mix:am_adam:0.3889|bm_lewis:0.6111`) are internal representation only and should not be the primary editing UI.

- A future enhancement may show “Custom” when a slot no longer matches any shipped preset.

> *NOTE: Earlier UI text such as “Male Voice / Female Voice” proved confusing. User-facing presentation should be WoW/race-oriented where possible, with accent flavor as a secondary description.*

**15.1 VoiceMixDialog**

Current implementation uses VoiceMixDialog for manual multi-voice blend editing.

UI:

- Available voices list (filters out already-selected voices).
- Add → button, Remove button.
- Selected voices grid with weight slider per voice (0–100%) + voice display name + gender markers where available.
- Remaining % indicator; Save disabled unless total ≈ 100%.
- Blend preview button.

Blend output format stored in settings:

> mix:voiceA:0.6000|voiceB:0.4000

> *NOTE: Raw mix strings are storage format only. The primary user-facing editing surface should present friendly names and percentages rather than internal mix syntax.*

**16. Development Phases**

| Phase | Status | Summary |
|---|---|---|
| Phase 1 | COMPLETE | Addon QR protocol, dialog capture, segmented payloads, preview flags, race/NPC metadata, pre-encoding, frame hooks. |
| Phase 2 | COMPLETE (Refined) | Local TTS integration, Kokoro support, settings UI, slot assignment, profile-aware voice refinement, dialect/language support, speech rate, presets, cache identity updates, improved voice editor UX. |
| Phase 3 | COMPLETE | Phrase-level streaming synthesis and playback pipeline, TextSplitter, Channel-based parallel phrase encoding, play-first cache strategy, WinRT gapless integration. |
| Phase 4 | COMPLETE | Linux support: GStreamer playback stub, Piper provider stub, cross-platform abstraction. |
| Phase 5 | IN PROGRESS | TTS HTTP server: remote renderer, shared cache, provider discovery/capability reporting, future higher-quality and multi-provider support. |

**17. Known Limitations / TODO**

- **Windows playback race (phrase streaming):** If the first phrase is too short and phrase 2 is not ready before phrase 1 ends, PlaylistPlayAsync may resolve completion early and later phrases are dropped. Fix in player, not splitter.

- **GstAudioPlayer:** EOS detection is still a stub. True gapless playback not implemented.
- **HTTP server provider:** Desktop HttpTtsProvider is still a stub in current local codebase; server parity is design direction, not full local implementation yet.
- **Kokoro cache identity normalization:** logically identical mixes with different ordering/weight formatting still hash differently unless canonicalized first.
- **Preview temp file cleanup:** preview writes temporary WAV files for direct playback. Cleanup strategy should be formalized to avoid temp-file buildup.
- **Preset catalog maintenance:** preset content and recommended flags must stay aligned so “Recommended” labels match actual recommended behavior.
- **Some future voice-flavor mappings** (for example race/theme nuance) may come more from blend + rate than from dialect code alone.

**18. Security / Privacy**

- No network transmission in Phases 1–4 unless the user explicitly selects a remote provider.
- Local providers keep text on the user’s machine.
- HTTP/cloud providers are opt-in. The UI should make it clear when text leaves the machine.
- Server auth/TLS requirements are defined by the Phase 5 deployment model.

**19. TTS HTTP Server (Phase 5 — Revised Direction)**

**19.1 Summary**

The TTS HTTP server is a separate project that acts as a remote rendering engine, shared cache, and provider capability discovery service.

The client remains authoritative over voice choice. The server does not choose race voices, mixes, dialects, or speech rates on its own. Instead, the client selects or edits the voice configuration and the server renders exactly what the client requests.

This server exists for:
- higher-quality synthesis than a local lightweight provider can offer,
- GPU-backed rendering,
- optional voice cloning / reference-audio workflows for supported backends,
- shared cache reuse across multiple clients on the same network,
- future expansion to multiple remote providers (Kokoro, TTSv2, Qwen-family or diffusion-based voices, etc.).

The client still keeps its own local cache. Expected request flow:
1. Check local client cache.
2. If local miss and remote provider selected, call server.
3. Server checks shared cache.
4. If server miss, synthesize, store, return result.
5. Later identical requests from any client reuse the server cache.

**19.2 Stack (Current Design Direction)**

- **Language / framework:** Python + FastAPI
- **Inference backends:** provider-pluggable
- **Storage:**
  - SQLite or equivalent lightweight metadata store for cache manifest and provider registry
  - filesystem storage for rendered audio and optional reference assets
- **Transport:** HTTP/JSON for control plane, binary audio response or staged file response for synthesis output
- **Deployment:** standalone LAN service, optionally GPU-backed, potentially containerized

**19.3 Core Responsibilities**

The server has three primary responsibilities:

1. **Remote rendering**
   - Accept client-requested provider + voice/profile settings
   - Render audio deterministically from those settings

2. **Shared caching**
   - Store rendered output keyed by normalized text + synthesis identity + provider/backend/model identity
   - Allow multiple clients to reuse identical renders

3. **Capability discovery**
   - Report available providers
   - Report provider-specific capabilities
   - Report available voices per provider where applicable

**19.4 Discovery Model**

The server should expose three layers of discovery.

### 19.4.1 Server-level capabilities

Example endpoint:

> GET /api/v1/capabilities

Returns server-wide information such as:
- API version
- supported output formats
- maximum text length
- whether authentication is required
- whether provider discovery is supported
- whether reference upload is supported anywhere on the server
- list of provider IDs available on this instance

### 19.4.2 Provider inventory and provider-level capabilities

Example endpoints:

> GET /api/v1/providers  
> GET /api/v1/providers/{provider_id}

For each provider, report fields such as:
- provider ID
- display name
- backend family
- model name/version
- supported languages
- supports built-in voice list
- supports multi-voice mixing
- maximum voices per mix
- supports zero-shot voice matching
- reference clip requirements (if any)
- supports style / emotion controls
- supports speech rate
- supports pitch
- supports streaming responses or only full-response
- cache identity notes relevant to this renderer

Examples:
- **Kokoro**
  - built-in voice list available
  - mixing supported
  - language/dialect support
  - speech rate support
  - zero-shot cloning not supported
- **TTSv2**
  - zero-shot reference cloning may be supported
  - built-in speakers may or may not exist depending on implementation
  - mixing support unknown / provider-specific
- **Future providers**
  - capabilities vary and must be discoverable rather than assumed

### 19.4.3 Provider-scoped voice list

Example endpoint:

> GET /api/v1/providers/{provider_id}/voices

Each voice entry may expose:
- `id`
- `display_name`
- `language` or `languages`
- `gender`
- `style`
- `tags`
- `speaker_type` (`built_in`, `reference_based`, `preset`, etc.)
- `mixable`
- `cloneable`

The client should adapt UI to the selected provider’s advertised capabilities instead of assuming all providers support the same features.

**19.5 Request Model**

The server should support two synthesis request styles.

### 19.5.1 Convenience request

Client sends:
- `provider_id`
- `preset_id`
- `text`

Use when the client intentionally wants a server-known preset shorthand.

### 19.5.2 Explicit request

Client sends:
- `provider_id`
- `voice_profile`
- `text`

This is the preferred path for local/remote parity and custom user-edited voices.

A provider-aware `voice_profile` may include:
- `voice_id`
- `lang_code`
- `speech_rate`
- optional mix data
- optional style data
- optional reference identity
- optional provider-specific extras

Presets remain convenience shortcuts. They must not be the only legal way to request synthesis.

**19.6 Synthesis Identity and Cache Key**

Server cache identity must include everything that changes rendered output.

Base cache key components:
- normalized text
- provider/backend identity
- model version
- resolved synthesis identity

For Kokoro-style rendering, resolved synthesis identity includes at minimum:
- voice or mix identity
- language/dialect code
- speech rate

For future reference-based or style-based providers, identity may also include:
- reference clip identity
- style controls
- provider-specific synthesis parameters

If two clients request the same text using the same provider/model and the same synthesis identity, the server should render once and serve cached output thereafter.

**19.7 Synthesis Pipeline**

High-level flow:

1. Receive request.
2. Validate text and provider/profile payload.
3. Normalize text.
4. Resolve synthesis identity.
5. Compute cache key.
6. Check shared cache.
7. On hit: return cached audio.
8. On miss: synthesize exactly as requested, store in cache, return result.

Recommended behavior:
- continue synthesis to completion even if the caller disconnects, so the result can still populate shared cache
- log cache hit/miss and resolved provider/profile summary for diagnostics

**19.8 Reference Audio / Cloning (Advanced, Provider-Specific)**

Reference audio upload and zero-shot cloning are optional advanced features for providers that support them. They are not part of the universal server contract.

Possible provider-specific capabilities:
- upload short reference clips
- register server-managed reference voices
- synthesize using a reference ID
- impose clip-length or format requirements
- expose cloning quality / similarity controls

Kokoro-style built-in voice and mix workflows do not depend on these features.

**19.9 Example Endpoints (Design Direction)**

- `GET /api/v1/health`
- `GET /api/v1/capabilities`
- `GET /api/v1/providers`
- `GET /api/v1/providers/{provider_id}`
- `GET /api/v1/providers/{provider_id}/voices`
- `POST /api/v1/synthesize`
- `POST /api/v1/reference/upload` (provider-specific / optional)
- `POST /api/v1/reference/register` (provider-specific / optional)

**19.10 Diagnostics**

Useful diagnostics/logging fields:
- request ID
- provider ID
- preset ID if present
- resolved profile summary
- cache hit / miss
- synth duration
- output duration
- output format
- caller/client ID if available

**19.11 Local/Remote Parity**

The long-term design goal is conceptual parity between local and remote rendering where practical.

- Local Kokoro already uses a voice profile concept (voice/mix + language + speech rate).
- The remote path should be able to accept equivalent client-selected identity rather than reducing everything to a preset-only contract.
- Future remote providers may expose additional capabilities beyond local Kokoro; therefore the client must remain capability-aware and adapt its UI accordingly.

**20. Licensing / Packaging Notes**

RuneReaderVoice and related code should clearly state GPL-3.0-or-later licensing in source headers where applicable, with the project license included in distribution artifacts. User-facing metadata (for example addon TOC or app about/license surfaces) should point to the included license text.

**21. Final Design Summary**

RuneReaderVoice now treats voice selection as a structured, user-editable voice profile rather than only a simple voice-id choice. For Kokoro, this includes voice or mix identity, dialect/language, and speech rate, with cache identity reflecting all synthesis-affecting fields. The UI is moving toward WoW race/theme-oriented presets and readable editing rather than raw technical strings. The planned TTS server is a remote rendering engine, shared cache, and capability discovery service: the client remains authoritative over voice selection, while the server renders exactly what the client requests and caches identical requests for reuse across clients.