"""
LongCat latent-carry continuation experiment with speed control.

WORKING CONTINUATION PIPELINE
=============================

Goal
----
Generate long-form speech in multiple chunks while keeping the boundaries
between chunks sounding natural and continuous.

Real continuation fix
---------------------
1. Monkey patch AudioDiTModel.forward() so it can accept prompt_latent directly.
2. Generate one chunk and keep the generated latent output.
3. Use forced alignment on the KNOWN generated chunk text to find where the
   last N carry words begin in the waveform.
4. Map that exact waveform boundary back into generated latent frames.
5. Carry forward:
   - original reference latent (identity anchor)
   - exact matched latent tail from the previous chunk (continuation)
6. Carry forward matching prompt_text:
   - original reference text
   - exact last N carry words

Speed control
-------------
LongCat does not expose a native speech-rate parameter.
The practical model-level speed control is to scale the GENERATED duration
budget only, not the prompt portion.

- speed = 1.0  -> normal
- speed > 1.0  -> faster
- speed < 1.0  -> slower

Implementation:
- compute base generated frames from text
- divide generated frames by speed
- keep prompt frames unchanged
- total duration = prompt frames + scaled generated frames

Outputs
-------
- longcat_single.ogg
- longcat_chunked_latent.ogg
"""

import os
import re
import sys
from pathlib import Path

import numpy as np
import soundfile as sf
import torch
import torch.nn.functional as F
import torchaudio
from torchaudio.functional import forced_align, merge_tokens

# Make sure local LongCat source is importable before importing audiodit.
sys.path.insert(0, "/home/mike/rrvserver/rrv-longcat/longcat-src")
os.environ.setdefault("TORCH_HOME", "/home/mike/rrvserver/data/models")

from audiodit import AudioDiTModel
import audiodit.modeling_audiodit as md
from transformers import AutoTokenizer
from utils import normalize_text, load_audio


# ── Wav2Vec2 forced-alignment bundle ───────────────────────────────────────────

_ALIGN_BUNDLE_NAME = "WAV2VEC2_ASR_BASE_960H"
_align_bundle = None
_align_model = None
_align_labels = None
_align_sr = None


def _get_aligner(device: torch.device):
    """
    Lazy-load wav2vec2 alignment model.

    We use this only to align KNOWN generated text to generated waveform audio.
    This is alignment, not recognition. The text is already known.
    """
    global _align_bundle, _align_model, _align_labels, _align_sr
    if _align_model is None:
        _align_bundle = torchaudio.pipelines.__dict__[_ALIGN_BUNDLE_NAME]
        _align_model = _align_bundle.get_model().to(device)
        _align_model.eval()
        _align_labels = _align_bundle.get_labels()
        _align_sr = _align_bundle.sample_rate
    return _align_model, _align_labels, _align_sr


def _text_to_tokens(text: str, labels: tuple) -> list[int]:
    """
    Convert known text into wav2vec2 token ids for forced alignment.

    Blank index 0 must NOT appear in targets.
    """
    label_to_idx = {c: i for i, c in enumerate(labels)}
    word_sep_idx = label_to_idx.get("|")
    tokens = []

    words = text.upper().split()
    for w_idx, word in enumerate(words):
        for ch in word:
            idx = label_to_idx.get(ch)
            if idx is not None and idx != 0:
                tokens.append(idx)
        if word_sep_idx is not None and w_idx < len(words) - 1:
            tokens.append(word_sep_idx)

    return tokens


