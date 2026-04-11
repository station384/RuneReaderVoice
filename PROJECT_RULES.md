# PROJECT_RULES.md

## Working baseline rules

1. Latest patched code is baseline.
    - Always use the most recent patched client/server zip as working baseline.
    - Do not revert to older uploaded source unless the user explicitly uploads newer source and says that is now baseline.

2. Code beats docs.
    - If current code and design doc disagree, current code is authoritative until docs are updated.

3. KISS.
    - Prefer straightforward, maintainable solutions.
    - Avoid cleverness unless real constraints require it.

## Current baseline files

- Client baseline: `RuneReaderVoice.1.3.8.90.zip`
- Server baseline: `rrvServer_70.zip`

## Design document rules

1. Document current behavior, not historical intent.
2. Remove stale statements when code changed.
3. Design doc must include current:
    - player-name replacement behavior
    - sentence-based cache-preserving splits
    - remote batch chaining / continuity
    - batch trim behavior
    - wait-for-full-text behavior after post-split expansion
    - cache identity rules for both client and server
    - Chatterbox-family server large-text handling

## Provider test file rules

1. Merge new findings into existing test blocks.
    - Do not create detached benchmark appendix for routine updates. :contentReference[oaicite:0]{index=0}

2. Preserve existing findings unless explicitly corrected. :contentReference[oaicite:1]{index=1}

3. Keep test IDs stable.
    - Do not rename existing test codes. :contentReference[oaicite:2]{index=2}

4. Generated sweep tests are first-class tests.
    - L001-L010 maintained same way as named tests. :contentReference[oaicite:3]{index=3}

## Client architecture rules

1. PCM-first playback contract.
    - Provider produces PCM.
    - Cache stores OGG.
    - Cache decodes OGG back to PCM.
    - Player receives PCM only.
    - Player should not know about WAV/OGG/files/decoder internals. :contentReference[oaicite:4]{index=4}

2. DSP is client-side post-retrieval.
    - DSP is applied after audio retrieval/decode on client.
    - Current client local audio cache key does not include DSP. :contentReference[oaicite:5]{index=5}

3. Wait-for-full-text must use post-split reality.
    - Final audible segment count after name expansion is authoritative.
    - Waiting logic must use remaining range, not original pre-split assumption. :contentReference[oaicite:6]{index=6}

4. Player-name replacement is normal behavior.
    - Not experimental.
    - All replacement modes use same sentence-based cache-preserving split flow.
    - Tiny bridge chunks around names are not acceptable. :contentReference[oaicite:7]{index=7} :contentReference[oaicite:8]{index=8}

5. Remote batch chaining is explicit.
    - Expanded remote segments chain as `seg_0 -> seg_1 -> seg_2 ...`.
    - Later pieces point to prior piece for continuity. :contentReference[oaicite:9]{index=9} :contentReference[oaicite:10]{index=10}

## Server architecture rules

1. Server is shared render engine.
    - Client remains authoritative for text, provider choice, voice choice, and DSP decisions.
    - Server renders and caches what client requests. :contentReference[oaicite:11]{index=11}

2. Conditioning cache stays separate.
    - Conditioning cache is separate from OGG/audio cache.
    - Tail-token sidecars are also separate from conditioning cache. :contentReference[oaicite:12]{index=12} :contentReference[oaicite:13]{index=13}

3. Current server OGG cache key does not include `model_version`.
    - If model artifacts change, provider cache may need manual clear until hardened. :contentReference[oaicite:14]{index=14} :contentReference[oaicite:15]{index=15}

4. Chatterbox-family server handles large text internally.
    - `chatterbox`, `chatterbox_full`, `chatterbox_multilingual` split oversized text server-side at sentence boundaries, fall back to clause boundaries, synthesize piecewise, then rejoin before returning OGG. :contentReference[oaicite:16]{index=16} :contentReference[oaicite:17]{index=17}

