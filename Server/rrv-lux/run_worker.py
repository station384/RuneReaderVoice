#!/usr/bin/env python3
# rrv-lux/run_worker.py
#
# LuxTTS worker subprocess launcher.
# Copy this file to ~/rrvserver/rrv-lux/run_worker.py
#
# This launcher adds the LuxTTS source directory (lux-src/) to sys.path
# before handing off to the main worker entrypoint.
#
# Usage (called automatically by WorkerBackend in the host process):
#   /home/mike/rrvserver/rrv-lux/.venv/bin/python run_worker.py \
#       --backend lux --socket /tmp/rrv_worker_lux_<pid>.sock \
#       --models-dir /home/mike/rrvserver/data/models \
#       --samples-dir /home/mike/rrvserver/data/samples \
#       --gpu auto

import sys
import os
from pathlib import Path

# Directory containing this launcher script
_HERE = Path(__file__).parent.resolve()

# Add lux-src to sys.path so zipvoice can be imported
_LUX_SRC = _HERE / "lux-src"
if _LUX_SRC.exists() and str(_LUX_SRC) not in sys.path:
    sys.path.insert(0, str(_LUX_SRC))

# Redirect HuggingFace hub downloads into the managed data directory
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
