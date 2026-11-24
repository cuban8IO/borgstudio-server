#!/usr/bin/env bash
# Creates a Debian container on a Proxmox VE host and installs BorgStudio Server in it.
# Run as root on the Proxmox host (shell of the node):
#
#   bash -c "$(curl -fsSL https://github.com/cuban8IO/borgstudio-server/releases/latest/download/proxmox-create-ct.sh)"
#
# Dialogs offer default settings or let you choose ID, name, resources, storage and network; then the
# script downloads the newest Debian standard template if needed, creates an unprivileged container
# that starts with the host, and runs lxc-install.sh inside it.
#
# Without dialogs:  BORGSTUDIO_DEFAULTS=1 (plus any of the variables below to change a value)
#   CT_ID CT_HOSTNAME CT_CORES CT_MEMORY CT_DISK CT_STORAGE CT_TEMPLATE_STORAGE CT_BRIDGE CT_IP CT_GATEWAY
set -euo pipefail

REPOSITORY=cuban8IO/borgstudio-server
INSTALLER_URL="https://github.com/$REPOSITORY/releases/latest/download/lxc-install.sh"
BACKTITLE="BorgStudio Server – Proxmox VE"

# --- Console output (same look in all BorgStudio Server scripts) -------------------------------
if [[ -t 1 && -z "${NO_COLOR:-}" ]]; then
  BOLD=$'\e[1m' RESET=$'\e[0m' GREEN=$'\e[38;5;48m' PINK=$'\e[38;5;205m'
  RED=$'\e[38;5;203m' GRAY=$'\e[38;5;245m' INTERACTIVE=1
else
  BOLD='' RESET='' GREEN='' PINK='' RED='' GRAY='' INTERACTIVE=0
fi
LOG=$(mktemp -t borgstudio-proxmox-XXXXXX.log)
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

abort() {
  clear
  info "Cancelled – nothing was changed."
  exit 0
}

# input <variable> <title> <question>: a whiptail input box, pre-filled with the variable's value.
input() {
  local value
  value=$(whiptail --backtitle "$BACKTITLE" --title "$2" --inputbox "\n$3" 11 66 "${!1}" 3>&1 1>&2 2>&3) || abort
  printf -v "$1" '%s' "$value"
}

# number <variable> <title> <question>: like input, asks again until the answer is a positive number.
number() {
  while true; do
    input "$@"
    [[ "${!1}" =~ ^[1-9][0-9]*$ ]] && return
    whiptail --backtitle "$BACKTITLE" --title "$2" --msgbox "\nPlease enter a whole number." 9 50
  done
}

banner "New container on Proxmox VE ($(hostname))"

[[ $EUID -eq 0 ]] || fail "Run as root on the Proxmox VE host"
if ! command -v pct > /dev/null || ! command -v pveam > /dev/null; then
  fail "This is not a Proxmox VE host (pct/pveam missing)"
fi

CT_ID=${CT_ID:-$(pvesh get /cluster/nextid)}
CT_HOSTNAME=${CT_HOSTNAME:-borgstudio-server}
CT_CORES=${CT_CORES:-2}
CT_MEMORY=${CT_MEMORY:-1024}
CT_DISK=${CT_DISK:-8}
CT_TEMPLATE_STORAGE=${CT_TEMPLATE_STORAGE:-local}
CT_BRIDGE=${CT_BRIDGE:-vmbr0}
CT_IP=${CT_IP:-dhcp}
CT_GATEWAY=${CT_GATEWAY:-}

