#!/usr/bin/env bash
# dev-install.sh — install bella CLI globally for local development
# Equivalent of: npm link
#
# Usage:
#   ./dev-install.sh          # first install or reinstall
#
# After running this, `bella` is available in your terminal from any directory.
# Re-run it after any code change to pick up the latest version.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT="$SCRIPT_DIR/BellaCli/BellaCli.csproj"
NUPKG_DIR="$SCRIPT_DIR/BellaCli/nupkg"
DOTNET_TOOLS="${HOME}/.dotnet/tools"

SDK_PROJECT="$(dirname "$SCRIPT_DIR")/sdk/dotnet-sdk/BellaClient.csproj"

# Pre-build the SDK project reference. dotnet pack has a known quirk where
# it fails to generate AssemblyInfo for project references on a cold (no obj/) build.
# Explicit dotnet build primes obj/ first, then pack succeeds.
echo "🔨 Building BellaCli (+ SDK dependency)..."
dotnet build "$PROJECT" -c Debug --nologo

echo "📦 Packing BellaCli..."
dotnet pack "$PROJECT" -c Debug -o "$NUPKG_DIR" --nologo --no-build --no-restore

# Uninstall old version first so dotnet always picks up the fresh binary,
# then reinstall (same-version updates are a no-op for dotnet tool update).
if dotnet tool list -g | grep -q "bellacli"; then
  echo "🔄 Reinstalling global tool..."
  dotnet tool uninstall -g BellaCli
else
  echo "📦 Installing global tool..."
fi
dotnet tool install -g BellaCli --add-source "$NUPKG_DIR"

echo ""
echo "✅ Done! The .NET bella is installed at: ${DOTNET_TOOLS}/bella"
echo ""

# Warn if another 'bella' (e.g. the old JS CLI) shadows the .NET tool
FIRST_BELLA="$(which bella 2>/dev/null || true)"
if [[ -n "$FIRST_BELLA" && "$FIRST_BELLA" != "${DOTNET_TOOLS}/bella" ]]; then
  echo "⚠️  WARNING: '$FIRST_BELLA' appears earlier in your PATH."
  echo "   The .NET bella won't be reached unless you fix the order."
  echo ""
  echo "   Option A — use the full path for now:"
  echo "     ${DOTNET_TOOLS}/bella --help"
  echo ""
  echo "   Option B — add .dotnet/tools BEFORE node/npm tools in your shell config:"
  echo "     # In ~/.zshrc or ~/.bashrc, move this line BEFORE nvm/npm exports:"
  echo '     export PATH="$HOME/.dotnet/tools:$PATH"'
  echo ""
  echo "   Then restart your shell or run: source ~/.zshrc"
else
  echo "   Try: bella --help"
  echo "   Try: bella --version"
  echo ""
  echo "Re-run this script after any code change to update the installed binary."
fi
