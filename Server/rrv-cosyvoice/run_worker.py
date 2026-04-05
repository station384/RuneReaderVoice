#!/usr/bin/env python3
# rrv-cosyvoice/run_worker.py
#
# CosyVoice3 worker subprocess launcher.
# Copy this file to ~/rrvserver/rrv-cosyvoice/run_worker.py
#
# This launcher adds cosyvoice-src and its Matcha-TTS submodule to sys.path,
# and sets RRV_COSYVOICE_SRC_DIR so the worker can find the paths at load time.
#
# Usage (called automatically by WorkerBackend in the host process):
#   /home/mike/rrvserver/rrv-cosyvoice/.venv/bin/python run_worker.py \
#       --backend cosyvoice --socket /tmp/rrv_worker_cosyvoice_<pid>.sock \
#       --models-dir /home/mike/rrvserver/data/models \
#       --samples-dir /home/mike/rrvserver/data/samples \
#       --gpu auto

import sys
import os
from pathlib import Path

# Directory containing this launcher script
_HERE = Path(__file__).parent.resolve()

# cosyvoice-src is cloned inside rrv-cosyvoice/
_COSY_SRC = _HERE / "cosyvoice-src"
_MATCHA   = _COSY_SRC / "third_party" / "Matcha-TTS"

for _p in [str(_COSY_SRC), str(_MATCHA)]:
    if _p not in sys.path:
        sys.path.insert(0, _p)

# Tell the worker where cosyvoice-src lives for any runtime path resolution
os.environ["RRV_COSYVOICE_SRC_DIR"] = str(_COSY_SRC)

# Redirect all HuggingFace hub downloads (wetext FSTs, CosyVoice internals)
# into data/models/hf-cache/ so nothing lands in ~/.cache/huggingface/.
# Must be set BEFORE any huggingface_hub import.
_HF_CACHE = _HERE.parent / "data" / "models" / "hf-cache"
_HF_CACHE.mkdir(parents=True, exist_ok=True)
os.environ.setdefault("HF_HUB_CACHE", str(_HF_CACHE))

# Add rrv-server to sys.path so server.worker can be imported
_SERVER_ROOT = _HERE.parent / "rrv-server"
if str(_SERVER_ROOT) not in sys.path:
    sys.path.insert(0, str(_SERVER_ROOT))

# Hand off to main worker entrypoint
from server.worker import _parse_args, _main
import asyncio

if __name__ == "__main__":
    args = _parse_args()
    try:
        asyncio.run(_main(args))
    except KeyboardInterrupt:
        pass
