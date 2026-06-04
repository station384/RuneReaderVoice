# Chatterbox Full Performance Notes

Last updated: 2026-06-04  
Server baseline after this note: v132  
Primary backend: `chatterbox_full` on CUDA RTX 3080

Purpose: preserve measured findings and decisions so future work does not retread old ground.

---

## Current accepted state

v131/v132 is the current server-side performance baseline for the Chatterbox Full path.

Current useful defaults / knobs:

```env
RRV_CB_TIMING=0
RRV_CB_TIMING_DETAIL=0
RRV_CB_FIRST_RENDER_WARMUP=1
RRV_T3_EOS_CHECK_INTERVAL=16
RRV_CB_TAIL_SIDECAR_WRITE=defer
RRV_S3GEN_COMPILE=0
```

Notes:
- `RRV_CB_TIMING=1` enables coarse timing.
- `RRV_CB_TIMING_DETAIL=1` enables detailed T3 loop timing and should normally stay off.
- `RRV_T3_EOS_CHECK_INTERVAL=1` restores exact old EOS-check behavior.
- `RRV_CB_TAIL_SIDECAR_WRITE=sync` restores blocking sidecar writes.
- `RRV_CB_TAIL_SIDECAR_WRITE=off` disables token sidecar persistence.

---

## What was investigated

### 1. Startup warmup / first render delay

Initial symptom:
- startup did heavy warmup
- first real render after server ready was still slow
- second/third/fourth render was fast regardless of sample

Finding:
- earlier warmup paths warmed parts of the model, not the exact first real render path.
- S3Gen compile warmup initially used `ref_wav/ref_sr`, while real render used `ref_dict=gen_dict`; different branch.
- no full live runtime state exists on disk. Disk cache only preserves artifacts such as TorchInductor compiled graphs, conditioning cache, and token sidecars. It cannot restore CUDA allocator state, live StaticCache/KV objects, or already-bound runtime callables.

Implemented:
- first-render mini warmup using a real sample and full `_synthesize_sync()` path.
- conditioning disk HIT logs promoted enough to verify behavior.
- warmup moved first user-facing wait into startup, which is acceptable.

Accepted result:
- first user render no longer pays the hidden one-time path cost after startup warmup.
- startup cost is a one-time boot hit and is not the current concern.

---

### 2. S3Gen compile cache

Tried / investigated:
- compiling `estimator.forward` directly because `CausalConditionalCFM` calls `.forward(...)` directly instead of module `__call__`.
- verified TorchInductor cache directory reaches worker.
- tested warmup token shapes and disk cache behavior.

Finding:
- S3Gen is not the steady-state bottleneck for current workload.
- measured steady render examples showed S3Gen around ~0.8s to ~1.5s while T3 was ~4.5s to ~7.5s+.
- `RRV_S3GEN_COMPILE=1` did not justify complexity for current path.

Accepted result:
- `RRV_S3GEN_COMPILE=0` is the current preferred setting.
- Do not re-open S3Gen compile unless new measurements show S3Gen dominates.

---

### 3. Single CPU core at 100% during render

Clarified target:
- not startup warmup
- render path pegs one CPU core while synthesizing
- server machine has many CPU cores, but one render is mostly serial

Finding:
- main render bottleneck is T3 autoregressive decode loop.
- token N+1 depends on token N, so one long chain is inherently serial.
- batch-level parallelism is mostly unsuitable for this project because requests usually arrive as two voice chains, each requiring joined continuity:
  - voice A: 1–2 joined segments
  - voice B: 1+n joined segments
- parallelizing inside a voice chain breaks continuity or adds seams.
- parallelizing voice chains risks GPU contention and memory pressure; not first-line optimization.

Accepted result:
- optimize single-chain hot path only.
- do not split chunks smaller just for CPU parallelism; longer chunks intentionally avoid start/stop overhead and audible seams.

---

## Measured render-path findings

Typical steady-state timing after warmup, from v128/v129/v131 tests:

- T3 dominates render time.
- S3Gen is secondary.
- OGG encode is small.
- sidecar writes can spike under storage load, but are no longer in critical path as of v131.

Representative timings:

