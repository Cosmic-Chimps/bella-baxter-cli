#!/usr/bin/env bash
# Bella CLI installer for Linux and macOS
# Usage: curl -sSfL https://raw.githubusercontent.com/cosmic-chimps/bella-baxter/main/scripts/install-bella.sh | bash
# Or with a specific version:
#   curl -sSfL ... | bash -s -- --version v1.2.3
# Or to install to a custom location:
#   curl -sSfL ... | BELLA_INSTALL_DIR=/usr/local/bin bash

set -euo pipefail

REPO="cosmic-chimps/bella-cli"
BINARY_NAME="bella"
INSTALL_DIR="${BELLA_INSTALL_DIR:-/usr/local/bin}"
VERSION="${1:-}"
SHIFT_DONE=0

# Parse --version flag
for arg in "$@"; do
  if [ "$arg" = "--version" ] || [ "$arg" = "-v" ]; then
    SHIFT_DONE=1
  elif [ "$SHIFT_DONE" = "1" ]; then
    VERSION="$arg"
    SHIFT_DONE=0
  fi
done

# Colors
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m' # No Color

info()    { echo -e "${BLUE}[bella]${NC} $*"; }
success() { echo -e "${GREEN}[bella]${NC} $*"; }
warn()    { echo -e "${YELLOW}[bella]${NC} $*"; }
error()   { echo -e "${RED}[bella]${NC} $*" >&2; exit 1; }

# Detect OS and architecture
detect_platform() {
  local os arch

  case "$(uname -s)" in
    Linux*)   os="linux" ;;
    Darwin*)  os="osx" ;;
    *)        error "Unsupported operating system: $(uname -s)" ;;
  esac

  case "$(uname -m)" in
    x86_64 | amd64)  arch="x64" ;;
    aarch64 | arm64) arch="arm64" ;;
    armv7l)          arch="arm" ;;
    *)               error "Unsupported architecture: $(uname -m)" ;;
  esac

  echo "${os}-${arch}"
}

# Get latest release version from GitHub
get_latest_version() {
  if command -v curl &>/dev/null; then
    curl -sSfL "https://api.github.com/repos/${REPO}/releases/latest" \
      | grep '"tag_name"' | sed -E 's/.*"v?([^"]+)".*/\1/' | head -1
  elif command -v wget &>/dev/null; then
    wget -qO- "https://api.github.com/repos/${REPO}/releases/latest" \
      | grep '"tag_name"' | sed -E 's/.*"v?([^"]+)".*/\1/' | head -1
  else
    error "Neither curl nor wget found. Please install one and retry."
  fi
}

# Download file (required — exits on failure)
download() {
  local url="$1" dest="$2"
  info "Downloading $(basename "${url}") ..."
  if command -v curl &>/dev/null; then
    curl -sSfL --progress-bar -o "$dest" "$url"
  elif command -v wget &>/dev/null; then
    wget -q --show-progress -O "$dest" "$url"
  fi
}

# Download file (optional — returns non-zero on failure without exiting)
download_optional() {
  local url="$1" dest="$2"
  if command -v curl &>/dev/null; then
    curl -sSfL -o "$dest" "$url" 2>/dev/null
  elif command -v wget &>/dev/null; then
    wget -qO "$dest" "$url" 2>/dev/null
  fi
}

# Compute SHA256 hash — works on Linux (sha256sum) and macOS (shasum)
sha256_of() {
  if command -v sha256sum &>/dev/null; then
    sha256sum "$1" | awk '{print $1}'
  elif command -v shasum &>/dev/null; then
    shasum -a 256 "$1" | awk '{print $1}'
  else
    error "Cannot verify checksum: neither 'sha256sum' nor 'shasum' found."
  fi
}

# Verify SHA256 against checksums.txt — hard fails on mismatch
verify_checksum() {
  local binary="$1" asset_name="$2" checksums_file="$3"
  info "Verifying SHA256 checksum..."

  local expected
  expected=$(grep "${asset_name}" "${checksums_file}" | awk '{print $1}')
  if [ -z "$expected" ]; then
    error "Checksum for '${asset_name}' not found in checksums.txt — cannot verify integrity."
  fi

  local actual
  actual=$(sha256_of "$binary")

  if [ "$expected" != "$actual" ]; then
    error "Checksum verification FAILED for ${asset_name}!
  Expected: ${expected}
  Got:      ${actual}
  The download may be corrupted or tampered with. Aborting."
  fi

  success "Checksum verified ✓"
}

