#!/usr/bin/env bash
# Bella CLI installer for Linux and macOS
#
# Usage (the script is a release asset, so it is served from a release, never from a branch):
#   curl -sSfL https://github.com/Cosmic-Chimps/bella-baxter-cli/releases/latest/download/install-bella.sh | bash
# A specific release — the script served by release vX.Y.Z installs vX.Y.Z by default:
#   curl -sSfL https://github.com/Cosmic-Chimps/bella-baxter-cli/releases/download/vX.Y.Z/install-bella.sh | bash
# Or pick the version explicitly:
#   curl -sSfL ... | bash -s -- --version 1.2.3
# Or to install to a custom location:
#   curl -sSfL ... | BELLA_INSTALL_DIR=/usr/local/bin bash
#
# Every install verifies, and ABORTS on any failure (#828):
#   1. the GPG signature over the release's checksums.txt, against the Cosmic Chimps release key
#      embedded below and pinned by fingerprint — this is what proves the release is ours;
#   2. the binary's SHA-256 against that (now authenticated) checksums.txt.
# A missing signature, a missing `gpg`, a key that will not import or a signature that does not
# verify each stop the install. The single opt-out, for air-gapped mirrors that cannot carry the
# signature, is BELLA_INSECURE_SKIP_SIGNATURE=1. It skips step 1 only, loudly; step 2 still runs.

set -euo pipefail

REPO="Cosmic-Chimps/bella-baxter-cli"
BINARY_NAME="bella"
INSTALL_DIR="${BELLA_INSTALL_DIR:-/usr/local/bin}"

# Stamped by the release workflow (publish.yml) with the version of the release this copy is an asset
# of, so `releases/download/vX/install-bella.sh` installs vX and not whatever is newest by then. An
# unstamped copy (a checkout of the repo) keeps the placeholder and falls back to the latest release.
RELEASE_VERSION="@BELLA_RELEASE_VERSION@"
case "$RELEASE_VERSION" in @*@) RELEASE_VERSION="" ;; esac

# The Cosmic Chimps release-signing key. The FINGERPRINT is the trust anchor: a signature is accepted
# only when gpg reports it VALID and made by this exact primary key. The key material is embedded so
# verification needs no keyserver and no second download; it is the same key as
# scripts/bella-signing-key.asc and the `bella-signing-key.asc` release asset, and publish.yml refuses
# to release when the key it signs with is not this one. Rotating it means changing all of them.
SIGNING_FINGERPRINT="65BB8D3CEEE3DD9E4FFD22B4119F114CA309C2FA"

# The first release whose checksums.txt carries a detached signature. Anything older cannot be
# verified and is refused unless the opt-out is set.
FIRST_SIGNED_RELEASE="0.1.1-preview.26"

VERSION=""
while [ "$#" -gt 0 ]; do
  case "$1" in
    --version | -v)
      [ "$#" -ge 2 ] || { echo "[bella] --version needs a value, e.g. --version 1.2.3" >&2; exit 1; }
      VERSION="$2"
      shift 2
      ;;
    *)
      shift
      ;;
  esac
done
VERSION="${VERSION#v}"

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

