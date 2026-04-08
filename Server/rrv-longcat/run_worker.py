#!/usr/bin/env python3
# rrv-longcat/run_worker.py
#
# LongCat-AudioDiT worker subprocess launcher.
# Copy this file to ~/rrvserver/rrv-longcat/run_worker.py
#
# This launcher adds longcat-src to sys.path so the audiodit package
# (which is not on PyPI and has no setup.py) can be imported.
#
# Setup:
#   git clone https://github.com/meituan-longcat/LongCat-AudioDiT.git \
#       ~/rrvserver/rrv-longcat/longcat-src
#
# Usage (called automatically by WorkerBackend in the host process):
#   /home/mike/rrvserver/rrv-longcat/.venv/bin/python run_worker.py \
#       --backend longcat --socket /tmp/rrv_worker_longcat_<pid>.sock \
#       --models-dir /home/mike/rrvserver/data/models \
#       --samples-dir /home/mike/rrvserver/data/samples \
#       --gpu auto \
#       --longcat-model-variant 1b

import sys
import os
from pathlib import Path

# Directory containing this launcher script
_HERE = Path(__file__).parent.resolve()

# longcat-src is cloned inside rrv-longcat/
# The audiodit package lives at longcat-src/audiodit/ — adding longcat-src
# to sys.path makes `import audiodit` work without pip install.
_LONGCAT_SRC = _HERE / "longcat-src"
if _LONGCAT_SRC.exists():
    if str(_LONGCAT_SRC) not in sys.path:
        sys.path.insert(0, str(_LONGCAT_SRC))
else:
    raise RuntimeError(
        f"longcat-src not found at {_LONGCAT_SRC}\n"
        f"Clone the repo first:\n"
        f"  git clone https://github.com/meituan-longcat/LongCat-AudioDiT.git \\\n"
        f"      {_LONGCAT_SRC}"
    )

# Redirect HuggingFace cache into the project data directory
_HF_CACHE = _HERE.parent / "data" / "models" / "hf-cache"
_HF_CACHE.mkdir(parents=True, exist_ok=True)
os.environ.setdefault("HF_HUB_CACHE", str(_HF_CACHE))

# Redirect torchaudio model cache (wav2vec2 aligner) into data/models/
# torchaudio looks for checkpoints at $TORCH_HOME/hub/checkpoints/
_TORCH_HOME = _HERE.parent / "data" / "models"
os.environ.setdefault("TORCH_HOME", str(_TORCH_HOME))

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
