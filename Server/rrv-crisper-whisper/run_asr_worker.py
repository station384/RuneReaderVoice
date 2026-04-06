# SPDX-License-Identifier: GPL-3.0-or-later
#
# Thin launcher — runs inside this ASR worker's isolated venv.
# Adds rrv-server/ to sys.path so the shared server.asr_worker module can be
# imported, then hands off to it. All actual logic lives in
# rrv-server/server/asr_worker.py — this file is intentionally minimal.
#
# Invoked by WorkerAsr in the host process:
#   <worker_dir>/.venv/bin/python <worker_dir>/run_asr_worker.py \
#       --provider <name> --socket <path> --models-dir <path> \
#       --gpu auto --log-level info

import sys
import asyncio
from pathlib import Path

# rrv-server/ is the sibling directory of this worker directory.
_server_root = Path(__file__).parent.parent / "rrv-server"
sys.path.insert(0, str(_server_root))

import os as _os
_hf_cache = Path(__file__).parent.parent / "data" / "models" / "hf-cache"
_hf_cache.mkdir(parents=True, exist_ok=True)
_os.environ.setdefault("HF_HUB_CACHE", str(_hf_cache))

from server.asr_worker import _parse_args, _main  # noqa: E402

if __name__ == "__main__":
    args = _parse_args()
    try:
        asyncio.run(_main(args))
    except KeyboardInterrupt:
        pass
