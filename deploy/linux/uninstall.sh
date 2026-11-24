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

purge=0
case "${1:-}" in
  --purge) purge=1 ;;
  "") ;;
  *)
    echo "Usage: $0 [--purge]" >&2
    exit 2
    ;;
esac

if [[ $EUID -ne 0 ]]; then
  echo "Error: run as root, e.g.: sudo $0" >&2
  exit 1
fi

if systemctl list-unit-files "$SERVICE.service" > /dev/null 2>&1; then
  systemctl disable --now "$SERVICE" 2> /dev/null || true
fi
rm -f "/etc/systemd/system/$SERVICE.service"
systemctl daemon-reload

if [[ -d "$INSTALL_DIR" ]]; then
  if [[ $purge -eq 1 ]]; then
    rm -rf "$INSTALL_DIR"
  else
    find "$INSTALL_DIR" -mindepth 1 -maxdepth 1 ! -name data ! -name "$CONFIG" -exec rm -rf {} +
  fi
fi

if [[ $purge -eq 1 ]] && id -u "$SERVICE_USER" > /dev/null 2>&1; then
  userdel "$SERVICE_USER"
fi

if [[ $purge -eq 1 ]]; then
  echo "BorgStudio Server was removed, including its data."
else
  echo "BorgStudio Server was removed. Data and configuration remain in $INSTALL_DIR."
fi
