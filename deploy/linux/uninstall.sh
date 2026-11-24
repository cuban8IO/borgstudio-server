#!/usr/bin/env bash
# Removes the BorgStudio Server service and program.
#
#   sudo /opt/borgstudio-server/uninstall.sh           keeps data/ and borgstudio-server.json
#   sudo /opt/borgstudio-server/uninstall.sh --purge   also deletes them and the "borgstudio" user
set -euo pipefail

INSTALL_DIR=/opt/borgstudio-server
SERVICE=borgstudio-server
SERVICE_USER=borgstudio
CONFIG=borgstudio-server.json

# --- Console output (same look in all BorgStudio Server scripts) -------------------------------
if [[ -t 1 && -z "${NO_COLOR:-}" ]]; then
  BOLD=$'\e[1m' RESET=$'\e[0m' GREEN=$'\e[38;5;48m' PINK=$'\e[38;5;205m'
  RED=$'\e[38;5;203m' GRAY=$'\e[38;5;245m' INTERACTIVE=1
else
  BOLD='' RESET='' GREEN='' PINK='' RED='' GRAY='' INTERACTIVE=0
fi
LOG=$(mktemp -t borgstudio-uninstall-XXXXXX.log)
# The log stays only when something went wrong.
cleanup() {
  local status=$?
  if [[ $status -eq 0 ]]; then rm -f "$LOG"; fi
  exit "$status"
}
trap cleanup EXIT

banner() {
  printf '%s%s' "$GREEN" "$BOLD"
  cat << 'EOF'
  ____                  ____  _             _ _
 | __ )  ___  _ __ __ _/ ___|| |_ _   _  __| (_) ___
 |  _ \ / _ \| '__/ _` \___ \| __| | | |/ _` | |/ _ \
 | |_) | (_) | | | (_| |___) | |_| |_| | (_| | | (_) |
 |____/ \___/|_|  \__, |____/ \__|\__,_|\__,_|_|\___/
EOF
  printf '                  |___/ %sS E R V E R%s\n\n  %s%s%s\n\n' "$PINK" "$RESET" "$GRAY" "$1" "$RESET"
}
ok() { printf ' %s✔%s %s\n' "$GREEN" "$RESET" "$*"; }
info() { printf ' %s•%s %s\n' "$GRAY" "$RESET" "$*"; }
fail() {
  printf ' %s✖%s %s\n' "$RED" "$RESET" "$*" >&2
  if [[ -s "$LOG" ]]; then
    printf '\n %sLast lines of %s:%s\n' "$GRAY" "$LOG" "$RESET" >&2
    tail -n 15 "$LOG" | sed 's/^/   /' >&2
  fi
  exit 1
}
step() {
  local message=$1
  shift
  if [[ $INTERACTIVE -eq 1 ]]; then
    "$@" >> "$LOG" 2>&1 < /dev/null &
    local pid=$! frame=0 frames=(⠋ ⠙ ⠹ ⠸ ⠼ ⠴ ⠦ ⠧ ⠇ ⠏)
    while kill -0 "$pid" 2> /dev/null; do
      printf '\r %s%s%s %s' "$PINK" "${frames[frame++ % 10]}" "$RESET" "$message"
      sleep 0.1
    done
    printf '\r\e[K'
    if wait "$pid"; then ok "$message"; else fail "$message"; fi
  elif "$@" >> "$LOG" 2>&1 < /dev/null; then
    ok "$message"
  else
    fail "$message"
  fi
}
# ------------------------------------------------------------------------------------------------

purge=0
case "${1:-}" in
  --purge) purge=1 ;;
  "") ;;
  *)
    echo "Usage: $0 [--purge]" >&2
    exit 2
    ;;
esac

if [[ $purge -eq 1 ]]; then
  banner "Removing the program and all data"
else
  banner "Removing the program (data and configuration stay)"
fi

[[ $EUID -eq 0 ]] || fail "Run as root, e.g.: sudo $0"

remove_service() {
  systemctl disable --now "$SERVICE" 2> /dev/null || true
  rm -f "/etc/systemd/system/$SERVICE.service"
  systemctl daemon-reload
}
step "Stopping and removing the service $SERVICE" remove_service

if [[ -d "$INSTALL_DIR" ]]; then
  if [[ $purge -eq 1 ]]; then
    step "Deleting $INSTALL_DIR with all data" rm -rf "$INSTALL_DIR"
  else
    step "Deleting the program files" \
      find "$INSTALL_DIR" -mindepth 1 -maxdepth 1 ! -name data ! -name "$CONFIG" -exec rm -rf {} +
  fi
fi

if [[ $purge -eq 1 ]] && id -u "$SERVICE_USER" > /dev/null 2>&1; then
  step "Removing the system user $SERVICE_USER" userdel "$SERVICE_USER"
fi

echo
if [[ $purge -eq 1 ]]; then
  ok "BorgStudio Server was removed, including its data."
else
  ok "BorgStudio Server was removed."
  info "Data and configuration remain in $INSTALL_DIR; install again to use them, or run with --purge to delete them."
fi
