#!/usr/bin/env bash
# Installs or updates BorgStudio Server inside a Debian 12/13 or Ubuntu 22.04/24.04 container (LXC)
# or virtual machine. Run as root:
#
#   curl -fsSL https://github.com/cuban8IO/borgstudio-server/releases/latest/download/lxc-install.sh | bash
#
# Downloads the latest release for this machine's architecture, checks its SHA-256 checksum and runs
# the install.sh inside. It also sets up the command "update", which does the same again later:
# it installs a newer release and does nothing when the server is up to date.
#
# Channels: "stable" (the default) follows the releases; "dev" follows the development builds, made
# from every change to the dev branch, to try things before they are released. The channel is kept
# in /etc/borgstudio-server/channel. Before switching, the database is backed up; going back to
# stable keeps it, and if the released version cannot start with it, the backup from before the
# switch to dev is put back.
#
# Options:      --channel stable|dev     switch to that channel and update right away
#               --status                 show the channel, the installed and the newest version
#               --force                  install even if that version (or a newer one) is installed
# Environment:  BORGSTUDIO_VERSION=0.1.0 install that release instead of the latest one
set -euo pipefail

REPOSITORY=cuban8IO/borgstudio-server
INSTALL_DIR=/opt/borgstudio-server
UPDATE_COMMAND=/usr/bin/update
SETTINGS_DIR=/etc/borgstudio-server
CHANNEL_FILE=$SETTINGS_DIR/channel
# Written when switching from stable to dev: the database backup to fall back to when going back.
STABLE_BACKUP_FILE=$SETTINGS_DIR/stable-backup
DATABASE=$INSTALL_DIR/data/borgstudio-server.db
BACKUPS=$INSTALL_DIR/data/backups
SERVICE=borgstudio-server

# --- Console output (same look in all BorgStudio Server scripts) -------------------------------
if [[ -t 1 && -z "${NO_COLOR:-}" ]]; then
  BOLD=$'\e[1m' RESET=$'\e[0m' GREEN=$'\e[38;5;48m' PINK=$'\e[38;5;205m'
  ORANGE=$'\e[38;5;214m' RED=$'\e[38;5;203m' GRAY=$'\e[38;5;245m' INTERACTIVE=1
else
  BOLD='' RESET='' GREEN='' PINK='' ORANGE='' RED='' GRAY='' INTERACTIVE=0
fi
LOG=$(mktemp -t borgstudio-lxc-XXXXXX.log)
WORK=$(mktemp -d)
# The log stays only when something went wrong.
cleanup() {
  local status=$?
  rm -rf "$WORK"
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
warn() { printf ' %s!%s %s\n' "$ORANGE" "$RESET" "$*"; }
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

force=0
status_only=0
requested_channel=""
while (($# > 0)); do
  case "$1" in
    --force) force=1 ;;
    --status) status_only=1 ;;
    --channel=*) requested_channel=${1#--channel=} ;;
    --channel)
      shift
      requested_channel=${1:-}
      ;;
    *)
      echo "Unknown option $1 (known: --channel stable|dev, --status, --force)" >&2
      exit 2
      ;;
  esac
  shift
done

current_channel=$(cat "$CHANNEL_FILE" 2> /dev/null || echo stable)
channel=${requested_channel:-$current_channel}
case "$channel" in
  stable | dev) ;;
  *)
    echo "Unknown channel $channel (known: stable, dev)" >&2
    exit 2
    ;;
esac
switching=0
[[ "$channel" != "$current_channel" ]] && switching=1

installed=$(cat "$INSTALL_DIR/VERSION" 2> /dev/null || true)
if ((status_only)); then
  banner "Status"
elif ((switching)); then
  banner "Switching to the $channel channel"
elif [[ -n "$installed" ]]; then
  banner "Update for LXC containers and virtual machines"
else
  banner "Installation for LXC containers and virtual machines"
fi

[[ $EUID -eq 0 ]] || fail "Run as root"