def _find_exact_tail_start_sample(
    audio_np: np.ndarray,
    text: str,
    tail_word_count: int,
    device: torch.device,
    sr: int,
    fallback_seconds: float = 1.0,
) -> int:
    """
    Find the exact sample index where the last `tail_word_count` words begin.

    This is the critical alignment step:
    - the generated text is already known
    - we align that known text to the generated waveform
    - then we find the exact start of the carried tail words

    That exact boundary is used to keep carried latent/audio/text matched.

    If alignment fails, we fall back to a short time-based tail from the end.
    """
    fallback = max(0, len(audio_np) - int(fallback_seconds * sr))

    words = text.split()
    if not words:
        return 0
    if len(words) <= tail_word_count:
        return 0

    tail_start_word = len(words) - tail_word_count

    try:
        align_model, align_labels, align_sr = _get_aligner(device)

        waveform = torch.from_numpy(audio_np).unsqueeze(0).float()
        if sr != align_sr:
            waveform = torchaudio.functional.resample(waveform, sr, align_sr)

        with torch.no_grad():
            emissions, _ = align_model(waveform.to(device))
            log_probs = torch.log_softmax(emissions, dim=-1)

        tokens = _text_to_tokens(text, align_labels)
        if not tokens:
            return fallback

        targets = torch.tensor([tokens], dtype=torch.int32, device=device)
        paths, scores = forced_align(log_probs, targets, blank=0)
        token_spans = merge_tokens(paths[0], scores[0], blank=0)

        label_list = list(align_labels)
        sep_idx = label_list.index("|") if "|" in label_list else None

        word_span_starts = []
        current_word_start = None

        for span in token_spans:
            if span.token == 0:
                continue
            if span.token == sep_idx:
                current_word_start = None
            else:
                if current_word_start is None:
                    current_word_start = span.start
                    word_span_starts.append(current_word_start)

        if tail_start_word >= len(word_span_starts):
            print(
                f"  [align] only found {len(word_span_starts)} words, "
                f"need {tail_start_word + 1}, using fallback"
            )
            return fallback

        tail_frame = word_span_starts[tail_start_word]
        frames_total = log_probs.shape[1]
        samples_total = waveform.shape[-1]

        sample_at_align_sr = int(tail_frame * samples_total / frames_total)
        tail_sample = int(sample_at_align_sr * sr / align_sr)
        tail_sample = max(0, min(tail_sample, len(audio_np) - 1))

        print(
            f"  [align] last {tail_word_count} words begin at "
            f"{tail_sample / sr:.2f}s from start "
            f"({(len(audio_np) - tail_sample) / sr:.2f}s remaining)"
        )
        return tail_sample

    except Exception as e:
        print(f"  [align] failed ({e}), using fallback")
        return fallback


# ── Monkey patch: add prompt_latent support to AudioDiTModel.forward() ────────