mapfile -t storages < <(pvesm status --content rootdir 2> /dev/null | awk 'NR > 1 { print $1 }')
((${#storages[@]} > 0)) || fail "No storage for containers found"
if [[ -z "${CT_STORAGE:-}" ]]; then
  CT_STORAGE=${storages[0]}
  for storage in "${storages[@]}"; do
    [[ "$storage" == "local-lvm" ]] && CT_STORAGE=local-lvm
  done
fi

if [[ "${BORGSTUDIO_DEFAULTS:-0}" != "1" ]]; then
  command -v whiptail > /dev/null || fail "whiptail is missing (or run with BORGSTUDIO_DEFAULTS=1)"

  whiptail --backtitle "$BACKTITLE" --title "BorgStudio Server" --yesno \
    "\nThis creates a new LXC container on this host and installs BorgStudio Server in it.\n\nContinue?" 12 66 || abort

  choice=$(whiptail --backtitle "$BACKTITLE" --title "Settings" --menu "\nUse the default settings or choose them yourself?" 14 76 2 \
    "Default" "ID $CT_ID, $CT_CORES cores, $CT_MEMORY MB, $CT_DISK GB on $CT_STORAGE, $CT_IP" \
    "Advanced" "Choose ID, name, resources, storage and network" 3>&1 1>&2 2>&3) || abort

  if [[ "$choice" == "Advanced" ]]; then
    number CT_ID "Container ID" "ID of the new container:"
    input CT_HOSTNAME "Host name" "Host name of the container:"
    number CT_CORES "CPU" "Number of CPU cores:"
    number CT_MEMORY "Memory" "Memory in MB:"
    number CT_DISK "Disk" "Disk size in GB:"

    storage_items=()
    for storage in "${storages[@]}"; do
      state=OFF
      [[ "$storage" == "$CT_STORAGE" ]] && state=ON
      storage_items+=("$storage" "" "$state")
    done
    CT_STORAGE=$(whiptail --backtitle "$BACKTITLE" --title "Storage" --radiolist \
      "\nStorage for the container's disk (space to select):" 16 66 "${#storages[@]}" "${storage_items[@]}" 3>&1 1>&2 2>&3) || abort

    input CT_BRIDGE "Network" "Network bridge:"
    input CT_IP "Network" "IPv4 address with prefix (e.g. 192.168.1.50/24), or dhcp:"
    if [[ "$CT_IP" != "dhcp" ]]; then
      input CT_GATEWAY "Network" "Gateway (e.g. 192.168.1.1):"
    fi
  fi

  network_text=$CT_IP
  [[ "$CT_IP" != "dhcp" && -n "$CT_GATEWAY" ]] && network_text="$CT_IP via $CT_GATEWAY"
  whiptail --backtitle "$BACKTITLE" --title "Create the container?" --yesno \
    "\nID:        $CT_ID\nName:      $CT_HOSTNAME\nCPU:       $CT_CORES cores\nMemory:    $CT_MEMORY MB\nDisk:      $CT_DISK GB on $CT_STORAGE\nNetwork:   $CT_BRIDGE, $network_text\n\nThe container is unprivileged and starts with the host." 18 66 || abort
fi

pct status "$CT_ID" > /dev/null 2>&1 && fail "Container $CT_ID already exists"
ok "Container $CT_ID ($CT_HOSTNAME): $CT_CORES cores, $CT_MEMORY MB, $CT_DISK GB on $CT_STORAGE, $CT_BRIDGE/$CT_IP"

step "Updating the list of templates" pveam update

template=$(pveam available --section system | awk '{ print $2 }' | grep -E '^debian-13-standard_' | sort -V | tail -n 1 || true)
[[ -n "$template" ]] || template=$(pveam available --section system | awk '{ print $2 }' | grep -E '^debian-12-standard_' | sort -V | tail -n 1 || true)
[[ -n "$template" ]] || fail "No Debian standard template available"
if pveam list "$CT_TEMPLATE_STORAGE" | grep -q "$template"; then
  ok "Template $template is present"
else
  step "Downloading $template" pveam download "$CT_TEMPLATE_STORAGE" "$template"
fi

network="name=eth0,bridge=$CT_BRIDGE,ip=$CT_IP"
if [[ "$CT_IP" != "dhcp" && -n "$CT_GATEWAY" ]]; then
  network+=",gw=$CT_GATEWAY"
fi

step "Creating container $CT_ID" pct create "$CT_ID" "$CT_TEMPLATE_STORAGE:vztmpl/$template" \
  --hostname "$CT_HOSTNAME" \
  --cores "$CT_CORES" \
  --memory "$CT_MEMORY" \
  --swap 512 \
  --rootfs "$CT_STORAGE:$CT_DISK" \
  --net0 "$network" \
  --unprivileged 1 \
  --features nesting=1 \
  --onboot 1 \
  --description "BorgStudio Server – https://github.com/$REPOSITORY"
step "Starting container $CT_ID" pct start "$CT_ID"

wait_for_network() {
  for _ in $(seq 1 60); do
    pct exec "$CT_ID" -- getent hosts github.com > /dev/null 2>&1 && return 0
    sleep 2
  done
  echo "Container $CT_ID has no internet access."
  return 1
}
step "Waiting for the network" wait_for_network

step "Preparing the container" pct exec "$CT_ID" -- bash -c \
  "apt-get update -qq && DEBIAN_FRONTEND=noninteractive apt-get install -y -qq curl ca-certificates"
step "Installing BorgStudio Server (this takes a minute)" pct exec "$CT_ID" -- bash -c "curl -fsSL '$INSTALLER_URL' | bash"

address=$(pct exec "$CT_ID" -- hostname -I 2> /dev/null | awk '{print $1}' || true)
version=$(pct exec "$CT_ID" -- cat /opt/borgstudio-server/VERSION 2> /dev/null || echo "?")
setup_code=$(pct exec "$CT_ID" -- cat /opt/borgstudio-server/data/setup-code.txt 2> /dev/null || true)

facts=(
  "Container" "$CT_ID ($CT_HOSTNAME)"
  "Address" "https://${address:-$CT_HOSTNAME}:8443/"
)
[[ -n "$setup_code" ]] && facts+=("Setup code" "$setup_code")
facts+=(
  "Update" "pct exec $CT_ID -- update"
  "Console" "pct enter $CT_ID"
)
summary "BorgStudio Server $version is running" "${facts[@]}"
info "Open the address in a browser and enter the setup code to create the first administrator."
info "The browser warns about the self-signed certificate; upload your own under Settings."