```text
~340 chars / ~55 words:
T3 ~= 6.9s to 7.3s
S3Gen ~= 1.2s to 1.4s
OGG ~= 0.15s
backend async_total ~= 8.3s to 9.0s
```

T3 detail after EOS interval work:

```text
~544-560 T3 steps:
loop ~= 6.9s to 7.1s
avg_step ~= 12.6ms to 13.0ms
store_eos ~= 0.68s to 0.72s with interval=16
transformer forward ~= 5.6s to 6.0s
```

Important conclusion:
- after EOS interval optimization, transformer forward dominates.
- remaining CPU-core pressure is mostly Python dispatch around a serial GPU autoregressive loop.
- big further gains likely require model/decode architecture changes, not small Python cleanup.

---

## Implemented optimizations and results

### v128 — T3 loop cleanup and detailed timings

Implemented:
- preallocated generated token buffer instead of `torch.cat()` every token
- precomputed position embeddings
- hoisted `cfg_weight` tensor
- removed `tqdm` from hot path by default (`RRV_T3_TQDM=1` can restore)
- added detailed timing under `RRV_CB_TIMING_DETAIL=1`

Result:
- small improvement only.
- exposed true bottleneck: EOS/token check and transformer forward.

---

### v129 — EOS check interval

Implemented:
- `RRV_T3_EOS_CHECK_INTERVAL`, default initially 8, tested safely at 16.
- EOS scan now checks recent token window every N tokens instead of syncing CPU/GPU every token.
- output trims to first EOS in checked window.

Findings:
- interval 8 reduced `store_eos` and average step modestly.
- interval 16 reduced `store_eos` further and looked safe in observed logs.

Accepted setting:

```env
RRV_T3_EOS_CHECK_INTERVAL=16
```

Rollback:

```env
RRV_T3_EOS_CHECK_INTERVAL=1
```

Caution:
- higher values may generate more extra tail before EOS is detected, though trim should remove tokens after first EOS.
- keep listening tests for odd endings if increasing above 16.

---

### v130/v131 — tail-token sidecar deferral

Problem:
- `.tokens.pt` sidecar writes sometimes cost ~0.3s to ~1.0s and were visible under storage pressure.
- current system may be slamming storage from unrelated process, making sync writes noisy.

v130:
- reduced writes to one sidecar per request.
- added `RRV_CB_TAIL_SIDECAR_WRITE` modes.
- first implementation still appeared too close to critical path.

v131:
- moved deferred sidecar submit after concat + OGG encode.
- default `defer` mode returns render result without waiting for sidecar disk write.

Accepted setting:

```env
RRV_CB_TAIL_SIDECAR_WRITE=defer
```

Modes:

```env
RRV_CB_TAIL_SIDECAR_WRITE=defer   # default, background write after OGG encode
RRV_CB_TAIL_SIDECAR_WRITE=sync    # blocking write
RRV_CB_TAIL_SIDECAR_WRITE=off     # no sidecar persistence
```

Expected log order in correct behavior:

```text
Chatterbox timing: concat=... ogg_encode=...
Chatterbox timing: sidecar_write=deferred mode=defer
Chatterbox timing: async_total=...
Chatterbox timing: sidecar_write=... mode=defer
```

Conclusion:
- sidecar writes no longer block backend synth executor in default mode.
- if storage pressure remains severe, try `RRV_CB_TAIL_SIDECAR_WRITE=off` temporarily.

---

## Things not worth retreading soon

1. **More CPU cores for one render**
   - one T3 chain is autoregressive and serial.
   - CPU core at 100% is expected while Python dispatches per-token GPU work.

2. **Batch-level parallelism as primary fix**
   - current workload needs voice continuity across joined segments.
   - parallelism risks seams, broken prior-token continuity, or GPU contention.

3. **S3Gen compile as primary fix**
   - S3Gen is not current bottleneck.
   - keep `RRV_S3GEN_COMPILE=0` unless future timings change.

4. **Shorter chunking as default fix**
   - shorter chunks reduce token count per chunk but add boundary overhead, S3Gen/OGG overhead, and audible seam risk.
   - long chunks are intentional because they avoid start/stop overhead and preserve continuity.

