# SPDX-License-Identifier: GPL-3.0-or-later
#
# Thin launcher — runs inside this worker's isolated venv.
# Adds rrv-server/ to sys.path so the shared server.worker module can be
# imported, then hands off to it directly. All actual logic lives in
# rrv-server/server/worker.py — this file is intentionally minimal.
#
# Invoked by WorkerBackend in the host process:
#   <worker_dir>/.venv/bin/python <worker_dir>/run_worker.py \
#       --backend <name> --socket <path> --models-dir <path> \
#       --samples-dir <path> --gpu auto --max-concurrent 2 --log-level info

import sys
import os

# TORCHINDUCTOR_CACHE_DIR must be set before torch is imported — cache_dir() is
# called at import time and caches its result. The host sets this in spawn_env
# but we enforce it here to guarantee it's set before any torch import occurs.
# Without this, compiled kernels go to /tmp/torchinductor_root which is wiped
# on reboot, forcing a full recompile on every server restart.
_torchinductor_cache = os.environ.get("TORCHINDUCTOR_CACHE_DIR", "")
if not _torchinductor_cache:
    # Fallback: derive from TORCH_HOME or XDG_CACHE_HOME if set
    _torch_home = os.environ.get("TORCH_HOME", "")
    _xdg_cache  = os.environ.get("XDG_CACHE_HOME", "")
    if _torch_home:
        _torchinductor_cache = os.path.join(_torch_home, "inductor_cache")
    elif _xdg_cache:
        _torchinductor_cache = os.path.join(_xdg_cache, "torchinductor")
    if _torchinductor_cache:
        os.environ["TORCHINDUCTOR_CACHE_DIR"] = _torchinductor_cache
        print(f"[run_worker] TORCHINDUCTOR_CACHE_DIR set to {_torchinductor_cache}", flush=True)
    else:
        print("[run_worker] WARNING: TORCHINDUCTOR_CACHE_DIR not set — compiled kernels will not persist across restarts", flush=True)
else:
    print(f"[run_worker] TORCHINDUCTOR_CACHE_DIR={_torchinductor_cache}", flush=True)

# Set dynamo cache size large enough to hold all warmup shapes without eviction.
# Default is 8 — with 11 warmup shapes the 9th+ evict earlier entries, causing
# recompilation on real requests. Must be set before torch is imported.
os.environ.setdefault("TORCH_COMPILE_DEBUG", "0")
# These are read at import time by torch._dynamo.config
os.environ["TORCHDYNAMO_CACHE_SIZE_LIMIT"] = "64"

import asyncio
import warnings
from pathlib import Path

# Suppress torch.backends.cuda.sdp_kernel() FutureWarning — emitted by chatterbox
# internals on every inference call. The warning is harmless and unfixable without
# patching the library; suppress it at the worker process level.
warnings.filterwarnings(
    "ignore",
    message=r".*sdp_kernel.*deprecated.*",
    category=FutureWarning,
)
warnings.filterwarnings(
    "ignore",
    message=r".*LoRACompatibleLinear.*deprecated.*",
    category=FutureWarning,
)

# rrv-server/ is the sibling directory of this worker directory.
# Path layout:  ~/rrvserver/rrv-kokoro/run_worker.py   (this file)
#               ~/rrvserver/rrv-server/server/worker.py (target)
_server_root = Path(__file__).parent.parent / "rrv-server"
sys.path.insert(0, str(_server_root))

import os as _os
# Redirect HuggingFace hub downloads into the managed data directory
# instead of ~/.cache/huggingface/
_hf_cache = Path(__file__).parent.parent / "data" / "models" / "hf-cache"
_hf_cache.mkdir(parents=True, exist_ok=True)
_os.environ.setdefault("HF_HUB_CACHE", str(_hf_cache))

from server.worker import _parse_args, _main  # noqa: E402

if __name__ == "__main__":
    args = _parse_args()
    try:
        asyncio.run(_main(args))
    except KeyboardInterrupt:
        pass
