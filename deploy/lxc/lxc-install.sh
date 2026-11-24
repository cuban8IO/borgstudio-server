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

die() {
  echo "Error: $*" >&2
  exit 1
}

force=0
for argument in "$@"; do
  case "$argument" in
    --force) force=1 ;;
    *) die "unknown option $argument (known: --force)" ;;
  esac
done

[[ $EUID -eq 0 ]] || die "run as root"

if [[ -r /etc/os-release ]]; then
  # shellcheck disable=SC1091
  . /etc/os-release
  case "${ID:-}:${VERSION_ID:-}" in
    debian:12 | debian:13 | ubuntu:22.04 | ubuntu:24.04) ;;
    *) echo "Warning: tested on Debian 12/13 and Ubuntu 22.04/24.04, this is ${PRETTY_NAME:-unknown}." >&2 ;;
  esac
fi

case "$(uname -m)" in
  x86_64) rid=linux-x64 ;;
  aarch64 | arm64) rid=linux-arm64 ;;
  *) die "unsupported architecture $(uname -m) (supported: x86_64, aarch64)" ;;
esac

# "update" fetches the newest installer each time, so fixes to this script arrive as well.
cat > "$UPDATE_COMMAND" << EOF
#!/usr/bin/env bash
# Updates BorgStudio Server to the latest release (set up by lxc-install.sh). Options: --force
set -euo pipefail
script=\$(curl -fsSL "$INSTALLER_URL")
exec bash -c "\$script" lxc-install.sh "\$@"
EOF
chmod 755 "$UPDATE_COMMAND"

if [[ -n "${BORGSTUDIO_VERSION:-}" ]]; then
  version="${BORGSTUDIO_VERSION#v}"
else
  version=$(curl -fsSL "https://api.github.com/repos/$REPOSITORY/releases/latest" \
    | grep -o '"tag_name": *"v[^"]*"' | head -n 1 | grep -o 'v[0-9][^"]*' | cut -c2-)
  [[ -n "$version" ]] || die "could not find the latest release"
fi

installed=$(cat "$INSTALL_DIR/VERSION" 2> /dev/null || true)
if [[ -n "$installed" && $force -eq 0 ]]; then
  if [[ "$installed" == "$version" ]]; then
    echo "BorgStudio Server $installed is up to date."
    exit 0
  fi
  # A downgrade could meet a database the older version does not understand.
  if [[ "$(printf '%s\n%s\n' "$installed" "$version" | sort -V | tail -n 1)" == "$installed" ]]; then
    die "BorgStudio Server $installed is newer than $version; use --force to install $version anyway"
  fi
  echo "Updating BorgStudio Server $installed to $version ..."
fi

echo "Installing what BorgStudio Server needs ..."
export DEBIAN_FRONTEND=noninteractive
apt-get update -qq
# .NET needs ICU for languages and time zones; the package name carries its version.
icu=$(apt-cache search --names-only '^libicu[0-9]+$' | awk '{print $1}' | sort -V | tail -n 1)
[[ -n "$icu" ]] || die "no libicu package found"
apt-get install -y -qq --no-install-recommends ca-certificates curl tar "$icu" > /dev/null

file="BorgStudio.Server-$version-$rid.tar.gz"
base="https://github.com/$REPOSITORY/releases/download/v$version"
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

echo "Downloading BorgStudio Server $version ($rid) ..."
curl -fsSL -o "$work/$file" "$base/$file"
curl -fsSL -o "$work/SHA256SUMS" "$base/SHA256SUMS"
(cd "$work" && grep " $file\$" SHA256SUMS | sha256sum -c --quiet -) || die "checksum mismatch for $file"

tar -xzf "$work/$file" -C "$work"
bash "$work/borgstudio-server/install.sh"

echo "Update later with:  update"