5. **Sidecar-write sync path**
   - no reason to use `sync` during normal gameplay unless debugging persistence behavior.

---

## Future experiments worth considering

### A. Model/decode architecture changes

This is the likely path for major improvement.

Ideas:
- different Chatterbox/T3 inference implementation
- CUDA graph capture for repeated decode step, if shape stability allows it
- deeper compile of sampling/warper path, not just transformer forward
- lower precision / half precision T3 experiment
- alternate model with less autoregressive overhead
- speculative decoding if model architecture supports it

Risk:
- voice quality, EOS behavior, and continuation behavior may change.

---

### B. Try `RRV_T3_EOS_CHECK_INTERVAL=32`

Reason:
- interval 16 looked safe and reduced sync overhead.
- interval 32 might save a little more on long chunks.

Expected gain:
- small, maybe fractions of a second on medium chunks.

Risk:
- more generated tail before EOS check; should trim, but listen for odd endings.

---

### C. Sidecar persistence policy tuning

If storage is under heavy load:

```env
RRV_CB_TAIL_SIDECAR_WRITE=off
```

Tradeoff:
- loses disk-persisted prior-token sidecars for future cache/continuation reuse.
- in-memory prior tokens still work inside current active request/chain.

Could add later:
- queue coalescing by cache key
- low-priority single background writer
- drop sidecar writes when queue is backed up

---

### D. Revisit startup warmup efficiency

Current concern is render path, not startup. But if startup becomes annoying:
- old tiny T3 compile warmup may be redundant with first-render warmup.
- possible future simplification: skip tiny T3 warmup when full first-render warmup is enabled.

Do not prioritize unless boot time matters.

---

## Quick diagnostic checklist for future sessions

When reviewing new logs, first check:

1. Server version line:

```text
Starting RuneReader Voice Server v###
```

2. Confirm timing knobs:

```text
eos_interval=16
```

3. Confirm sidecar deferral:

```text
sidecar_write=deferred mode=defer
```

4. Compare T3 vs S3Gen:

```text
Chatterbox timing: tokenize=... T3=... S3Gen=... ogg_encode=...
```

5. If T3 still dominates and `tfmr_forward` dominates T3 detail, further small Python fixes will have limited payoff.

---

## Current bottom line

v131/v132 reached practical low-risk optimization limit for current Chatterbox Full implementation:

- first-render delay moved to startup warmup
- EOS CPU/GPU sync reduced
- generated token accumulation cleaned up
- sidecar disk writes removed from render critical path
- S3Gen compile de-prioritized
- remaining render cost is mostly serial T3 autoregressive decode / transformer forward

Major future gains likely require changing how the model decodes or using a different model/backend, not more surface-level warmup/cache work.

## v133 Precision Experiment Notes

Added gated render precision support for Chatterbox Full.

### Controls

```env
# Historical v133 meaning. This was later changed in v138/v139.
# Current meaning is documented in the v139/v140 sections below.
RRV_CB_PRECISION=fp32   # default
RRV_CB_PRECISION=fp16   # v133-only experiment: cast T3 + S3Gen to float16
RRV_CB_PRECISION=fp8    # recognized, but intentionally fails fast for now
```

### Cache identity requirements

Precision is part of every cache identity that can contain dtype-sensitive state:

- server OGG render cache key
- client-provided server cache-key composition path
- Chatterbox conditioning cache key (`prec:<mode>` suffix)
- prior-token voice key
- tail-token sidecar payload guard

This prevents fp32/fp16 conditionals, OGG outputs, and sidecar token state from being reused across incompatible precision modes.

### fp16 intent (historical v133 experiment)

This describes the original v133 full-fp16 experiment. Current `RRV_CB_PRECISION=fp16` now maps to `t3_fp16`, documented below. In v133, the fp16 path cast `self._model.t3` and `self._model.s3gen` to `torch.float16` after model load and before patches/warmups. Floating conditionals and `ref_dict` tensors are cast to the active runtime precision when loaded from memory/disk cache or freshly prepared. Integer token tensors remain integer/long.

Expected possible gains:

