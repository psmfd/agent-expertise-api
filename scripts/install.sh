#!/usr/bin/env bash
#
# install.sh — install agent-expertise-api as a native OS service on
# macOS (launchd LaunchAgent) or Linux (systemd --user unit).
#
# This is the Archetype A2 installer (no Docker). For the Compose flow see
# deploy/local/docker-compose.yml; for k8s see helm/expertise-api/.
#
# Usage:
#   scripts/install.sh [--prefix DIR] [--bind ADDR:PORT] [--system]
#                      [--publish-mode fdd|scd] [--skip-preflight]
#                      [--rid RID] [--help]
#
# Defaults: per-user install, fdd publish, bind 127.0.0.1:8080.
#

set -euo pipefail

# ---------------------------------------------------------------------------
# Helpers (style matches scripts/download-models.sh)
# ---------------------------------------------------------------------------
log()  { printf '[install] %s\n' "$1"; }
warn() { printf '[install] WARN: %s\n' "$1" >&2; }
err()  { printf '[install] ERROR: %s\n' "$1" >&2; exit 1; }

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"

# ---------------------------------------------------------------------------
# Defaults & arg parsing
# ---------------------------------------------------------------------------
INSTALL_SCOPE="user"          # user | system
PUBLISH_MODE="fdd"            # fdd | scd
BIND_ADDR="127.0.0.1:8080"
SKIP_PREFLIGHT=0
EXPLICIT_RID=""
PREFIX_OVERRIDE=""

usage() { sed -n '2,15p' "$0" | sed 's/^# \{0,1\}//'; }

while [[ $# -gt 0 ]]; do
  case "$1" in
    --prefix)         PREFIX_OVERRIDE="${2:?--prefix needs a path}"; shift 2 ;;
    --bind)           BIND_ADDR="${2:?--bind needs ADDR:PORT}"; shift 2 ;;
    --system)         INSTALL_SCOPE="system"; shift ;;
    --publish-mode)   PUBLISH_MODE="${2:?--publish-mode needs fdd|scd}"; shift 2 ;;
    --skip-preflight) SKIP_PREFLIGHT=1; shift ;;
    --rid)            EXPLICIT_RID="${2:?--rid needs a runtime identifier}"; shift 2 ;;
    --help|-h)        usage; exit 0 ;;
    *)                err "unknown flag: $1 (try --help)" ;;
  esac
done

[[ "${PUBLISH_MODE}" == "fdd" || "${PUBLISH_MODE}" == "scd" ]] \
  || err "--publish-mode must be 'fdd' or 'scd'"

# ---------------------------------------------------------------------------
# OS detection
# ---------------------------------------------------------------------------
detect_os() {
  case "$(uname -s)" in
    Darwin)  echo macos ;;
    Linux)
      if grep -qiE '(microsoft|wsl)' /proc/version 2>/dev/null; then
        echo wsl
      else
        echo linux
      fi ;;
    MINGW*|MSYS*|CYGWIN*)
      err "Use scripts/install.ps1 from native PowerShell 7+; Git-Bash cannot create Windows Services." ;;
    *) err "unsupported OS: $(uname -s)" ;;
  esac
}

detect_rid() {
  local arch; arch="$(uname -m)"
  case "${OS}" in
    macos)
      case "${arch}" in
        arm64)  echo osx-arm64 ;;
        x86_64) echo osx-x64 ;;
        *)      err "unsupported macOS arch: ${arch}" ;;
      esac ;;
    linux|wsl)
      case "${arch}" in
        x86_64)         echo linux-x64 ;;
        aarch64|arm64)  echo linux-arm64 ;;
        *)              err "unsupported Linux arch: ${arch}" ;;
      esac ;;
  esac
}

OS="$(detect_os)"
RID="${EXPLICIT_RID:-$(detect_rid)}"

# ---------------------------------------------------------------------------
# Path layout per OS (matches notes/agent-expertise-api-hosting.md §A2)
# ---------------------------------------------------------------------------
if [[ -n "${PREFIX_OVERRIDE}" ]]; then
  PREFIX="${PREFIX_OVERRIDE}"
