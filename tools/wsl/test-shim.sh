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
[ -z "${CLI_SLEEP:-}" ] || sleep "$CLI_SLEEP"
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
observed='2026-10-01T10:00:00Z'
cache_file="$TMP/claude-cache/codexbar-claude-$uid-$(printf '%s' 'usage --provider claude --format json --source cli' | md5sum | cut -c1-8).json"
printf '%s\n' "[{\"provider\":\"claude\",\"source\":\"oauth\",\"usage\":{\"updatedAt\":\"$observed\",\"primary\":{\"usedPercent\":30}}}]" > "$cache_file"
cache_stamp=$(stat -c %Y "$cache_file")
output=$(OAUTH_OUT=$expired OAUTH_EXIT=3 CLI_OUT='[]' CLI_EXIT=1 bash "$TMP/claude-shim" usage --provider claude --format json --source cli)
check "$output" '"cached": true'
check "$output" "$observed"
[[ -z "$(cat "$CALLS")" || "$(cat "$CALLS")" = $'oauth\ncli' ]]
[[ "$(stat -c %Y "$cache_file")" = "$cache_stamp" ]]
touch -d '8 days ago' "$cache_file"
cache_stamp=$(stat -c %Y "$cache_file")
output=$(OAUTH_OUT=$expired OAUTH_EXIT=3 CLI_OUT='[]' CLI_EXIT=1 bash "$TMP/claude-shim" usage --provider claude --format json --source cli)
check "$output" '"cached": true'
check "$output" "$observed"
check "$output" '"cacheUpdatedAt":'
[[ "$(stat -c %Y "$cache_file")" = "$cache_stamp" ]]

# When python3 is missing, cached_output falls back to `cat` without failing.
mkdir -p "$TMP/nopy-bin"
for cmd in bash id stat date cat md5sum cut mktemp mv rm grep; do
  ln -sf "$(command -v "$cmd")" "$TMP/nopy-bin/$cmd"