- lower VRAM pressure from T3/S3Gen weights and StaticCache dtype
- possible T3/S3Gen speed improvement on CUDA

Risks to test:

- NaN/inf on long sequences
- EOS behavior changes or generation loops
- quality/prosody changes
- S3Gen/HiFT-GAN fp16 op incompatibility

### fp8 status

`RRV_CB_PRECISION=fp8` is recognized but not implemented as a blanket cast. It raises a clear error instead of silently producing fp32 output under an fp8 cache key. Future fp8 work needs explicit quantization/autocast support for the model stack; naive `.to(torch.float8_*)` is not expected to work safely for transformers + sampling + HiFT-GAN.

### fp32 cache compatibility

The fp32/default mode intentionally keeps existing fp32 server OGG cache identities compatible where possible. Precision only adds a new cache-key component for non-fp32 modes such as fp16/fp8.

## v134 fp16 Correction — S3Gen Speaker Encoder

v133 proved that blanket half-converting all of `self._model.s3gen` is unsafe.

Observed failure with `RRV_CB_PRECISION=fp16`:

```text
Input type (torch.cuda.FloatTensor) and weight type (torch.cuda.HalfTensor) should be the same
```

Root cause:

- `s3gen.embed_ref()` calls `speaker_encoder.inference(...)`
- `speaker_encoder.inference()` internally casts speech back to `torch.float32`
- v133 had converted `speaker_encoder` weights to `torch.float16`
- reference conditioning therefore failed during `prepare_conditionals()` before synthesis could run

v134 correction:

- keep `self._model.s3gen.speaker_encoder` in `torch.float32`
- keep T3 in fp16 for the experiment
- keep the rest of S3Gen render path in fp16
- cast prepared floating conditionals/ref_dict tensors to the active render precision after `prepare_conditionals()`

Expected behavior after v134:

- conditioning extraction remains fp32-compatible
- generated render path can still test fp16 T3/S3Gen memory/speed behavior
- fp16 cache identities remain isolated from fp32

Important first v133 finding:

- VRAM dropped from roughly `3876 MiB` to `3066 MiB`
- fp16 T3 compile/warmup was much slower on first run (`~146s` vs `~43s` in fp32), likely due fresh dtype-specific compile path
- do not judge steady-state fp16 speed until v134 successfully renders and a second restart/cache-warm run is tested


## v135 Precision Correction — Monkey Patch Root Cause

v134 kept `s3gen.speaker_encoder` in fp32 to avoid v133 crash. That worked around the symptom but left part of S3Gen unconverted.

v135 fixes the root dtype mismatch by monkey-patching CAMPPlus/xvector `speaker_encoder.inference()`:

- feature extraction input is forced to fp32 so torchaudio FFT/mel work avoids ComplexHalf paths
- extracted feature tensor is then cast to the speaker encoder parameter dtype/device before `forward()`
- this allows the speaker encoder weights to be fp16 along with the rest of S3Gen

This preserves fp16 cache isolation from v133 (`prec:fp16` in conditioning/cache/tail-token identity).

Expected result: no `FloatTensor input vs HalfTensor weight` crash during `prepare_conditionals()`. Second-run timing is required before judging speed because fp16 uses separate torch.compile/cache paths.


## v136 fp16 SourceModuleHnNSF dtype patch

The v135 fp16 monkey patch fixed `speaker_encoder.inference()` but exposed the next fp16 mismatch in HiFT-GAN:

```text
expected mat1 and mat2 to have the same dtype, but got: float != c10::Half
... hifigan.py line 279: sine_merge = self.l_tanh(self.l_linear(sine_wavs))
```

Root cause: stock `SourceModuleHnNSF.forward()` lets generated sine/uv tensors remain fp32, then passes `sine_wavs` into `l_linear`. That works for fp32 weights, but fails when HiFT-GAN is converted to fp16.

v136 monkey patches `SourceModuleHnNSF.forward()` so source generation can remain stable, then casts `sine_wavs` and `uv` to `l_linear.weight` dtype/device before trainable HiFT-GAN layers consume them. This fixes the core boundary instead of reverting HiFT-GAN to fp32.

