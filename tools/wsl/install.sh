#!/usr/bin/env bash
# Installs the CodexBar CLI and the AgentsWinBar shim inside WSL. Run as root inside the distro:
#   sudo bash install.sh [path/to/codexbar-shim.sh]
# Env: CODEXBAR_VERSION (default v0.69.0, the version this was tested with)
set -euo pipefail

VERSION="${CODEXBAR_VERSION:-v0.69.0}"
[[ "$VERSION" =~ ^v[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo "Invalid CODEXBAR_VERSION: expected vMAJOR.MINOR.PATCH" >&2; exit 1; }
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
TMP="$(mktemp -d)"; STAGED=''; trap 'rm -rf "$TMP"; [ -z "$STAGED" ] || rm -f "$STAGED"' EXIT

echo "Downloading ${FILE} ..."
curl -fsSL --retry 3 --connect-timeout 15 --max-time 300 -o "$TMP/$FILE" "$BASE/$FILE"
curl -fsSL --retry 3 --connect-timeout 15 --max-time 300 -o "$TMP/$FILE.sha256" "$BASE/$FILE.sha256"
mapfile -t checksum_entries < "$TMP/$FILE.sha256"
[ "${#checksum_entries[@]}" -eq 1 ] || { echo "Checksum file must contain exactly one entry for $FILE" >&2; exit 1; }
read -r expected_digest checksum_file checksum_extra <<< "${checksum_entries[0]}"
checksum_file=${checksum_file#\*}
[[ "$expected_digest" =~ ^[[:xdigit:]]{64}$ ]] && [ "$checksum_file" = "$FILE" ] && [ -z "$checksum_extra" ] || {
  echo "Invalid checksum entry: expected $FILE" >&2; exit 1;
}
actual_digest=$(sha256sum "$TMP/$FILE")
actual_digest=${actual_digest%% *}
[ "${actual_digest,,}" = "${expected_digest,,}" ] || { echo "Checksum mismatch for $FILE" >&2; exit 1; }

mkdir -p "$INSTALL_DIR"
mkdir "$TMP/extracted"
tar -xzf "$TMP/$FILE" -C "$TMP/extracted"
[ -f "$TMP/extracted/CodexBarCLI" ] && [ ! -L "$TMP/extracted/CodexBarCLI" ] || { echo "Archive does not contain a regular CodexBarCLI executable" >&2; exit 1; }
# The archive also carries CodexBar_CodexBarCore.bundle (provider plugins), VERSION and a codexbar symlink.
# Copy everything else first, then swap the binary in with an atomic rename on the destination filesystem.
(cd "$TMP/extracted" && find . -mindepth 1 -maxdepth 1 ! -name CodexBarCLI -exec cp -a --remove-destination -t "$INSTALL_DIR/" {} +)
STAGED="$(mktemp "$INSTALL_DIR/.CodexBarCLI.XXXXXX")"
install -m 0755 "$TMP/extracted/CodexBarCLI" "$STAGED"
mv -f "$STAGED" "$INSTALL_DIR/CodexBarCLI"
STAGED=''

STAGED="$(mktemp "$INSTALL_DIR/.codexbar-shim.XXXXXX")"
install -m 0755 "$SHIM_SRC" "$STAGED"
mv -f "$STAGED" "$INSTALL_DIR/codexbar-shim"
STAGED=''
ln -sf "$INSTALL_DIR/codexbar-shim" /usr/local/bin/codexbar

installed_version="$("$INSTALL_DIR/codexbar-shim" --version)" || { echo "Installed CLI failed its version check" >&2; exit 1; }
[ -n "$installed_version" ] || { echo "Installed CLI returned an empty version" >&2; exit 1; }
echo "Installed: $installed_version"
echo "Next: sign in to the tools you use inside WSL (claude, agy) - see tools/wsl/README.md"
