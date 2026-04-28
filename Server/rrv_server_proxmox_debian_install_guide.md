# RuneReader Voice Server — Debian 13 / Proxmox LXC Install Guide

This guide documents a **clean Debian 13 / Proxmox LXC install** for `rrvServer`, updated from the older Ubuntu/development-machine setup.

It assumes:

- Proxmox VE 9.x host
- NVIDIA RTX 3080 or similar CUDA-capable GPU
- NVIDIA driver installed on the Proxmox host
- Debian 13/Trixie LXC container for the RRV server
- RRV server source installed to `/opt/rrvserver`
- Shared model/cache/sample data mounted at `/media/dataStore/rrvserver/data`
- RRV server components use separate Python virtual environments per worker

The normal install path is:

```text
1. Prepare Proxmox host GPU driver
2. Pass NVIDIA devices into the Debian LXC
3. Install Python 3.11 under /opt/python/3.11
4. Copy or clone clean RRV source to /opt/rrvserver
5. Create fresh venvs for rrv-server and enabled workers
6. Install each worker's dependencies
7. Configure .env
8. Test manually
9. Install systemd service
```

A migration from an already-working development machine is a special case. That workflow is included later as an appendix because copied venvs can contain local modifications, absolute paths, damaged packages, or machine-specific binary state.

---

## 1. Architecture overview

The current RRV server layout is not a single Python environment. The host FastAPI server launches provider workers from separate sibling directories, each with its own virtual environment.

Expected structure:

```text
/opt/rrvserver/
  rrv-server/
    .env
    .venv/
    server/
  rrv-chatterbox/
    .venv/
    run_worker.py
  rrv-kokoro/
    .venv/
    run_worker.py
  rrv-f5/
    .venv/
    run_worker.py
  rrv-qwen/
    .venv/
    run_worker.py
  rrv-longcat/
    .venv/
    run_worker.py
  rrv-lux/
    .venv/
    run_worker.py
  rrv-whisper/
    .venv/
```

The server `.env` maps provider IDs to worker venvs:

```ini
RRV_WORKER_VENV_kokoro=../rrv-kokoro/.venv
RRV_WORKER_VENV_chatterbox=../rrv-chatterbox/.venv
RRV_WORKER_VENV_chatterbox_full=../rrv-chatterbox/.venv
RRV_WORKER_VENV_f5tts=../rrv-f5/.venv
RRV_WORKER_VENV_qwen_natural=../rrv-qwen/.venv
RRV_WORKER_VENV_qwen_custom=../rrv-qwen/.venv
RRV_WORKER_VENV_qwen_design=../rrv-qwen/.venv
RRV_WORKER_VENV_lux=../rrv-lux/.venv
RRV_WORKER_VENV_cosyvoice=../rrv-cosyvoice/.venv
RRV_WORKER_VENV_cosyvoice_vllm=../rrv-cosyvoice-vllm/.venv
RRV_WORKER_VENV_longcat=../rrv-longcat/.venv
RRV_ASR_VENV_whisper=../rrv-whisper/.venv
```

Only load the providers that are actually configured and tested:

```ini
RRV_BACKENDS=kokoro,chatterbox_full
```

---

## 2. Proxmox host NVIDIA driver baseline

For Proxmox 9.1 with kernel `6.17.13-4-pve`, Debian's `nvidia-driver` 550 DKMS package may fail to build. The working path was NVIDIA's Debian 13 CUDA repository with the 590 open DKMS driver.

Working host state:

```text
Kernel: 6.17.13-4-pve
NVIDIA driver: 590.48.01
GPU: NVIDIA GeForce RTX 3080
CUDA reported by driver: 13.1
```

Validate on the Proxmox host:

```bash
uname -r
dkms status
lsmod | grep -E '^nvidia'
nvidia-smi
ls -l /dev/nvidia*
```

Expected device nodes:

```text
/dev/nvidia0
/dev/nvidiactl
/dev/nvidia-modeset
/dev/nvidia-uvm
/dev/nvidia-uvm-tools
```

If `/dev/nvidia-uvm` is missing:

```bash
modprobe nvidia-uvm
nvidia-modprobe -u -c=0
ls -l /dev/nvidia*
```

To load NVIDIA modules at boot:

```bash
cat >/etc/modules-load.d/nvidia.conf <<'EOF'
nvidia
nvidia-modeset
nvidia-drm
nvidia-uvm
EOF

systemctl restart systemd-modules-load.service
```