elif [[ "${OS}" == "macos" ]]; then
  PREFIX="${HOME}/Library/Application Support/expertise-api"
  LOG_DIR="${HOME}/Library/Logs/expertise-api"
  CONFIG_DIR="${HOME}/Library/Application Support/expertise-api"
else
  # Linux / WSL — XDG
  if [[ "${INSTALL_SCOPE}" == "system" ]]; then
    PREFIX="/opt/expertise-api"
    LOG_DIR="/var/log/expertise-api"
    CONFIG_DIR="/etc/expertise-api"
  else
    PREFIX="${HOME}/.local/share/expertise-api"
    LOG_DIR="${XDG_STATE_HOME:-${HOME}/.local/state}/expertise-api"
    CONFIG_DIR="${XDG_CONFIG_HOME:-${HOME}/.config}/expertise-api"
  fi
fi

BIN_DIR="${PREFIX}/bin"
MODEL_DIR="${PREFIX}/models"
SECRETS_FILE="${CONFIG_DIR}/secrets.env"
WRAPPER_SCRIPT="${BIN_DIR}/launch-expertise-api.sh"

# ---------------------------------------------------------------------------
# Pre-flight
# ---------------------------------------------------------------------------
preflight() {
  log "pre-flight: OS=${OS} RID=${RID} scope=${INSTALL_SCOPE} mode=${PUBLISH_MODE}"

  if [[ "${PUBLISH_MODE}" == "fdd" ]]; then
    command -v dotnet >/dev/null 2>&1 \
      || err "dotnet CLI not found in PATH (required for fdd; pass --publish-mode scd to skip)"
    if ! dotnet --list-runtimes 2>/dev/null | grep -qE '^Microsoft\.AspNetCore\.App 10\.'; then
      err "ASP.NET Core 10 runtime not installed (need 'Microsoft.AspNetCore.App 10.x'; install via https://dot.net)"
    fi
    log "dotnet runtime: OK"
  fi

  local port="${BIND_ADDR##*:}"
  if command -v lsof >/dev/null 2>&1; then
    if lsof -iTCP:"${port}" -sTCP:LISTEN -n -P >/dev/null 2>&1; then
      err "port ${port} already in use"
    fi
    log "port ${port}: free"
  elif command -v ss >/dev/null 2>&1; then
    if ss -ltn "sport = :${port}" 2>/dev/null | grep -q LISTEN; then
      err "port ${port} already in use"
    fi
    log "port ${port}: free"
  else
    warn "skipping port-in-use check (no lsof or ss)"
  fi

  local pg_host="${PG_HOST:-127.0.0.1}" pg_port="${PG_PORT:-5432}"
  if command -v nc >/dev/null 2>&1; then
    if nc -z -w 2 "${pg_host}" "${pg_port}" 2>/dev/null; then
      log "postgres ${pg_host}:${pg_port}: reachable"
    else
      warn "postgres ${pg_host}:${pg_port} NOT reachable — service will start but health checks will fail"
    fi
  elif (exec 3<>"/dev/tcp/${pg_host}/${pg_port}") 2>/dev/null; then
    exec 3<&-; exec 3>&-
    log "postgres ${pg_host}:${pg_port}: reachable (via /dev/tcp)"
  else
    warn "postgres reachability skipped (no nc, no bash /dev/tcp)"
  fi

  local need_mib=200 avail_kib avail_mib
  avail_kib=$(df -P "$(dirname "${PREFIX}")" 2>/dev/null | awk 'NR==2 {print $4}' || echo 0)
  avail_mib=$(( avail_kib / 1024 ))
  if (( avail_mib > 0 )) && (( avail_mib < need_mib )); then
    err "insufficient disk space at ${PREFIX}: ${avail_mib} MiB free, need ${need_mib}"
  fi
  log "disk space: ${avail_mib} MiB"
}

