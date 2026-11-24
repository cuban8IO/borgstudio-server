#!/usr/bin/env bash
# Creates a Debian container on a Proxmox VE host and installs BorgStudio Server in it.
# Run as root on the Proxmox host (shell of the node):
#
#   bash -c "$(curl -fsSL https://github.com/cuban8IO/borgstudio-server/releases/latest/download/proxmox-create-ct.sh)"
#
# Asks for container ID, name, resources, storage and network (Enter keeps the suggestion), downloads
# the newest Debian standard template if needed, creates an unprivileged container that starts with
# the host, and runs lxc-install.sh inside it.
#
# Without questions:  BORGSTUDIO_DEFAULTS=1 (plus any of the variables below to change a value)
#   CT_ID CT_HOSTNAME CT_CORES CT_MEMORY CT_DISK CT_STORAGE CT_TEMPLATE_STORAGE CT_BRIDGE CT_IP CT_GATEWAY
set -euo pipefail

REPOSITORY=cuban8IO/borgstudio-server
INSTALLER_URL="https://github.com/$REPOSITORY/releases/latest/download/lxc-install.sh"

die() {
  echo "Error: $*" >&2
  exit 1
}

[[ $EUID -eq 0 ]] || die "run as root on the Proxmox VE host"
command -v pct > /dev/null && command -v pveam > /dev/null || die "this is not a Proxmox VE host (pct/pveam missing)"

# ask <variable> <question> <suggestion>: keeps an already set variable, else asks (or takes the suggestion).
ask() {
  local name=$1 question=$2 suggestion=$3 answer
  if [[ -n "${!name:-}" ]]; then
    return
  fi
  if [[ "${BORGSTUDIO_DEFAULTS:-0}" == "1" ]]; then
    printf -v "$name" '%s' "$suggestion"
    return
  fi
  read -r -p "$question [$suggestion]: " answer < /dev/tty
  printf -v "$name" '%s' "${answer:-$suggestion}"
}

echo "BorgStudio Server – new container on $(hostname)"
echo

ask CT_ID "Container ID" "$(pvesh get /cluster/nextid)"
ask CT_HOSTNAME "Host name" "borgstudio-server"
ask CT_CORES "CPU cores" "2"
ask CT_MEMORY "Memory in MB" "1024"
ask CT_DISK "Disk size in GB" "8"

storages=$(pvesm status --content rootdir 2> /dev/null | awk 'NR > 1 { print $1 }' | paste -sd ' ' -)
suggested_storage=$(grep -qw local-lvm <<< "$storages" && echo local-lvm || awk '{ print $1 }' <<< "$storages")
[[ -n "$suggested_storage" ]] || die "no storage for containers found"
ask CT_STORAGE "Storage for the container ($storages)" "$suggested_storage"
ask CT_TEMPLATE_STORAGE "Storage for the template" "local"
ask CT_BRIDGE "Network bridge" "vmbr0"
ask CT_IP "IPv4 address with prefix (e.g. 192.168.1.50/24) or dhcp" "dhcp"
if [[ "$CT_IP" != "dhcp" ]]; then
  ask CT_GATEWAY "Gateway" ""
fi

pct status "$CT_ID" > /dev/null 2>&1 && die "container $CT_ID already exists"

echo
echo "Looking for the newest Debian template ..."
pveam update > /dev/null
template=$(pveam available --section system | awk '{ print $2 }' | grep -E '^debian-13-standard_' | sort -V | tail -n 1)
[[ -n "$template" ]] || template=$(pveam available --section system | awk '{ print $2 }' | grep -E '^debian-12-standard_' | sort -V | tail -n 1)
[[ -n "$template" ]] || die "no Debian standard template available"
if ! pveam list "$CT_TEMPLATE_STORAGE" | grep -q "$template"; then
  echo "Downloading $template ..."
  pveam download "$CT_TEMPLATE_STORAGE" "$template" > /dev/null
fi

network="name=eth0,bridge=$CT_BRIDGE,ip=$CT_IP"
if [[ "$CT_IP" != "dhcp" && -n "${CT_GATEWAY:-}" ]]; then
  network+=",gw=$CT_GATEWAY"
fi

echo "Creating container $CT_ID ($CT_HOSTNAME) ..."
pct create "$CT_ID" "$CT_TEMPLATE_STORAGE:vztmpl/$template" \
  --hostname "$CT_HOSTNAME" \
  --cores "$CT_CORES" \
  --memory "$CT_MEMORY" \
  --swap 512 \
  --rootfs "$CT_STORAGE:$CT_DISK" \
  --net0 "$network" \
  --unprivileged 1 \
  --features nesting=1 \
  --onboot 1 \
  --description "BorgStudio Server – https://github.com/$REPOSITORY" > /dev/null
pct start "$CT_ID"

echo "Waiting for the network ..."
for _ in $(seq 1 60); do
  if pct exec "$CT_ID" -- getent hosts github.com > /dev/null 2>&1; then
    break
  fi
  sleep 2
done
pct exec "$CT_ID" -- getent hosts github.com > /dev/null 2>&1 || die "container $CT_ID has no internet access"

echo "Installing BorgStudio Server in the container ..."
pct exec "$CT_ID" -- bash -c "apt-get update -qq && DEBIAN_FRONTEND=noninteractive apt-get install -y -qq curl ca-certificates > /dev/null"
pct exec "$CT_ID" -- bash -c "curl -fsSL '$INSTALLER_URL' | bash"

echo
echo "Done. Container $CT_ID runs BorgStudio Server; update later with:"
echo "  pct exec $CT_ID -- bash -c \"curl -fsSL '$INSTALLER_URL' | bash\""
