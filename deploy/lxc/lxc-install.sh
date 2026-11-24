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
# Options:      --force                  install even if that version (or a newer one) is installed
# Environment:  BORGSTUDIO_VERSION=0.1.0 install that release instead of the latest one
set -euo pipefail

REPOSITORY=cuban8IO/borgstudio-server
INSTALLER_URL="https://github.com/$REPOSITORY/releases/latest/download/lxc-install.sh"
INSTALL_DIR=/opt/borgstudio-server
UPDATE_COMMAND=/usr/bin/update

# --- Console output (same look in all BorgStudio Server scripts) -------------------------------
if [[ -t 1 && -z "${NO_COLOR:-}" ]]; then
  BOLD=$'\e[1m' RESET=$'\e[0m' GREEN=$'\e[38;5;48m' PINK=$'\e[38;5;205m'
  ORANGE=$'\e[38;5;214m' RED=$'\e[38;5;203m' GRAY=$'\e[38;5;245m' INTERACTIVE=1
else
  BOLD='' RESET='' GREEN='' PINK='' ORANGE='' RED='' GRAY='' INTERACTIVE=0
fi
LOG=$(mktemp -t borgstudio-lxc-XXXXXX.log)
WORK=$(mktemp -d)
trap 'status=$?; rm -rf "$WORK"; [[ $status -eq 0 ]] && rm -f "$LOG"; exit $status' EXIT

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
for argument in "$@"; do
  case "$argument" in
    --force) force=1 ;;
    *)
      echo "Unknown option $argument (known: --force)" >&2
      exit 2
      ;;
  esac
done

installed=$(cat "$INSTALL_DIR/VERSION" 2> /dev/null || true)
if [[ -n "$installed" ]]; then
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

# "update" fetches the newest installer each time, so fixes to this script arrive as well.
write_update_command() {
  cat > "$UPDATE_COMMAND" << EOF
#!/usr/bin/env bash
# Updates BorgStudio Server to the latest release (set up by lxc-install.sh). Options: --force
set -euo pipefail
script=\$(curl -fsSL "$INSTALLER_URL")
exec bash -c "\$script" lxc-install.sh "\$@"
EOF
  chmod 755 "$UPDATE_COMMAND"
}
step "Setting up the command \"update\"" write_update_command

if [[ -n "${BORGSTUDIO_VERSION:-}" ]]; then
  version="${BORGSTUDIO_VERSION#v}"
  ok "Requested release: $version"
else
  version=$(curl -fsSL "https://api.github.com/repos/$REPOSITORY/releases/latest" 2>> "$LOG" \
    | grep -o '"tag_name": *"v[^"]*"' | head -n 1 | grep -o 'v[0-9][^"]*' | cut -c2- || true)
  [[ -n "$version" ]] || fail "Could not find the latest release on GitHub"
  ok "Latest release: $version"
fi

if [[ -n "$installed" && $force -eq 0 ]]; then
  if [[ "$installed" == "$version" ]]; then
    echo
    ok "${BOLD}BorgStudio Server $installed is up to date.${RESET}"
    echo
    exit 0
  fi
  # A downgrade could meet a database the older version does not understand.
  if [[ "$(printf '%s\n%s\n' "$installed" "$version" | sort -V | tail -n 1)" == "$installed" ]]; then
    fail "BorgStudio Server $installed is newer than $version; use --force to install $version anyway"
  fi
  info "Updating from $installed to $version"
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
base="https://github.com/$REPOSITORY/releases/download/v$version"
step "Downloading BorgStudio Server $version ($rid)" \
  curl -fsSL -o "$WORK/$file" "$base/$file"
step "Downloading checksums" curl -fsSL -o "$WORK/SHA256SUMS" "$base/SHA256SUMS"

verify() { (cd "$WORK" && grep " $file\$" SHA256SUMS | sha256sum -c -); }
step "Checking the SHA-256 checksum" verify
step "Unpacking" tar -xzf "$WORK/$file" -C "$WORK"

echo
BORGSTUDIO_NO_BANNER=1 bash "$WORK/borgstudio-server/install.sh" < /dev/null
info "Update later with the command: ${BOLD}update${RESET}"
echo
