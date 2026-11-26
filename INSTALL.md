# Install AgentsWinBar - instructions for an AI agent

Run setup as the human's normal Windows user on Windows 11, x64 or ARM64. The default setup uses native Windows providers and requires no reboot.

## Before you start

Tell the human to stay nearby. They may need to approve one Windows admin (UAC) prompt for the app certificate or Windows App Runtime, and complete any missing sign-ins in the official Windows tools.

Run the installer in ordinary Windows PowerShell, **outside your sandbox** (request full access or escalated permissions for this command), so it can register the app for the original user and display the UAC prompt if needed. Do not run the whole installer as administrator.

## Run

Clone the repository and allow up to 30 minutes for setup. Keep the command running through elevation.

```powershell
git clone https://github.com/thomasyuxizeng/AgentsWinBar.git
cd AgentsWinBar
powershell -NoProfile -ExecutionPolicy Bypass -File install.ps1
```

For private access, obtain access from the owner, have the human complete `gh auth login`, then use `gh repo clone thomasyuxizeng/AgentsWinBar`. GitHub authentication is separate from setup's UAC prompt.

Preview the install:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File install.ps1 -DryRun
```

Setup verifies the release checksum and MSIX signer, installs or updates the app, and launches it. If the Windows App Runtime is missing, setup downloads a Microsoft-signed redistributable via WinGet or Microsoft and tries it unelevated first. Any required administrator actions share one UAC prompt.

Setup detects providers from Windows tools and defaults to Claude and Codex when it detects none. Set the list explicitly with `-Providers claude,codex,gemini`. Other options include `-Repo owner/repo`, `-MsixPath <local-msix>`, and `-SkipApp` to check sign-ins without installing or launching the app. `-SkipWsl` remains accepted and has no effect.

Setup stages its script, downloads, settings for reruns, and logs in `%LOCALAPPDATA%\AgentsWinBar-Setup`. It reuses saved selections when rerun from there. Use `-Resume` to reload saved selections from the clone and restore `-SkipApp` if previously set. Explicit arguments override remembered ones.

## Handle the result

Read the summary, the process exit code, and the last line, `AGENTSWINBAR_SETUP=<status>`.

| Exit | Status | Next action |
| --- | --- | --- |
| `0` | `ok` | Setup found a sign-in for every selected provider. For usage marked `none`, allow the app a few minutes to refresh. |
| `20` | `signin` | Give the human the printed sign-in instructions, then rerun after they finish. |
| `30` | `uac` | Elevation was declined or an administrator action failed. Read the setup log, resolve the cause, and rerun. |
| `40` | `access` | Confirm that the release is available. For a private repository, obtain access and have the human complete `gh auth login`, then rerun. |
| `50` | `failed` | Resolve the reported Windows, hardware, or network precondition and rerun. |
| `1` | `failed` | Read the phase summary and setup log, fix the reported failure, and rerun. |
| `3010` | `reboot` | Applies only with `-WithWsl`. Ask the human to reboot, then rerun with `-WithWsl`. |

Only the human signs in. For Claude, run `claude` in a Windows terminal once and sign in. For Codex, sign in to the Windows Codex app or run `codex login` once. For Gemini, sign in to Antigravity on Windows once.

For a staged retry, run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "$env:LOCALAPPDATA\AgentsWinBar-Setup\install.ps1"
```

## Safety

- Leave sign-ins to the human. Never start an interactive provider session for them or give an agent a task during sign-in.
- Never read, print, copy, upload, or move provider credential files or blobs. Never handle passwords or tokens or pass tokens on command lines.
- Run setup as the original Windows user. Approve elevation only for the batched administrator step so app registration stays with the original user.
- Stop on checksum or signer failures. Preserve existing app settings.

## Verify

Confirm the app launched and setup returned exit `0` with `AGENTSWINBAR_SETUP=ok` after all selected sign-ins were present. A dry run previews the steps. Setup waits up to 120 seconds after launching the app and reports only the usage status for each selected provider.

- `live` means setup found a current provider reading after launch.
- `stale` means setup found an outdated reading or a rate-limit indication.
- `none` means setup has yet to confirm a usable reading. Let the app refresh for a few minutes, then check usage in the tray app.

## Update

Update the clone and rerun setup. It installs the latest release over the current app and preserves existing settings and cost totals.

```powershell
cd AgentsWinBar
git pull
powershell -NoProfile -ExecutionPolicy Bypass -File install.ps1
```

Handle the result as for a first install. Administrator approval is needed only if the package signer needs trust or Windows App Runtime needs elevation.

## Upgrading from CodexBarWin

The app was renamed from CodexBarWin. AgentsWinBar installs beside the old app and copies its data on first launch; setup never removes the old app. Once AgentsWinBar shows the human's data, the human can remove the old app as the original user:

```powershell
Get-AppxPackage -Name CodexBarWin | Remove-AppxPackage
```

## Uninstall

Remove the Windows app as the original user:

```powershell
Get-AppxPackage -Name AgentsWinBar | Remove-AppxPackage
```

Keep shared runtimes, provider tools, and sign-ins. If the human requests cleanup, remove `%LOCALAPPDATA%\AgentsWinBar-Setup` and any remaining app settings under `%LOCALAPPDATA%\AgentsWinBar` or the package's `LocalCache`.

## Optional: WSL fallback

Use `-WithWsl` only when a provider's usage source is set to `wsl` in `settings.json`. Pass it on every WSL setup run, including retries. This route may enable or update WSL, provision a distro, install the pinned CodexBar CLI and shim, and open a human sign-in console. Use `-Distro` to select a distro, `-NoSignIn` to skip the console, or `-NoWindowsSignIns` to use a separate Linux Codex sign-in. Keep the staged files through any requested reboot.

For verification, rerun with `-WithWsl -NoSignIn` and require exit `0`. It means each selected provider returned live or temporarily stale usage. For diagnostics, use the default distro and user, matching the app:

```powershell
wsl --exec bash -ic "codexbar usage --provider codex --source cli --format json"
```

Repeat for the configured providers and keep raw usage output private. Report any WSL default-distro change to the human. Keep existing distro defaults beyond setup and leave Windows Claude credentials in Windows. If an interactive Antigravity login opens, have the human sign in and exit immediately.

On uninstall, keep WSL and shared distro resources. If the human asks to remove the CodexBar CLI and shim, use `wsl -u root --exec bash -c 'rm -f /usr/local/bin/codexbar; rm -rf /opt/codexbar'`. To disable the Windows Codex bridge, remove only the `CODEXBAR_CODEX_HOME` line from the default user's `~/.config/codexbar/shim.env`. Leave distros registered and preserve provider credential directories.