# ---------------------------------------------------------------------------
# Publish
# ---------------------------------------------------------------------------
publish_app() {
  log "publishing to ${BIN_DIR} (rid=${RID}, mode=${PUBLISH_MODE})"
  mkdir -p "${BIN_DIR}"
  local self_contained="false"
  [[ "${PUBLISH_MODE}" == "scd" ]] && self_contained="true"
  ( cd "${REPO_ROOT}" && dotnet publish src/ExpertiseApi/ExpertiseApi.csproj \
      --configuration Release \
      --runtime "${RID}" \
      --self-contained "${self_contained}" \
      -p:UseAppHost=true \
      --output "${BIN_DIR}" )
  log "publish: complete"
}

# ---------------------------------------------------------------------------
# Models
# ---------------------------------------------------------------------------
ensure_models() {
  if [[ -f "${MODEL_DIR}/model.onnx" && -f "${MODEL_DIR}/vocab.txt" ]]; then
    log "ONNX models present at ${MODEL_DIR}"
    return
  fi
  log "downloading ONNX models to ${MODEL_DIR}"
  mkdir -p "${MODEL_DIR}"
  DEST_DIR="${MODEL_DIR}" "${REPO_ROOT}/scripts/download-models.sh"
}

# ---------------------------------------------------------------------------
# Config / secrets stub
# ---------------------------------------------------------------------------
ensure_config_stubs() {
  mkdir -p "${CONFIG_DIR}" "${LOG_DIR}"
  if [[ ! -f "${SECRETS_FILE}" ]]; then
    log "creating secrets stub at ${SECRETS_FILE} (chmod 600) — edit before starting service"
    cat > "${SECRETS_FILE}.tmp" <<'EOF'
# secrets.env — sourced by launch-expertise-api.sh / EnvironmentFile= directive.
# chmod 600. Do NOT commit. Do NOT log.
#
# Set the connection string after install. Example for native Postgres:
#   ConnectionStrings__DefaultConnection=Host=127.0.0.1;Port=5432;Database=expertise;Username=expertise;Password=CHANGE_ME
#
# When Auth:Mode=Oidc, populate per-issuer overrides via Auth__Oidc__Issuers__N__*
# environment variables — see appsettings.json for the shape.
EOF
    mv "${SECRETS_FILE}.tmp" "${SECRETS_FILE}"
    chmod 600 "${SECRETS_FILE}"
  else
    log "secrets file present at ${SECRETS_FILE} (preserved)"
  fi
}

# ---------------------------------------------------------------------------
# Wrapper script — sources secrets, then exec's dotnet (or native binary)
# ---------------------------------------------------------------------------
write_wrapper() {
  cat > "${WRAPPER_SCRIPT}.tmp" <<EOF
#!/usr/bin/env bash
# launch-expertise-api.sh — service entrypoint.
# Sources secrets.env then exec's the API. Generated by scripts/install.sh.
set -euo pipefail

SECRETS_FILE="${SECRETS_FILE}"
if [[ -f "\${SECRETS_FILE}" ]]; then
  set -a
  # shellcheck disable=SC1090
  . "\${SECRETS_FILE}"
  set +a
fi

export ASPNETCORE_URLS="http://${BIND_ADDR}"
export ASPNETCORE_ENVIRONMENT="\${ASPNETCORE_ENVIRONMENT:-Production}"
export DOTNET_NOLOGO=true
export DOTNET_PRINT_TELEMETRY_MESSAGE=false
export Onnx__ModelPath="${MODEL_DIR}/model.onnx"
export Onnx__VocabPath="${MODEL_DIR}/vocab.txt"

if [[ -x "${BIN_DIR}/ExpertiseApi" ]]; then
  exec "${BIN_DIR}/ExpertiseApi"
else
  exec dotnet "${BIN_DIR}/ExpertiseApi.dll"
fi
EOF
  mv "${WRAPPER_SCRIPT}.tmp" "${WRAPPER_SCRIPT}"
  chmod 755 "${WRAPPER_SCRIPT}"
  log "wrapper: ${WRAPPER_SCRIPT}"
}

