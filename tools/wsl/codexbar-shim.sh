#!/usr/bin/env bash
# codexbar shim for CodexBarWin. Installed as /usr/local/bin/codexbar inside WSL; the real CodexBar CLI
# lives at /opt/codexbar/CodexBarCLI. CodexBarWin calls `codexbar --provider <id> --format json ...`.
#
# What it changes, and why:
#   * gemini  -> the Gemini CLI login was shut down for individual Google accounts, so Gemini quota is
#                served from Antigravity (`agy`) and presented as the Gemini provider (5-hour + weekly).
#   * claude  -> the OAuth usage endpoint is rate limited by Anthropic (exit code 3); when rate limited,
#                serve the last good result immediately, try the Claude CLI route (bounded by a timeout)
#                only when no cache exists, and remember CLI failures for 30 minutes.
#   * others  -> passed straight through to the real CLI.
REAL=/opt/codexbar/CodexBarCLI

# Cost readers must bypass usage caching/backoff. Their results depend on log-path environment
# variables, which are deliberately absent from usage commands and from the usage cache key.
if [ "${1:-}" = "cost" ]; then exec "$REAL" "$@"; fi

# The bridge is explicitly enabled by this one literal setting. Never execute the config as shell code.
# Only usage invocations for the Codex provider can use a Windows-mounted home.
codex_provider=0; args=("$@")
for ((i=0; i<$#; i++)); do
  if [ "${args[$i]}" = "--provider" ] && [ "${args[$((i+1))]:-}" = "codex" ]; then codex_provider=1; fi
done
if [ "$codex_provider" -eq 1 ] && { [ "${1:-}" = "usage" ] || [[ "${1:-}" = --* ]]; }; then
  bridge_defined=0; bridge_home=''
  config="${HOME}/.config/codexbar/shim.env"
  if [ -f "$config" ]; then
    while IFS= read -r line || [ -n "$line" ]; do
      line="${line%$'\r'}"
      case "$line" in
        CODEXBAR_CODEX_HOME=*) bridge_defined=1; bridge_home="${line#CODEXBAR_CODEX_HOME=}" ;;
      esac
    done < "$config"
  fi
  if [ "$bridge_defined" -eq 1 ]; then
    [ -n "$bridge_home" ] && [[ "$bridge_home" = /* ]] || { echo 'CODEXBAR_CODEX_HOME must be a nonempty absolute WSL path.' >&2; exit 1; }
    bridge_path=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin
    # Linux Codex in a system directory could refresh bridged credentials. Fail closed if it exists.
    if PATH="$bridge_path" command -v codex >/dev/null 2>&1 || PATH="$bridge_path" command -v codex.exe >/dev/null 2>&1; then
      echo 'Codex bridge refused: move the Linux Codex CLI to ~/.local/bin (outside system PATH), or remove CODEXBAR_CODEX_HOME from shim.env.' >&2
      exit 1
    fi
    bridge_args=(); args=("$@"); i=0
    while [ "$i" -lt "${#args[@]}" ]; do
      case "${args[$i]}" in
        --source) i=$((i+2)); continue ;;
        --source=*) i=$((i+1)); continue ;;
      esac
      bridge_args+=("${args[$i]}"); i=$((i+1))
    done
    exec env PATH="$bridge_path" CODEX_HOME="$bridge_home" "$REAL" "${bridge_args[@]}" --source oauth
  fi
fi

args=("$@"); gem=0; json=0; out=()
i=0
while [ $i -lt ${#args[@]} ]; do
  a="${args[$i]}"
  if [ "$a" = "--provider" ] && [ "${args[$((i+1))]}" = "gemini" ]; then gem=1; out+=("--provider" "antigravity"); i=$((i+2)); continue; fi
  if [ "$a" = "--source" ] && [ $gem -eq 1 ]; then i=$((i+2)); continue; fi
  if [ "$a" = "json" ]; then json=1; fi
  out+=("$a"); i=$((i+1))
done

# Gemini: drop --source regardless of argument order, then reshape Antigravity's output
if [ $gem -eq 1 ]; then
  clean=(); j=0
  while [ $j -lt ${#out[@]} ]; do
    if [ "${out[$j]}" = "--source" ]; then j=$((j+2)); continue; fi
    clean+=("${out[$j]}"); j=$((j+1))
  done
  out=("${clean[@]}")
  if [ $json -eq 1 ]; then
    "$REAL" "${out[@]}" | python3 -c '
import sys, json
from datetime import datetime, timezone
d = json.load(sys.stdin)
for e in d:
    if e.get("provider") != "antigravity": continue
    e["provider"] = "gemini"
    u = e.get("usage", {})
    if "identity" in u: u["identity"]["providerID"] = "gemini"
    w = {x["id"]: x["window"] for x in u.get("extraRateWindows", [])}
    p = w.get("antigravity-quota-summary-gemini-5h"); s = w.get("antigravity-quota-summary-gemini-weekly")
    if p: u["primary"] = p
    if s: u["secondary"] = s
    e.pop("rateWindowLabels", None)
    # Antigravity also gives access to Claude and GPT models with their own quota. Keep those as extra
    # windows (shown on the detail page only); Gemini'"'"'s own two windows are already primary/secondary.
    titles = {"antigravity-quota-summary-3p-5h": "Claude and GPT 5-hour",
              "antigravity-quota-summary-3p-weekly": "Claude and GPT weekly"}
    extras = []
    for x in u.get("extraRateWindows", []):
        if x.get("id", "").startswith("antigravity-quota-summary-gemini"): continue
        if x.get("id") in titles: x["title"] = titles[x["id"]]
        extras.append(x)
    u["extraRateWindows"] = extras
    for k in ("primary", "secondary"):
        x = u.get(k)
        if x and x.get("resetsAt"):
            t = datetime.fromisoformat(x["resetsAt"].replace("Z", "+00:00"))
            m = max(0, int((t - datetime.now(timezone.utc)).total_seconds() // 60))
            dd, hh, mm = m // 1440, (m % 1440) // 60, m % 60
            x["resetDescription"] = "Resets in " + (f"{dd}d {hh}h" if dd else f"{hh}h {mm}m")
print(json.dumps(d))
'
    pipeline_status=("${PIPESTATUS[@]}")
    [ "${pipeline_status[0]}" -eq 0 ] || exit "${pipeline_status[0]}"
    exit "${pipeline_status[1]}"
  fi
fi

# Claude: cache for 10 minutes and always answer quickly. Routes, in order:
#   1. OAuth usage endpoint: ~1 second, but Anthropic rate limits it, so after a rate-limit answer it is
#      left alone for 10 minutes. When in 429 cooldown and a usable cache exists, route 3 is served
#      immediately to avoid blocking refreshes. Other failures (notably "OAuth token expired": CodexBar
#      never refreshes the token itself) set no cooldown and go straight to route 2.
#   2. Claude CLI: bounded by a timeout (25 s default, CODEXBAR_CLAUDE_CLI_TIMEOUT: refreshing an expired OAuth token takes ~18 s, and the app allows 30 s) so it cannot hang
#      a refresh. Running `claude` also refreshes an expired OAuth token, so route 1 recovers on the next
#      refresh. A failure or timeout (e.g. plans with a subscription notice without session quota) is
#      remembered for 30 minutes (CODEXBAR_CLAUDE_CLI_COOLDOWN) to keep the slow attempt off subsequent refreshes.
#   3. The last good result, however old. Served immediately when OAuth is in rate-limit cooldown and cached
#      data exists, or as fallback when all active routes fail. Its own usage.updatedAt is kept, so the app
#      can show its age. With no cached result, the real error is returned rather than a generic one.
case " $* " in *" --provider claude "*)
  uid=$(id -u)
  key=$(printf '%s' "$*" | md5sum | cut -c1-8)
  C=/var/tmp/codexbar-claude-$uid-$key.json
  COOLDOWN=/var/tmp/codexbar-claude-$uid-oauth-cooldown
  CLI_BROKEN=/var/tmp/codexbar-claude-$uid-cli-broken
  CLI_ATTEMPT=/var/tmp/codexbar-claude-$uid-cli-attempt
  # Legacy state is usable only when it belongs to this user. New writes always use UID-scoped names.
  owned() { [ -f "$1" ] && [ ! -L "$1" ] && [ -O "$1" ]; }
  readable_state() {
    if owned "$1"; then printf '%s' "$1"; elif owned "$2"; then printf '%s' "$2"; fi
  }
  cache_read=$(readable_state "$C" "/var/tmp/codexbar-claude-$key.json")
  cooldown_read=$(readable_state "$COOLDOWN" /var/tmp/codexbar-claude-oauth-cooldown)
  broken_read=$(readable_state "$CLI_BROKEN" /var/tmp/codexbar-claude-cli-broken)
  attempt_read=''
  if owned "$CLI_ATTEMPT"; then attempt_read=$CLI_ATTEMPT; fi
  write_state() {
    local staged
    staged=$(mktemp "/var/tmp/codexbar-claude-$uid.XXXXXX") || return 1
    if printf '%s\n' "$2" > "$staged" && mv -f "$staged" "$1"; then return 0; fi
    rm -f "$staged"; return 1
  }
  age() {
    local stamp
    # A concurrent successful refresh may remove a stamp after our ownership check.
    stamp=$(stat -c %Y "$1" 2>/dev/null) || { echo 2147483647; return 0; }
    echo $(( $(date +%s) - stamp ))
  }
  has_past_reset() {
    local raw_resets='' now=0 match='' ts='' reset_s=0
    raw_resets=$(grep -oE '"resetsAt"[[:space:]]*:[[:space:]]*"[^"]+"' "$1" 2>/dev/null || true)
    [ -n "$raw_resets" ] || return 1
    now=$(date -u +%s)
    while IFS= read -r match; do
      [ -n "$match" ] || continue
      ts=$(printf '%s' "$match" | cut -d'"' -f4)
      if [ -n "$ts" ] && reset_s=$(date -d "$ts" +%s 2>/dev/null); then
        if [ "$reset_s" -le "$now" ]; then return 0; fi
      fi
    done <<< "$raw_resets"
    return 1
  }
  cached_output() {
    # Add provenance without changing source/usage or the cache file's observation time.
    # Non-JSON output from older/custom CLIs remains byte-compatible.
    if ! command -v python3 >/dev/null 2>&1; then
      cat "$1"
      return 0
    fi
    python3 - "$1" "$(stat -c %Y "$1")" <<'PY'
import datetime, json, sys
with open(sys.argv[1]) as f:
    raw = f.read()
try:
    data = json.loads(raw)
except ValueError:
    sys.stdout.write(raw)
    sys.exit(0)
rows = data if isinstance(data, list) else [data]
if not all(isinstance(row, dict) for row in rows):
    sys.stdout.write(raw)
    sys.exit(0)
stamp = datetime.datetime.fromtimestamp(int(sys.argv[2]), datetime.timezone.utc).isoformat()
for row in rows:
    row['cached'] = True
    row['cacheUpdatedAt'] = stamp
print(json.dumps(data))
PY
  }

  if [ -n "$cache_read" ] && [ -s "$cache_read" ] && [ "$(age "$cache_read")" -lt 600 ] && ! has_past_reset "$cache_read"; then cached_output "$cache_read"; exit $?; fi

  oauth=(); cli=()
  for a in "${out[@]}"; do
    if [ "$a" = oauth ] || [ "$a" = cli ]; then oauth+=("oauth"); cli+=("cli"); else oauth+=("$a"); cli+=("$a"); fi
  done

  o='[{"provider":"claude","source":"oauth","error":{"kind":"provider","code":3,"message":"Claude usage is rate limited right now; retrying shortly."}}]'
  rc=3; oauth_tried=0; oauth_rate_limited=0

  if [ -n "$cooldown_read" ] && [ "$(age "$cooldown_read")" -lt 600 ]; then
    oauth_rate_limited=1
  else
    oauth_tried=1
    if r=$("$REAL" "${oauth[@]}"); then rc=0; else rc=$?; fi
    o=$r
    if [ $rc -eq 0 ]; then
      [ -n "$cooldown_read" ] && owned "$cooldown_read" && rm -f "$cooldown_read"
      owned "$COOLDOWN" && rm -f "$COOLDOWN"
    elif printf '%s' "$r" | grep -Eqi 'rate.?limit|too many requests|429'; then
      write_state "$COOLDOWN" '' || true
      oauth_rate_limited=1
    fi
  fi

  if [ "$oauth_rate_limited" -eq 1 ] && [ -n "$cache_read" ] && [ -s "$cache_read" ]; then
    cached_output "$cache_read"
    exit $?
  fi

  cli_timeout="${CODEXBAR_CLAUDE_CLI_TIMEOUT:-${CODEXBAR_CLI_TIMEOUT:-25}}"
  cli_cooldown="${CODEXBAR_CLAUDE_CLI_COOLDOWN:-${CODEXBAR_CLI_COOLDOWN:-1800}}"
  cli_attempt_ttl="${CODEXBAR_CLAUDE_CLI_ATTEMPT_TTL:-90}"
  if [ $rc -ne 0 ] && { [ -z "$broken_read" ] || [ "$(age "$broken_read")" -ge "$cli_cooldown" ]; } &&
     { [ -z "$attempt_read" ] || [ "$(age "$attempt_read")" -ge "$cli_attempt_ttl" ]; }; then
    # Suppress overlapping calls briefly; an interrupted refresh is not a CLI failure.
    write_state "$CLI_ATTEMPT" '' || true
    if command -v timeout >/dev/null 2>&1; then
      if r=$(timeout -k 3 "$cli_timeout" "$REAL" "${cli[@]}"); then rc2=0; else rc2=$?; fi
    else
      # Never launch an unbounded fallback. Keep errors generic and free of credentials.
      r=''; rc2=127
    fi
    if owned "$CLI_ATTEMPT"; then rm -f "$CLI_ATTEMPT" || true; fi
    if [ $rc2 -eq 0 ]; then
      o=$r; rc=0
      if [ -n "$broken_read" ] && owned "$broken_read"; then rm -f "$broken_read" || true; fi
      if owned "$CLI_BROKEN"; then rm -f "$CLI_BROKEN" || true; fi
    else
      write_state "$CLI_BROKEN" '' || true
      # With OAuth in its rate-limit cooldown, the CLI's error is the most current explanation.
      if [ $oauth_tried -eq 0 ]; then o=$r; rc=$rc2; fi
    fi
  fi

  if [ $rc -eq 0 ]; then write_state "$C" "$o" || true; printf '%s\n' "$o"; exit 0; fi
  if [ -n "$cache_read" ] && [ -s "$cache_read" ]; then cached_output "$cache_read"; exit $?; fi
  printf '%s\n' "$o"; exit $rc ;;
esac

exec "$REAL" "${out[@]}"