done
output=$(PATH="$TMP/nopy-bin" OAUTH_OUT=$expired OAUTH_EXIT=3 CLI_OUT='[]' CLI_EXIT=1 bash "$TMP/claude-shim" usage --provider claude --format json --source cli)
check "$output" "$observed"
[[ "$output" != *'"cached": true'* ]]

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
# During OAuth cooldown with no cache, preserve CLI failures even with empty stdout.
for cli_exit in 1 125 127; do
  : > "$CALLS"; rm -f "$TMP"/claude-cache/*
  touch "$TMP/claude-cache/codexbar-claude-$uid-oauth-cooldown"
  set +e
  output=$(OAUTH_OUT=$good OAUTH_EXIT=0 CLI_OUT='' CLI_EXIT=$cli_exit bash "$TMP/claude-shim" usage --provider claude --format json --source cli); rc=$?
  set -e
  [[ "$rc" -eq "$cli_exit" && -z "$output" && "$(cat "$CALLS")" = cli ]]
done

# Slow CLI route is cut off at the bound and the call returns promptly.
: > "$CALLS"; rm -f "$TMP"/claude-cache/*
printf '%s\n' "[{\"provider\":\"claude\",\"source\":\"oauth\",\"usage\":{\"updatedAt\":\"$observed\",\"primary\":{\"usedPercent\":30}}}]" > "$cache_file"
touch -d '15 minutes ago' "$cache_file"
start_s=$SECONDS
output=$(CODEXBAR_CLAUDE_CLI_TIMEOUT=1 CLI_SLEEP=10 OAUTH_OUT=$expired OAUTH_EXIT=3 CLI_OUT=$good CLI_EXIT=0 bash "$TMP/claude-shim" usage --provider claude --format json --source cli)
elapsed=$(( SECONDS - start_s ))
[[ "$elapsed" -lt 5 ]]
check "$output" '"cached": true'
check "$output" "$observed"
[[ "$(cat "$CALLS")" = $'oauth\ncli' ]]
[[ -f "$TMP/claude-cache/codexbar-claude-$uid-cli-broken" ]]

# Negative cache skips the CLI route on the next call (assert the fake CLI call log).
: > "$CALLS"
start_s=$SECONDS
output=$(CODEXBAR_CLAUDE_CLI_TIMEOUT=1 CLI_SLEEP=10 OAUTH_OUT=$expired OAUTH_EXIT=3 CLI_OUT=$good CLI_EXIT=0 bash "$TMP/claude-shim" usage --provider claude --format json --source cli)
elapsed=$(( SECONDS - start_s ))
[[ "$elapsed" -lt 3 ]]
check "$output" '"cached": true'
check "$output" "$observed"
[[ "$(cat "$CALLS")" = oauth ]]

# 429 + cache serves cache without calling the CLI route.
: > "$CALLS"; rm -f "$TMP"/claude-cache/*
printf '%s\n' "[{\"provider\":\"claude\",\"source\":\"oauth\",\"usage\":{\"updatedAt\":\"$observed\",\"primary\":{\"usedPercent\":30}}}]" > "$cache_file"
touch -d '15 minutes ago' "$cache_file"
output=$(OAUTH_OUT=$limited OAUTH_EXIT=3 CLI_OUT=$good CLI_EXIT=0 bash "$TMP/claude-shim" usage --provider claude --format json --source cli)
check "$output" '"cached": true'
check "$output" "$observed"
[[ "$(cat "$CALLS")" = oauth ]]
[[ -f "$TMP/claude-cache/codexbar-claude-$uid-oauth-cooldown" ]]
[[ ! -e "$TMP/claude-cache/codexbar-claude-$uid-cli-broken" ]]

# Subsequent call while OAuth is in 429 cooldown also serves cache immediately without calling CLI.
: > "$CALLS"
output=$(OAUTH_OUT=$limited OAUTH_EXIT=3 CLI_OUT=$good CLI_EXIT=0 bash "$TMP/claude-shim" usage --provider claude --format json --source cli)
check "$output" '"cached": true'
[[ -z "$(cat "$CALLS")" ]]

# 429 + no cache still tries the CLI route once (success case caches data).
: > "$CALLS"; rm -f "$TMP"/claude-cache/*
output=$(OAUTH_OUT=$limited OAUTH_EXIT=3 CLI_OUT=$good CLI_EXIT=0 bash "$TMP/claude-shim" usage --provider claude --format json --source cli)
[[ "$output" = "$good" ]]
[[ "$(cat "$CALLS")" = $'oauth\ncli' ]]
[[ -f "$TMP/claude-cache/codexbar-claude-$uid-oauth-cooldown" ]]
[[ -s "$cache_file" ]]
[[ ! -e "$TMP/claude-cache/codexbar-claude-$uid-cli-broken" ]]

# 429 + no cache with a failing CLI records the broken CLI and returns the error.
: > "$CALLS"; rm -f "$TMP"/claude-cache/*
set +e
output=$(OAUTH_OUT=$limited OAUTH_EXIT=3 CLI_OUT='[{"error":"cli dead"}]' CLI_EXIT=1 bash "$TMP/claude-shim" usage --provider claude --format json --source cli); rc=$?
set -e
[[ "$rc" -eq 3 ]]
check "$output" 'rate limited'
[[ "$(cat "$CALLS")" = $'oauth\ncli' ]]
[[ -f "$TMP/claude-cache/codexbar-claude-$uid-oauth-cooldown" ]]
[[ -f "$TMP/claude-cache/codexbar-claude-$uid-cli-broken" ]]

# Negative cache expiry retries (via age and via CODEXBAR_CLAUDE_CLI_COOLDOWN).
: > "$CALLS"; rm -f "$TMP"/claude-cache/*
touch "$TMP/claude-cache/codexbar-claude-$uid-cli-broken"
touch -d '31 minutes ago' "$TMP/claude-cache/codexbar-claude-$uid-cli-broken"
output=$(OAUTH_OUT=$expired OAUTH_EXIT=3 CLI_OUT=$good CLI_EXIT=0 bash "$TMP/claude-shim" usage --provider claude --format json --source cli)
[[ "$output" = "$good" ]]
[[ "$(cat "$CALLS")" = $'oauth\ncli' ]]
[[ ! -e "$TMP/claude-cache/codexbar-claude-$uid-cli-broken" ]]

: > "$CALLS"; rm -f "$TMP"/claude-cache/*
touch "$TMP/claude-cache/codexbar-claude-$uid-cli-broken"
touch -d '3 seconds ago' "$TMP/claude-cache/codexbar-claude-$uid-cli-broken"
output=$(CODEXBAR_CLAUDE_CLI_COOLDOWN=2 OAUTH_OUT=$expired OAUTH_EXIT=3 CLI_OUT=$good CLI_EXIT=0 bash "$TMP/claude-shim" usage --provider claude --format json --source cli)
[[ "$output" = "$good" ]]
[[ "$(cat "$CALLS")" = $'oauth\ncli' ]]
[[ ! -e "$TMP/claude-cache/codexbar-claude-$uid-cli-broken" ]]

# Gemini must report transform failure when the real CLI succeeds, and retain CLI failures.
mkdir "$TMP/transform-bin"
cat > "$TMP/transform-bin/python3" <<'PYTHON'
#!/usr/bin/env bash
cat >/dev/null
printf 'partial-transform-output\n'
exit 43
PYTHON
chmod +x "$TMP/transform-bin/python3"
for cli_exit in 0 125 127; do
  set +e
  output=$(PATH="$TMP/transform-bin:$PATH" FAKE_EXIT=$cli_exit bash "$TMP/shim" usage --provider gemini --format json); rc=$?
  set -e
  expected=$cli_exit; [ "$cli_exit" -ne 0 ] || expected=43
  [[ "$rc" -eq "$expected" ]]
  check "$output" 'partial-transform-output'
done

# Installer commands and destinations are redirected into TMP; no downloads or system writes.
mkdir -p "$TMP/install-bin" "$TMP/install-fixture" "$TMP/archive/CodexBar_CodexBarCore.bundle"
export REAL_CP="$(command -v cp)" REAL_INSTALL="$(command -v install)" REAL_MV="$(command -v mv)"
export INSTALL_FIXTURE="$TMP/install-fixture" INSTALL_TARGET="$TMP/installed"
export INSTALL_SHIM_SOURCE="$TMP/install-shim" INSTALL_LOG="$TMP/install-log"
cat > "$TMP/archive/CodexBarCLI" <<'CLI'
#!/usr/bin/env bash
printf 'fixture-cli-version\n'
CLI
printf 'fixture-plugin\n' > "$TMP/archive/CodexBar_CodexBarCore.bundle/plugin"
printf 'fixture-version\n' > "$TMP/archive/VERSION"
chmod +x "$TMP/archive/CodexBarCLI"
tar -czf "$INSTALL_FIXTURE/archive.tar.gz" -C "$TMP/archive" .
sed "s|^REAL=.*|REAL=$INSTALL_TARGET/CodexBarCLI|" "$ROOT/codexbar-shim.sh" > "$INSTALL_SHIM_SOURCE"
sed -e "s|^INSTALL_DIR=.*|INSTALL_DIR=$INSTALL_TARGET|" \
    -e "s|/usr/local/bin/codexbar|$TMP/installed-link|g" "$ROOT/install.sh" > "$TMP/installer"
cat > "$TMP/install-bin/curl" <<'CURL'
#!/usr/bin/env bash
dest=''
while [ "$#" -gt 0 ]; do
  if [ "$1" = -o ]; then dest=$2; shift 2; else shift; fi
done
if [[ "$dest" = *.sha256 ]]; then
  file=${dest##*/}; file=${file%.sha256}
  digest=$(sha256sum "$INSTALL_FIXTURE/archive.tar.gz"); digest=${digest%% *}
  case "$INSTALL_CASE" in
    wrong-name) file=unrelated.tar.gz ;;
    absolute-name) file="$INSTALL_FIXTURE/archive.tar.gz" ;;
    traversal-name) file="../$file" ;;
    wrong-digest) digest=$(printf '%064d' 0) ;;
    binary-checksum) file="*$file" ;;
  esac
  printf '%s  %s\n' "$digest" "$file" > "$dest"
  if [ "$INSTALL_CASE" = extra-entry ]; then printf '%s  unrelated.tar.gz\n' "$digest" >> "$dest"; fi