---

## 3. LXC GPU passthrough for the RRV container

Edit the CT config on the Proxmox host, for example CT116:

```bash
nano /etc/pve/lxc/116.conf
```

Add:

```text
lxc.cgroup2.devices.allow: c 195:* rwm
lxc.cgroup2.devices.allow: c 510:* rwm

lxc.mount.entry: /dev/nvidia0 dev/nvidia0 none bind,optional,create=file
lxc.mount.entry: /dev/nvidiactl dev/nvidiactl none bind,optional,create=file
lxc.mount.entry: /dev/nvidia-modeset dev/nvidia-modeset none bind,optional,create=file
lxc.mount.entry: /dev/nvidia-uvm dev/nvidia-uvm none bind,optional,create=file
lxc.mount.entry: /dev/nvidia-uvm-tools dev/nvidia-uvm-tools none bind,optional,create=file
```

Restart the container:

```bash
pct stop 116
pct start 116
```

Validate inside the CT:

```bash
ls -l /dev/nvidia*
nvidia-smi
```

---

## 4. Base OS packages inside the Debian 13 container

Install basic build/runtime packages:

```bash
apt update
apt install -y \
  build-essential \
  curl \
  wget \
  git \
  pkg-config \
  libssl-dev \
  zlib1g-dev \
  libbz2-dev \
  libreadline-dev \
  libsqlite3-dev \
  libffi-dev \
  liblzma-dev \
  libncurses-dev \
  tk-dev \
  uuid-dev \
  xz-utils \
  ffmpeg \
  openssh-server \
  sudo
```

`ffmpeg` is required by the server audio path. Without it, workers may warn:

```text
pcm_to_ogg: ffmpeg not found — falling back to soundfile
```

---

## 5. Python 3.11 on Debian 13

The working RRV venvs were built for Python 3.11. Debian 13/Trixie defaults to Python 3.13, and may not provide `python3.11` packages in the configured repositories.

Do not replace system Python. Install CPython 3.11 under `/opt/python/3.11`.

```bash
cd /usr/src
wget https://www.python.org/ftp/python/3.11.14/Python-3.11.14.tgz
tar -xzf Python-3.11.14.tgz
cd Python-3.11.14

./configure \
  --prefix=/opt/python/3.11 \
  --enable-optimizations \
  --with-ensurepip=install

make -j"$(nproc)"
make altinstall
```

Verify:

```bash
/opt/python/3.11/bin/python3.11 --version
/opt/python/3.11/bin/python3.11 -m pip --version
/opt/python/3.11/bin/python3.11 -m venv --help >/dev/null && echo "venv ok"
```

Expected:

```text
Python 3.11.14
venv ok
```

---

## 6. Install clean source tree

Install or copy the RRV server source to:

```text
/opt/rrvserver
```

For a clean install, do **not** copy old virtual environments. Create fresh venvs on the target server.

Expected clean source layout:

```text
/opt/rrvserver/
  rrv-server/
    .env
    server/
  rrv-chatterbox/
    run_worker.py
  rrv-kokoro/
    run_worker.py
  rrv-f5/
    run_worker.py
  rrv-qwen/
    run_worker.py
  rrv-longcat/
    run_worker.py
  rrv-lux/
    run_worker.py
  rrv-whisper/
```

The large data/model directory should live outside the source tree or be mounted remotely. In this deployment the data root is:

```text
/media/dataStore/rrvserver/data
```

Create the install directory:

```bash
mkdir -p /opt/rrvserver
```

If copying from another machine, copy only source/config by default:

```bash
rsync -a --info=progress2 \
  --exclude='.git/' \
  --exclude='__pycache__/' \
  --exclude='data/' \
  --exclude='*/.venv/' \
  /home/mike/rrvserver/ \
  root@<CT116-IP>:/opt/rrvserver/
```

Verify:

```bash
du -sh /opt/rrvserver
find /opt/rrvserver -maxdepth 2 -type f -name ".env" -print
find /opt/rrvserver -maxdepth 2 -type f -name "run_worker.py" -print
```

---

## 7. Create fresh Python virtual environments

Create the server venv:

```bash
cd /opt/rrvserver/rrv-server
/opt/python/3.11/bin/python3.11 -m venv .venv
. .venv/bin/activate
pip install --upgrade pip setuptools wheel
```