## v137 fp16 HiFT-GAN decode dtype patch

v136 fixed the source-generator `l_linear` dtype boundary but exposed the next fp16 mismatch in HiFT-GAN decode:

```text
Input type (float) and bias type (c10::Half) should be the same
... hifigan.py line 425: si = self.source_downs[i](s_stft)
```

Root cause: `decode()` builds `s_stft` through `torch.stft()`. That path produces fp32/complex64-derived tensors even when the surrounding HiFT-GAN layers are fp16. The fp32 `s_stft` was then fed directly into fp16 `source_downs` Conv1d layers.

v137 monkey patches `HiFTGenerator.decode()` so:

- STFT/ISTFT math remains fp32 for operator support and numerical stability.
- `x` is cast to `conv_pre.weight` dtype/device before trainable conv layers.
- `s_stft` is cast to each `source_downs[i].weight` dtype/device before the fp16 source-down conv.
- source residual output is cast to `x` dtype/device before fusion.
- final ISTFT receives fp32 magnitude/phase and returns fp32 waveform.

This continues the strategy: fix dtype boundaries at trainable-layer inputs instead of reverting whole submodules to fp32.

## v138 Quality-safe mixed precision baseline

v137 successfully rendered in full fp16 mode, but audio quality was not acceptable. The output was recognizable but choppy/cut off, suggesting HiFT-GAN/S3Gen full fp16 changes waveform generation behavior even when dtype mismatches are patched.

v138 changes the recommended fp16 path:

- `RRV_CB_PRECISION=fp16` now maps to effective `t3_fp16`.
- T3 runs in fp16 because it is the hot autoregressive loop and showed strong speed improvement (`~8.5 ms/token` vs `~12.7-13 ms/token` in fp32 tests).
- S3Gen / HiFT-GAN / vocoder remains fp32 to preserve audio quality.
- Conditioning cache, server OGG cache, and tail-token sidecars use the effective precision key (`t3_fp16`) so mixed-mode output does not collide with prior full-fp16 or fp32 artifacts.
- Full S3Gen fp16 is retained only as explicit `RRV_CB_PRECISION=fp16_full` for future investigation.

Expected usage:

```bash
RRV_CB_PRECISION=fp16      # quality-safe mixed mode: T3 fp16, S3Gen fp32
RRV_CB_PRECISION=t3_fp16  # same explicit mode
RRV_CB_PRECISION=fp16_full # experimental, known bad audio in v137
```

Future model-file conversion work should start from `t3_fp16` behavior, not full-S3Gen fp16, unless the vocoder quality issue is solved separately.


## v139 — Canonical precision identity fix

`RRV_CB_PRECISION=fp16` is a user-facing alias for the quality-safe mixed mode where T3 runs fp16 and S3Gen/HiFT-GAN remain fp32. Cache identity must not store this as plain `fp16`, because `fp16_full` is a different render mode with different audio characteristics.

Current canonical precision identity rules:

- `fp32` → no precision suffix, preserving existing fp32 cache compatibility.
- `fp16` → `t3_fp16` for all server OGG cache keys, client-composed cache keys, conditioning cache keys, and tail-token sidecar guards.
- `t3_fp16` → `t3_fp16`.
- `fp16_full` → `fp16_full`.
- `fp8` → reserved/unsupported; must not silently share cache with any other mode.

This removes ambiguity from logs and cache keys. A route log/cache key ending in `.t3_fp16` means quality-safe mixed precision, not full fp16.


## v140 — Precision identity comments clarified

No runtime behavior changed.  The canonical precision helper was expanded with user-facing comments so future readers understand the distinction between requested precision and effective render precision.

Important rule:

- `RRV_CB_PRECISION=fp16` is a user-friendly alias for `t3_fp16`, not full-fp16 rendering.
- `t3_fp16` means T3/token generation uses fp16 while S3Gen / HiFT-GAN / vocoder stay fp32 for quality.
- `fp16_full` is the explicit full-fp16 experiment and remains separate because it produced recognizable but choppy/bad audio.
- `fp32` keeps the historical no-suffix cache identity for backward compatibility.
- `fp8` is a reserved unsupported experiment identity, not a working runtime mode.