signing_public_key() {
  cat <<'BELLA_SIGNING_KEY'
-----BEGIN PGP PUBLIC KEY BLOCK-----
Comment: 65BB 8D3C EEE3 DD9E 4FFD  22B4 119F 114C A309 C2FA
Comment: Cosmic Chimps <it@cosmic-chimps.com>

xsFNBGnFpIYBEADWbCNcVK58NxqJjbYHTJrslRpvim70WMLl+l8MIc0afxqRMhrb
oijgYSyVY3pKpv4CBTaBLj+F4RYs6G9VuSMzJth3mTeKArHloOzjdjuZXVA5YEyz
V6MgTKdjhd+vYP87LsJw+h+ceO7Jpq1j+PK1SosA+bRDun4jg1U2pBUNN8arEs+3
l4kug/pJaP1fBmAz77izg3z2lV0wYGAYVY/VS3rZ+SwZW6TWlzv7ddt6tfmcNGtY
1HVBwo78DI78zj5xFMMvQOYGKgWpuzyfMfBxNlQu+q3REOyM1P1yzvymvWY/mgHw
Vi7Y1jTm5acu3JSNb4bC7C0Tvo6sLEp8WjojdXBTg56deippTd4L9lcO78QzXIxk
uZ5kSQTuo8sySpG5qp4zZy+19sck6Kpu+fA8jtjbAie5sPYonqNoeVSqgwfDMaF7
zQN2ixNzUAG7MWuG+qz1/5a5b1Imj+dxNa2rH9nA/F23ucoigKjLllonuZDR8eSb
ahq5ws0ScFstKw2afIKrztv26QmUGz5p0f8O52Bu/ttRXXOs70MR/qAROy4OU92u
GXj82n4FxFvRaZ9fG7lTaB0r+4ePSB10OmB29k3wURhbsm4qKqVqwgAlXBHUSDIF
PiyTIi8ovGe+7iQcD1Xcq8p3hgDtt1H9iYfXDGKzs5rw3+ZWFZR/pniIfQARAQAB
zSRDb3NtaWMgQ2hpbXBzIDxpdEBjb3NtaWMtY2hpbXBzLmNvbT7CwY4EEwEIADgW
IQRlu4087uPdnk/9IrQRnxFMownC+gUCacWkhgIbLwULCQgHAgYVCgkICwIEFgID
AQIeAQIXgAAKCRARnxFMownC+gSuEAC8j2nHUtZXKVD8elpgIozcnzJ4NsVMJMjB
avAlKxSDJ3PqP3NdlCPZ4QEwGknMisUD9hPUo7JYm7ayMxtea18xVxZ72JnYUfpd
nqA0kPAKNuLeY0jQsKUyECQQ6sfLGve11c6+K1yxKDY3u3hIwDYaKG7U5UnTKINY
jD8d3ZCZdS7Pb22NuqiNFsSLz/0DWiAS4IPgChJPHAWNZsnJP6R/KLZCKNkh+Ne4
XkVuEO/lgAl2Hmkxrwhh17QyNxoTEYssufXcKGGjhTo3tdTlz+4GQkrg26gYGxII
gHpXMFw1ehXjvceGyrwB+BUjLmwgpH3nRK4gMHuUrMvJNvP+6bTMlULTrUT9O7dA
RT6XSF4fuvmGQ5aHVsWiDAWSx4bAtNrahMnY7Kutl75bHRneAiCQ+uQXM16+xgOJ
h8oiGlkLvEayMwWHEWC3ZqmRtHCsU3Q38RBN8qkykpraPr0+8u75M90PM8Mg4PSs
vQY+dAGvaBmRF/ZWQojjBUQPxFddtGGtbNN+LOvAsv4h9dg2EmqKBZ2YMQFiAoRm
qVT8k75esOst6iCWsXzttbHpfbotFCenbkmBzYSL4zPH8rLkTyUYToiiXC+IKCGW
eQ5obHQ35H9yTxdBN/knB5CCmAMjfb6BCTqyWYbemC3drzEvnrM+E3hxh2SLsstL
kq+DsXQGlw==
=+0iq
-----END PGP PUBLIC KEY BLOCK-----
BELLA_SIGNING_KEY
}

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

  # Detect musl libc (Alpine Linux, Void Linux, etc.) — releases publish cli-linux-musl-* for it.
  if [ "$os" = "linux" ] && \
     { [ -f /etc/alpine-release ] || ldd /bin/sh 2>&1 | grep -qi musl; }; then
    echo "${os}-musl-${arch}"
    return
  fi

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
    curl -sSfL --progress-bar -o "$dest" "$url" || error "Download failed: ${url}"
  elif command -v wget &>/dev/null; then
    wget -q --show-progress -O "$dest" "$url" || error "Download failed: ${url}"
  else
    error "Neither curl nor wget found. Please install one and retry."
  fi
}

