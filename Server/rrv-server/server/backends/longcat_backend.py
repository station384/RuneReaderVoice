# SPDX-License-Identifier: GPL-3.0-or-later
#
# server/backends/longcat_backend.py
#
# LongCat-AudioDiT backend — waveform-latent diffusion voice cloning.
# MIT licensed (model and code from Meituan LongCat team).
#
# Continuation pipeline: monkey-patches AudioDiTModel.forward() to accept
# prompt_latent directly, enabling exact latent carry across chunks.
# Voice identity anchors to original reference latent every chunk.
# Prosodic continuity from forced-aligned latent tail carry.
#
# Speed: scales only generated frame budget (base_gen_frames / speed).
# Prompt/carry latent frames never scaled.

from __future__ import annotations
import asyncio, logging, re
from pathlib import Path
import numpy as np
from .base import AbstractTtsBackend, SynthesisRequest, SynthesisResult, VoiceInfo
from .audio import pcm_to_ogg, estimate_duration

log = logging.getLogger(__name__)

_SAMPLE_RATE        = 24000   # LongCat output rate — model generates at 24kHz
# Reference clips are extracted at 22050Hz mono by sample_extractor.py.
# load_audio() in _encode_prompt() resamples to self._model.config.sampling_rate
# (24000Hz) automatically before VAE encoding — no manual resampling needed.
_DEFAULT_STEPS      = 16
_DEFAULT_CFG        = 4.0
_DEFAULT_GUIDANCE   = "apg"
_DEFAULT_CHUNK_WORDS = 20
_CARRY_TAIL_WORDS   = 3
_TRIM_TAIL_MS       = 180.0
_ALIGN_FALLBACK_SEC = 1.0

_align_model = _align_labels = _align_sr = None


def _get_aligner(device):
    global _align_model, _align_labels, _align_sr
    if _align_model is None:
        import torchaudio
        bundle = torchaudio.pipelines.WAV2VEC2_ASR_BASE_960H
        _align_model  = bundle.get_model().to(device); _align_model.eval()
        _align_labels = bundle.get_labels()
        _align_sr     = bundle.sample_rate
        log.info("LongCat: wav2vec2 aligner loaded sr=%d", _align_sr)
    return _align_model, _align_labels, _align_sr


def _text_to_tokens(text: str, labels: tuple) -> list[int]:
    lbl = {c: i for i, c in enumerate(labels)}
    sep = lbl.get("|")
    toks, words = [], text.upper().split()
    for wi, word in enumerate(words):
        for ch in word:
            idx = lbl.get(ch)
            if idx is not None and idx != 0:
                toks.append(idx)
        if sep is not None and wi < len(words) - 1:
            toks.append(sep)
    return toks


def _find_tail_start_sample(audio_np, text, tail_word_count, device, sr):
    import torch, torchaudio
    from torchaudio.functional import forced_align, merge_tokens
    fallback = max(0, len(audio_np) - int(_ALIGN_FALLBACK_SEC * sr))
    words = text.split()
    if len(words) <= tail_word_count:
        return 0
    tail_start_word = len(words) - tail_word_count
    try:
        am, al, asr = _get_aligner(device)
        wf = torch.from_numpy(audio_np).unsqueeze(0).float()
        if sr != asr:
            wf = torchaudio.functional.resample(wf, sr, asr)
        with torch.no_grad():
            em, _ = am(wf.to(device))
            lp = torch.log_softmax(em, dim=-1)
        toks = _text_to_tokens(text, al)
        if not toks:
            return fallback
        tgt = torch.tensor([toks], dtype=torch.int32, device=device)
        paths, scores = forced_align(lp, tgt, blank=0)
        spans = merge_tokens(paths[0], scores[0], blank=0)
        lbl_list = list(al); sep = lbl_list.index("|") if "|" in lbl_list else None
        ws, cur = [], None
        for sp in spans:
            if sp.token == 0: continue
            if sp.token == sep: cur = None
            elif cur is None: cur = sp.start; ws.append(cur)
        if tail_start_word >= len(ws):
            return fallback
        tf = ws[tail_start_word]
        s  = int(tf * wf.shape[-1] / lp.shape[1])
        return max(0, min(int(s * sr / asr), len(audio_np) - 1))
    except Exception as e:
        log.debug("LongCat align failed (%s)", e)
        return fallback


