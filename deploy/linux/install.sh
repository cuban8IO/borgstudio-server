#!/usr/bin/env bash
# Installs or updates BorgStudio Server as a systemd service.
#
# Run as root from the unpacked archive:  sudo ./install.sh
#
# The program goes to /opt/borgstudio-server and runs as the system user "borgstudio".
# An update replaces the program files and keeps data/ and borgstudio-server.json.
set -euo pipefail

INSTALL_DIR=/opt/borgstudio-server
SERVICE=borgstudio-server
SERVICE_USER=borgstudio
CONFIG=borgstudio-server.json
SOURCE_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)

# --- Console output (same look in all BorgStudio Server scripts) -------------------------------
if [[ -t 1 && -z "${NO_COLOR:-}" ]]; then
  BOLD=$'\e[1m' RESET=$'\e[0m' GREEN=$'\e[38;5;48m' PINK=$'\e[38;5;205m'
  ORANGE=$'\e[38;5;214m' RED=$'\e[38;5;203m' GRAY=$'\e[38;5;245m' INTERACTIVE=1
else
  BOLD='' RESET='' GREEN='' PINK='' ORANGE='' RED='' GRAY='' INTERACTIVE=0
fi
LOG=$(mktemp -t borgstudio-install-XXXXXX.log)
# The log stays only when something went wrong.
cleanup() {
  local status=$?
  if [[ $status -eq 0 ]]; then rm -f "$LOG"; fi
  exit "$status"
}
trap cleanup EXIT

