# WSL setup for CodexBarWin

CodexBarWin reads usage by running the [CodexBar CLI](https://github.com/steipete/CodexBar) inside WSL.
This folder holds everything that has to exist inside WSL, so a fresh machine or a reinstalled distro can
be set up in one step.

## Install

From PowerShell, in this folder:

```powershell
.\install-wsl-shim.ps1 -Distro Ubuntu-24.04
```

This downloads the CodexBar CLI (checksum verified) to `/opt/codexbar`, installs the shim as
`/usr/local/bin/codexbar`, and checks it runs. Inside the distro you can also run
`sudo bash install.sh` directly. Set `CODEXBAR_VERSION` to pin another CLI release.

## What the shim does

`codexbar-shim.sh` sits in front of the real CLI:

| Provider | Behaviour |
|----------|-----------|
| `gemini` | Google shut down the Gemini CLI login for individual accounts, so Gemini quota is read from Antigravity (`agy`) and shown as the Gemini provider: 5-hour and weekly windows. |
| `claude` | Uses the Claude CLI route instead of the OAuth usage endpoint (which Anthropic rate limits, giving exit code 3), caches results for 5 minutes, and falls back to the last good result if a fetch fails. |
| anything else | Passed straight through. |

## Sign in (once, inside WSL)

- **Claude:** run `claude` and log in with your Claude account.
- **Codex:** install the Linux CLI with `npm install -g @openai/codex` (it needs its own copy inside WSL:
  a Windows `codex` found on the PATH cannot run there), then run `codex login` and sign in with your
  ChatGPT account. If the browser redirect doesn't reach WSL, use `codex login --device-auth` instead.
  Then turn Codex on in CodexBarWin under Settings, Providers.
- **Gemini:** install Antigravity's CLI with `curl -fsSL https://antigravity.google/cli/install.sh | bash`,
  run `agy` once and sign in with Google. Exit it straight away: it is an agent and will start working
  on anything you type.

To undo the shim: `ln -sf /opt/codexbar/CodexBarCLI /usr/local/bin/codexbar`.