def _patch_audiodit():
    import torch, torch.nn.functional as F
    import audiodit.modeling_audiodit as md
    from audiodit import AudioDiTModel
    if getattr(AudioDiTModel, "_rrv_patched", False):
        return

    @torch.no_grad()
    def patched_forward(self, input_ids=None, attention_mask=None, text_embedding=None,
                        prompt_audio=None, prompt_latent=None, duration=None,
                        steps=16, cfg_strength=4.0, guidance_method="cfg", return_dict=True):
        dev  = self.device
        sr   = self.config.sampling_rate
        mdf  = int(self.config.max_wav_duration * sr // self.config.latent_hop)
        repa = self.config.repa_dit_layer
        if text_embedding is not None:
            tc   = text_embedding.to(dev, torch.float32)
            tcl  = (attention_mask.sum(dim=1).to(dev) if attention_mask is not None
                    else torch.full((tc.shape[0],), tc.shape[1], device=dev))
        else:
            tc   = self.encode_text(input_ids.to(dev), attention_mask.to(dev))
            tcl  = attention_mask.sum(dim=1).to(dev)
        B = tc.shape[0]
        pp = False
        if prompt_latent is not None:
            pl   = prompt_latent.to(dev, torch.float32); pd = pl.shape[1]; pp = True
        elif prompt_audio is not None:
            pl, pd = self.encode_prompt_audio(prompt_audio); pp = True
        else:
            pl = torch.empty(B, 0, self.config.latent_dim, device=dev); pd = 0
        td  = min(duration if duration is not None else mdf, mdf)
        dt  = torch.full((B,), td, device=dev, dtype=torch.long)
        msk = md.lens_to_mask(dt)
        tmsk= md.lens_to_mask(tcl, length=tc.shape[1])
        nt  = torch.zeros_like(tc); ntl = tcl; ll = pd
        if pp:
            gl = td - ll
            if gl < 0: raise ValueError(f"prompt ({ll}f) > duration ({td}f)")
            lc  = F.pad(pl, (0, 0, 0, gl)); elc = torch.zeros_like(lc)
        else:
            lc  = torch.zeros(B, td, self.config.latent_dim, device=dev); elc = lc
        if guidance_method == "apg":
            apg = md._MomentumBuffer(momentum=-0.3)
        def fn(t, x):
            x[:, :ll] = pn * (1-t) + lc[:, :ll] * t
            o = self.transformer(x=x, text=tc, text_len=tcl, time=t, mask=msk,
                                 cond_mask=tmsk, return_ith_layer=repa, latent_cond=lc)
            pr = o["last_hidden_state"]
            if cfg_strength < 1e-5: return pr
            x[:, :ll] = 0
            no = self.transformer(x=x, text=nt, text_len=ntl, time=t, mask=msk,
                                  cond_mask=tmsk, return_ith_layer=repa, latent_cond=elc)
            np_ = no["last_hidden_state"]
            if guidance_method == "cfg": return pr + (pr - np_) * cfg_strength
            xs = x[:, ll:]; ps = pr[:, ll:]; ns = np_[:, ll:]
            psa = xs + (1-t)*ps; nsa = xs + (1-t)*ns
            out = md._apg_forward(psa, nsa, cfg_strength, apg, eta=0.5,
                                  norm_threshold=0.0, dims=[-1,-2])
            out = (out - xs) / (1-t)
            return F.pad(out, (0,0,ll,0), value=0.0)
        y0 = md.pad_sequence([torch.randn(d.item(), self.config.latent_dim, device=dev)
                               for d in dt], padding_value=0, batch_first=True)
        ts = torch.linspace(0, 1, steps, device=dev)
        pn = y0[:, :ll].clone()
        sam = md.odeint_euler(fn, y0, ts)[-1]
        lat = sam[:, pd:] if pp else sam
        lat = lat.permute(0, 2, 1).float()
        wav = self.vae.decode(lat).squeeze(1)
        if not return_dict: return (wav, lat)
        return md.AudioDiTOutput(waveform=wav, latent=lat)

    AudioDiTModel.forward = patched_forward
    AudioDiTModel._rrv_patched = True
    log.info("LongCat: forward patched for prompt_latent")


class LongCatBackend(AbstractTtsBackend):

    def __init__(self, models_dir: Path, torch_device: str,
                 model_variant: str = "1B", steps: int = _DEFAULT_STEPS,
                 cfg_strength: float = _DEFAULT_CFG,
                 guidance_method: str = _DEFAULT_GUIDANCE) -> None:
        self._models_dir    = models_dir
        self._torch_device  = torch_device
        self._model_variant = model_variant
        self._steps         = steps
        self._cfg_strength  = cfg_strength
        self._guidance      = guidance_method
        self._model         = None
        self._tokenizer     = None
        self._model_version = ""
        self._latent_cache: dict[str, tuple] = {}

    @property
    def provider_id(self) -> str: return "longcat"
    @property
    def display_name(self) -> str: return f"LongCat-AudioDiT {self._model_variant}"
    @property
    def supports_base_voices(self) -> bool: return False
    @property
    def supports_voice_matching(self) -> bool: return True
    @property
    def supports_voice_blending(self) -> bool: return False
    @property
    def supports_inline_pronunciation(self) -> bool: return False
    @property
    def languages(self) -> list[str]: return ["en", "en-us", "en-gb", "zh", "zh-cn"]
    @property
    def model_version(self) -> str: return self._model_version

    def extra_controls(self) -> dict:
        return {
            "longcat_steps":        {"type": "int",    "default": _DEFAULT_STEPS, "min": 4, "max": 64},
            "longcat_cfg_strength": {"type": "float",  "default": _DEFAULT_CFG,   "min": 1.0, "max": 10.0},
            "longcat_guidance":     {"type": "string", "default": _DEFAULT_GUIDANCE},
        }

    async def load(self) -> None:
        loop = asyncio.get_event_loop()
        await loop.run_in_executor(None, self._load_sync)

    def _load_sync(self) -> None:
        try:
            import audiodit  # noqa
        except ImportError:
            raise RuntimeError(
                "audiodit not found — add longcat-src to sys.path in run_worker.py:\n"
                "  git clone https://github.com/meituan-longcat/LongCat-AudioDiT.git longcat-src"
            )
        _patch_audiodit()
        import torch
        from audiodit import AudioDiTModel
        from transformers import AutoTokenizer
        model_dir = self._models_dir / "longcat" / self._model_variant
        if not model_dir.exists() or not any(model_dir.iterdir()):
            raise RuntimeError(f"LongCat model not found: {model_dir}")
        log.info("LongCat: loading %s from %s", self._model_variant, model_dir)
        dtype = torch.bfloat16 if "bf16" in self._model_variant.lower() else torch.float32
        self._model = AudioDiTModel.from_pretrained(str(model_dir), torch_dtype=dtype)
        self._model = self._model.to(self._torch_device)
        self._model.vae.to_half(); self._model.eval()
        tok_dir = self._models_dir / "longcat" / "umt5-base"
        tok_path = str(tok_dir) if tok_dir.exists() else \
            getattr(self._model.config, "text_encoder_model", "google/umt5-base")
        self._tokenizer = AutoTokenizer.from_pretrained(tok_path)
        cfg_path = model_dir / "config.json"
        import hashlib
        self._model_version = hashlib.sha256(cfg_path.read_bytes()).hexdigest()[:8] \
            if cfg_path.exists() else f"longcat-{self._model_variant}"
        log.info("LongCat: ready variant=%s device=%s version=%s",
                 self._model_variant, self._torch_device, self._model_version)

    def get_voices(self) -> list[VoiceInfo]:
        return []

    def _encode_prompt(self, sample_path: Path) -> tuple:
        import torch
        key = str(sample_path)
        if key not in self._latent_cache:
            from utils import load_audio  # type: ignore
            wav = load_audio(str(sample_path), self._model.config.sampling_rate).unsqueeze(0)
            with torch.no_grad():
                lat, dur = self._model.encode_prompt_audio(wav.to(self._torch_device))
            self._latent_cache[key] = (lat, int(dur))
        return self._latent_cache[key]

    def _run_chunk(self, full_text, chunk_text, prompt_latent, steps, cfg, guidance, speed) -> tuple:
        import torch
        sr, fh = self._model.config.sampling_rate, self._model.config.latent_hop
        mdf    = int(self._model.config.max_wav_duration * sr // fh)
        pd     = prompt_latent.shape[1]
        inp    = self._tokenizer([full_text], padding="longest", return_tensors="pt")
        # Word count from chunk_text only — NOT full_text.
        # full_text = prompt_text + chunk_text; counting all words would massively
        # over-budget the generation frames, causing the model to speak the
        # prompt/reference transcript as well as the target chunk.
        nw     = len(re.findall(r"\b[\w']+\b", chunk_text))
        bgf    = max(1, int(max(nw * 0.50, 0.25) * sr // fh))
        sf_    = max(1, int(round(bgf / speed)))
        total  = min(pd + sf_, mdf)
        with torch.no_grad():
            wav, lat = self._model(
                input_ids=inp.input_ids.to(self._torch_device),
                attention_mask=inp.attention_mask.to(self._torch_device),
                prompt_latent=prompt_latent,
                duration=total, steps=steps, cfg_strength=cfg,
                guidance_method=guidance, return_dict=False,
            )
        return wav.squeeze().detach().cpu().numpy().astype(np.float32), lat.detach()

    def _trim_tail(self, wav, lat) -> tuple:
        trim = int(_TRIM_TAIL_MS * self._model.config.sampling_rate / 1000.0)
        if trim <= 0 or trim >= len(wav): return wav, lat
        gf  = lat.shape[-1]
        tf  = max(0, min(int(round(trim * gf / len(wav))), gf - 1))
        return wav[:-trim], (lat[:, :, :-tf] if tf > 0 else lat)

    async def synthesize(self, request: SynthesisRequest) -> SynthesisResult:
        if self._model is None:
            raise RuntimeError("LongCat backend is not loaded")
        if request.sample_path is None:
            raise ValueError("LongCat requires a reference audio clip (sample_path).")
        if not (request.ref_text or "").strip():
            raise ValueError(
                "LongCat requires the reference transcript (ref_text). "
                "The text encoder conditions on orig_ref_text + carried tail words every chunk — "
                "without it the text-side prompt anchor is missing even though latent carry is correct."
            )
        steps    = int(getattr(request, "longcat_steps",        None) or self._steps)
        cfg      = float(getattr(request, "longcat_cfg_strength", None) or self._cfg_strength)
        guidance = str(getattr(request, "longcat_guidance",       None) or self._guidance)
        speed    = max(0.1, min(4.0, float(request.speech_rate or 1.0)))
        loop = asyncio.get_event_loop()
        ogg = await loop.run_in_executor(
            None, self._synthesize_sync, request, steps, cfg, guidance, speed)
        return SynthesisResult(ogg_bytes=ogg, duration_sec=estimate_duration(ogg))

    def _synthesize_sync(self, request, steps, cfg, guidance, speed) -> bytes:
        import torch
        try:
            from utils import normalize_text  # type: ignore
        except ImportError:
            normalize_text = lambda t: t.strip()

        # Set deterministic seed if requested
        if request.synthesis_seed is not None:
            torch.manual_seed(request.synthesis_seed)
            torch.cuda.manual_seed_all(request.synthesis_seed)

        text     = normalize_text(request.text)
        ref_text = normalize_text(request.ref_text or "").strip()
        if not ref_text:
            raise ValueError("LongCat: ref_text is empty — cannot build text-side prompt anchor.")
        sr       = self._model.config.sampling_rate

        orig_lat, _ = self._encode_prompt(request.sample_path)
        chunks = [text.split()[i:i + _DEFAULT_CHUNK_WORDS]
                  for i in range(0, len(text.split()), _DEFAULT_CHUNK_WORDS)]

        cur_lat   = orig_lat
        cur_ptxt  = ref_text
        combined  = []

        for i, cwords in enumerate(chunks):
            chunk_text = " ".join(cwords)
            full_text  = f"{cur_ptxt} {chunk_text}".strip()
            log.debug("LongCat: chunk %d/%d %d words", i + 1, len(chunks), len(cwords))
            wav, lat = self._run_chunk(full_text, chunk_text, cur_lat, steps, cfg, guidance, speed)
            wav, lat = self._trim_tail(wav, lat)
            combined.append(wav)
            if i >= len(chunks) - 1:
                break
            tail_s = _find_tail_start_sample(wav, chunk_text, _CARRY_TAIL_WORDS,
                                              self._torch_device, sr)
            gf  = lat.shape[-1]
            tf  = max(0, min(int(round(tail_s * gf / len(wav))), gf - 1))
            tl  = lat[:, :, tf:].permute(0, 2, 1).contiguous()
            btext = " ".join(cwords[-_CARRY_TAIL_WORDS:])
            cur_lat  = torch.cat([orig_lat, tl], dim=1)
            cur_ptxt = f"{ref_text} {btext}".strip()
            log.debug("LongCat: carry %d latent frames bridge='%s'", tl.shape[1], btext)

        full_wav = np.concatenate(combined).astype(np.float32) if combined \
                   else np.zeros(sr, dtype=np.float32)
        return pcm_to_ogg(full_wav, sr)