# ---------------------------------------------------------------------------
# Service file installation per OS
# ---------------------------------------------------------------------------
install_systemd_user() {
  local unit_dir unit_path
  unit_dir="${XDG_CONFIG_HOME:-${HOME}/.config}/systemd/user"
  unit_path="${unit_dir}/expertise-api.service"
  mkdir -p "${unit_dir}"

  local tpl="${SCRIPT_DIR}/service-templates/expertise-api.service.tmpl"
  [[ -f "${tpl}" ]] || err "missing template: ${tpl}"

  # sed substitutions (avoid envsubst dependency)
  sed \
    -e "s|@@WRAPPER@@|${WRAPPER_SCRIPT}|g" \
    -e "s|@@WORKDIR@@|${PREFIX}|g" \
    -e "s|@@SECRETS_FILE@@|${SECRETS_FILE}|g" \
    -e "s|@@LOG_DIR@@|${LOG_DIR}|g" \
    "${tpl}" > "${unit_path}.tmp"
  mv "${unit_path}.tmp" "${unit_path}"
  log "systemd user unit: ${unit_path}"

  systemctl --user daemon-reload
  if systemctl --user is-enabled expertise-api.service >/dev/null 2>&1; then
    systemctl --user restart expertise-api.service
    log "systemd: restarted"
  else
    systemctl --user enable --now expertise-api.service
    log "systemd: enabled + started"
  fi

  # Lingering — service survives logout
  if command -v loginctl >/dev/null 2>&1; then
    if ! loginctl show-user "$(id -un)" 2>/dev/null | grep -q 'Linger=yes'; then
      warn "user lingering not enabled — service stops at logout. Enable with: sudo loginctl enable-linger \$USER"
    fi
  fi
}

install_launchd() {
  local plist_dir plist_path label="com.thesemicolon.expertise-api"
  plist_dir="${HOME}/Library/LaunchAgents"
  plist_path="${plist_dir}/${label}.plist"
  mkdir -p "${plist_dir}"

  local tpl="${SCRIPT_DIR}/service-templates/expertise-api.plist.tmpl"
  [[ -f "${tpl}" ]] || err "missing template: ${tpl}"

  sed \
    -e "s|@@LABEL@@|${label}|g" \
    -e "s|@@WRAPPER@@|${WRAPPER_SCRIPT}|g" \
    -e "s|@@WORKDIR@@|${PREFIX}|g" \
    -e "s|@@LOG_DIR@@|${LOG_DIR}|g" \
    "${tpl}" > "${plist_path}.tmp"
  mv "${plist_path}.tmp" "${plist_path}"
  log "launchd plist: ${plist_path}"

  local domain
  domain="gui/$(id -u)"
  launchctl bootout "${domain}/${label}" 2>/dev/null || true
  launchctl bootstrap "${domain}" "${plist_path}"
  launchctl enable "${domain}/${label}"
  launchctl kickstart -k "${domain}/${label}"
  log "launchd: bootstrapped + kickstarted"
}

install_service() {
  case "${OS}" in
    linux|wsl)
      [[ "${INSTALL_SCOPE}" == "user" ]] \
        || err "--system install not yet supported by this script (use systemd unit at /etc/systemd/system/ manually)"
      install_systemd_user ;;
    macos)
      install_launchd ;;
  esac
}

# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------
main() {
  if (( SKIP_PREFLIGHT == 0 )); then preflight; fi
  publish_app
  ensure_models
  ensure_config_stubs
  write_wrapper
  install_service

  log "install complete"
  log "  binary:   ${BIN_DIR}"
  log "  models:   ${MODEL_DIR}"
  log "  config:   ${CONFIG_DIR}"
  log "  logs:     ${LOG_DIR}"
  log "  bind:     http://${BIND_ADDR}"
  log ""
  log "Edit ${SECRETS_FILE} to set the database connection string,"
  log "then check the service with: scripts/expertise-apictl status"
}

main "$@"