system="unknown system"
if [[ -r /etc/os-release ]]; then
  # shellcheck disable=SC1091
  . /etc/os-release
  system="${PRETTY_NAME:-$system}"
  case "${ID:-}:${VERSION_ID:-}" in
    debian:12 | debian:13 | ubuntu:22.04 | ubuntu:24.04) ;;
    *) warn "Tested on Debian 12/13 and Ubuntu 22.04/24.04; this is $system" ;;
  esac
fi

case "$(uname -m)" in
  x86_64) rid=linux-x64 ;;
  aarch64 | arm64) rid=linux-arm64 ;;
  *) fail "Unsupported architecture $(uname -m) (supported: x86_64, aarch64)" ;;
esac
ok "System: $system ($(uname -m))"

# "update" fetches the newest installer of the channel each time, so fixes to this script arrive
# as well; "update --channel …" fetches the one of the channel it switches to.
write_update_command() {
  cat > "$UPDATE_COMMAND" << 'EOF'
#!/usr/bin/env bash
# Updates BorgStudio Server (set up by lxc-install.sh).
# Options: --channel stable|dev, --status, --force
set -euo pipefail
channel=$(cat /etc/borgstudio-server/channel 2> /dev/null || echo stable)
previous=""
for argument in "$@"; do
  case "$argument" in
    --channel=*) channel=${argument#--channel=} ;;
    *) [[ "$previous" == --channel ]] && channel=$argument ;;
  esac
  previous=$argument
done
if [[ "$channel" == dev ]]; then
  url=https://github.com/cuban8IO/borgstudio-server/releases/download/dev/lxc-install.sh
else
  url=https://github.com/cuban8IO/borgstudio-server/releases/latest/download/lxc-install.sh
fi
script=$(curl -fsSL "$url")
exec bash -c "$script" lxc-install.sh "$@"
EOF
  chmod 755 "$UPDATE_COMMAND"
}
((status_only)) || step "Setting up the command \"update\"" write_update_command

if [[ -n "${BORGSTUDIO_VERSION:-}" ]]; then
  version="${BORGSTUDIO_VERSION#v}"
  tag="v$version"
  ok "Requested release: $version"
elif [[ "$channel" == dev ]]; then
  # The development build is one pre-release, "dev", replaced with every change; its archives carry the version.
  version=$(curl -fsSL "https://api.github.com/repos/$REPOSITORY/releases/tags/dev" 2>> "$LOG" \
    | grep -o "\"name\": *\"BorgStudio\.Server-[^\"]*-$rid\.tar\.gz\"" | head -n 1 \
    | sed -e 's/.*BorgStudio\.Server-//' -e "s/-$rid\.tar\.gz\"//" || true)
  tag=dev
  [[ -n "$version" ]] || fail "Could not find the development build on GitHub"
  ok "Newest development build: $version"
else
  version=$(curl -fsSL "https://api.github.com/repos/$REPOSITORY/releases/latest" 2>> "$LOG" \
    | grep -o '"tag_name": *"v[^"]*"' | head -n 1 | grep -o 'v[0-9][^"]*' | cut -c2- || true)
  tag="v$version"
  [[ -n "$version" ]] || fail "Could not find the latest release on GitHub"
  ok "Latest release: $version"
fi

if ((status_only)); then
  echo
  info "Channel:   ${BOLD}$current_channel${RESET}"
  info "Installed: ${installed:-nothing}"
  info "Newest:    $version"
  echo
  info "Switch with: update --channel stable   or   update --channel dev"
  echo
  exit 0
fi

if [[ -n "$installed" && $force -eq 0 && $switching -eq 0 ]]; then
  if [[ "$installed" == "$version" ]]; then
    echo
    ok "${BOLD}BorgStudio Server $installed is up to date${RESET} (channel $channel)."
    echo
    exit 0
  fi
  # A downgrade could meet a database the older version does not understand. Development builds
  # follow each other, so on that channel any other build is the next one.
  if [[ "$channel" == stable && "$(printf '%s\n%s\n' "$installed" "$version" | sort -V | tail -n 1)" == "$installed" ]]; then
    fail "BorgStudio Server $installed is newer than $version; use --force to install $version anyway"
  fi
  info "Updating from $installed to $version"
fi

