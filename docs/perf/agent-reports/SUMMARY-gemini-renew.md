# Gemini sign-in renewal (perf/gemini-renew)

An expired agy (Antigravity) sign-in is now renewed by running `agy models`, the way Claude's is renewed with
`claude auth status --json`. The real `agy` was never run: tests use a fake renewer, a fake credential source and
cmd.exe as a stand-in process. No credential store or file was read.

## Behavior
When the stored agy token is expired (the existing `ExpirySafetyWindow` check) and `RenewGeminiSignIn` is on,
`AntigravityNativeClient` asks `IAgySignInRenewer` to run `agy models`. At most one attempt per 30 minutes; concurrent
fetches share the attempt in flight; the run is stopped (process tree) after 30 s. The credential is then read once
more, and the fetch continues with it only if its expiry moved past the old one and is in the future. Otherwise the
existing `SignInExpired` result is returned unchanged (message still says to run agy). Logs carry only "not found",
the timeout, or exit code and duration; agy output is discarded unread.

`AgyModelsRunner` finds `agy.exe` on absolute PATH entries (relative entries ignored), else
`%LOCALAPPDATA%\Microsoft\WinGet\Links\agy.exe`, and runs exactly `models`, hidden, in the temp folder, stdin closed.
When a Debug test override supplies a fake Antigravity credential file, no renewer is created, so the real agy is never
started against a fake profile.

## Files changed
- `Services/Native/AgySignInRenewal.cs` (new): `AgyModelsRun`, `IAgySignInRenewer`, `AgyModelsRunner`
- `Services/Native/AntigravityNativeClient.cs`: options `RenewExpiredSignIn`, `RenewalInterval`, `RenewalTimeout`; renewal logic
- `Services/Native/NativeUsageService.cs`: new trailing parameter `renewGeminiSignIn`, creates the renewer
- `Models/AppSettings.cs`: `RenewGeminiSignIn` (default true, no UI toggle)
- `App.xaml.cs`: passes the setting to `NativeUsageService`
- `CHANGELOG.md`: one Unreleased line

## Tests added (18, all in `CodexBarWin.Tests.Unit`)
`Services/Native/AgySignInRenewalTests.cs` (15): renewed and fetch continues with the new token; setting off; no renewer
configured; agy not found; same expiry (stays SignInExpired); expiry moved but still past; timed out; throttled within
30 minutes; two concurrent fetches share one attempt; logs only exit code and duration; default runner runs only
`models`; runner discards output and reports exit code; runner times out and kills; PATH lookup ignores relative
entries (WinGet link fallback); PATH preferred and null when missing.
`Models/RenewGeminiSignInSettingTests.cs` (3): default on, old settings file loads on, JSON round-trip.

## Results
- `dotnet build CodexBarWin.csproj -c Release -p:Platform=x64 -p:PublishReadyToRun=false`: succeeded, 0 errors (1 existing NETSDK1198 warning).
- `dotnet test CodexBarWin.Tests.Unit -c Debug -p:Platform=x64` (`CODEXBARWIN_SKIP_WSL_INTEGRATION=1`): Passed 2667, Failed 0, Skipped 10 (WSL tests), Total 2677.
- New tests alone: Passed 18, Failed 0.
