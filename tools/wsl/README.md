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
| `claude` | Tries the OAuth usage endpoint first (about 1 second). Anthropic rate limits it, so after a rate-limit answer it is left alone for 10 minutes. Any other failure, such as an expired OAuth token (CodexBar never refreshes it), goes straight to the Claude CLI, which also refreshes the token. The CLI is slow, so it is skipped for 30 minutes after it fails. Results are cached for 10 minutes. If every route fails, the last good result is shown with its original `updatedAt`, which the app uses to label it as old data. With no cached result, the real error is shown. |
| `codex` | Passed straight through, unless the Windows sign-in bridge is on (below). |
| anything else | Passed straight through. |

### Windows Codex sign-in bridge (optional)

If `~/.config/codexbar/shim.env` contains `CODEXBAR_CODEX_HOME=/mnt/c/Users/<you>/.codex`, Codex usage is read
from your Windows Codex sign-in instead of a separate Linux one. The shim runs the CLI with `--source oauth`,
that `CODEX_HOME`, and a minimal `PATH`, so no Codex binary can refresh (and so rotate) the Windows tokens. It
refuses to run if a `codex` binary is on the system `PATH`. The bridge only reads the sign-in. If Codex usage
goes stale, use Codex on Windows once so it refreshes its own sign-in.

`install.ps1` turns this on automatically when WSL Codex isn't signed in and a Windows ChatGPT sign-in exists,
and checks that it returns usage. `install.ps1 -NoWindowsSignIns` turns it off. To turn it off by hand, delete
the `CODEXBAR_CODEX_HOME` line.

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