# Download file (optional — returns non-zero on failure without exiting)
download_optional() {
  local url="$1" dest="$2"
  if command -v curl &>/dev/null; then
    curl -sSfL -o "$dest" "$url" 2>/dev/null
  elif command -v wget &>/dev/null; then
    wget -qO "$dest" "$url" 2>/dev/null
  else
    return 1
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

# Verify SHA256 against checksums.txt — hard fails on mismatch.
# The entry is matched on the EXACT file name (sha256sum's `*` binary-mode marker tolerated), so one
# asset can never borrow another's line.
verify_checksum() {
  local binary="$1" asset_name="$2" checksums_file="$3"
  info "Verifying SHA256 checksum..."

  local expected
  expected=$(awk -v name="$asset_name" '{ n = $2; sub(/^\*/, "", n); if (n == name) { print $1; exit } }' "$checksums_file")
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

insecure_skip_requested() {
  [ "${BELLA_INSECURE_SKIP_SIGNATURE:-0}" = "1" ]
}

refusal_hint() {
  echo "  If you cannot verify signatures (an air-gapped mirror without the .asc, say), you may
  set BELLA_INSECURE_SKIP_SIGNATURE=1 to install on the SHA-256 checksum alone. That proves the
  download is intact, NOT that Cosmic Chimps published it."
}

# Verify the detached GPG signature over checksums.txt — FAILS CLOSED (#828).
#
# Trust model: the only key gpg can see is the embedded one, imported into a throwaway GNUPGHOME (the
# user's own keyring is never read or written), and a signature is accepted only when gpg's machine-
# readable status reports VALIDSIG with the pinned primary fingerprint. A key in the user's keyring,
# a key from a keyserver, or a key shipped next to the release can therefore never vouch for a release.
verify_gpg_signature() {
  local checksums_file="$1" sig_file="$2" work_dir="$3"

  if insecure_skip_requested; then
    warn "════════════════════════════════════════════════════════════════════════"
    warn " WARNING: BELLA_INSECURE_SKIP_SIGNATURE=1 — the GPG signature is NOT checked."
    warn " Only the SHA-256 checksum is verified: that proves the download is intact,"
    warn " NOT that Cosmic Chimps published it. Do not use this outside air-gapped setups."
    warn "════════════════════════════════════════════════════════════════════════"
    return 0
  fi

  if [ ! -s "$sig_file" ]; then
    error "No GPG signature (checksums.txt.asc) could be downloaded for v${VERSION}.
  Releases before v${FIRST_SIGNED_RELEASE} were never signed and cannot be verified; for a later
  release the download failed. Refusing to install an unverified release.
  Install v${FIRST_SIGNED_RELEASE} or newer, or retry.
$(refusal_hint)"
  fi

  if ! command -v gpg &>/dev/null; then
    error "gpg is required to verify the release signature and was not found.
  Install GnuPG (e.g. 'apt-get install gnupg', 'apk add gnupg', 'brew install gnupg') and retry.
$(refusal_hint)"
  fi

  info "Verifying GPG signature..."

  local gnupg_home="${work_dir}/gnupg"
  mkdir -p "$gnupg_home"
  chmod 700 "$gnupg_home"

  if ! signing_public_key | gpg --homedir "$gnupg_home" --batch --quiet --import >/dev/null 2>&1; then
    stop_gpg "$gnupg_home"
    error "Could not import the embedded Cosmic Chimps signing key (${SIGNING_FINGERPRINT}).
  Your gpg may be too old or broken. Refusing to install an unverified release.
$(refusal_hint)"
  fi

  local status
  status=$(gpg --homedir "$gnupg_home" --batch --status-fd 1 --verify "$sig_file" "$checksums_file" 2>/dev/null) \
    || status="__BELLA_GPG_FAILED__"
  stop_gpg "$gnupg_home"

  # VALIDSIG's LAST field is the fingerprint of the PRIMARY key that made the signature.
  if [ "$status" = "__BELLA_GPG_FAILED__" ] || \
     ! printf '%s\n' "$status" | awk -v fpr="$SIGNING_FINGERPRINT" \
         '$1 == "[GNUPG:]" && $2 == "VALIDSIG" && $NF == fpr { found = 1 } END { exit found ? 0 : 1 }'; then
    error "GPG signature verification FAILED for v${VERSION}!
  checksums.txt is not signed by the Cosmic Chimps release key (${SIGNING_FINGERPRINT}).
  This may indicate tampering. Aborting.
$(refusal_hint)"
  fi

  success "GPG signature verified ✓ (key ${SIGNING_FINGERPRINT})"
}

# gpg may start an agent/keyboxd in the throwaway homedir; stop it before the directory is removed.
stop_gpg() {
  if command -v gpgconf &>/dev/null; then
    gpgconf --homedir "$1" --kill all >/dev/null 2>&1 || true
  fi
}

TMP_DIR=""
cleanup() { if [ -n "$TMP_DIR" ]; then rm -rf "$TMP_DIR"; fi; }

main() {
  info "Installing Bella CLI..."

  if [ "${BELLA_SKIP_GPG:-}" = "1" ]; then
    warn "BELLA_SKIP_GPG is no longer honoured — the signature is always verified."
    warn "The only opt-out is BELLA_INSECURE_SKIP_SIGNATURE=1 (air-gapped installs only)."
  fi

  local platform
  platform="$(detect_platform)"
  info "Detected platform: ${platform}"

  # Resolve version: --version, else the release this script was published with, else latest.
  if [ -z "$VERSION" ]; then
    VERSION="$RELEASE_VERSION"
  fi
  if [ -z "$VERSION" ]; then
    info "Fetching latest release..."
    VERSION="$(get_latest_version)"
  fi
  if ! printf '%s' "$VERSION" | grep -Eq '^[0-9A-Za-z][0-9A-Za-z.+-]*$'; then
    error "Could not determine a valid version to install (got '${VERSION}')."
  fi
  info "Installing version: ${VERSION}"

  # Asset name matches the filenames published by the release workflow
  local asset_name="cli-${platform}"
  local base_url="https://github.com/${REPO}/releases/download/v${VERSION}"

  TMP_DIR="$(mktemp -d)"
  trap cleanup EXIT

  local tmp_binary="${TMP_DIR}/${BINARY_NAME}"
  local tmp_checksums="${TMP_DIR}/checksums.txt"
  local tmp_sig="${TMP_DIR}/checksums.txt.asc"

  # Download checksums.txt first (required)
  download "${base_url}/checksums.txt" "$tmp_checksums"

  # Download the GPG signature. Its absence is judged by verify_gpg_signature (a refusal).
  if ! insecure_skip_requested; then
    download_optional "${base_url}/checksums.txt.asc" "$tmp_sig" || rm -f "$tmp_sig"
  fi

  # Download binary
  download "${base_url}/${asset_name}" "$tmp_binary"
  chmod +x "$tmp_binary"

  # Authenticate the manifest, then check the binary against it — both hard fail.
  verify_gpg_signature "$tmp_checksums" "$tmp_sig" "$TMP_DIR"
  verify_checksum "$tmp_binary" "$asset_name" "$tmp_checksums"

  # Smoke test
  smoke_output=$("$tmp_binary" --version 2>&1 || true)
  if ! "$tmp_binary" --version &>/dev/null; then
    error "Downloaded binary failed to execute.
  Output: ${smoke_output}
  Please report this at https://github.com/${REPO}/issues"
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

# BELLA_INSTALLER_NO_MAIN=1 lets a test source this file for its functions without installing.
if [ "${BELLA_INSTALLER_NO_MAIN:-0}" != "1" ]; then
  main "$@"
fi
