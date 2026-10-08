#!/usr/bin/env bash
# Fake CLI tests: no installation, login, credential access, or system PATH changes.
set -euo pipefail
ROOT=$(cd "$(dirname "$0")" && pwd)
TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT
mkdir -p "$TMP/home/.config/codexbar" "$TMP/bin"
# The bridge PATH holds only this directory, so a real codex in /usr/bin can't affect the result.
ln -s "$(command -v bash)" "$TMP/bin/bash"
export HOME="$TMP/home"
export CODEX_HOME=original-home
cat > "$TMP/real" <<'CLI'
#!/usr/bin/env bash
printf 'HOME=%s\nPATH=%s\n' "${CODEX_HOME:-unset}" "$PATH"
printf 'ARG=%s\n' "$@"
exit "${FAKE_EXIT:-0}"
CLI
chmod +x "$TMP/real"
# Redirect only the fixed executable and bridge PATH in a disposable copy.
sed -e "s|^REAL=.*|REAL=$TMP/real|" \
    -e "s|^    bridge_path=.*|    bridge_path=$TMP/bin|" \
    "$ROOT/codexbar-shim.sh" > "$TMP/shim"
check() { [[ "$1" == *"$2"* ]] || { echo "Missing expected output: $2" >&2; exit 1; }; }

output=$(bash "$TMP/shim" usage --provider codex --source cli)
check "$output" 'HOME=original-home'
check "$output" $'ARG=--source\nARG=cli'
printf 'CODEXBAR_CODEX_HOME=/mounted/home with spaces\nIGNORED=$(touch %s/pwned)\n' "$TMP" > "$HOME/.config/codexbar/shim.env"
output=$(bash "$TMP/shim" usage --source cli --provider codex --source=auto)
check "$output" 'HOME=/mounted/home with spaces'
check "$output" $'ARG=--source\nARG=oauth'
[[ "$output" != *'ARG=cli'* && "$output" != *'ARG=--source=auto'* && ! -e "$TMP/pwned" ]]
output=$(bash "$TMP/shim" cost --provider codex --source cli)
check "$output" 'HOME=original-home'
check "$output" 'ARG=cli'
output=$(bash "$TMP/shim" usage --provider cursor --source cli)
check "$output" 'HOME=original-home'
printf 'CODEXBAR_CODEX_HOME=/literal/$(touch %s/pwned)\n' "$TMP" > "$HOME/.config/codexbar/shim.env"
output=$(bash "$TMP/shim" --provider codex --format json)
check "$output" 'HOME=/literal/$(touch '
[[ ! -e "$TMP/pwned" ]]
set +e
FAKE_EXIT=7 bash "$TMP/shim" usage --provider codex >/dev/null
rc=$?
set -e
[[ "$rc" -eq 7 ]]
printf 'CODEXBAR_CODEX_HOME=relative\n' > "$HOME/.config/codexbar/shim.env"
if bash "$TMP/shim" usage --provider codex >/dev/null 2>&1; then echo 'Relative bridge home accepted' >&2; exit 1; fi
printf 'CODEXBAR_CODEX_HOME=/mounted/home\n' > "$HOME/.config/codexbar/shim.env"
for name in codex codex.exe; do
  printf '#!/bin/sh\nexit 99\n' > "$TMP/bin/$name"
  chmod +x "$TMP/bin/$name"
  if bash "$TMP/shim" usage --provider codex > "$TMP/result" 2>&1; then echo "Bridge accepted $name in PATH" >&2; exit 1; fi
  check "$(cat "$TMP/result")" 'Codex bridge refused'
  rm "$TMP/bin/$name"
done
mkdir "$TMP/cache"
sed "s|/var/tmp/|$TMP/cache/|g" "$TMP/shim" > "$TMP/cache-shim"
output=$(bash "$TMP/cache-shim" usage --provider claude --source oauth)
check "$output" 'ARG=claude'
uid=$(id -u)
key=$(printf '%s' 'usage --provider claude --source oauth' | md5sum | cut -c1-8)
[[ -s "$TMP/cache/codexbar-claude-$uid-$key.json" ]]
cached=$(FAKE_EXIT=7 bash "$TMP/cache-shim" usage --provider claude --source oauth)
[[ "$cached" = "$output" ]]
rm "$TMP/cache/codexbar-claude-$uid-$key.json"
printf 'legacy-owned-cache\n' > "$TMP/cache/codexbar-claude-$key.json"
output=$(FAKE_EXIT=7 bash "$TMP/cache-shim" usage --provider claude --source oauth)
[[ "$output" = legacy-owned-cache ]]
rm "$TMP/cache/codexbar-claude-$key.json"
FAKE_EXIT=7 bash "$TMP/cache-shim" usage --provider claude --source oauth >/dev/null && { echo 'Expected CLI failure' >&2; exit 1; }
[[ -f "$TMP/cache/codexbar-claude-$uid-oauth-cooldown" ]]
[[ -f "$TMP/cache/codexbar-claude-$uid-cli-broken" ]]
echo 'Shim fake-CLI tests passed.'
