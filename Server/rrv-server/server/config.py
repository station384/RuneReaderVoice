# SPDX-License-Identifier: GPL-3.0-or-later
#
# This file is part of RuneReader Voice Server (rrv-server).
#
# Copyright (C) 2026 Michael Sutton / Tanstaafl Gaming
#
# RuneReader Voice Server is free software: you can redistribute it and/or
# modify it under the terms of the GNU General Public License as published by
# the Free Software Foundation, either version 3 of the License, or
# (at your option) any later version.
#
# RuneReader Voice Server is distributed in the hope that it will be useful,
# but WITHOUT ANY WARRANTY; without even the implied warranty of
# MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
# GNU General Public License for more details.
#
# You should have received a copy of the GNU General Public License
# along with RuneReader Voice Server. If not, see <https://www.gnu.org/licenses/>.
# server/config.py
#
# Single source of truth for all server configuration.
# Reads environment variables (with .env file support via python-dotenv if present)
# and exposes a frozen Settings object. CLI arguments are merged in main.py
# after this module loads — CLI takes precedence over env vars.

from __future__ import annotations

import os
from pathlib import Path
from typing import FrozenSet, Optional

# Optional dotenv support — load .env if present, ignore if not installed
try:
    from dotenv import load_dotenv
    load_dotenv()
except ImportError:
    pass


def _env_str(key: str, default: str) -> str:
    return os.environ.get(key, default).strip()


def _env_int(key: str, default: int) -> int:
    raw = os.environ.get(key, "").strip()
    if not raw:
        return default
    try:
        return int(raw)
    except ValueError:
        raise ValueError(f"Environment variable {key} must be an integer, got: {raw!r}")


def _env_set(key: str, default: str) -> FrozenSet[str]:
    raw = os.environ.get(key, default).strip()
    return frozenset(v.strip().lower() for v in raw.split(",") if v.strip())


# ── Valid values ──────────────────────────────────────────────────────────────

VALID_BACKENDS: FrozenSet[str] = frozenset({"kokoro", "f5tts", "chatterbox", "chatterbox_full", "chatterbox_multilingual", "qwen_natural", "qwen_custom", "qwen_design", "lux", "cosyvoice", "cosyvoice_vllm"})
VALID_GPU_MODES: FrozenSet[str] = frozenset({"auto", "cuda", "rocm", "cpu"})
VALID_LOG_LEVELS: FrozenSet[str] = frozenset({"debug", "info", "warning", "error"})


# ── Settings ──────────────────────────────────────────────────────────────────