def patch_audiodit_for_prompt_latent():
    """
    Monkey patch AudioDiTModel.forward() so it can accept prompt_latent directly.

    Why this matters:
    - The stock public path expects prompt_audio and re-encodes every time.
    - For real continuation, we want to carry generated LATENT tail frames
      directly into the next chunk.
    - The model already uses prompt_latent internally after encoding prompt_audio.
      This patch simply exposes that latent path directly.
    """
    if getattr(AudioDiTModel, "_rrv_prompt_latent_patch_applied", False):
        return

    @torch.no_grad()
    def patched_forward(
        self,
        input_ids: torch.LongTensor | None = None,
        attention_mask: torch.LongTensor | None = None,
        text_embedding: torch.FloatTensor | None = None,
        prompt_audio: torch.FloatTensor | None = None,
        prompt_latent: torch.FloatTensor | None = None,
        duration: int | None = None,
        steps: int = 16,
        cfg_strength: float = 4.0,
        guidance_method: str = "cfg",
        return_dict: bool = True,
    ):
        device = self.device
        sr = self.config.sampling_rate
        max_duration_frames = int(self.config.max_wav_duration * sr // self.config.latent_hop)
        repa_layer = self.config.repa_dit_layer

        # ── text encoding ─────────────────────────────────────────────
        if text_embedding is not None:
            text_condition = text_embedding.to(device, torch.float32)
            if attention_mask is not None:
                text_condition_len = attention_mask.sum(dim=1).to(device)
            else:
                text_condition_len = torch.full(
                    (text_condition.shape[0],), text_condition.shape[1], device=device
                )
        else:
            text_condition = self.encode_text(
                input_ids.to(device), attention_mask.to(device)
            )
            text_condition_len = attention_mask.sum(dim=1).to(device)

        batch = text_condition.shape[0]

        # ── prompt handling ───────────────────────────────────────────
        prompt_provided = False
        if prompt_latent is not None:
            prompt_latent = prompt_latent.to(device, torch.float32)
            prompt_dur = prompt_latent.shape[1]
            prompt_provided = True
        elif prompt_audio is not None:
            prompt_latent, prompt_dur = self.encode_prompt_audio(prompt_audio)
            prompt_provided = True
        else:
            prompt_latent = torch.empty(batch, 0, self.config.latent_dim, device=device)
            prompt_dur = 0

        # ── duration ──────────────────────────────────────────────────
        if duration is None:
            duration = max_duration_frames
        total_duration = min(duration, max_duration_frames)

        # ── masks and conditioning ────────────────────────────────────
        duration_tensor = torch.full((batch,), total_duration, device=device, dtype=torch.long)
        max_dur = total_duration
        mask = md.lens_to_mask(duration_tensor)
        text_mask = md.lens_to_mask(text_condition_len, length=text_condition.shape[1])

        neg_text = torch.zeros_like(text_condition)
        neg_text_len = text_condition_len

        latent_len = prompt_dur
        if prompt_provided:
            gen_len = max_dur - latent_len
            if gen_len < 0:
                raise ValueError(
                    f"prompt_latent/prompt_audio length ({latent_len} frames) "
                    f"exceeds total duration ({max_dur} frames)"
                )
            latent_cond = F.pad(prompt_latent, (0, 0, 0, gen_len))
            empty_latent_cond = torch.zeros_like(latent_cond)
        else:
            latent_cond = torch.zeros(batch, max_dur, self.config.latent_dim, device=device)
            empty_latent_cond = latent_cond

        if guidance_method == "apg":
            apg_buffer = md._MomentumBuffer(momentum=-0.3)

        def fn(t, x):
            x[:, :latent_len] = prompt_noise * (1 - t) + latent_cond[:, :latent_len] * t
            output = self.transformer(
                x=x,
                text=text_condition,
                text_len=text_condition_len,
                time=t,
                mask=mask,
                cond_mask=text_mask,
                return_ith_layer=repa_layer,
                latent_cond=latent_cond,
            )
            pred = output["last_hidden_state"]

            if cfg_strength < 1e-5:
                return pred

            x[:, :latent_len] = 0
            null_output = self.transformer(
                x=x,
                text=neg_text,
                text_len=neg_text_len,
                time=t,
                mask=mask,
                cond_mask=text_mask,
                return_ith_layer=repa_layer,
                latent_cond=empty_latent_cond,
            )
            null_pred = null_output["last_hidden_state"]

            if guidance_method == "cfg":
                return pred + (pred - null_pred) * cfg_strength

            # APG
            x_s = x[:, latent_len:]
            pred_s = pred[:, latent_len:]
            null_s = null_pred[:, latent_len:]
            pred_sample = x_s + (1 - t) * pred_s
            null_sample = x_s + (1 - t) * null_s
            out = md._apg_forward(
                pred_sample,
                null_sample,
                cfg_strength,
                apg_buffer,
                eta=0.5,
                norm_threshold=0.0,
                dims=[-1, -2],
            )
            out = (out - x_s) / (1 - t)
            return F.pad(out, (0, 0, latent_len, 0), value=0.0)

        # ── initial noise ─────────────────────────────────────────────
        y0 = []
        for dur in duration_tensor:
            noise = torch.randn(dur.item(), self.config.latent_dim, device=device)
            y0.append(noise)
        y0 = md.pad_sequence(y0, padding_value=0, batch_first=True)

        # ── ODE solve ─────────────────────────────────────────────────
        t = torch.linspace(0, 1, steps, device=device)
        prompt_noise = y0[:, :latent_len].clone()
        trajectory = md.odeint_euler(fn, y0, t)
        sampled = trajectory[-1]

        # ── decode ────────────────────────────────────────────────────
        pred_latent = sampled
        if prompt_provided:
            pred_latent = pred_latent[:, prompt_dur:]

        pred_latent = pred_latent.permute(0, 2, 1).float()
        waveform = self.vae.decode(pred_latent).squeeze(1)

        if not return_dict:
            return (waveform, pred_latent)

        return md.AudioDiTOutput(waveform=waveform, latent=pred_latent)

    AudioDiTModel.forward = patched_forward
    AudioDiTModel._rrv_prompt_latent_patch_applied = True


# ── Runner ─────────────────────────────────────────────────────────────────────

class LongCatRunner:
    def __init__(self, model_dir: str, tokenizer_dir: str | None = None, device: str = "cuda"):
        patch_audiodit_for_prompt_latent()

        self.device = torch.device(device if torch.cuda.is_available() else "cpu")

        self.model = AudioDiTModel.from_pretrained(model_dir).to(self.device)
        self.model.vae.to_half()
        self.model.eval()

        tok_path = tokenizer_dir or self.model.config.text_encoder_model
        self.tokenizer = AutoTokenizer.from_pretrained(tok_path)

        self.sr = self.model.config.sampling_rate
        self.full_hop = self.model.config.latent_hop
        self.max_duration = self.model.config.max_wav_duration

    # ── Small helpers ────────────────────────────────────────────────

    def _count_words(self, text: str) -> int:
        return len(re.findall(r"\b[\w']+\b", text))

    def _split_words_fixed(self, text: str, chunk_word_count: int) -> list[list[str]]:
        words = text.split()
        return [
            words[i:i + chunk_word_count]
            for i in range(0, len(words), chunk_word_count)
        ]

    def _load_prompt_audio(self, prompt_audio_path: str) -> torch.Tensor:
        wav = load_audio(prompt_audio_path, self.sr).unsqueeze(0)
        return wav.to(self.device)

    def _encode_prompt_latent(self, prompt_audio_path: str) -> tuple[torch.Tensor, int]:
        """
        Encode original reference prompt once into latent space.
        """
        prompt_wav = self._load_prompt_audio(prompt_audio_path)
        with torch.no_grad():
            prompt_latent, prompt_dur = self.model.encode_prompt_audio(prompt_wav)
        return prompt_latent.to(self.device), int(prompt_dur)

    def _run_model(
        self,
        full_text: str,
        duration_frames: int,
        steps: int,
        cfg_strength: float,
        guidance_method: str,
        prompt_latent: torch.Tensor | None = None,
    ) -> tuple[np.ndarray, torch.Tensor]:
        inputs = self.tokenizer([full_text], padding="longest", return_tensors="pt")

        with torch.no_grad():
            waveform, gen_latent = self.model(
                input_ids=inputs.input_ids.to(self.device),
                attention_mask=inputs.attention_mask.to(self.device),
                prompt_latent=prompt_latent.to(self.device) if prompt_latent is not None else None,
                duration=duration_frames,
                steps=steps,
                cfg_strength=cfg_strength,
                guidance_method=guidance_method,
                return_dict=False,
            )

        waveform_np = waveform.squeeze().detach().cpu().numpy()
        return waveform_np, gen_latent.detach()

    def _trim_generated_tail(
        self,
        wav: np.ndarray,
        gen_latent: torch.Tensor,
        trim_tail_ms: float,
    ) -> tuple[np.ndarray, torch.Tensor]:
        """
        Trim the fixed decoder/model tail from waveform and generated latent together.

        Why:
        The model tends to leave a short extra tail after useful speech.
        We trim waveform and latent together so they stay aligned.
        """
        if trim_tail_ms <= 0 or len(wav) == 0:
            return wav, gen_latent

        trim_samples = int(trim_tail_ms * self.sr / 1000.0)
        if trim_samples <= 0 or trim_samples >= len(wav):
            return wav, gen_latent

        gen_frames = gen_latent.shape[-1]
        trim_frames = int(round(trim_samples * gen_frames / len(wav)))
        trim_frames = max(0, min(trim_frames, gen_frames - 1))

        trimmed_wav = wav[:-trim_samples]
        trimmed_latent = gen_latent[:, :, :-trim_frames] if trim_frames > 0 else gen_latent

        print(
            f"  trimmed fixed tail {trim_tail_ms:.0f}ms "
            f"({trim_samples} samples, {trim_frames} latent frames)"
        )
        return trimmed_wav, trimmed_latent

    def _sample_to_latent_frame(
        self,
        sample_index: int,
        total_samples: int,
        total_frames: int,
    ) -> int:
        """
        Convert a waveform sample boundary into the matching generated latent frame.
        """
        if total_samples <= 0 or total_frames <= 0:
            return 0
        frame_index = int(round(sample_index * total_frames / total_samples))
        return max(0, min(frame_index, total_frames - 1))

    # ── Public synthesis API ─────────────────────────────────────────

    def synthesize(
        self,
        text: str,
        prompt_text: str,
        prompt_audio_path: str | None = None,
        prompt_latent: torch.Tensor | None = None,
        steps: int = 16,
        cfg_strength: float = 4.0,
        guidance_method: str = "apg",
        seed: int = 1024,
        speed: float = 1.0,
    ) -> tuple[np.ndarray, torch.Tensor]:
        """
        Synthesize one chunk.

        Returns:
        - generated waveform
        - generated latent only (prompt region already removed by patched forward)

        speed:
        - 1.0 = normal
        - >1.0 = faster (fewer generated frames)
        - <1.0 = slower (more generated frames)

        Important:
        Speed scales ONLY the generated portion of the budget.
        Prompt/carry latent length is left unchanged.
        """
        if speed <= 0:
            raise ValueError("speed must be > 0")

        torch.manual_seed(seed)
        if torch.cuda.is_available():
            torch.cuda.manual_seed(seed)

        text = normalize_text(text)
        prompt_text = normalize_text(prompt_text)
        full_text = f"{prompt_text} {text}"

        if prompt_latent is None:
            if prompt_audio_path is None:
                raise ValueError("Either prompt_audio_path or prompt_latent is required")
            prompt_latent_local, prompt_dur = self._encode_prompt_latent(prompt_audio_path)
        else:
            prompt_latent_local = prompt_latent.to(self.device)
            prompt_dur = prompt_latent_local.shape[1]

        # Simple base estimate for generated speech.
        gen_words = self._count_words(text)
        base_dur_sec = max(gen_words * 0.50, 0.25)

        # Speed scales ONLY the generated portion.
        base_gen_frames = max(1, int(base_dur_sec * self.sr // self.full_hop))
        scaled_gen_frames = max(1, int(round(base_gen_frames / speed)))

        max_frames = int(self.max_duration * self.sr // self.full_hop)
        duration_frames = min(prompt_dur + scaled_gen_frames, max_frames)

        prompt_time = prompt_dur * self.full_hop / self.sr
        prompt_words = self._count_words(prompt_text)

        print(
            f"  prompt: {prompt_words} total prompt words "
            f"| prompt_audio_equiv={prompt_time:.2f}s "
            f"| gen={gen_words} words "
            f"| base_gen_frames={base_gen_frames} "
            f"| speed={speed:.2f} "
            f"| scaled_gen_frames={scaled_gen_frames}"
        )
        print(
            f"  prompt_dur={prompt_dur} frames ({prompt_time:.2f}s) "
            f"| total_frames={duration_frames}"
        )

        waveform, gen_latent = self._run_model(
            full_text=full_text,
            duration_frames=duration_frames,
            steps=steps,
            cfg_strength=cfg_strength,
            guidance_method=guidance_method,
            prompt_latent=prompt_latent_local,
        )

        print(f"  output: {len(waveform) / self.sr:.2f}s | gen_latent_frames={gen_latent.shape[-1]}")
        return waveform, gen_latent

    def synthesize_chunked(
        self,
        full_text: str,
        prompt_audio_path: str,
        prompt_text: str,
        chunk_word_count: int = 20,
        carry_tail_words: int = 3,
        trim_tail_ms: float = 180.0,
        carry_extra_ms: float = 120.0,
        steps: int = 16,
        cfg_strength: float = 4.0,
        guidance_method: str = "apg",
        seed: int = 1024,
        speed: float = 1.0,
    ) -> np.ndarray:
        """
        Synthesize long text by fixed-size word chunks with latent carryover.

        Continuation pipeline:
        1. Encode original reference once into latent space.
        2. Generate a chunk.
        3. Trim the fixed model tail from waveform+latent together.
        4. Force-align the KNOWN chunk text to the generated waveform.
        5. Find where the last N carry words begin.
        6. Convert that exact waveform boundary into latent-frame boundary.
        7. Carry forward:
           - original reference latent
           - exact generated latent tail from that boundary
        8. Carry forward matching prompt_text:
           - original reference text
           - exact last N carry words

        speed:
        - passed through to each chunk
        - scales only generated frames, not prompt/carry latent frames
        """
        orig_ref_text = normalize_text(prompt_text)
        orig_prompt_latent, orig_prompt_dur = self._encode_prompt_latent(prompt_audio_path)

        chunks = self._split_words_fixed(full_text, chunk_word_count)
        combined = []

        current_prompt_latent = orig_prompt_latent
        current_prompt_text = orig_ref_text

        print("=== Chunked synthesis test (latent carry) ===")

        for i, chunk_words in enumerate(chunks, start=1):
            chunk_text = " ".join(chunk_words)

            print("")
            print(f"===== chunk {i} =====")
            print(
                f"  anchor_prompt: {orig_prompt_dur * self.full_hop / self.sr:.1f}s "
                f"| chunk_words={len(chunk_words)}"
            )
            print(f"  chunk_text: {chunk_text}")

            wav, gen_latent = self.synthesize(
                text=chunk_text,
                prompt_text=current_prompt_text,
                prompt_latent=current_prompt_latent,
                steps=steps,
                cfg_strength=cfg_strength,
                guidance_method=guidance_method,
                seed=seed,
                speed=speed,
            )

            wav, gen_latent = self._trim_generated_tail(
                wav,
                gen_latent,
                trim_tail_ms=trim_tail_ms,
            )

            combined.append(wav)

            if i >= len(chunks):
                break

            # Find exact start of last N words in waveform.
            tail_start_sample = _find_exact_tail_start_sample(
                audio_np=wav,
                text=chunk_text,
                tail_word_count=carry_tail_words,
                device=self.device,
                sr=self.sr,
                fallback_seconds=1.0,
            )

            # Preserve a little extra post-word tail so the next chunk does not
            # feel too tight at the seam.
            extra_samples = int(carry_extra_ms * self.sr / 1000.0)

            tail_start_sample = max(0, min(tail_start_sample, len(wav) - 1))
            tail_end_sample = len(wav)

            # Keep everything from the aligned start through the actual chunk end.
            bridge_audio = wav[tail_start_sample:tail_end_sample]

            # Map waveform boundary back to generated latent frames.
            gen_frames = gen_latent.shape[-1]
            tail_start_frame = self._sample_to_latent_frame(
                sample_index=tail_start_sample,
                total_samples=len(wav),
                total_frames=gen_frames,
            )

            # Carry generated latent tail starting at the exact aligned boundary.
            # gen_latent shape: (batch, latent_dim, frames)
            tail_latent = gen_latent[:, :, tail_start_frame:].permute(0, 2, 1).contiguous()

            # Carried text must match the carried tail words exactly.
            bridge_text = " ".join(chunk_words[-carry_tail_words:])

            # Build next prompt latent:
            # stable original reference + exact carried generated tail
            current_prompt_latent = torch.cat([orig_prompt_latent, tail_latent], dim=1)
            current_prompt_text = f"{orig_ref_text} {bridge_text}"

            print(
                f"  carry: audio_tail={len(bridge_audio) / self.sr:.2f}s "
                f"| latent_tail_frames={tail_latent.shape[1]} "
                f"| bridge_text='{bridge_text}' "
                f"| extra_pause_bias={carry_extra_ms:.0f}ms"
            )
            print(
                f"  next prompt latent: {current_prompt_latent.shape[1]} frames "
                f"({current_prompt_latent.shape[1] * self.full_hop / self.sr:.2f}s)"
            )

        if not combined:
            return np.array([], dtype=np.float32)

        return np.concatenate(combined)


if __name__ == "__main__":
    MODEL_DIR = "/home/mike/rrvserver/data/models/longcat/1b"
    TOKENIZER_DIR = "/home/mike/rrvserver/data/models/longcat/umt5-base"
    SAMPLE_PATH = "/home/mike/rrvserver/data/samples/M_Narrator/M_Narrator-f5.wav"
    SAMPLE_TEXT = Path(SAMPLE_PATH).with_suffix(".ref.txt").read_text().strip()

    runner = LongCatRunner(MODEL_DIR, tokenizer_dir=TOKENIZER_DIR)

    test_text = (
        "I have watched many adventurers pass through these lands and most never return,"
        "If you dare to venture on you may perish in the darkness beyond the ridge, "
        "The old roads are no longer safe and the bridges have long since fallen into ruin, "
        "Turn back now while you still have the chance, or face whatever waits in the shadow."
    )

    print("=== Single synthesis test ===")
    wav_single, _ = runner.synthesize(
        text="I have watched many adventurers pass through these lands and most never return.",
        prompt_audio_path=SAMPLE_PATH,
        prompt_text=SAMPLE_TEXT,
        steps=16,
        cfg_strength=4.0,
        guidance_method="apg",
        seed=1024,
        speed=1.0,
    )
    sf.write("./longcat_single.ogg", wav_single, runner.sr)
    print(f"wrote ./longcat_single.ogg ({len(wav_single) / runner.sr:.2f}s)")

    print("")
    wav_chunked = runner.synthesize_chunked(
        full_text=test_text,
        prompt_audio_path=SAMPLE_PATH,
        prompt_text=SAMPLE_TEXT,
        chunk_word_count=20,
        carry_tail_words=3,
        trim_tail_ms=180.0,
        carry_extra_ms=120.0,
        steps=16,
        cfg_strength=4.6,
        guidance_method="apg",
        seed=2048,
        speed=2.0, #1.0 .. #2.0 words by manipualting the budget.  Somewhat works
    )
    sf.write("./longcat_chunked_latent.ogg", wav_chunked, runner.sr)
    print(f"wrote ./longcat_chunked_latent.ogg ({len(wav_chunked) / runner.sr:.2f}s)")
