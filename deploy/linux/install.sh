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

die() {
  echo "Error: $*" >&2
  exit 1
}

[[ $EUID -eq 0 ]] || die "run as root, e.g.: sudo $0"
command -v systemctl > /dev/null || die "systemd is required"
[[ -x "$SOURCE_DIR/BorgStudio.Server" ]] || die "run this script from the unpacked BorgStudio Server archive"
[[ "$SOURCE_DIR" != "$INSTALL_DIR" ]] || die "run install.sh from the new archive, not from $INSTALL_DIR"

version=$(cat "$SOURCE_DIR/VERSION" 2> /dev/null || echo "unknown")
echo "Installing BorgStudio Server $version to $INSTALL_DIR"

# .NET needs ICU (languages, time zones) and does not start without it; minimal systems often lack it.
if ! ldconfig -p 2> /dev/null | grep -q 'libicuuc\.so'; then
  echo "Installing ICU (libicu) ..."
  if command -v apt-get > /dev/null; then
    apt-get update -qq
    icu=$(apt-cache search --names-only '^libicu[0-9]+$' | awk '{print $1}' | sort -V | tail -n 1)
    [[ -n "$icu" ]] || die "no libicu package found; install ICU and run this script again"
    DEBIAN_FRONTEND=noninteractive apt-get install -y -qq --no-install-recommends "$icu" > /dev/null
  elif command -v dnf > /dev/null; then
    dnf install -y -q libicu
  elif command -v zypper > /dev/null; then
    zypper --non-interactive install libicu
  else
    die "ICU (libicu) is missing; install it with your package manager and run this script again"
  fi
fi

if ! id -u "$SERVICE_USER" > /dev/null 2>&1; then
  useradd --system --home-dir "$INSTALL_DIR" --no-create-home --shell /usr/sbin/nologin "$SERVICE_USER"
fi

if systemctl is-active --quiet "$SERVICE"; then
  echo "Stopping the running service ..."
  systemctl stop "$SERVICE"
fi

# Replace the program, keep the data and the configuration.
mkdir -p "$INSTALL_DIR"
find "$INSTALL_DIR" -mindepth 1 -maxdepth 1 ! -name data ! -name "$CONFIG" -exec rm -rf {} +
(cd "$SOURCE_DIR" && tar --exclude=./data --exclude="./$CONFIG" -cf - .) | (cd "$INSTALL_DIR" && tar -xf -)
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
systemctl enable "$SERVICE" > /dev/null
systemctl start "$SERVICE"

address=$(hostname -I 2> /dev/null | awk '{print $1}')
port=$(grep -A3 '"Https"' "$INSTALL_DIR/$CONFIG" | grep -o '"Port": *[0-9]*' | grep -o '[0-9]*$' || true)
echo
echo "BorgStudio Server $version is running: https://${address:-$(hostname)}:${port:-8443}/"

# "systemctl start" returns once the server reports it is ready (Type=notify); by then a fresh
# installation has written the setup code for the first administrator.
if [[ -f "$INSTALL_DIR/data/setup-code.txt" ]]; then
  echo "Setup code for the first administrator: $(cat "$INSTALL_DIR/data/setup-code.txt")"
fi

echo
echo "Configuration:  $INSTALL_DIR/$CONFIG (then: systemctl restart $SERVICE)"
echo "Log:            journalctl -u $SERVICE"
echo "Locked out:     runuser -u $SERVICE_USER -- $INSTALL_DIR/BorgStudio.Server reset-admin"
echo "Uninstall:      $INSTALL_DIR/uninstall.sh"
