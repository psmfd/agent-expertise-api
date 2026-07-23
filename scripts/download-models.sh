#!/usr/bin/env bash
# download-models.sh — Download jina-embeddings-v2-small-en ONNX model files
#
# Usage:
#   ./scripts/download-models.sh                          # default: src/ExpertiseApi/models/
#   DEST_DIR=/custom/path ./scripts/download-models.sh    # custom destination
#   FORCE=1 ./scripts/download-models.sh                  # re-download even if files exist
#
# Downloads the jina-embeddings-v2-small-en model (~130 MB, FP32) and
# vocab.txt (~232 KB) from the upstream jinaai Hugging Face repo (ADR-017).
# The root model.onnx (token-level outputs; the SK connector applies mean
# pooling) is REQUIRED — model-w-mean-pooling.onnx bakes pooling into the
# graph and would be pooled twice.
#
# Idempotent: skips files that already exist and pass size + checksum
# validation; re-downloads stale/corrupt files (#456).
#
# To update checksums after a model version bump:
#   FORCE=1 ./scripts/download-models.sh
#   sha256sum src/ExpertiseApi/models/model.onnx src/ExpertiseApi/models/vocab.txt
# Then update SHA256_MODEL_ONNX, SHA256_VOCAB_TXT, and bump MODEL_VERSION below.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
DEST_DIR="${DEST_DIR:-${REPO_ROOT}/src/ExpertiseApi/models}"
FORCE="${FORCE:-0}"

# Bump MODEL_VERSION when model files change. This string is embedded in the
# script, so any change here automatically busts the CI cache (keyed on
# hashFiles('scripts/download-models.sh')).
MODEL_VERSION="2"

HF_BASE="https://huggingface.co/jinaai/jina-embeddings-v2-small-en/resolve/main"

# SHA-256 checksums for model version ${MODEL_VERSION}.
# Computed from: sha256sum model.onnx vocab.txt
SHA256_MODEL_ONNX="974fdefe71fc9889258f569132b35acae6278874c8d09dbdf7806d23ad0b4497"
SHA256_VOCAB_TXT="109753d618dbb576a35112f9c20ef35cf3517d46106175bcf010c986a4bef1df"

log() { printf '[download-models] %s\n' "$1"; }
err() { printf '[download-models] ERROR: %s\n' "$1" >&2; exit 1; }

# Portable SHA-256 computation (macOS ships shasum, not sha256sum).
sha256_of() {
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum "$1" | awk '{print $1}'
  elif command -v shasum >/dev/null 2>&1; then
    shasum -a 256 "$1" | awk '{print $1}'
  else
    err "No sha256 tool found (need sha256sum or shasum)"
  fi
}

# Download a single file if missing or suspiciously small.
# Args: <dest_file> <url> <min_bytes> <display_name> <expected_sha256>
download_file() {
  local dest="$1" url="$2" min_bytes="$3" display_name="$4" expected_sha256="$5"
  local filename="${dest##*/}"

  if [[ "${FORCE}" != "1" ]] && [[ -f "${dest}" ]]; then
    local existing_size
    existing_size=$(wc -c < "${dest}")
    if (( existing_size >= min_bytes )); then
      log "${filename} already present — verifying checksum"
      local existing_sha
      existing_sha=$(sha256_of "${dest}")
      if [[ "${existing_sha}" == "${expected_sha256}" ]]; then
        log "  ${filename}: checksum OK"
        return 0
      fi
      # A mismatching existing file is stale (model version bump) or corrupt —
      # re-download instead of aborting, so upgrade installs pick up new model
      # files automatically (#456). The temp-file checksum below is still the
      # hard gate: a bad upstream file aborts as before.
      log "  ${filename} checksum mismatch (expected ${expected_sha256}, got ${existing_sha}) — stale or corrupt; re-downloading"
    else
      log "  ${filename} exists but is suspiciously small (${existing_size} bytes) — re-downloading"
    fi
  fi

  log "Downloading ${display_name}..."
  local tmpfile
  tmpfile=$(mktemp "${dest}.XXXXXX")
  # No --retry-delay: a fixed delay makes curl IGNORE the server's
  # Retry-After header, so Hugging Face 429 rate-limits burned all four
  # attempts in ~15s and failed three CI smoke runs on 2026-06-11 (#295).
  # Without it curl uses exponential backoff (1,2,4,... s) AND honors
  # Retry-After on 429/503. --retry-max-time caps the worst case so a
  # hard outage cannot stall the install indefinitely.
  curl -fsSL --retry 8 --retry-max-time 300 --retry-all-errors "${url}" -o "${tmpfile}" \
    || { rm -f "${tmpfile}"; err "Failed to download ${filename} from ${url}"; }

  # Size + checksum are verified on the TEMP file, and the destination is only
  # replaced after both pass (atomic-on-success). Moving before verifying let a
  # failed re-download overwrite the existing file with unverified bytes
  # (review finding, 2026-07-23).
  local size
  size=$(wc -c < "${tmpfile}")
  (( size < min_bytes )) && { rm -f "${tmpfile}"; err "${filename} is suspiciously small (${size} bytes). Check the URL."; }

  local actual
  actual=$(sha256_of "${tmpfile}")
  if [[ "${actual}" != "${expected_sha256}" ]]; then
    rm -f "${tmpfile}"
    err "${filename} downloaded file failed checksum (expected ${expected_sha256}, got ${actual}). Upstream may have changed — verify HF_BASE and the pinned SHA-256; any pre-existing ${filename} was left untouched."
  fi

  mv "${tmpfile}" "${dest}"
  log "  ${filename}: ${size} bytes, checksum OK"
}

mkdir -p "${DEST_DIR}"

# min_bytes is a coarse error-page detector only (an HTML 404/consent page is
# far under 1 MiB) — the SHA-256 pin below is the actual integrity gate, so the
# floor deliberately does NOT track the model size (tests/install/ fixtures
# depend on it staying small).
download_file "${DEST_DIR}/model.onnx" "${HF_BASE}/model.onnx" 1048576 \
  "model.onnx (jina-embeddings-v2-small-en FP32, ~130 MB)" "${SHA256_MODEL_ONNX}"

download_file "${DEST_DIR}/vocab.txt" "${HF_BASE}/vocab.txt" 1024 \
  "vocab.txt" "${SHA256_VOCAB_TXT}"

log "Model files ready in ${DEST_DIR} (model version ${MODEL_VERSION})"
