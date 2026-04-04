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
import asyncio
from pathlib import Path

# rrv-server/ is the sibling directory of this worker directory.
# Path layout:  ~/rrvserver/rrv-kokoro/run_worker.py   (this file)
#               ~/rrvserver/rrv-server/server/worker.py (target)
_server_root = Path(__file__).parent.parent / "rrv-server"
sys.path.insert(0, str(_server_root))

from server.worker import _parse_args, _main  # noqa: E402

if __name__ == "__main__":
    args = _parse_args()
    try:
        asyncio.run(_main(args))
    except KeyboardInterrupt:
        pass