5. Batch join tail trim exists at batch handoff layer.
    - Non-final client-requested batch items for Chatterbox-family providers may be tail-trimmed using `RRV_CB_BATCH_JOIN_TAIL_TRIM_MS` default 100 ms.
    - This trim applies only at batch rejoin layer, not backend internal sentence splitting. :contentReference[oaicite:18]{index=18} :contentReference[oaicite:19]{index=19}

## Cache rules

### Client current cache identity

Current local audio cache key is based on:
- text
- resolved voice identity
- provider ID

Current code no longer includes DSP in local audio cache identity. :contentReference[oaicite:20]{index=20}

### Client resolved voice identity fields

Current `BuildIdentityKey()` includes:
- `VoiceId`
- `LangCode`
- `SpeechRate`
- `CfgWeight`
- `Exaggeration`
- `CfgStrength`
- `NfeStep`
- `SwaysamplingCoef`
- `VoiceInstruct`
- `CosyInstruct`
- `SynthesisSeed`
- `ChatterboxTemperature`
- `ChatterboxTopP`
- `ChatterboxRepetitionPenalty`
- `LongcatSteps`
- `LongcatCfgStrength`
- `LongcatGuidance` :contentReference[oaicite:21]{index=21}

### Server current cache identity

Current server OGG cache key is based on:
- normalized text
- provider ID
- voice identity
- lang code
- speech rate
- cfg weight
- exaggeration
- cfg strength
- nfe step
- cross fade duration
- sway sampling coef
- voice context :contentReference[oaicite:22]{index=22} :contentReference[oaicite:23]{index=23}

### Server voice identity rules

- base voice -> raw `voice_id`
- reference voice -> content hash of reference sample
- description voice -> hash of description text
- blend voice -> canonical sorted blend identity :contentReference[oaicite:24]{index=24}

### Current cache weak points

1. Client and server normalize text differently.
2. Numeric precision rules differ across fields/sides.
3. Several server controls are packed into freeform `voice_context`.
4. Stale assumptions are dangerous:
    - DSP no longer affects client audio cache
    - `model_version` no longer affects server OGG cache :contentReference[oaicite:25]{index=25}

### Cache hardening direction

These are current direction rules for upcoming work:
1. Canonicalize cache-affecting numbers before key generation.
2. Prefer structured canonical serialization over freeform identity strings.
3. Align client/server normalization where practical.
4. Keep conditioning cache and tail-token sidecars separate from main OGG cache. :contentReference[oaicite:26]{index=26}

## UI / UX rules

1. Optimize common workflow in voice editor:
    - set voice
    - preview
    - tweak
    - preview again :contentReference[oaicite:27]{index=27}

2. Keep common controls fixed, advanced controls scroll.
    - Fixed top: voice controls, language, speech rate, live preview state
    - Lower scroll area: applies/accent info, warnings, standard setup, advanced render controls, DSP, summary :contentReference[oaicite:28]{index=28}

3. User-facing labels must be meaningful.
    - Do not use misleading internal wording for user settings.

## Default preferences currently in force

Preserve unless user explicitly changes them:
- Player name handling default: `Use cache-friendly title`
- Replacement title default: `Champion`
- Append realm default: `false`
- Playback mode default: `Wait for full text`
- Phrase chunking default: `off`
- Audio Output Device appears above Speed in main settings UI :contentReference[oaicite:29]{index=29}

## Collaboration rules for future chats

1. Prefer latest patched baseline automatically unless user uploads newer source.
2. Do not assume older uploaded source is still current.
3. When summarizing work, separate:
    - implemented now
    - known fragile areas
    - planned next work
4. For design and test documentation, update current truth, not legacy assumptions.

## Current authoritative docs

- Design doc current baseline: `Design_RuneReader_Voice_v25.md` :contentReference[oaicite:30]{index=30}
- Provider test record: `Provider Tests.md` :contentReference[oaicite:31]{index=31}