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
# A failure that isn't a rate limit sets no OAuth cooldown; the failed CLI route is remembered.
[[ ! -e "$TMP/cache/codexbar-claude-$uid-oauth-cooldown" ]]
[[ -f "$TMP/cache/codexbar-claude-$uid-cli-broken" ]]

# Claude routes with a fake CLI that answers per --source and logs each call.
cat > "$TMP/claude-real" <<'CLI'
#!/usr/bin/env bash
src=''; prev=''
for a in "$@"; do [ "$prev" = --source ] && src=$a; prev=$a; done
printf '%s\n' "$src" >> "$CALLS"
if [ "$src" = oauth ]; then printf '%s\n' "$OAUTH_OUT"; exit "$OAUTH_EXIT"; fi
printf '%s\n' "$CLI_OUT"; exit "$CLI_EXIT"
CLI
chmod +x "$TMP/claude-real"
mkdir "$TMP/claude-cache"
sed -e "s|^REAL=.*|REAL=$TMP/claude-real|" -e "s|/var/tmp/|$TMP/claude-cache/|g" "$ROOT/codexbar-shim.sh" > "$TMP/claude-shim"
export CALLS="$TMP/calls"
claude_run() { : > "$CALLS"; rm -f "$TMP"/claude-cache/*; bash "$TMP/claude-shim" usage --provider claude --format json --source cli; }
expired='[{"provider":"claude","source":"oauth","error":{"kind":"provider","code":3,"message":"Claude OAuth token expired. CodexBar CLI does not launch Claude to refresh credentials. Run `claude login`, then retry."}}]'
limited='[{"provider":"claude","source":"oauth","error":{"kind":"provider","code":3,"message":"Claude usage API rate limited (429)"}}]'
good='[{"provider":"claude","source":"claude","usage":{"primary":{"usedPercent":30}}}]'

# Expired token: no cooldown, straight to the CLI, which answers (and in real life refreshes the token).
output=$(OAUTH_OUT=$expired OAUTH_EXIT=3 CLI_OUT=$good CLI_EXIT=0 claude_run)
[[ "$output" = "$good" ]]
[[ "$(cat "$CALLS")" = $'oauth\ncli' ]]
[[ ! -e "$TMP/claude-cache/codexbar-claude-$uid-oauth-cooldown" ]]

# Expired token and a failing CLI, nothing cached: the real OAuth error is returned, not "rate limited".
set +e
output=$(OAUTH_OUT=$expired OAUTH_EXIT=3 CLI_OUT='[{"provider":"claude","error":{"message":"cli broke"}}]' CLI_EXIT=1 claude_run); rc=$?
set -e
[[ "$rc" -eq 3 ]]
check "$output" 'token expired'
[[ "$output" != *'rate limited right now'* ]]

# A genuine rate limit starts the 10-minute OAuth cooldown.
set +e
OAUTH_OUT=$limited OAUTH_EXIT=3 CLI_OUT='[]' CLI_EXIT=1 claude_run >/dev/null
set -e
[[ -f "$TMP/claude-cache/codexbar-claude-$uid-oauth-cooldown" ]]

# A failed CLI route is skipped for 30 minutes, then retried.
: > "$CALLS"; rm -f "$TMP"/claude-cache/*
touch "$TMP/claude-cache/codexbar-claude-$uid-cli-broken"
set +e
OAUTH_OUT=$expired OAUTH_EXIT=3 CLI_OUT=$good CLI_EXIT=0 bash "$TMP/claude-shim" usage --provider claude --format json --source cli >/dev/null
set -e
[[ "$(cat "$CALLS")" = oauth ]]
: > "$CALLS"
touch -d '31 minutes ago' "$TMP/claude-cache/codexbar-claude-$uid-cli-broken"
output=$(OAUTH_OUT=$expired OAUTH_EXIT=3 CLI_OUT=$good CLI_EXIT=0 bash "$TMP/claude-shim" usage --provider claude --format json --source cli)
[[ "$output" = "$good" ]]
[[ ! -e "$TMP/claude-cache/codexbar-claude-$uid-cli-broken" ]]

# In the rate-limit cooldown, a failing CLI's own error is the most current explanation.
: > "$CALLS"; rm -f "$TMP"/claude-cache/*
touch "$TMP/claude-cache/codexbar-claude-$uid-oauth-cooldown"
set +e
output=$(OAUTH_OUT=$good OAUTH_EXIT=0 CLI_OUT='[{"provider":"claude","error":{"message":"cli says sign in"}}]' CLI_EXIT=1 bash "$TMP/claude-shim" usage --provider claude --format json --source cli)
set -e
[[ "$(cat "$CALLS")" = cli ]]
check "$output" 'cli says sign in'
echo 'Shim fake-CLI tests passed.'