class Settings:
    """
    Immutable server configuration resolved from environment variables.
    CLI overrides are applied in main.py by calling Settings.override().
    """

    def __init__(self) -> None:
        # Network
        self.host: str = _env_str("RRV_HOST", "0.0.0.0")
        self.port: int = _env_int("RRV_PORT", 8765)

        # Paths
        self.cache_dir:   Path = Path(_env_str("RRV_CACHE_DIR",   "./data/cache"))
        self.db_path:     Path = Path(_env_str("RRV_DB_PATH",     "./data/server-cache.db"))
        self.models_dir:  Path = Path(_env_str("RRV_MODELS_DIR",  "./data/models"))
        # F5-TTS vocoder: "auto" (BigVGAN if staged, else Vocos), "bigvgan", "vocos"
        self.f5_vocoder: str = _env_str("RRV_F5_VOCODER", "auto")
        self.samples_dir:          Path = Path(_env_str("RRV_SAMPLES_DIR",          "./data/samples"))
        self.whisper_model_dir:    Path = Path(_env_str("RRV_WHISPER_MODEL_DIR",    "./data/models/whisper"))
        # ASR provider selection.
        # Options: whisper (default, in-process), qwen_asr, crisper_whisper, cohere_transcribe
        self.asr_provider: str = _env_str("RRV_ASR_PROVIDER", "whisper").lower()
        # Resource manager eviction window — backends idle longer than this
        # are eligible for eviction when a new backend needs to load.
        self.backend_recent_use_window: float = float(
            _env_str("RRV_BACKEND_RECENT_USE_WINDOW", "60")
        )
        # Optional per-provider ASR venv path overrides (RRV_ASR_VENV_<provider>)
        self.asr_worker_venvs: dict = {}
        _asr_venv_prefix = "RRV_ASR_VENV_"
        for _av_key, _av_val in os.environ.items():
            if _av_key.startswith(_asr_venv_prefix):
                _av_provider = _av_key[len(_asr_venv_prefix):].lower()
                _av_path = _av_val.strip()
                if _av_path:
                    self.asr_worker_venvs[_av_provider] = Path(_av_path)
        self.sample_scan_interval: int  = _env_int("RRV_SAMPLE_SCAN_INTERVAL", 30)
        self.f5_sample_channels: int = _env_int("RRV_F5_SAMPLE_CHANNELS", 1)
        self.chatterbox_sample_channels: int = _env_int("RRV_CHATTERBOX_SAMPLE_CHANNELS", 2)
        self.f5_sample_rate: int = _env_int("RRV_F5_SAMPLE_RATE", 22050)
        self.chatterbox_sample_rate: int = _env_int("RRV_CHATTERBOX_SAMPLE_RATE", 44100)
        if self.f5_sample_channels not in (1, 2):
            raise ValueError("RRV_F5_SAMPLE_CHANNELS must be 1 or 2")
        if self.chatterbox_sample_channels not in (1, 2):
            raise ValueError("RRV_CHATTERBOX_SAMPLE_CHANNELS must be 1 or 2")
        if self.f5_sample_rate <= 0:
            raise ValueError("RRV_F5_SAMPLE_RATE must be a positive integer")
        if self.chatterbox_sample_rate <= 0:
            raise ValueError("RRV_CHATTERBOX_SAMPLE_RATE must be a positive integer")

        # Maximum concurrent synthesis requests for Chatterbox Turbo and Full backends.
        # Chatterbox Multilingual is always limited to 1 regardless of this setting.
        # Lower values reduce VRAM pressure when running alongside other GPU workloads.
        self.chatterbox_max_concurrent: int = _env_int("RRV_CHATTERBOX_MAX_CONCURRENT", 2)
        if self.chatterbox_max_concurrent < 1:
            raise ValueError("RRV_CHATTERBOX_MAX_CONCURRENT must be at least 1")

        # Community / sync
        self.community_db_path: Path = Path(_env_str("RRV_COMMUNITY_DB_PATH", "../data/community.db"))
        self.defaults_dir:      Path = Path(_env_str("RRV_DEFAULTS_DIR",      "../data/defaults"))
        # Contribute key — low-friction shared secret for crowd-source contributions.
        # Good-citizen gate: empty = open contributions (trusted LAN/guild mode).
        self.contribute_key: str = _env_str("RRV_CONTRIBUTE_KEY", "")
        # Admin key — gates PUT /defaults and PUT /npc-overrides/{id} (confirm).
        # Empty = open admin (trusted LAN mode). Set for any internet-facing deployment.
        self.admin_key: str = _env_str("RRV_ADMIN_KEY", "")

        # Backends
        raw_backends = _env_set("RRV_BACKENDS", "kokoro")
        invalid = raw_backends - VALID_BACKENDS
        if invalid:
            raise ValueError(
                f"Unknown backend(s) in RRV_BACKENDS: {sorted(invalid)}. "
                f"Valid values: {sorted(VALID_BACKENDS)}"
            )
        self.backends: FrozenSet[str] = raw_backends

        # GPU
        gpu = _env_str("RRV_GPU", "auto").lower()
        if gpu not in VALID_GPU_MODES:
            raise ValueError(
                f"Unknown GPU mode in RRV_GPU: {gpu!r}. "
                f"Valid values: {sorted(VALID_GPU_MODES)}"
            )
        self.gpu: str = gpu

        # Cache
        self.cache_max_mb: int = _env_int("RRV_CACHE_MAX_MB", 2048)

        # Security
        self.api_key: str = _env_str("RRV_API_KEY", "")

        # Reverse proxy — comma-separated trusted proxy IPs/CIDRs for X-Forwarded-For.
        # "127.0.0.1" covers Caddy on the same host (default).
        # Set "0.0.0.0/0" to trust all proxies on a private LAN.
        # Set "" to disable proxy header trust entirely (direct connections only).
        self.trusted_proxy_ips: str = _env_str("RRV_TRUSTED_PROXY_IPS", "127.0.0.1")

        # Text normalization
        # RRV_WETEXT=false disables wetext layer-2 normalization.
        # Use to diagnose text truncation or mutation caused by wetext.
        self.wetext_enabled: bool = _env_str("RRV_WETEXT", "true").lower() not in ("false", "0", "no", "off")

        # Logging
        log_level = _env_str("RRV_LOG_LEVEL", "info").lower()
        if log_level not in VALID_LOG_LEVELS:
            raise ValueError(
                f"Unknown log level in RRV_LOG_LEVEL: {log_level!r}. "
                f"Valid values: {sorted(VALID_LOG_LEVELS)}"
            )
        self.log_level: str = log_level

        # Worker venvs — optional per-backend subprocess isolation.
        # Scans for RRV_WORKER_VENV_<backend_name> env vars.
        # Any backend with a configured venv path is run as a worker subprocess
        # instead of being imported directly into the host process.
        # This allows backends with conflicting dependencies (e.g. different
        # transformers versions) to coexist.
        # Example:
        #   RRV_WORKER_VENV_kokoro=/home/mike/rrvserver/rrv-kokoro/.venv
        #   RRV_WORKER_VENV_chatterbox=/home/mike/rrvserver/rrv-chatterbox/.venv
        self.worker_venvs: dict[str, Path] = {}
        _wv_prefix = "RRV_WORKER_VENV_"
        for _wv_key, _wv_val in os.environ.items():
            if _wv_key.startswith(_wv_prefix):
                _wv_backend = _wv_key[len(_wv_prefix):].lower()
                _wv_path = _wv_val.strip()
                if _wv_path:
                    self.worker_venvs[_wv_backend] = Path(_wv_path)

        # ── Qwen model configuration ──────────────────────────────────────────
        # Size of the model to use for qwen_natural and qwen_custom backends.
        # "large" = 1.7B checkpoint, "small" = 0.6B checkpoint.
        # qwen_design always uses the large (1.7B) checkpoint.
        qwen_natural_size = _env_str("RRV_QWEN_NATURAL_SIZE", "large").lower()
        if qwen_natural_size not in ("large", "small"):
            raise ValueError("RRV_QWEN_NATURAL_SIZE must be 'large' or 'small'")
        self.qwen_natural_size: str = qwen_natural_size

        qwen_custom_size = _env_str("RRV_QWEN_CUSTOM_SIZE", "large").lower()
        if qwen_custom_size not in ("large", "small"):
            raise ValueError("RRV_QWEN_CUSTOM_SIZE must be 'large' or 'small'")
        self.qwen_custom_size: str = qwen_custom_size

        # Root directory for all Qwen model files
        self.qwen_models_dir: Path = Path(_env_str("RRV_QWEN_MODELS_DIR",
                                                    "../data/models/qwen"))

        # ── CosyVoice configuration ───────────────────────────────────────────
        self.cosyvoice_src_dir: str = _env_str("RRV_COSYVOICE_SRC_DIR", "")
        self.cosyvoice_vllm_max_concurrent: int = _env_int("RRV_COSYVOICE_VLLM_MAX_CONCURRENT", 6)
        if self.cosyvoice_vllm_max_concurrent < 1:
            self.cosyvoice_vllm_max_concurrent = 1

        # ── LuxTTS sample format ─────────────────────────────────────────────
        self.lux_sample_channels: int = _env_int("RRV_LUX_SAMPLE_CHANNELS", 1)
        self.lux_sample_rate: int = _env_int("RRV_LUX_SAMPLE_RATE", 48000)
        # Number of ODE solver steps — higher = better quality, slightly slower.
        # LuxTTS is fast enough that 10-20 steps are practical. Default: 10.
        self.lux_num_steps: int = _env_int("RRV_LUX_NUM_STEPS", 10)

    def override(self, **kwargs) -> None:
        """
        Apply CLI argument overrides. Called from main.py after argparse.
        Only overrides fields that were explicitly provided (not None).
        Validates the same constraints as __init__.
        """
        for key, value in kwargs.items():
            if value is None:
                continue

            if key == "backends":
                # CLI passes comma-separated string or list
                if isinstance(value, str):
                    value = frozenset(v.strip().lower() for v in value.split(",") if v.strip())
                else:
                    value = frozenset(str(v).lower() for v in value)
                invalid = value - VALID_BACKENDS
                if invalid:
                    raise ValueError(
                        f"Unknown backend(s): {sorted(invalid)}. "
                        f"Valid values: {sorted(VALID_BACKENDS)}"
                    )

            elif key == "gpu":
                value = str(value).lower()
                if value not in VALID_GPU_MODES:
                    raise ValueError(
                        f"Unknown GPU mode: {value!r}. "
                        f"Valid values: {sorted(VALID_GPU_MODES)}"
                    )

            elif key == "log_level":
                value = str(value).lower()
                if value not in VALID_LOG_LEVELS:
                    raise ValueError(
                        f"Unknown log level: {value!r}. "
                        f"Valid values: {sorted(VALID_LOG_LEVELS)}"
                    )

            elif key in ("cache_dir", "db_path", "models_dir", "samples_dir",
                         "whisper_model_dir"):
                value = Path(value)

            elif key in ("port", "cache_max_mb", "sample_scan_interval", "f5_sample_channels",
                         "chatterbox_sample_channels", "chatterbox_max_concurrent"):
                value = int(value)
                if key in ("f5_sample_channels", "chatterbox_sample_channels") and value not in (1, 2):
                    raise ValueError(f"{key} must be 1 or 2")
                if key == "chatterbox_max_concurrent" and value < 1:
                    raise ValueError("chatterbox_max_concurrent must be at least 1")

            setattr(self, key, value)

    @property
    def cache_max_bytes(self) -> int:
        return self.cache_max_mb * 1024 * 1024

    @property
    def auth_enabled(self) -> bool:
        return bool(self.api_key)

    def ensure_directories(self) -> None:
        """Create required directories if they do not exist."""
        self.cache_dir.mkdir(parents=True, exist_ok=True)
        self.db_path.parent.mkdir(parents=True, exist_ok=True)
        self.models_dir.mkdir(parents=True, exist_ok=True)
        self.samples_dir.mkdir(parents=True, exist_ok=True)
        self.community_db_path.parent.mkdir(parents=True, exist_ok=True)
        self.defaults_dir.mkdir(parents=True, exist_ok=True)

    def __repr__(self) -> str:
        return (
            f"Settings("
            f"host={self.host!r}, port={self.port}, "
            f"backends={sorted(self.backends)}, gpu={self.gpu!r}, "
            f"cache_dir={self.cache_dir}, models_dir={self.models_dir}, "
            f"samples_dir={self.samples_dir}, "
            f"f5_sample_channels={self.f5_sample_channels}, "
            f"chatterbox_sample_channels={self.chatterbox_sample_channels}, "
            f"chatterbox_max_concurrent={self.chatterbox_max_concurrent}, "
            f"cosyvoice_vllm_max_concurrent={self.cosyvoice_vllm_max_concurrent}, "
            f"cache_max_mb={self.cache_max_mb}, "
            f"f5_vocoder={self.f5_vocoder}, "
            f"auth_enabled={self.auth_enabled}, "
            f"contribute_key_set={bool(self.contribute_key)}, "
            f"admin_key_set={bool(self.admin_key)}, "
            f"log_level={self.log_level!r}, "
            f"wetext={self.wetext_enabled}, "
            f"asr_provider={self.asr_provider!r}, "
            f"worker_backends={sorted(self.worker_venvs)}"
            f")"
        )


# Module-level singleton — imported by all other modules
settings = Settings()
