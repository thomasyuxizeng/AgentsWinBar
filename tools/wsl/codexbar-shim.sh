#!/usr/bin/env bash
# codexbar shim for CodexBarWin. Installed as /usr/local/bin/codexbar inside WSL; the real CodexBar CLI
# lives at /opt/codexbar/CodexBarCLI. CodexBarWin calls `codexbar --provider <id> --format json ...`.
#
# What it changes, and why:
#   * gemini  -> the Gemini CLI login was shut down for individual Google accounts, so Gemini quota is
#                served from Antigravity (`agy`) and presented as the Gemini provider (5-hour + weekly).
#   * claude  -> the OAuth usage endpoint is rate limited by Anthropic (exit code 3), so use the Claude CLI
#                route, cache results for 10 minutes, and fall back to the last good result on failure.
#   * others  -> passed straight through to the real CLI.
REAL=/opt/codexbar/CodexBarCLI

# Cost readers must bypass usage caching/backoff. Their results depend on log-path environment
# variables, which are deliberately absent from usage commands and from the usage cache key.
if [ "${1:-}" = "cost" ]; then exec "$REAL" "$@"; fi

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
    exit ${PIPESTATUS[0]}
  fi
fi

# Claude: cache for 10 minutes and always answer quickly. Routes, in order:
#   1. OAuth usage endpoint: ~1 second, but Anthropic rate limits it (exit code 3), so after a failure it is
#      left alone for 10 minutes.
#   2. Claude CLI: ~18 seconds, and currently returns "a subscription notice without session quota data"
#      for some plans. Only tried as a fallback, and a failure is remembered for 6 hours so the slow
#      attempt isn't repeated on every refresh (which used to push a refresh past the app's timeout).
#   3. The last good result, however old.
case " $* " in *" --provider claude "*)
  C=/var/tmp/codexbar-claude-$(printf '%s' "$*" | md5sum | cut -c1-8).json
  COOLDOWN=/var/tmp/codexbar-claude-oauth-cooldown
  CLI_BROKEN=/var/tmp/codexbar-claude-cli-broken
  age() { echo $(( $(date +%s) - $(stat -c %Y "$1") )); }

  if [ -s "$C" ] && [ "$(age "$C")" -lt 600 ]; then cat "$C"; exit 0; fi

  oauth=(); cli=()
  for a in "${out[@]}"; do
    if [ "$a" = oauth ] || [ "$a" = cli ]; then oauth+=("oauth"); cli+=("cli"); else oauth+=("$a"); cli+=("$a"); fi
  done

  o='[{"provider":"claude","source":"oauth","error":{"kind":"provider","code":3,"message":"Claude usage is rate limited right now; retrying shortly."}}]'
  rc=3

  if [ ! -e "$COOLDOWN" ] || [ "$(age "$COOLDOWN")" -ge 600 ]; then
    r=$("$REAL" "${oauth[@]}"); rc=$?
    if [ $rc -eq 0 ]; then o=$r; rm -f "$COOLDOWN"; else o=$r; touch "$COOLDOWN"; fi
  fi

  if [ $rc -ne 0 ] && { [ ! -e "$CLI_BROKEN" ] || [ "$(age "$CLI_BROKEN")" -ge 21600 ]; }; then
    r=$("$REAL" "${cli[@]}"); rc2=$?
    if [ $rc2 -eq 0 ]; then o=$r; rc=0; rm -f "$CLI_BROKEN"; else touch "$CLI_BROKEN"; fi
  fi

  if [ $rc -eq 0 ]; then printf '%s\n' "$o" > "$C"; printf '%s\n' "$o"; exit 0; fi
  if [ -s "$C" ]; then cat "$C"; exit 0; fi
  printf '%s\n' "$o"; exit $rc ;;
esac

exec "$REAL" "${out[@]}"
