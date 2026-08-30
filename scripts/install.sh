#!/usr/bin/env bash
# Installs Sancho for all users on macOS or Linux:
#   1. Installs the .NET 10 SDK if dotnet is not already present
#   2. Downloads the latest sancho binary from GitHub releases
#   3. Installs it to /usr/local/bin, accessible to all users
#
# One-liner usage:
#   curl -fsSL https://raw.githubusercontent.com/PavlovicDzFilip/Sancho/master/scripts/install.sh | bash
#
# Requires root privileges (re-executes itself with sudo).
set -euo pipefail

REPO_OWNER="PavlovicDzFilip"
REPO_NAME="Sancho"
RAW_BASE="https://raw.githubusercontent.com/$REPO_OWNER/$REPO_NAME/master"
RELEASE_BASE="https://github.com/$REPO_OWNER/$REPO_NAME/releases/latest/download"

# ---- Elevate with sudo if needed -------------------------------------------
if [ "$(id -u)" -ne 0 ]; then
    echo "Requesting administrator privileges..."
    if [ -f "$0" ]; then
        # Invoked as a file - re-exec directly.
        exec sudo bash "$0"
    else
        # Piped via "curl | bash" - fetch a copy to a temp file first.
        tmp="$(mktemp "${TMPDIR:-/tmp}/sancho-install.XXXXXX")"
        curl -fsSL "$RAW_BASE/scripts/install.sh" -o "$tmp"
        exec sudo bash "$tmp"
    fi
fi

# ---- Detect OS and architecture ---------------------------------------------
case "$(uname -s)" in
    Linux)  os="linux" ;;
    Darwin) os="osx" ;;
    *)      echo "Unsupported platform: $(uname -s)" >&2; exit 1 ;;
esac

case "$(uname -m)" in
    x86_64|amd64)   arch="x64" ;;
    arm64|aarch64)  arch="arm64" ;;
    *)              echo "Unsupported architecture: $(uname -m)" >&2; exit 1 ;;
esac

# ---- Install the .NET SDK if missing ----------------------------------------
if ! command -v dotnet >/dev/null 2>&1; then
    echo "dotnet not found - installing the .NET 10 SDK..."
    install_dir="/usr/local/share/dotnet"
    installer="$(mktemp "${TMPDIR:-/tmp}/dotnet-install.XXXXXX")"
    curl -fsSL "https://dot.net/v1/dotnet-install.sh" -o "$installer"
    bash "$installer" --channel 10.0 --install-dir "$install_dir"
    rm -f "$installer"
    ln -sf "$install_dir/dotnet" /usr/local/bin/dotnet
fi
echo "dotnet: $(dotnet --version)"

# ---- Download the published executable --------------------------------------
echo "Downloading sancho ($os-$arch)..."
binary="$(mktemp "${TMPDIR:-/tmp}/sancho.XXXXXX")"
if ! curl -fsSL "$RELEASE_BASE/sancho-$os-$arch" -o "$binary"; then
    echo "Could not download sancho-$os-$arch from $RELEASE_BASE." >&2
    echo "No GitHub release with a build for this platform exists yet." >&2
    exit 1
fi

# ---- Install for all users --------------------------------------------------
install -m 0755 "$binary" /usr/local/bin/sancho
rm -f "$binary"

echo ""
echo "Sancho installed to /usr/local/bin/sancho (available to all users)."
echo "Run: sancho --help"