Cache identity must always use the canonical value, not the raw environment variable, so mixed precision, full fp16, fp32, and future fp8 experiments cannot reuse each other's OGG cache entries, conditioning cache entries, or tail-token sidecars.


## v141 — CPU-load precision move experiment

Added `RRV_CB_LOAD_STRATEGY=cpu_precision_gpu` as an opt-in loader experiment.

Purpose: reduce peak GPU memory/load pressure, not necessarily steady-state VRAM.
The stock Chatterbox loader moves each fp32 module to CUDA during `from_local()`, then our runtime precision policy converts selected modules. That can create a transient fp32-on-GPU window.

`cpu_precision_gpu` mirrors the Chatterbox loader but loads each major module on CPU, applies its final intended dtype, moves that finalized module to GPU, releases the CPU state dict, then proceeds to the next module.

Current final dtype policy remains unchanged:

- `fp32`: all modules fp32.
- `t3_fp16`: T3 fp16, S3Gen / HiFT-GAN / vocoder fp32. This is current recommended quality-safe mixed mode.
- `fp16_full`: T3 and S3Gen fp16. This remains experimental and produced bad/choppy audio in earlier testing.

Use:

```bash
RRV_CB_PRECISION=fp16          # alias for t3_fp16
RRV_CB_LOAD_STRATEGY=cpu_precision_gpu
```

Expected logs:

```text
Chatterbox load strategy: cpu_precision_gpu starting — device=cuda t3_dtype=torch.float16 s3gen_dtype=torch.float32
Chatterbox load strategy: cpu_precision_gpu complete
Chatterbox precision: t3_fp16 applied during CPU-load strategy
```

Compare against direct loader for:

- startup peak VRAM, via nvidia-smi while loading
- final `vram_used_mib` in worker ready log
- startup time
- steady render timing

If final `vram_used_mib` is the same, experiment still may be useful by reducing peak load spikes. If startup time grows too much, direct loader may remain better for normal use.


## v142 — Runtime profile and VRAM checkpoints

No render behavior changed.  Added diagnostics so future work starts from measured facts instead of reconstructing from chat history.

New startup diagnostics:

- `Chatterbox VRAM checkpoint: ...`
  - logged around model-load milestones and warmup milestones
  - values are PyTorch allocator stats:
    - `allocated` = live tensors tracked by PyTorch
    - `reserved` = CUDA caching allocator pool held by PyTorch
    - `peak_allocated` = peak live allocation since the last reset or process start
  - checkpoints are intended to compare `direct` vs `cpu_precision_gpu` load strategies and catch peak-load regressions.

- `Chatterbox runtime profile: ...`
  - one summary line after warmups
  - includes canonical precision, requested precision, load strategy, EOS interval, sidecar mode, StaticCache state, T3 dtype, and S3Gen dtype.

Observed best profile from v141 testing:

```text
RRV_CB_PRECISION=fp16          # canonical/effective precision: t3_fp16
RRV_CB_LOAD_STRATEGY=cpu_precision_gpu
RRV_T3_EOS_CHECK_INTERVAL=16
RRV_CB_TAIL_SIDECAR_WRITE=defer
```

Observed results on RTX 3080 test host:

- fp32 baseline final worker VRAM was about `3876 MiB`.
- `t3_fp16` with direct loader was about `3162 MiB`.
- `t3_fp16 + cpu_precision_gpu` was about `2536 MiB`.
- That is about `1340 MiB` less than the fp32 baseline.
- T3 steady-state token loop stayed near `8.5 ms/token` in `t3_fp16` mode.
- Audio quality was good with S3Gen / HiFT-GAN kept fp32.
- Full `fp16_full` still produced bad/choppy audio and remains experimental only.

Future precision work should start from the v141/v142 profile above unless a newer measured profile supersedes it.


## v143 T3 compile mode experiment

Added `RRV_T3_COMPILE_MODE` for testing whether PyTorch `reduce-overhead` helps now that `t3_fp16` moved the bottleneck away from pure GPU math toward CPU/kernel-launch overhead.

Supported values:

```env
RRV_T3_COMPILE_MODE=default          # proven baseline
RRV_T3_COMPILE_MODE=reduce-overhead  # experiment: may use CUDA graphs / lower launch overhead
RRV_T3_COMPILE_MODE=max-autotune     # exposed only for experiments; may be slower/heavier
```

Current recommended test profile:

```env
RRV_CB_PRECISION=fp16                # canonicalizes to t3_fp16
RRV_CB_LOAD_STRATEGY=cpu_precision_gpu
RRV_T3_EOS_CHECK_INTERVAL=16
RRV_CB_TAIL_SIDECAR_WRITE=defer
RRV_T3_COMPILE_MODE=reduce-overhead
RRV_CB_TIMING=1
RRV_CB_TIMING_DETAIL=1
```

Compare against `RRV_T3_COMPILE_MODE=default` using:

- `Chatterbox T3 timing: total=... loop=... avg_step=...`
- `Chatterbox T3 detail: tfmr_forward=...`
- VRAM checkpoints and runtime profile line
- startup/warmup time
- audio correctness

Result update: `reduce-overhead` has now been tested and rejected for this decode path. It can increase VRAM and compile/warmup time, and with this T3 per-token loop it failed under CUDA graph handling. Keep `default` as the supported production path.

## v144: StaticCache kill-switch for throwaway compile-mode experiments

Added a real `RRV_T3_STATIC_CACHE` switch. Before v144, `RRV_T3_STATIC_CACHE=0` did nothing; only `RRV_T3_STATIC_CACHE_LEN` existed. StaticCache was enabled automatically when `transformers.cache_utils.StaticCache` imported successfully.

Current behavior:

```env
RRV_T3_STATIC_CACHE=1    # default, proven path
RRV_T3_STATIC_CACHE=0    # disables StaticCache; experimental DynamicCache/standard cache path
```

Reason: `RRV_T3_COMPILE_MODE=reduce-overhead` failed because CUDA graph capture disliked StaticCache's in-place KV cache mutation (`index_copy_` inside `past_key_values.update`). Disabling StaticCache may let reduce-overhead run, but it may also be slower or less stable. Treat this as a throwaway test only.

Recommended production path remains:

```env
RRV_CB_PRECISION=fp16              # canonicalizes to t3_fp16
RRV_CB_LOAD_STRATEGY=cpu_precision_gpu
RRV_T3_COMPILE_MODE=default
RRV_T3_STATIC_CACHE=1
RRV_T3_EOS_CHECK_INTERVAL=16
RRV_CB_TAIL_SIDECAR_WRITE=defer
```

Throwaway test profile:

```env
RRV_T3_COMPILE_MODE=reduce-overhead
RRV_T3_STATIC_CACHE=0
```

Pass/fail criteria: backend must not crash; compare `avg_step`, `tfmr_forward`, T3 total, VRAM checkpoints, and audio quality against default compile + StaticCache.


## v145: reduce-overhead + StaticCache-off experiment result

Result: rejected. Worth testing, but not a viable path right now.

Measured throwaway profile:

```env
RRV_CB_PRECISION=fp16              # canonicalizes to t3_fp16
RRV_CB_LOAD_STRATEGY=cpu_precision_gpu
RRV_T3_COMPILE_MODE=reduce-overhead
RRV_T3_STATIC_CACHE=0
RRV_T3_EOS_CHECK_INTERVAL=32       # test run used 32; production recommendation remains 16
RRV_CB_TAIL_SIDECAR_WRITE=defer
```

What happened:

- v144 correctly disabled StaticCache. Log showed `static_cache=False static_cache_env=0 compile_mode=reduce-overhead`.
- `reduce-overhead` still used CUDA graph machinery and failed during warmup/use.
- First warning during warmup: CUDA graph output was overwritten by a subsequent run; PyTorch suggested `torch.compiler.cudagraph_mark_step_begin()` before each model invocation.
- Real synthesis then failed inside TorchInductor CUDA graph trees with `AssertionError` in `dealloc_current_path_weakrefs()`.
- This means StaticCache mutation was not the only blocker. The current T3 per-token compiled call pattern is not compatible with `reduce-overhead` as-is.