Install server dependencies using the current dependency file or project metadata in the source tree:

```bash
ls -la requirements*.txt pyproject.toml setup.py 2>/dev/null || true
```

Typical install forms are one of:

```bash
pip install -r requirements.txt
```

or:

```bash
pip install -e .
```

Create worker venvs only for providers enabled in `RRV_BACKENDS`.

Example Chatterbox venv:

```bash
cd /opt/rrvserver/rrv-chatterbox
/opt/python/3.11/bin/python3.11 -m venv .venv
. .venv/bin/activate
pip install --upgrade pip setuptools wheel
```

If Chatterbox requires PyTorch with CUDA 12.4, install:

```bash
pip install --no-cache-dir \
  torch==2.6.0+cu124 \
  torchaudio==2.6.0+cu124 \
  --index-url https://download.pytorch.org/whl/cu124
```

Then install the Chatterbox/provider package requirements from the source tree as appropriate.

Example Kokoro venv:

```bash
cd /opt/rrvserver/rrv-kokoro
/opt/python/3.11/bin/python3.11 -m venv .venv
. .venv/bin/activate
pip install --upgrade pip setuptools wheel
pip install kokoro-onnx numpy onnxruntime-gpu
```

If both `onnxruntime` and `onnxruntime-gpu` are installed and CUDA providers are missing, repair with:

```bash
pip uninstall -y onnxruntime onnxruntime-gpu
pip install --no-cache-dir onnxruntime-gpu==1.25.0
```

Validate worker GPU support:

```bash
/opt/rrvserver/rrv-chatterbox/.venv/bin/python - <<'PY'
import torch
print("torch", torch.__version__)
print("cuda available", torch.cuda.is_available())
print(torch.cuda.get_device_name(0) if torch.cuda.is_available() else "no cuda")
PY

/opt/rrvserver/rrv-kokoro/.venv/bin/python - <<'PY'
import onnxruntime as ort
print(ort.__version__)
print(ort.get_available_providers())
PY
```

Expected results include:

```text
cuda available True
NVIDIA GeForce RTX 3080
CUDAExecutionProvider
```

---

## 8. `.env` cache and path settings

The server loads `.env` from the `rrv-server` working directory through `python-dotenv` during `server.config` import.

Important: when running under systemd, use `EnvironmentFile=` so cache variables exist before Python starts and before any libraries create default caches under `/root/.cache`.

Recommended cache/path settings:

```ini
RRV_CACHE_DIR=/media/dataStore/rrvserver/data/cache
RRV_DB_PATH=/media/dataStore/rrvserver/data/server-cache.db
RRV_COMMUNITY_DB_PATH=/media/dataStore/rrvserver/data/community.db
RRV_DEFAULTS_DIR=/media/dataStore/rrvserver/data/defaults
RRV_MODELS_DIR=/media/dataStore/rrvserver/data/models
RRV_SAMPLES_DIR=/media/dataStore/rrvserver/data/samples
RRV_WHISPER_MODEL_DIR=/media/dataStore/rrvserver/data/models/whisper/small

RRV_COND_CACHE_DIR=/media/dataStore/rrvserver/data/cond_cache

HF_HUB_CACHE=/media/dataStore/rrvserver/data/models/hf-cache
HF_HOME=/media/dataStore/rrvserver/data/huggingface
HF_ASSETS_CACHE=/media/dataStore/rrvserver/data/huggingface/assets
TRANSFORMERS_CACHE=/media/dataStore/rrvserver/data/huggingface/transformers
TORCH_HOME=/media/dataStore/rrvserver/data/torch
XDG_CACHE_HOME=/media/dataStore/rrvserver/data/.cache

CONDA_PKGS_DIRS=/media/dataStore/rrvserver/data/conda/pkgs
CONDA_ENVS_PATH=/media/dataStore/rrvserver/data/conda/envs

TORCHINDUCTOR_CACHE_DIR=/media/dataStore/rrvserver/data/.torch_compile_cache
```

The intended Hugging Face hub cache for this deployment is:

```text
/media/dataStore/rrvserver/data/models/hf-cache
```

Verify `.env` loading:

```bash
cd /opt/rrvserver/rrv-server

PYTHONPATH=/opt/rrvserver/rrv-server \
  /opt/rrvserver/rrv-server/.venv/bin/python - <<'PY'
import os
from server.config import settings

print(settings)
for k in [
    "HF_HOME",
    "HF_HUB_CACHE",
    "HF_ASSETS_CACHE",
    "TRANSFORMERS_CACHE",
    "TORCH_HOME",
    "XDG_CACHE_HOME",
    "RRV_COND_CACHE_DIR",
    "CONDA_PKGS_DIRS",
    "CONDA_ENVS_PATH",
]:
    print(f"{k}={os.environ.get(k)}")
PY
```

---

## 9. Manual server startup test

Use this form for manual startup so `.env` values are exported before Python starts:

```bash
cd /opt/rrvserver/rrv-server

set -a
. ./.env
set +a

PYTHONPATH=/opt/rrvserver/rrv-server \
  /opt/rrvserver/rrv-server/.venv/bin/python -m server.main
```

Expected successful startup includes:

```text
GPU: CUDA selected — NVIDIA GeForce RTX 3080
Cache initialized
Backend 'chatterbox_full' configured as worker subprocess
Worker 'kokoro' ready
Loaded 2 backend(s): chatterbox_full, kokoro
RuneReader Voice Server ready — 0.0.0.0:8765
```

Kokoro may emit ONNX Runtime affinity warnings in LXC:

```text
pthread_setaffinity_np failed ... Specify the number of threads explicitly so the affinity is not set.
```

These warnings did not block backend startup.

---

## 10. Systemd service

Create:

```bash
nano /etc/systemd/system/rrv-server.service
```

Service file:

```ini
[Unit]
Description=RuneReader Voice Server
Wants=network-online.target
After=network-online.target

[Service]
Type=simple
WorkingDirectory=/opt/rrvserver/rrv-server

EnvironmentFile=/opt/rrvserver/rrv-server/.env
Environment=PYTHONPATH=/opt/rrvserver/rrv-server

ExecStart=/opt/rrvserver/rrv-server/.venv/bin/python -m server.main

Restart=on-failure
RestartSec=10

StandardOutput=journal
StandardError=journal

[Install]
WantedBy=multi-user.target
```

Reload and start:

```bash
systemctl daemon-reload
systemctl start rrv-server.service
systemctl status rrv-server.service --no-pager
```

Watch logs:

```bash
journalctl -u rrv-server.service -f
```

Enable on boot:

```bash
systemctl enable rrv-server.service
```

Validate after reboot:

```bash
systemctl status rrv-server.service --no-pager
curl -s http://127.0.0.1:8765/health | python3 -m json.tool
```

---

## 11. Caddy reverse proxy notes

If Caddy reports:

```text
dial tcp <ip>:8765: connect: connection refused
```

then Caddy can reach that IP, but nothing is listening on that IP/port. In this deployment the cause was an IP conflict: the configured upstream IP belonged to the NAS, not CT116.

Validate from the Caddy host:

```bash
curl -v http://<CT116-IP>:8765/health
curl -v http://<CT116-IP>:8765/docs
```

Example Caddyfile block:

```caddyfile
rrv.mkfam.com {
    reverse_proxy <CT116-IP>:8765
}
```

Reload Caddy:

```bash
caddy validate --config /etc/caddy/Caddyfile
systemctl reload caddy
```

Scanner noise such as `invalid CR in chunked line` against unrelated hosts can usually be ignored.

---

## 12. Admin SSH user inside CT

Create an admin user:

```bash
adduser admin
apt install -y sudo
usermod -aG sudo admin
id admin
groups admin
```

Enable SSH:

```bash
apt install -y openssh-server
systemctl enable --now ssh
systemctl status ssh --no-pager
```

Test:

```bash
ssh admin@<CT116-IP>
sudo whoami
```

Expected:

```text
root
```

---

## 13. Troubleshooting checklist

### Host GPU works but CT does not see GPU

On Proxmox host:

```bash
ls -l /dev/nvidia*
modprobe nvidia-uvm
```

Restart the CT after device nodes exist:

```bash
pct stop 116
pct start 116
```

Inside CT:

```bash
ls -l /dev/nvidia*
nvidia-smi
```

### `ModuleNotFoundError: No module named 'server'`

Start from the project context with `PYTHONPATH`:

```bash
cd /opt/rrvserver/rrv-server
PYTHONPATH=/opt/rrvserver/rrv-server \
  /opt/rrvserver/rrv-server/.venv/bin/python -m server.main
```