else
  "$REAL_CP" "$INSTALL_FIXTURE/archive.tar.gz" "$dest"
fi
CURL
cat > "$TMP/install-bin/cp" <<'COPY'
#!/usr/bin/env bash
if [ "$INSTALL_CASE" = copy-failure ] && [ "${1:-}" = -a ]; then exit 41; fi
exec "$REAL_CP" "$@"
COPY
cat > "$TMP/install-bin/install" <<'INSTALL'
#!/usr/bin/env bash
if [ "${3:-}" = "$INSTALL_SHIM_SOURCE" ]; then
  [[ "$4" = "$INSTALL_TARGET"/.codexbar-shim.* ]] || { echo 'Shim was not staged in its destination directory' >&2; exit 42; }
  [ "$(cat "$INSTALL_TARGET/codexbar-shim")" = old-shim ] || { echo 'Existing shim changed before staging completed' >&2; exit 42; }
  printf 'shim-staged\n' >> "$INSTALL_LOG"
  if [ "$INSTALL_CASE" = shim-stage-failure ]; then printf 'partial-shim\n' > "$4"; exit 42; fi
fi
exec "$REAL_INSTALL" "$@"
INSTALL
cat > "$TMP/install-bin/mv" <<'MOVE'
#!/usr/bin/env bash
if [ "${3:-}" = "$INSTALL_TARGET/codexbar-shim" ]; then
  [[ "$2" = "$INSTALL_TARGET"/.codexbar-shim.* ]] || exit 42
  printf 'shim-renamed\n' >> "$INSTALL_LOG"
