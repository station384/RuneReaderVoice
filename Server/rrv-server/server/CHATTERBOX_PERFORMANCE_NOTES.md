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