banner() {
  [[ -n "${BORGSTUDIO_NO_BANNER:-}" ]] && return 0
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
warn() { printf ' %s!%s %s\n' "$ORANGE" "$RESET" "$*"; }
fail() {
  printf ' %s✖%s %s\n' "$RED" "$RESET" "$*" >&2
  if [[ -s "$LOG" ]]; then
    printf '\n %sLast lines of %s:%s\n' "$GRAY" "$LOG" "$RESET" >&2
    tail -n 15 "$LOG" | sed 's/^/   /' >&2
  fi
  exit 1
}
# step <message> <command…>: runs the command quietly (output goes to $LOG), with a spinner on a terminal.
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
# summary <title> <label> <value> [<label> <value> …]: a box with the most important facts.
summary() {
  local title=$1 width i length rule
  shift
  local -a labels=() values=()
  while (($# >= 2)); do
    labels+=("$1")
    values+=("$2")
    shift 2
  done
  width=${#title}
  for i in "${!labels[@]}"; do
    length=$((15 + ${#values[i]}))
    ((length > width)) && width=$length
  done
  rule=$(printf '%*s' $((width + 4)) '' | sed 's/ /─/g')
  printf '\n %s╭%s╮%s\n' "$GREEN" "$rule" "$RESET"
  printf ' %s│%s  %s%s%s%*s  %s│%s\n' "$GREEN" "$RESET" "$BOLD" "$title" "$RESET" $((width - ${#title})) '' "$GREEN" "$RESET"
  printf ' %s│%*s│%s\n' "$GREEN" $((width + 4)) '' "$RESET"
  for i in "${!labels[@]}"; do
    local color=''
    [[ "${labels[i]}" == "Setup code" ]] && color="$PINK$BOLD"
    printf ' %s│%s  %s%-15s%s%s%s%s%*s  %s│%s\n' "$GREEN" "$RESET" "$GRAY" "${labels[i]}" "$RESET" \
      "$color" "${values[i]}" "$RESET" $((width - 15 - ${#values[i]})) '' "$GREEN" "$RESET"
  done
  printf ' %s╰%s╯%s\n\n' "$GREEN" "$rule" "$RESET"
}
# ------------------------------------------------------------------------------------------------

version=$(cat "$SOURCE_DIR/VERSION" 2> /dev/null || echo "unknown")
banner "Installing version $version"

[[ $EUID -eq 0 ]] || fail "Run as root, e.g.: sudo $0"
command -v systemctl > /dev/null || fail "systemd is required"
[[ -x "$SOURCE_DIR/BorgStudio.Server" ]] || fail "Run this script from the unpacked BorgStudio Server archive"
[[ "$SOURCE_DIR" != "$INSTALL_DIR" ]] || fail "Run install.sh from the new archive, not from $INSTALL_DIR"

previous=$(cat "$INSTALL_DIR/VERSION" 2> /dev/null || true)
if [[ -n "$previous" ]]; then
  info "Installed: $previous – it is replaced, data and configuration stay"
fi

install_icu() {
  if command -v apt-get > /dev/null; then
    apt-get update -qq
    local icu
    icu=$(apt-cache search --names-only '^libicu[0-9]+$' | awk '{print $1}' | sort -V | tail -n 1)
    [[ -n "$icu" ]] || return 1
    DEBIAN_FRONTEND=noninteractive apt-get install -y -qq --no-install-recommends "$icu"
  elif command -v dnf > /dev/null; then
    dnf install -y -q libicu
  elif command -v zypper > /dev/null; then
    zypper --non-interactive install libicu
  else
    echo "No supported package manager; install ICU (libicu) yourself." >&2
    return 1
  fi
}

# .NET needs ICU (languages, time zones) and does not start without it; minimal systems often lack it.
if ldconfig -p 2> /dev/null | grep -q 'libicuuc\.so'; then
  ok "ICU is present"
else
  step "Installing ICU (libicu)" install_icu
fi

if id -u "$SERVICE_USER" > /dev/null 2>&1; then
  ok "System user $SERVICE_USER is present"
else
  step "Creating system user $SERVICE_USER" \
    useradd --system --home-dir "$INSTALL_DIR" --no-create-home --shell /usr/sbin/nologin "$SERVICE_USER"
fi

if systemctl is-active --quiet "$SERVICE"; then
  step "Stopping the running service" systemctl stop "$SERVICE"
fi

copy_program() {
  mkdir -p "$INSTALL_DIR"
  find "$INSTALL_DIR" -mindepth 1 -maxdepth 1 ! -name data ! -name "$CONFIG" -exec rm -rf {} +
  (cd "$SOURCE_DIR" && tar --exclude=./data --exclude="./$CONFIG" -cf - .) | (cd "$INSTALL_DIR" && tar -xf -)
}
step "Copying the program to $INSTALL_DIR" copy_program

set_permissions() {
  find "$INSTALL_DIR" -mindepth 1 -maxdepth 1 ! -name data ! -name "$CONFIG" -exec chown -R root:root {} +
  chmod 755 "$INSTALL_DIR" "$INSTALL_DIR/BorgStudio.Server" "$INSTALL_DIR/install.sh" "$INSTALL_DIR/uninstall.sh"
  mkdir -p "$INSTALL_DIR/data"
  chown -R "$SERVICE_USER:$SERVICE_USER" "$INSTALL_DIR/data"
  chmod 700 "$INSTALL_DIR/data"
  if [[ ! -f "$INSTALL_DIR/$CONFIG" ]]; then
    cp "$SOURCE_DIR/borgstudio-server.example.json" "$INSTALL_DIR/$CONFIG"
  fi
  # May hold a certificate password: readable for the service, not for everyone.
  chown "root:$SERVICE_USER" "$INSTALL_DIR/$CONFIG"
  chmod 640 "$INSTALL_DIR/$CONFIG"
}
step "Setting up data folder and configuration" set_permissions

write_service() {
  cat > "/etc/systemd/system/$SERVICE.service" << EOF
[Unit]
Description=BorgStudio Server
Documentation=https://github.com/cuban8IO/borgstudio-server
After=network-online.target
Wants=network-online.target

[Service]
Type=notify
User=$SERVICE_USER
Group=$SERVICE_USER
WorkingDirectory=$INSTALL_DIR
ExecStart=$INSTALL_DIR/BorgStudio.Server
Restart=on-failure
RestartSec=5
Environment=DOTNET_NOLOGO=1
NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=strict
ProtectHome=true
ReadWritePaths=$INSTALL_DIR/data

[Install]
WantedBy=multi-user.target
EOF
  systemctl daemon-reload
  systemctl enable "$SERVICE"
}
step "Setting up the systemd service $SERVICE" write_service

# "systemctl start" returns once the server reports it is ready (Type=notify); by then a fresh
# installation has written the setup code for the first administrator.
start_service() {
  if ! systemctl start "$SERVICE"; then
    journalctl -u "$SERVICE" -n 30 --no-pager
    return 1
  fi
}
step "Starting BorgStudio Server" start_service

address=$(hostname -I 2> /dev/null | awk '{print $1}')
port=$(grep -A3 '"Https"' "$INSTALL_DIR/$CONFIG" | grep -o '"Port": *[0-9]*' | grep -o '[0-9]*$' || true)

facts=(
  "Address" "https://${address:-$(hostname)}:${port:-8443}/"
)
if [[ -f "$INSTALL_DIR/data/setup-code.txt" ]]; then
  facts+=("Setup code" "$(cat "$INSTALL_DIR/data/setup-code.txt")")
fi
facts+=(
  "Configuration" "$INSTALL_DIR/$CONFIG"
  "Log" "journalctl -u $SERVICE"
  "Locked out" "runuser -u $SERVICE_USER -- $INSTALL_DIR/BorgStudio.Server reset-admin"
  "Uninstall" "$INSTALL_DIR/uninstall.sh"
)

if [[ -n "$previous" && "$previous" != "$version" ]]; then
  summary "BorgStudio Server updated: $previous -> $version" "${facts[@]}"
else
  summary "BorgStudio Server $version is running" "${facts[@]}"
fi

if [[ -f "$INSTALL_DIR/data/setup-code.txt" ]]; then
  info "Open the address in a browser and enter the setup code to create the first administrator."
  info "The browser warns about the self-signed certificate; upload your own under Settings."
fi
