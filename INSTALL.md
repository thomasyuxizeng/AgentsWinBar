# Install CodexBarWin — instructions for an AI agent

Run as the human's normal Windows user on Windows 11, x64 or ARM64. Do the work yourself; the human should only have to click.

## Before you start

Tell the human once, up front, so they stay nearby: "This takes about 5–15 minutes. You may get one Windows admin (UAC) prompt, possibly a request to reboot, and one window where you approve browser sign-ins. I'll do everything else."

Run the installer **outside your sandbox** (request full access or escalated permissions for this command). Sandboxed shells usually can't reach WSL (`E_ACCESSDENIED`) or show a UAC prompt; the installer stops with exit 50 and changes nothing if it detects that.

## Run

Clone and run with a **30-minute timeout**, or run in the background and wait for exit. Keep the sign-in window visible. Do not kill a long-running elevated step.

```powershell
git clone https://github.com/Wave-OCE/CodexBarWin.git
cd CodexBarWin
powershell -NoProfile -ExecutionPolicy Bypass -File install.ps1
```

For private access, obtain access from the owner, have the human approve `gh auth login`, then use `gh repo clone Wave-OCE/CodexBarWin`.

Preview without making changes:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File install.ps1 -DryRun
```

The installer verifies checksums and the MSIX signer, uses the WSL default distro/user, installs missing dependencies and CLIs, installs the app and checks usage JSON. It preserves a pre-existing distro's default user, including root. It provisions a user only for a distro it installs or when its saved state records interrupted provisioning. Provider CLIs are installed only when usage is unavailable and the Linux CLI is missing; live or stale usage preserves the working setup. Scripts, downloads, parameter state and logs live in `%LOCALAPPDATA%\CodexBarWin-Setup`. Keep them through reboot. Reruns probe state and finish missing work.

Runtime setup uses WinGet download or Microsoft's official download, verifies the Microsoft signature and tries unelevated installation. This avoids a separate `winget install` UAC prompt; required elevation joins the single batch.

Providers are detected from Windows tools, falling back to Claude and Codex. Options: `-Providers claude,codex,gemini`, `-Repo owner/repo`, `-Distro Ubuntu-24.04`, `-MsixPath <local-msix>`, `-NoSignIn`, `-NoWindowsSignIns`, `-SkipApp`, `-SkipWsl`. Staged reruns remember choices; sign-in suppression and skip flags are transient. `-Resume` explicitly reloads all saved parameters. There is no auto-resume or RunOnce.

Codex can reuse Windows ChatGPT sign-in read-only. If usage goes stale, have the human use Codex on Windows once, or rerun with `-NoWindowsSignIns` for Linux Codex. Claude uses its own WSL sign-in.

## Handle the result

Read the summary, process exit code and last line `CODEXBARWIN_SETUP=<status>`.

| Exit | Status | Next action |
| --- | --- | --- |
| `0` | `ok` | Installation is complete; each selected provider has live or temporarily stale usage. Stale providers are listed separately and do not need sign-in. |
| `3010` | `reboot` | Ask the human to reboot, then run the same command again. No automatic resume is registered. |
| `20` | `signin` | Installation is complete but listed providers have no usable usage and no rate-limit indication. Rerun without `-NoSignIn`; follow the printed instructions. Sign-in or provider/network recovery may be needed. |
| `30` | `uac` | The single elevation was declined or an admin action failed. Read the setup log, resolve the reported cause, then rerun. |
| `40` | `access` | Obtain repository access and have the human run `gh auth login`, or wait for the repository to become public. Rerun. |
| `50` | `failed` | Resolve the reported Windows, hardware, virtualization or network precondition, then rerun. |
| `1` | `failed` | Read the phase summary and setup log; fix the reported failure and rerun. |

For a staged retry, run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "$env:LOCALAPPDATA\CodexBarWin-Setup\install.ps1"
```

The human approves **at most one UAC prompt**, reboots if WSL enablement requires it, and completes browser sign-ins through **one console window**. Private repository access requires separate approval.

## Safety

- Never print, copy, upload or move credential files, handle passwords or tokens, or pass tokens on command lines.
- Never type a task or prompt into `agy`. If its interactive login is required, tell the human to sign in with Google and exit immediately; it is an agent.
- Leave sign-ins to the human. Do not start your own interactive `agy` or Claude session.
- Do not change WSL defaults beyond the installer. Report its default-distro change to the human.
- Do not rerun the whole installer as administrator. Per-user registration, app installation and sign-ins must belong to the original Windows user, even when UAC uses another admin account.
- Do not bypass checksum or signer failures. Do not overwrite existing app settings or copy Windows Claude credentials into WSL.

## Verify

Rerun the staged installer with `-NoSignIn`; require exit `0`, `CODEXBARWIN_SETUP=ok` and app launch. A dry run or skipped phase does not prove success.

For diagnostics, use the same command shape as the app, without `-d`, for each selected provider:

```powershell
wsl --exec bash -ic "codexbar usage --provider codex --source cli --format json"
```

Usage has three states: `live` means provider-matching, current usage windows; `stale` means usage older than the 30-minute freshness window, otherwise outdated usage, or a rate-limit error (including a temporarily cached Claude result); `none` means no usable usage and no rate-limit indication, including authentication errors. An explicit authentication failure means `none` even with cached windows; a rate-limit indication takes precedence and means `stale`. Stale providers are reported as "temporarily stale (rate limit or provider issue), not a sign-in problem". Only `none` opens sign-in and prevents exit `0`. Empty JSON or exit zero alone is insufficient. Repeat for `claude` and `gemini` if selected.

## Uninstall

Remove the Windows app as the original user:

```powershell
Get-AppxPackage -Name CodexBarWin | Remove-AppxPackage
```

Keep WSL, its distro, shared runtimes, provider CLIs and sign-ins unless the human explicitly asks to remove them. To remove only the CodexBar CLI and shim, use `wsl -u root --exec bash -c 'rm -f /usr/local/bin/codexbar; rm -rf /opt/codexbar'`. To disable the Windows Codex bridge, remove only the `CODEXBAR_CODEX_HOME` line from the default user's `~/.config/codexbar/shim.env`.

Optionally remove `%LOCALAPPDATA%\CodexBarWin-Setup` and the app's `settings.json` under `%LOCALAPPDATA%\CodexBarWin` or packaged `LocalCache`. Never unregister a distro or delete provider credential directories as part of this app's uninstall.
