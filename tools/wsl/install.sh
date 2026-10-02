#!/usr/bin/env bash
# Installs the CodexBar CLI and the CodexBarWin shim inside WSL. Run as root inside the distro:
#   sudo bash install.sh [path/to/codexbar-shim.sh]
# Env: CODEXBAR_VERSION (default v0.69.0, the version this was tested with)
set -euo pipefail

VERSION="${CODEXBAR_VERSION:-v0.69.0}"
SHIM_SRC="${1:-$(dirname "$(readlink -f "$0")")/codexbar-shim.sh}"
INSTALL_DIR=/opt/codexbar

for tool in curl tar sha256sum python3; do
  command -v "$tool" >/dev/null || { echo "Missing required tool: $tool" >&2; exit 1; }
done
[ -f "$SHIM_SRC" ] || { echo "Shim not found: $SHIM_SRC" >&2; exit 1; }

case "$(uname -m)" in
  x86_64)  ARCH=x86_64 ;;
  aarch64) ARCH=aarch64 ;;
  *) echo "Unsupported architecture: $(uname -m)" >&2; exit 1 ;;
esac

FILE="CodexBarCLI-${VERSION}-linux-${ARCH}.tar.gz"
BASE="https://github.com/steipete/CodexBar/releases/download/${VERSION}"
TMP="$(mktemp -d)"; trap 'rm -rf "$TMP"' EXIT

echo "Downloading ${FILE} ..."
curl -fsSL -o "$TMP/$FILE" "$BASE/$FILE"
curl -fsSL -o "$TMP/$FILE.sha256" "$BASE/$FILE.sha256"
(cd "$TMP" && sha256sum -c "$FILE.sha256")

mkdir -p "$INSTALL_DIR"
tar -xzf "$TMP/$FILE" -C "$INSTALL_DIR"

install -m 0755 "$SHIM_SRC" "$INSTALL_DIR/codexbar-shim"
ln -sf "$INSTALL_DIR/codexbar-shim" /usr/local/bin/codexbar

echo "Installed: $(codexbar --version)"
echo "Next: sign in to the tools you use inside WSL (claude, agy) - see tools/wsl/README.md"
