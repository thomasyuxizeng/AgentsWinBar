# Native Windows install summary

## Changes

| File | Changes |
| --- | --- |
| `install.ps1` | Made Windows-native setup the default. An explicit `-WithWsl` opts into the distro, shim, Linux provider tools, and WSL sign-in flow. Native setup skips those probes, staging steps, elevation actions, and reboot handling. `-SkipWsl` is an accepted no-op. Existing saved state, including state with a legacy `SkipWsl` field, still loads. Each invocation requires an explicit WSL opt-in, including staged retries and `-Resume`. |
| `install.ps1` | Kept release access, checksum and signer checks, certificate trust, runtime installation, Windows provider detection, app installation and launch, and existing package flags. The dry run looks up release metadata directly and previews native verification without launching the app. |
| `install.ps1` | Added presence-only checks for Claude and Codex using file metadata and Gemini using a scoped Credential Manager listing. Native setup prints one human instruction per missing provider and returns `20/signin`. |
| `install.ps1` | Added packaged cache polling for up to 120 seconds after launch. The parser uses each provider's `fetchedAt` and the cache save timestamp, reports `live`, `stale`, or `none`, and keeps unconfirmed usage informational. Setup returns `0/ok` when every selected Windows sign-in is present. Also corrected timestamp handling in the WSL usage helper to preserve the time zone of dates already parsed by PowerShell 7. |
| `tools/test-install.ps1` | Extended the AST-isolated tests and added a pass count. Temporary fixtures live under the worktree and are cleaned up. |
| `INSTALL.md` | Rewrote setup, sign-in, verification, update, and uninstall instructions around the Windows-native route. Documented the single possible UAC prompt, native exit statuses, and the optional WSL setup, verification, and uninstall steps. |
| `SUMMARY-native-install.md` | Recorded changes, test coverage, and verification output. |

## Tests added

- Mocked Claude and Codex file metadata against synthetic nonempty, absent, and empty files. The mocks reject credential content reads. Mocked Gemini target listings cover the exact target, a suffix mismatch, a username match, no credentials, and command failure.
- Synthetic cache tests cover current readings (including zero, calculated percentages, and extra windows), stale flags and observations, expired windows, rate limits, absent and malformed files, missing and mismatched providers, invalid readings, failed fetches, authentication errors, and an old provider entry republished in a new cache save. Output-stream checks verify that the helper emits only the status.
- The AST guard check walks top-level WSL command sites, including known WSL helpers and the staged shim installer. It requires each of the 55 sites to be inside the true branch of a `$WithWsl` condition. Native runtime regression checks also verify that a runtime `3010` allows the original-user registration attempt and produces no setup reboot verdict.

## Verification

### Installer helpers

`pwsh -NoProfile -File C:\Users\ThomasFramework\worktrees\CodexBarWin\native-install\tools\test-install.ps1`

```text
WSL opt-in AST check passed (55 call sites).
Installer helper mock tests passed (324 assertions).
Installer test exit code: 0
```

### Release security

`pwsh -NoProfile -File C:\Users\ThomasFramework\worktrees\CodexBarWin\native-install\tools\test-release-security.ps1`

```text
Signer release security tests passed.
Release security exit code: 0
```

Additional elevation coverage ran with the same command and `-Group Elevation`.

```text
Elevation release security tests passed.
Release security Elevation exit code: 0
```

The release security script prints a group verdict. A separate in-memory assertion count of the unchanged Signer and Elevation tests produced:

```text
Signer release security tests passed.
Signer assertions: 13
Elevation release security tests passed.
Elevation assertions: 35
```

### Windows PowerShell 5.1 installer helpers

`powershell -NoProfile -ExecutionPolicy Bypass -File C:\Users\ThomasFramework\worktrees\CodexBarWin\native-install\tools\test-install.ps1`

```text
WSL opt-in AST check passed (55 call sites).
Installer helper mock tests passed (324 assertions).
Windows PowerShell installer test exit code: 0
```

### Native dry run

`powershell -NoProfile -ExecutionPolicy Bypass -File C:\Users\ThomasFramework\worktrees\CodexBarWin\native-install\install.ps1 -DryRun`

The surrounding command sampled `Get-Process wsl, wslhost, vmmemWSL -ErrorAction SilentlyContinue` immediately before and after the dry run.

```text
WSL processes before: none
Preflight: Windows build 26300; OS x64; app 1.5.4.1; runtime Microsoft.WindowsAppRuntime.1.8 matching current-user package visible=True.
Release access: Latest release v1.5.4 is accessible in Wave-OCE/CodexBarWin.
Download / signer: Would download/verify SHA256 for CodexBarWin-v1.5.4-x64.msix using CodexBarWin-v1.5.4-checksums.sha256 (local MSIX overrides download). Signer trust: unknown until the verified package is downloaded; use its actual signer, never the release .cer alone.
Elevation: At most one UAC for the package signer and Windows App Runtime. If the runtime is absent (visible=True), first try the verified redistributable unelevated.
App: Would install/upgrade only if older than v1.5.4; current=1.5.4.1; SkipApp=False. Preseed only absent settings, then launch.
Sign-ins: Would check Windows sign-in presence for claude, codex, gemini.
Usage: Would wait up to 120 seconds for readings after app launch for claude, codex, gemini.

Setup summary:
[ok] Preflight: Windows build 26300; OS x64; app 1.5.4.1; runtime Microsoft.WindowsAppRuntime.1.8 matching current-user package visible=True.
[ok] Release access: Latest release v1.5.4 is accessible in Wave-OCE/CodexBarWin.
[ok] Download / signer: Would download/verify SHA256 for CodexBarWin-v1.5.4-x64.msix using CodexBarWin-v1.5.4-checksums.sha256 (local MSIX overrides download). Signer trust: unknown until the verified package is downloaded; use its actual signer, never the release .cer alone.
[ok] Elevation: At most one UAC for the package signer and Windows App Runtime. If the runtime is absent (visible=True), first try the verified redistributable unelevated.
[ok] App: Would install/upgrade only if older than v1.5.4; current=1.5.4.1; SkipApp=False. Preseed only absent settings, then launch.
[ok] Sign-ins: Would check Windows sign-in presence for claude, codex, gemini.
[ok] Usage: Would wait up to 120 seconds for readings after app launch for claude, codex, gemini.

What the human still needs to do:
- DryRun changed nothing. Run the same command without -DryRun to perform verified installation and determine pending sign-ins.
CODEXBARWIN_SETUP=ok
WSL processes after: none
New WSL processes: 0
Dry run exit code: 0
```

Verification used the dry-run route and isolated tests with synthetic sign-in and cache data. Real installation, app launch, and the WSL route were outside this run.