fi
exec "$REAL_MV" "$@"
MOVE
cat > "$TMP/install-bin/codexbar" <<'CLI'
#!/usr/bin/env bash
printf 'wrong-path-cli\n' >> "$INSTALL_LOG"
exit 99
CLI
ln -s "$TMP/transform-bin/python3" "$TMP/install-bin/python3"
chmod +x "$TMP"/install-bin/{curl,cp,install,mv,codexbar}
installer_run() {
  rm -rf "$INSTALL_TARGET"; rm -f "$TMP/installed-link"; : > "$INSTALL_LOG"
  mkdir "$INSTALL_TARGET"; printf 'old-shim\n' > "$INSTALL_TARGET/codexbar-shim"
  PATH="$TMP/install-bin:$PATH" bash "$TMP/installer" "$INSTALL_SHIM_SOURCE"
}
for install_case in wrong-name absolute-name traversal-name wrong-digest extra-entry copy-failure shim-stage-failure; do
  set +e
  output=$(INSTALL_CASE=$install_case installer_run 2>&1); rc=$?
  set -e
  [[ "$rc" -ne 0 && "$(cat "$INSTALL_TARGET/codexbar-shim")" = old-shim ]]
  [[ ! -e "$TMP/installed-link" ]]
  [[ -z "$(find "$INSTALL_TARGET" -name '.codexbar-shim.*' -o -name '.CodexBarCLI.*')" ]]
  case "$install_case" in
    wrong-name|absolute-name|traversal-name) check "$output" 'Invalid checksum entry' ;;
    wrong-digest) check "$output" 'Checksum mismatch' ;;
    extra-entry) check "$output" 'exactly one entry' ;;
    copy-failure) [[ ! -e "$INSTALL_TARGET/CodexBarCLI" ]] ;;
    shim-stage-failure) [[ "$(cat "$INSTALL_LOG")" = shim-staged ]] ;;
  esac
done
for install_case in success binary-checksum; do
  output=$(INSTALL_CASE=$install_case installer_run)
  check "$output" 'Installed: fixture-cli-version'
  [[ "$(cat "$INSTALL_LOG")" = $'shim-staged\nshim-renamed' ]]
  [[ -x "$INSTALL_TARGET/codexbar-shim" && -L "$TMP/installed-link" ]]
  [[ "$(cat "$INSTALL_TARGET/CodexBar_CodexBarCore.bundle/plugin")" = fixture-plugin ]]
  [[ -z "$(find "$INSTALL_TARGET" -name '.codexbar-shim.*' -o -name '.CodexBarCLI.*')" ]]
done

echo 'Shim and installer fake-CLI tests passed.'
