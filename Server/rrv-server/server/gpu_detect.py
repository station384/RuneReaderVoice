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
# the best execution provider for worker backends.
#
# Priority order: CUDA -> ROCm -> CPU
#
# Detection strategy:
#   GPU presence is detected via system tools (nvidia-smi, rocm-smi) rather
#   than importing torch. This keeps the host venv lean — torch is only
#   needed in the worker venvs that actually run ML inference.
#
#   The detected provider string is passed to each worker subprocess via
#   the --gpu CLI arg, where the worker runs its own gpu_detect (with torch
#   available in its venv) to resolve ORT providers and torch device strings.
#
# Intel Arc on Linux: ONNX Runtime DirectML is Windows-only. On Linux,
# Arc uses ORT-CPU for Kokoro — which is fast enough given Kokoro's size.

from __future__ import annotations

import logging
import shutil
import subprocess
from dataclasses import dataclass

log = logging.getLogger(__name__)


@dataclass(frozen=True)
class GpuInfo:
    """
    Resolved GPU execution context for the server lifetime.
    Set once at startup, passed to workers via --gpu CLI arg.
    """
    provider: str           # "cuda" | "rocm" | "cpu"
    device_name: str        # Human-readable device name or "CPU"
    ort_providers: list     # ORT ExecutionProviders for any in-process backends
    torch_device: str       # "cuda" | "cpu" — for any in-process backends
    cuda_available: bool
    rocm_available: bool


def detect(requested: str = "auto") -> GpuInfo:
    """
    Probe hardware and return the best available GpuInfo.

    Detection uses system tools (nvidia-smi, rocm-smi) — no torch import
    required in the host venv.

    Args:
        requested: "auto" | "cuda" | "rocm" | "cpu"
    """
    cuda_info = _probe_cuda()
    rocm_info = _probe_rocm() if not cuda_info else None

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
                "Check nvidia-smi is accessible and NVIDIA drivers are installed. "
                "Use RRV_GPU=auto or RRV_GPU=cpu to avoid this error."
            )
        result = _make_cuda(cuda_info)

    elif requested == "rocm":
        if not rocm_info:
            raise RuntimeError(
                "GPU mode 'rocm' was requested but ROCm is not available. "
                "Check rocm-smi is accessible and ROCm drivers are installed. "
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

def _probe_cuda() -> dict | None:
    """
    Probe for NVIDIA CUDA via nvidia-smi.
    Returns device info dict or None if CUDA is not available.
    No torch import — works in the lean host venv.
    """
    if not shutil.which("nvidia-smi"):
        return None
    try:
        result = subprocess.run(
            [
                "nvidia-smi",
                "--query-gpu=name,memory.total",
                "--format=csv,noheader,nounits",
            ],
            capture_output=True,
            text=True,
            timeout=5,
        )
        if result.returncode != 0:
            return None
        lines = [l.strip() for l in result.stdout.strip().splitlines() if l.strip()]
        if not lines:
            return None
        # First GPU — e.g. "NVIDIA GeForce RTX 3080 Laptop GPU, 16384"
        parts = lines[0].split(",")
        device_name = parts[0].strip()
        vram_mb = int(parts[1].strip()) if len(parts) > 1 else 0
        return {"device_name": device_name, "device_count": len(lines), "vram_mb": vram_mb}
    except (subprocess.TimeoutExpired, OSError, ValueError) as e:
        log.debug("CUDA probe failed: %s", e)
        return None


def _probe_rocm() -> dict | None:
    """
    Probe for AMD ROCm via rocm-smi.
    Returns device info dict or None if ROCm is not available.
    """
    if not shutil.which("rocm-smi"):
        return None
    try:
        result = subprocess.run(
            ["rocm-smi", "--showproductname"],
            capture_output=True,
            text=True,
            timeout=5,
        )
        if result.returncode != 0:
            return None
        # Extract first device name from rocm-smi output
        device_name = "AMD GPU"
        for line in result.stdout.splitlines():
            if "Card series" in line or "GPU" in line:
                parts = line.split(":")
                if len(parts) > 1:
                    device_name = parts[-1].strip()
                    break
        return {"device_name": device_name}
    except (subprocess.TimeoutExpired, OSError) as e:
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
            "GPU: CPU selected (nvidia-smi/rocm-smi not found or no GPU detected) | "
            "ORT providers: %s",
            info.ort_providers,
        )
        log.info(
            "Note: F5-TTS and Chatterbox Turbo will run on CPU in worker processes "
            "unless their venvs have CUDA-enabled torch."
        )
