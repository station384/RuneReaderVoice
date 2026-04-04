# RuneReader Voice Server — Dockerfile
#
# Build arguments:
#   BACKENDS   comma-separated list of backends to install: kokoro,xtts,f5tts
#              default: kokoro
#   GPU        gpu dependency group to install: none, gpu-cuda, gpu-rocm
#              default: none  (CPU-only)
#
# Examples:
#   Kokoro CPU only (default):
#     docker build -t rrv-server .
#
#   Full CUDA (XTTS + F5 + Kokoro on GPU):
#     docker build --build-arg BACKENDS=kokoro,xtts,f5tts \
#                  --build-arg GPU=gpu-cuda \
#                  -t rrv-server:cuda .
#
#   Full ROCm:
#     docker build --build-arg BACKENDS=kokoro,xtts,f5tts \
#                  --build-arg GPU=gpu-rocm \
#                  -t rrv-server:rocm .
#
# Runtime volumes (mount these to persist data):
#   /app/models    — model files (read-only recommended)
#   /app/samples   — reference audio clips (read-only recommended)
#   /app/cache     — generated OGG files (read-write)
#   /app/data      — SQLite manifest database (read-write)

# ── Base image ────────────────────────────────────────────────────────────────
# Use slim Python 3.11. CUDA builds should use nvidia/cuda base instead —
# see the gpu-cuda variant comment below.

ARG BASE_IMAGE=python:3.12-slim
FROM ${BASE_IMAGE} AS base

# System dependencies
RUN apt-get update && apt-get install -y --no-install-recommends \
        libsndfile1 \
        libsndfile1-dev \
        ffmpeg \
        git \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app

# ── Builder stage ─────────────────────────────────────────────────────────────
FROM base AS builder

ARG BACKENDS=kokoro
ARG GPU=none

COPY pyproject.toml .
COPY server/ server/

# Install the package with selected optional groups.
# Build the pip extras string: always include the backend groups,
# optionally add the GPU group.
RUN python -m pip install --upgrade pip && \
    if [ "$GPU" = "none" ]; then \
        pip install --no-cache-dir -e ".[${BACKENDS}]"; \
    else \
        pip install --no-cache-dir -e ".[${BACKENDS},${GPU}]"; \
    fi

# ── Runtime stage ─────────────────────────────────────────────────────────────
FROM base AS runtime

# Copy installed packages from builder
COPY --from=builder /usr/local/lib/python3.12/site-packages /usr/local/lib/python3.12/site-packages
COPY --from=builder /usr/local/bin /usr/local/bin
COPY --from=builder /app /app

WORKDIR /app

# Create volume mount points
RUN mkdir -p /app/models /app/samples /app/cache /app/data

# Non-root user for security
RUN useradd -m -u 1000 rrv && chown -R rrv:rrv /app
USER rrv

# Default environment — all overridable at runtime via -e or docker-compose env
ENV RRV_HOST=0.0.0.0 \
    RRV_PORT=8765 \
    RRV_MODELS_DIR=/app/models \
    RRV_SAMPLES_DIR=/app/samples \
    RRV_CACHE_DIR=/app/cache \
    RRV_DB_PATH=/app/data/server-cache.db \
    RRV_BACKENDS=kokoro \
    RRV_GPU=auto \
    RRV_CACHE_MAX_MB=2048 \
    RRV_API_KEY= \
    RRV_LOG_LEVEL=info

EXPOSE 8765

HEALTHCHECK --interval=30s --timeout=5s --start-period=60s --retries=3 \
    CMD python -c "import urllib.request; urllib.request.urlopen('http://localhost:8765/api/v1/health')" || exit 1

CMD ["python", "-m", "server.main"]
