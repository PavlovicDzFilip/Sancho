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
    # Keep the invoking user's PATH across sudo — dotnet/ffmpeg often live in
    # user-local places (~/.dotnet, ~/.local/bin, Homebrew) that sudo's default
    # environment drops. System dirs come first so commands run as root still
    # resolve to system binaries.
    user_path="PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin:$PATH"
    if [ -f "$0" ]; then
        # Invoked as a file - re-exec directly.
        exec sudo env "$user_path" bash "$0"
    else
        # Piped via "curl | bash" - fetch a copy to a temp file first.
        tmp="$(mktemp "${TMPDIR:-/tmp}/sancho-install.XXXXXX")"
        curl -fsSL "$RAW_BASE/scripts/install.sh" -o "$tmp"
        exec sudo env "$user_path" bash "$tmp"
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
# Probe by running dotnet and checking the version, not by PATH lookup alone:
# a broken stub or a non-10.x install would pass an existence check.
if ! dotnet --version 2>/dev/null | grep -qE '^10\.'; then
    echo "dotnet 10 not available - installing the .NET 10 SDK..."
    # The install dir must be on the framework-dependent apphost's default
    # probe path, which differs per platform: /usr/share/dotnet on Linux,
    # /usr/local/share/dotnet on macOS. Anywhere else and `sancho` reports
    # "You must install .NET to run this application" despite the SDK being
    # present (only DOTNET_ROOT would make it visible).
    if [ "$os" = "linux" ]; then
        install_dir="/usr/share/dotnet"
    else
        install_dir="/usr/local/share/dotnet"
    fi
    installer="$(mktemp "${TMPDIR:-/tmp}/dotnet-install.XXXXXX")"
    curl -fsSL "https://dot.net/v1/dotnet-install.sh" -o "$installer"
    bash "$installer" --channel 10.0 --install-dir "$install_dir"
    rm -f "$installer"
    ln -sf "$install_dir/dotnet" /usr/local/bin/dotnet
fi
echo "dotnet: $(dotnet --version)"

# ---- Ensure ffmpeg is available (mic capture on all platforms) --------------
if ! ffmpeg -version >/dev/null 2>&1; then
    echo "ffmpeg not found - installing..."
    if [ "$os" = "linux" ]; then
        if command -v apt-get >/dev/null 2>&1; then
            apt-get update -qq && apt-get install -y ffmpeg
        elif command -v dnf >/dev/null 2>&1; then
            dnf install -y ffmpeg
        elif command -v pacman >/dev/null 2>&1; then
            pacman -Sy --noconfirm ffmpeg
        elif command -v zypper >/dev/null 2>&1; then
            zypper --non-interactive install ffmpeg
        elif command -v apk >/dev/null 2>&1; then
            apk add ffmpeg
        else
            echo "No supported package manager found. Install ffmpeg manually and re-run." >&2
            exit 1
        fi
    else
        # macOS
        if command -v brew >/dev/null 2>&1; then
            brew install ffmpeg
        else
            echo "Homebrew not found. Install ffmpeg manually and re-run." >&2
            exit 1
        fi
    fi
fi
echo "ffmpeg: $(ffmpeg -version 2>/dev/null | head -n1)"

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
