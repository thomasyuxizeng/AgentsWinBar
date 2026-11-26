# Security Policy

AgentsWinBar is a free, open-source Windows 11 tray app maintained by one person in spare time. Because it reads your local AI provider sign-ins, calls provider endpoints, can invoke provider command-line tools, and uses a self-signed signing certificate, this document explains exactly what it touches, what leaves your machine, what the installer changes, where the limits and risks are, and how to report a problem privately.

## Supported Versions

| Version | Supported |
|---|---|
| Latest AgentsWinBar release (`1.6.x`) | Yes |
| Earlier AgentsWinBar releases | No |
| Releases under the old name (`CodexBarWin`) | No |

## Reporting a Vulnerability

Report security issues privately through GitHub's private vulnerability reporting on [thomasyuxizeng/AgentsWinBar](https://github.com/thomasyuxizeng/AgentsWinBar) (**Security** tab, **Report a vulnerability**).

- **Do not open a public GitHub issue** for a security vulnerability.
- No security email address is published.
- AgentsWinBar has a single maintainer working in spare time. Reports are handled on a best-effort basis, and no response or resolution timeline is promised.

When reporting, include:

- The AgentsWinBar version and Windows 11 build/architecture (`x64` or `ARM64`).
- Steps to reproduce or a minimal proof of concept.
- What is affected and what an attacker or bug can achieve.
- Any relevant logs only after reviewing them (see [Local Data and Logs](#local-data-and-logs) regarding `crash.log`).

## Scope

**In scope:**

- The AgentsWinBar desktop application
- The installer script (`install.ps1`)
- The GitHub Actions release workflow

**Out of scope:**

- The providers' own CLIs, desktop apps, and services (Claude Code, Codex, Antigravity, Anthropic, OpenAI, Google)
- Third-party libraries and runtimes (their own security policies apply; see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md))

## Trust Model and Limits

Before installing AgentsWinBar, be clear about the trade-offs it makes:

1. **Full-trust desktop package (`runFullTrust`).** Although distributed as an MSIX, AgentsWinBar declares `runFullTrust` in its manifest and runs with your Windows user account's permissions rather than inside a restricted app sandbox. That is what lets it read provider credential files, Windows Credential Manager, and local session logs, and run provider CLIs.
2. **In-memory access to live provider tokens.** The app reads your existing OAuth access tokens (and, if you opt in, your `ANTHROPIC_ADMIN_KEY` environment variable) to query usage and billing. Safeguards keep those secrets out of disk files and logs, but they do exist in process memory while requests are made.
3. **Machine-wide self-signed certificate trust.** Official MSIX packages are signed with a self-signed certificate rather than a public code-signing certificate or Microsoft Store signature. `install.ps1` adds that certificate to `LocalMachine\TrustedPeople`, which applies to all Windows user accounts on the PC and stays installed even after you uninstall AgentsWinBar until you remove it manually.
4. **Non-public provider usage endpoints.** The Claude, Codex, and Gemini usage endpoints are not public APIs. AgentsWinBar sends each provider's own client `User-Agent` because those endpoints only answer those clients. Providers can change, rate-limit, or stop answering them at any time.
5. **Background execution of provider CLIs.** By default, when a Claude or Gemini sign-in expires, AgentsWinBar can run `claude.exe` or `agy.exe` from absolute folders on your `PATH` (or their default install folders) to let the provider renew its own sign-in. This can be turned off in `settings.json`, and relies on the directories on your `PATH` being trustworthy.

## What the App Reads

AgentsWinBar treats all provider sign-ins as **read-only**. It never creates, edits, refreshes, or deletes any provider credential file or Windows Credential Manager entry. It reads only the default Windows user profile (`CLAUDE_CONFIG_DIR` and `CODEX_HOME` are not used).

| Source | Location | What is read | What is skipped or guarded |
|---|---|---|---|
| **Claude sign-in** | `%USERPROFILE%\.claude\.credentials.json` | Only the `claudeAiOauth` object (`accessToken`, `expiresAt`, `subscriptionType`, `rateLimitTier`) | All other JSON keys are skipped unread; opened read-only, 4 MiB file cap |
| **Codex sign-in** | `%USERPROFILE%\.codex\auth.json` | Access token and account metadata (`tokens.access_token`, `tokens.account_id`, `tokens.id_token`, `last_refresh`) | Refresh token is skipped unread; `OPENAI_API_KEY` is only checked for presence (never materialized); opened read-only, 4 MiB file cap |
| **Gemini sign-in** | Windows Credential Manager generic credential `gemini:antigravity` | Access token, expiry, and account metadata via read-only `CredReadW` | `CredWriteW` and `CredDeleteW` are not imported; refresh token and client secret are never read |
| **Local cost logs** (optional, when cost totals are enabled) | `%USERPROFILE%\.claude\projects`, `%USERPROFILE%\.codex\sessions`, `%USERPROFILE%\.codex\archived_sessions` (including `.jsonl.zst`) | Model names, dates, and token counts | Ordinary prompt/tool lines are filtered out before JSON parsing; lines over 512 KiB are skipped; `.jsonl.zst` decompression is capped at 1 GiB per archive; local index stores hashed paths, byte offsets, model names, dates, and token counts, with no raw file paths, prompts, or message text |
| **Anthropic Admin key** (optional, dashboard API credits panel) | `ANTHROPIC_ADMIN_KEY` environment variable (process env first, then user env) | Read on each use to query Anthropic's Admin API while the dashboard is visible | Never stored in app state or written to disk; spend figures are kept in memory only. Treat this as a sensitive organization key and only set it if you want the API credits panel |
| **Gemini spend log** (optional, dashboard Gemini panel) | Path set in `CODEXBAR_GEMINI_SPEND_LOG` environment variable | Dollar figures, timestamps, model names, and optional route from local JSONL lines | Only the last 8 MiB is read, only while the dashboard is visible; never uploaded |

**Token handling in memory and logs:**

- Access tokens are held in memory only, in buffers zeroed after use.
- Credential record types override `ToString` so tokens cannot be accidentally formatted or printed.
- Usage readers log only event types and exception type names, not raw messages that could contain token values.
- Tokens and keys are never logged, printed, or written to disk.

## Network Traffic

All outbound network requests use **HTTPS only**, a **fixed host allow list**, **no HTTP redirects**, and **no cookies**. Response bodies are capped (2 MiB for usage and status calls; 64 MiB for the model price catalog).

| Host and endpoint | When it is called | What is sent |
|---|---|---|
| `api.anthropic.com` (`/api/oauth/usage`) | Claude usage refresh (cached in memory for 9.5 min; backs off at least 10 min on HTTP 429) | Claude Bearer token, `anthropic-beta: oauth-2025-04-20`, `User-Agent: claude-code/2.1.0` |
| `api.anthropic.com` (`/v1/organizations/cost_report`, `/api_keys`, `/users`) | Only when `ANTHROPIC_ADMIN_KEY` is set and the dashboard window is open (at most once every 5 min, or on manual Refresh) | `x-api-key` Admin key, `anthropic-version` header |
| `chatgpt.com` (`/backend-api/wham/usage`) | Codex usage refresh | Codex Bearer token, `ChatGPT-Account-Id` when known, `User-Agent: CodexBar` |
| `cloudcode-pa.googleapis.com` and `daily-cloudcode-pa.googleapis.com` | Gemini usage refresh (read-only quota queries; never onboards an account) | Antigravity Bearer token, fixed quota query body (plus project ID when known), `User-Agent: antigravity/hub/2.9.1 linux/amd64` |
| `status.claude.com`, `status.openai.com`, `www.google.com/appsstatus` | With refreshes, at most once every 5 min per provider | Plain `GET`, no credentials |
| `models.dev/api.json` | At most once every 24 h while cost totals are enabled | Plain `GET`, no user data |

**What never leaves your PC:**

- There is **no telemetry, analytics, or crash reporting service**.
- Nothing from your local Claude Code or Codex session logs, prompts, message text, cost totals, or `CODEXBAR_GEMINI_SPEND_LOG` ever leaves the machine.

## Programs the App Starts

AgentsWinBar starts external programs in three specific cases:

### 1. Background sign-in renewal (on expired sign-in)

When a Claude or Gemini token is expired, AgentsWinBar can ask that provider's own CLI to renew its sign-in:

- **Claude:** runs `claude.exe auth status --json` (`claude.exe` resolved from an absolute `PATH` folder, else `%USERPROFILE%\.local\bin\claude.exe`).
- **Gemini:** runs `agy.exe models` (`agy.exe` resolved from an absolute `PATH` folder, else `%LOCALAPPDATA%\Microsoft\WinGet\Links\agy.exe`).
- **Codex:** no automatic renewal.

Safeguards:

- Fixed arguments only; no shell is invoked.
- Runs hidden with standard input closed and output discarded unread (for example, `claude auth status` prints the account email, which AgentsWinBar discards without reading).
- One renewal at a time per provider, with a 30-second time limit after which the process tree is killed.
- Rate-limited to at most one attempt per 30 minutes, backing off to 2 hours and then 6 hours while renewal keeps failing.
- A renewal is accepted only if the credential's expiry moves into the future.

To disable background renewal, quit the app and set `renewClaudeSignIn` and/or `renewGeminiSignIn` to `false` in `settings.json` (there is no Settings UI toggle for these options).

### 2. Sign in buttons (user-initiated only)

Only when you click a **Sign in** button, AgentsWinBar launches the provider's sign-in flow:

- **Claude:** `claude auth login` in Windows Terminal (or `cmd /k`) if `claude` is found in an absolute `PATH` folder or in `%USERPROFILE%\.local\bin`, else `https://code.claude.com/docs/en/setup` in your browser.
- **Codex:** `codex login` in a terminal if `codex` is found in an absolute `PATH` folder, else `https://developers.openai.com/codex/cli/`.
- **Gemini:** `%LOCALAPPDATA%\Programs\antigravity\Antigravity.exe` if present, else `agy` in a terminal (from an absolute `PATH` folder or the WinGet links folder), else `https://antigravity.google`.

The tool is started by its full resolved path, from the system folder, with Command Prompt's AutoRun and current-folder search turned off. Arguments and URLs are fixed constants in the source code; nothing is built from files or network responses.

### 3. Optional WSL fallback (off by default)

An older route that runs the CodexBar CLI inside WSL still exists as an opt-in fallback (`usageSources` or `costSource` set to `"wsl"` in `settings.json`, provisioned via `install.ps1 -WithWsl`). It is off by default, and AgentsWinBar never starts WSL unless you explicitly configure a provider to use it. When enabled, WSL output is size-capped (4 MiB per command, 32 MiB per batch) and error text from the WSL route is redacted for `Bearer` tokens, `access_token=` values, and long opaque strings.

## Installer, Signing, and Releases

### Build and release pipeline

- Releases are built by GitHub Actions from a version tag (`v*`), after running unit tests and the third-party license check.
- Package signing runs in its own isolated job that checks out and builds nothing.
- Every GitHub Action in the workflow is pinned to a commit SHA.
- Each release publishes a SHA-256 checksum file (`AgentsWinBar-<tag>-checksums.sha256`).
- Debug-only test hooks (`AGENTSWINBAR_TEST_*`) are compiled out of Release builds.

### Self-signed release certificate

Official MSIX packages are signed with a self-signed certificate:

- **Subject:** `CN=CodexBarWin OSS` (kept from the app's earlier name so existing installs upgrade and preserve the package family identity)
- **SHA-1 thumbprint:** `4E280F44AE28900533BBC497F28F62505990F3BD`
- **Valid until:** August 2036

### What `install.ps1` changes on your machine

You can run `install.ps1 -DryRun` to inspect the steps without making any changes. During a normal install, `install.ps1`:

1. Stages downloads and `state.json` (setup parameters only, never credentials) in `%LOCALAPPDATA%\AgentsWinBar-Setup`.
2. Downloads the release MSIX and SHA-256 checksum file from GitHub, verifies the SHA-256 hash, verifies the package signature, and **refuses any package not signed by thumbprint `4E280F44AE28900533BBC497F28F62505990F3BD`**.
3. Checks for Microsoft's **Windows App Runtime 1.8** (`8000.731.1532.0` or newer); if missing, downloads Microsoft's installer, verifies it is Authenticode-signed by Microsoft Corporation, and runs it quietly (unelevated first, elevated only if needed).
4. Prompts once with UAC (batched with the runtime install if needed) to add the release certificate to `LocalMachine\TrustedPeople`.
5. Installs the MSIX with `Add-AppxPackage` and launches AgentsWinBar.
6. Checks whether selected providers have Windows sign-ins present (reading only file length / `auth_mode` in `auth.json` and Credential Manager target existence; it never displays, copies, or serializes credentials).

If you do not want to trust the project's self-signed certificate, you can build and sign an MSIX from source with your own certificate (see [README.md](README.md#development)).

### How to uninstall and undo certificate trust

Uninstalling AgentsWinBar does **not** remove the certificate from `LocalMachine\TrustedPeople`, because that trust is machine-wide. To remove both the app and the certificate trust:

1. **Uninstall the app** (normal PowerShell, or via **Settings > Apps > Installed apps**):

   ```powershell
   Get-AppxPackage -Name AgentsWinBar | Remove-AppxPackage
   ```

2. **Remove the machine-wide certificate trust** (administrator PowerShell):

   ```powershell
   Remove-Item Cert:\LocalMachine\TrustedPeople\4E280F44AE28900533BBC497F28F62505990F3BD
   ```

   Once removed, Windows will no longer install packages signed with that certificate.

3. **Optional cleanup:** delete `%LOCALAPPDATA%\AgentsWinBar-Setup` and any leftover files in the app data folder below. Uninstalling leaves shared Windows runtimes, your provider tools, and your provider sign-ins untouched.

## Local Data and Logs

For the packaged MSIX app, AgentsWinBar stores its local data in:

```text
%LOCALAPPDATA%\Packages\AgentsWinBar_w05y65rx3mcz6\LocalCache\Local\AgentsWinBar\
```

This folder contains settings (`settings.json`), usage and status caches (`cache.json`, expired after 7 days), 14 days of usage history (`usage-history.json`), early-reset records (`early-resets.json`), the Claude 429 cooldown timestamp (`claude-cooldown.json`), the cost index (`cost-index\index.sqlite`) and permanent daily ledger (`cost-ledger.json`), cached model prices (`model-prices.json`), optional user-created `custom-pricing.json`, and the local logs `app.log` and `crash.log`. **No tokens or API keys are stored here.**

**Note on the local logs:**

- There is no external crash reporter. Release builds write warnings to `app.log` and unexpected errors to `crash.log`, each capped at 1 MB with one older copy (`.1`) kept.
- Both logs record exception types and, in `crash.log`, stack frames, but never exception messages, tokens or keys. Read them before attaching them to an issue or vulnerability report all the same.