# Verify GPG signature on checksums.txt — optional (skipped gracefully if gpg/key/sig absent)
verify_gpg_signature() {
  local checksums_file="$1" sig_file="$2"

  # Skip if no signature was published for this release
  if [ ! -s "$sig_file" ]; then
    warn "No GPG signature published for this release — skipping signature check."
    return 0
  fi

  # Skip if gpg is not installed
  if ! command -v gpg &>/dev/null; then
    warn "gpg not found — SHA256 verified but GPG signature check skipped."
    warn "Install gpg for cryptographic authenticity verification."
    return 0
  fi

  info "Verifying GPG signature..."

  # Import Cosmic Chimps public signing key (bundled in the repo)
  local pubkey_url="https://raw.githubusercontent.com/${REPO}/main/scripts/bella-signing-key.asc"
  local pubkey_tmp="${sig_file}.pubkey"
  if download_optional "$pubkey_url" "$pubkey_tmp" && [ -s "$pubkey_tmp" ]; then
    gpg --batch --import "$pubkey_tmp" 2>/dev/null || true
    rm -f "$pubkey_tmp"
  fi

  if gpg --batch --verify "$sig_file" "$checksums_file" 2>/dev/null; then
    success "GPG signature verified ✓"
  else
    error "GPG signature verification FAILED!
  The checksums file does not match the expected signing key.
  This may indicate tampering. Aborting.
  Set BELLA_SKIP_GPG=1 to bypass this check (not recommended)."
  fi
}

main() {
  info "Installing Bella CLI..."

  local platform
  platform="$(detect_platform)"
  info "Detected platform: ${platform}"

  # Resolve version
  if [ -z "$VERSION" ]; then
    info "Fetching latest release..."
    VERSION="$(get_latest_version)"
  fi
  info "Installing version: ${VERSION}"

  # Asset name matches the filenames published by the release workflow
  local asset_name="cli-${platform}"
  local base_url="https://github.com/${REPO}/releases/download/v${VERSION}"

  # Create temp dir
  local tmp_dir
  tmp_dir="$(mktemp -d)"
  trap 'rm -rf "$tmp_dir"' EXIT

  local tmp_binary="${tmp_dir}/${BINARY_NAME}"
  local tmp_checksums="${tmp_dir}/checksums.txt"
  local tmp_sig="${tmp_dir}/checksums.txt.asc"

  # Download checksums.txt first (required)
  download "${base_url}/checksums.txt" "$tmp_checksums"

  # Download GPG signature (optional — non-fatal if absent)
  download_optional "${base_url}/checksums.txt.asc" "$tmp_sig" || true

  # Download binary
  download "${base_url}/${asset_name}" "$tmp_binary"
  chmod +x "$tmp_binary"

  # Verify integrity — hard fail on mismatch
  verify_checksum "$tmp_binary" "$asset_name" "$tmp_checksums"

  # Verify GPG signature — soft fail if gpg/key/sig not available
  if [ "${BELLA_SKIP_GPG:-0}" != "1" ]; then
    verify_gpg_signature "$tmp_checksums" "$tmp_sig"
  fi

  # Smoke test
  if ! "$tmp_binary" --version &>/dev/null; then
    error "Downloaded binary failed to execute. Please report this at https://github.com/${REPO}/issues"
  fi

  # Install
  local install_path="${INSTALL_DIR}/${BINARY_NAME}"

  if [ -w "$INSTALL_DIR" ]; then
    mv "$tmp_binary" "$install_path"
  else
    info "Writing to ${INSTALL_DIR} requires elevated privileges (sudo)..."
    sudo mv "$tmp_binary" "$install_path"
  fi

  success "Bella CLI ${VERSION} installed to ${install_path}"

  # Check PATH
  if ! command -v "$BINARY_NAME" &>/dev/null; then
    warn ""
    warn "${INSTALL_DIR} is not in your PATH."
    warn "Add this to your shell config (e.g. ~/.bashrc or ~/.zshrc):"
    warn ""
    warn "  export PATH=\"${INSTALL_DIR}:\$PATH\""
    warn ""
  else
    success "Run 'bella --help' to get started!"
  fi
}

main "$@"