Conclusion:

```env
RRV_T3_COMPILE_MODE=default
RRV_T3_STATIC_CACHE=1
```

Keep that for production. Do not retry `reduce-overhead` unless doing a deeper CUDA graph / decode-loop rewrite experiment. If revisiting later, likely required work includes explicit graph step markers or a graph-safe custom decode wrapper; treat that as model-runtime surgery, not a simple knob change.

Updated best-known profile:

```env
RRV_CB_PRECISION=fp16              # canonicalizes to t3_fp16
RRV_CB_LOAD_STRATEGY=cpu_precision_gpu
RRV_T3_COMPILE_MODE=default
RRV_T3_STATIC_CACHE=1
RRV_T3_EOS_CHECK_INTERVAL=16
RRV_CB_TAIL_SIDECAR_WRITE=defer
```

Keep the `RRV_T3_STATIC_CACHE` switch for future experiments, but default-on remains the proven fast/stable path.

## v146/v147: T3/S3Gen pipeline overlap experiment — tested/rejected for production

v146 added opt-in S3Gen pipelining for multi-chunk requests:

```env
RRV_CB_PIPELINE_S3GEN=1
RRV_CB_PIPELINE_S3GEN_WORKERS=1   # default/safest
```

Purpose: after `t3_fp16`, GPU utilization during render can sit around ~60% because the T3 token loop is partly CPU/launch-bound. Multi-chunk requests have a natural pipeline opportunity: chunk N+1 only needs chunk N's generated T3 tokens for continuity, not chunk N's decoded audio. Therefore S3Gen for chunk N can run in a background worker while T3 generates chunk N+1.

Pipeline behavior:

```text
T3 chunk 1 -> submit S3Gen chunk 1
T3 chunk 2 -> submit S3Gen chunk 2 while S3Gen chunk 1 may still run
...
collect S3Gen futures in original order
concat audio in original order
```

Implementation worked technically:

- T3 remained serial per voice chain.
- Prior-token continuity updated immediately after each chunk's T3 output.
- S3Gen futures collected in original chunk order.
- Logs showed overlap working: most `pipeline_s3gen_collect` waits were `0.000s`; only final chunk usually waited ~0.4s.

Test results:

```text
Default chunking 380/480:
  342 chars -> 1 chunk
  pipeline_s3gen=False because only one chunk
  synth_time ~= 6.38s
  rtf ~= 3.42x

Tiny chunking 50/100:
  342 chars -> 8 chunks
  pipeline_s3gen=True
  overlap worked, but synth_time ~= 9.27s
  rtf ~= 2.69x
```

Conclusion:

- Pipeline overlap is not worth the route for current Chatterbox path.
- Smaller chunks create too much fixed overhead: repeated T3 setup, repeated S3Gen calls, executor scheduling, more concat/encode work, and more EOS/initial-forward overhead.
- T3 `avg_step` also worsened on later tiny chunks, often ~10–14ms instead of the stable ~8.5ms seen on larger chunks.
- User observed more voice drift with tiny/many chunks. Voice quality/continuity loss outweighs any hardware utilization gain.

Keep the knob for future experiments, but default-off is correct:

```env
RRV_CB_PIPELINE_S3GEN=0
RRV_CB_CHUNK_TARGET_CHARS=380
RRV_CB_CHUNK_HARD_CHARS=480
```

Do not retest the tiny-chunk pipeline path unless the model/runtime changes substantially. A future model with true streaming audio decode or lower per-chunk overhead might make this worth revisiting, but current Chatterbox does not.

Best-known production profile remains:

```env
RRV_CB_PRECISION=fp16              # canonicalizes to t3_fp16
RRV_CB_LOAD_STRATEGY=cpu_precision_gpu
RRV_T3_COMPILE_MODE=default
RRV_T3_STATIC_CACHE=1
RRV_T3_EOS_CHECK_INTERVAL=16
RRV_CB_TAIL_SIDECAR_WRITE=defer
RRV_CB_PIPELINE_S3GEN=0
RRV_CB_CHUNK_TARGET_CHARS=380
RRV_CB_CHUNK_HARD_CHARS=480
```
