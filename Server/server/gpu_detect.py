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
# server/gpu_detect.py
#
# Hardware probe: detects available GPU compute capabilities and selects
# the best execution provider for ONNX Runtime and PyTorch backends.
#
# Priority order: CUDA → ROCm → CPU
#
# Intel Arc on Linux: ONNX Runtime DirectML is Windows-only. On Linux,
# Arc uses ORT-CPU for Kokoro — which is fast enough given Kokoro's size.
# PyTorch IPEX (Intel Extension for PyTorch) is experimental and not relied on;
# F5-TTS and Chatterbox Turbo fall back to CPU on Arc.
#
# Video encode engines (NVENC, VCE, Quick Sync) run on dedicated silicon
# and do not contend with compute workloads. No GPU resource monitoring needed.

from __future__ import annotations

import logging
from dataclasses import dataclass
from typing import Optional

log = logging.getLogger(__name__)


@dataclass(frozen=True)
class GpuInfo:
    """
    Resolved GPU execution context for the server lifetime.
    Set once at startup, never changed.
    """
    provider: str           # "cuda" | "rocm" | "cpu"
    device_name: str        # Human-readable device name or "CPU"
    ort_providers: list     # ONNX Runtime ExecutionProviders list (ordered)
    torch_device: str       # "cuda" | "cuda" (ROCm presents as cuda) | "cpu"
    cuda_available: bool
    rocm_available: bool


def detect(requested: str = "auto") -> GpuInfo:
    """
    Probe hardware and return the best available GpuInfo.

    Args:
        requested: "auto" | "cuda" | "rocm" | "cpu"
                   "auto" probes in priority order: CUDA → ROCm → CPU.
                   Forcing a specific provider raises RuntimeError if unavailable.
    """
    cuda_info = _probe_cuda()
    rocm_info = _probe_rocm()

    if requested == "auto":
        if cuda_info:
            result = _make_cuda(cuda_info)
        elif rocm_info:
            result = _make_rocm(rocm_info)
        else:
            result = _make_cpu()

    elif requested == "cuda":
        if not cuda_info:
            raise RuntimeError(
                "GPU mode 'cuda' was requested but CUDA is not available. "
                "Check that the CUDA toolkit and nvidia drivers are installed, "
                "and that a CUDA-enabled torch is installed. "
                "Use RRV_GPU=auto or RRV_GPU=cpu to avoid this error."
            )
        result = _make_cuda(cuda_info)

    elif requested == "rocm":
        if not rocm_info:
            raise RuntimeError(
                "GPU mode 'rocm' was requested but ROCm is not available. "
                "Check that ROCm is installed and a ROCm-enabled torch is installed. "
                "Use RRV_GPU=auto or RRV_GPU=cpu to avoid this error."
            )
        result = _make_rocm(rocm_info)

    elif requested == "cpu":
        result = _make_cpu()

    else:
        raise ValueError(f"Unknown GPU mode: {requested!r}")

    _log_result(result)
    return result


# ── Probes ────────────────────────────────────────────────────────────────────

def _probe_cuda() -> Optional[dict]:
    """Returns CUDA device info dict or None."""
    try:
        import torch
        if torch.cuda.is_available():
            idx = torch.cuda.current_device()
            return {
                "device_name": torch.cuda.get_device_name(idx),
                "device_count": torch.cuda.device_count(),
                "vram_mb": torch.cuda.get_device_properties(idx).total_memory // (1024 * 1024),
            }
    except ImportError:
        pass
    except Exception as e:
        log.debug("CUDA probe failed: %s", e)
    return None


def _probe_rocm() -> Optional[dict]:
    """Returns ROCm device info dict or None. ROCm presents as HIP via torch."""
    try:
        import torch
        # ROCm builds of PyTorch report hip version; cuda.is_available() is True
        # for ROCm devices as well, but torch.version.hip distinguishes them.
        if hasattr(torch.version, "hip") and torch.version.hip is not None:
            if torch.cuda.is_available():
                idx = torch.cuda.current_device()
                return {
                    "device_name": torch.cuda.get_device_name(idx),
                    "hip_version": torch.version.hip,
                }
    except ImportError:
        pass
    except Exception as e:
        log.debug("ROCm probe failed: %s", e)
    return None


# ── Builders ──────────────────────────────────────────────────────────────────

def _make_cuda(info: dict) -> GpuInfo:
    return GpuInfo(
        provider="cuda",
        device_name=info["device_name"],
        ort_providers=["CUDAExecutionProvider", "CPUExecutionProvider"],
        torch_device="cuda",
        cuda_available=True,
        rocm_available=False,
    )


def _make_rocm(info: dict) -> GpuInfo:
    # ROCm presents itself as CUDA in PyTorch; ORT-ROCm uses ROCMExecutionProvider
    return GpuInfo(
        provider="rocm",
        device_name=info["device_name"],
        ort_providers=["ROCMExecutionProvider", "CPUExecutionProvider"],
        torch_device="cuda",   # ROCm devices are addressed as "cuda" in PyTorch
        cuda_available=False,
        rocm_available=True,
    )


def _make_cpu() -> GpuInfo:
    return GpuInfo(
        provider="cpu",
        device_name="CPU",
        ort_providers=["CPUExecutionProvider"],
        torch_device="cpu",
        cuda_available=False,
        rocm_available=False,
    )


# ── Logging ───────────────────────────────────────────────────────────────────

def _log_result(info: GpuInfo) -> None:
    if info.provider == "cuda":
        log.info(
            "GPU: CUDA selected — %s | ORT providers: %s",
            info.device_name,
            info.ort_providers,
        )
    elif info.provider == "rocm":
        log.info(
            "GPU: ROCm selected — %s | ORT providers: %s",
            info.device_name,
            info.ort_providers,
        )
    else:
        log.info(
            "GPU: CPU selected (no CUDA/ROCm available or CPU forced) | "
            "ORT providers: %s",
            info.ort_providers,
        )
        log.info(
            "Note: F5-TTS and Chatterbox Turbo will be very slow on CPU. "
            "Kokoro on CPU is fast and unaffected."
        )