# Before switching channels: a copy of the database, with the service stopped so the copy is complete.
backup_database() {
  local name
  name="$(date +%Y%m%d-%H%M%S)-$installed-before-$channel"
  mkdir -p "$BACKUPS"
  systemctl stop "$SERVICE"
  for file in "$DATABASE" "$DATABASE-wal" "$DATABASE-shm"; do
    if [[ -f "$file" ]]; then
      cp -a "$file" "$BACKUPS/$name${file#"$DATABASE"}.db"
    fi
  done
  chown -R borgstudio:borgstudio "$BACKUPS"
  chmod 700 "$BACKUPS"
  # Keep the ten newest.
  find "$BACKUPS" -maxdepth 1 -name '*-before-*.db' -printf '%T@ %p\n' | sort -rn | tail -n +31 | cut -d' ' -f2- | xargs -r rm -f
  if [[ "$channel" == dev ]]; then
    mkdir -p "$SETTINGS_DIR"
    printf '%s\n' "$BACKUPS/$name.db" > "$STABLE_BACKUP_FILE"
  fi
}
if ((switching)) && [[ -f "$DATABASE" ]]; then
  step "Backing up the database" backup_database
  info "Backup: $BACKUPS"
fi

install_requirements() {
  export DEBIAN_FRONTEND=noninteractive
  apt-get update -qq
  # .NET needs ICU for languages and time zones; the package name carries its version.
  local icu
  icu=$(apt-cache search --names-only '^libicu[0-9]+$' | awk '{print $1}' | sort -V | tail -n 1)
  [[ -n "$icu" ]] || return 1
  apt-get install -y -qq --no-install-recommends ca-certificates curl tar "$icu"
}
step "Installing requirements (ICU, certificates)" install_requirements

file="BorgStudio.Server-$version-$rid.tar.gz"
base="https://github.com/$REPOSITORY/releases/download/$tag"
step "Downloading BorgStudio Server $version ($rid)" \
  curl -fsSL -o "$WORK/$file" "$base/$file"
step "Downloading checksums" curl -fsSL -o "$WORK/SHA256SUMS" "$base/SHA256SUMS"

verify() { (cd "$WORK" && grep " $file\$" SHA256SUMS | sha256sum -c -); }
step "Checking the SHA-256 checksum" verify
step "Unpacking" tar -xzf "$WORK/$file" -C "$WORK"

# Back on stable, the released version may not start with a database a development build changed:
# then the backup from before the switch to dev comes back.
restore_stable_backup() {
  local backup
  backup=$(cat "$STABLE_BACKUP_FILE")
  [[ -f "$backup" ]] || return 1
  systemctl stop "$SERVICE" || true
  rm -f "$DATABASE-wal" "$DATABASE-shm"
  cp -a "$backup" "$DATABASE"
  for suffix in -wal -shm; do
    if [[ -f "${backup%.db}$suffix.db" ]]; then
      cp -a "${backup%.db}$suffix.db" "$DATABASE$suffix"
    fi
  done
  chown borgstudio:borgstudio "$DATABASE"*
  systemctl start "$SERVICE"
}

echo
if ! BORGSTUDIO_NO_BANNER=1 bash "$WORK/borgstudio-server/install.sh" < /dev/null; then
  if [[ "$channel" == stable && $switching -eq 1 && -f "$STABLE_BACKUP_FILE" ]]; then
    warn "BorgStudio Server $version does not start with the database of the development build"
    step "Putting back the database from before the switch to dev" restore_stable_backup
    warn "Changes made during the development build (repositories, users, shares …) are gone; backups in the repositories stay"
  else
    exit 1
  fi
fi

if ((switching)) || [[ ! -f "$CHANNEL_FILE" ]]; then
  mkdir -p "$SETTINGS_DIR"
  printf '%s\n' "$channel" > "$CHANNEL_FILE"
  [[ "$channel" == stable ]] && rm -f "$STABLE_BACKUP_FILE"
  ok "Channel: $channel"
fi
info "Update later with the command: ${BOLD}update${RESET} (channel $channel; switch with update --channel stable|dev)"
echo