### Chatterbox Torch import error

If Chatterbox fails importing Torch, reinstall Torch/Torchaudio inside the Chatterbox venv:

```bash
/opt/rrvserver/rrv-chatterbox/.venv/bin/python -m pip install --force-reinstall --no-cache-dir \
  torch==2.6.0+cu124 \
  torchaudio==2.6.0+cu124 \
  --index-url https://download.pytorch.org/whl/cu124
```

Validate:

```bash
/opt/rrvserver/rrv-chatterbox/.venv/bin/python - <<'PY'
import torch
print(torch.__version__)
print(torch.cuda.is_available())
print(torch.cuda.get_device_name(0) if torch.cuda.is_available() else "no cuda")
PY
```

### Kokoro loads CPU ORT only

Inside Kokoro venv:

```bash
pip uninstall -y onnxruntime onnxruntime-gpu
pip install --no-cache-dir onnxruntime-gpu==1.25.0
```

Verify:

```bash
python - <<'PY'
import onnxruntime as ort
print(ort.get_available_providers())
PY
```

### ffmpeg warning

Install FFmpeg in CT:

```bash
apt install -y ffmpeg
```

Verify:

```bash
which ffmpeg
ffmpeg -version | head -3
```

---

## 14. Appendix: migrating an existing modified development install

A migration from an already-working development machine is not the normal install path. Use this only if you intentionally need to preserve modified code inside existing `.venv` directories.

In the validated migration, custom changes existed inside copied venvs, including:

```text
rrv-server/.venv
rrv-chatterbox/.venv
```

That required preserving those environments and repairing them on the target machine.

### Copy source and selected venvs

If you need to preserve modified venv content, do not exclude those specific venvs. Still exclude the large data directory:

```bash
rsync -a --info=progress2 \
  --exclude='.git/' \
  --exclude='__pycache__/' \
  --exclude='data/' \
  /home/mike/rrvserver/ \
  root@<CT116-IP>:/opt/rrvserver/
```

### Repair copied venv Python symlinks

Copied venvs may point to the old machine's interpreter:

```text
.venv/bin/python3.11 -> /usr/bin/python3.11
```

Repair all copied venv Python 3.11 symlinks:

```bash
find /opt/rrvserver -path "*/.venv/bin/python3.11" -type l \
  -exec ln -sf /opt/python/3.11/bin/python3.11 {} \;
```

Validate:

```bash
find /opt/rrvserver -path "*/.venv/bin/python" \( -type f -o -type l \) -exec sh -c '
  for p do
    echo "===== $p ====="
    "$p" --version || true
    "$p" -c "import sys; print(sys.prefix); print(sys.base_prefix)" || true
  done
' sh {} +
```

### Repair copied venv launcher shebangs

Copied entrypoint scripts may still point to old machine paths:

```text
#!/home/mike/rrvserver/rrv-server/.venv/bin/python3.11
```

Patch the server venv scripts:

```bash
sed -i '1s|^#!.*python3.11$|#!/opt/rrvserver/rrv-server/.venv/bin/python3.11|' \
  /opt/rrvserver/rrv-server/.venv/bin/*
```

### Repair copied Chatterbox Torch if needed

If copied Torch is incomplete or corrupted, reinstall only Torch/Torchaudio:

```bash
/opt/rrvserver/rrv-chatterbox/.venv/bin/python -m pip install --force-reinstall --no-cache-dir \
  torch==2.6.0+cu124 \
  torchaudio==2.6.0+cu124 \
  --index-url https://download.pytorch.org/whl/cu124
```

This preserves custom provider code while repairing the binary PyTorch install.

---

## 15. Known-good final state

```text
Proxmox host:
  Kernel 6.17.13-4-pve
  NVIDIA 590.48.01 driver
  RTX 3080 visible in nvidia-smi

CT116:
  Debian 13/Trixie
  CUDA visible via nvidia-smi
  Python 3.11 installed at /opt/python/3.11
  RRV installed at /opt/rrvserver
  Data mounted at /media/dataStore/rrvserver/data
  ffmpeg installed

RRV:
  rrv-server starts from /opt/rrvserver/rrv-server
  .env exported through systemd EnvironmentFile
  PYTHONPATH=/opt/rrvserver/rrv-server
  chatterbox_full loads
  kokoro loads with CUDAExecutionProvider
  systemd service enabled and starts on boot
```

