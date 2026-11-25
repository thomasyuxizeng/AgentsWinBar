# Spike: native C# usage reads for Claude, Codex and Antigravity

Date: 2026-11-25 (all times UTC). Branch: `perf/native-usage`. Status: spike only, not wired into
`Services/CodexBarService.cs`, the view models or the UI.

## Summary

- All three usage reads work in-process in C# with the Windows-side sign-ins, read-only. Verified live:
  Claude (HTTP 200), Codex (HTTP 200), Antigravity (HTTP 200 on both calls).
- One refresh of all three, in parallel, from a fresh probe process: 3.5 s wall, 0.9 s CPU, 0.9 MB allocated
  (cold); 2.6 s wall, 0.19 s CPU, 0.35 MB allocated (warm). The whole probe process peaked at 57 MB working set and
  18 MB private bytes, of which about 27 MB / 8 MB is the bare .NET runtime before any call. For comparison, the owner
  measured the current WSL route at 1.4 to 1.7 GB of RAM for about 40 s per refresh (owner's figure, not re-measured
  in this spike).
- Parity against the app's cache.json: Codex and Antigravity match within the tolerances. Claude did not match in the
  spike because the spike agent's launcher set `CLAUDE_CONFIG_DIR` to another seat's sign-in; a rerun from a normal
  environment at 07:35Z matched (see "Correction" under Parity).
- The decision the owner has to make: Antigravity access tokens last about one hour and are only renewed while agy
  runs. Without a refresh, the native Gemini row will often say "sign-in expired". An in-memory refresh path is built
  and tested with synthetic fixtures, but is off by default and was never run against a real token or endpoint.
  After the council review the owner decided to remove it (25 Nov); it is kept on branch
  `perf/antigravity-refresh-optin`.
- One budget rule was broken: the second Claude call came about 90 s after the first, not 2 minutes (details under
  Live call log). It returned 200; no 429 was seen.

## What was built

| Path | What |
|---|---|
| `Services/Native/NativeUsageResult.cs` | `NativeUsageResult`, `NativeUsageStatus` (Ok, SignInMissing, SignInExpired, Unauthorized, RateLimited, Failed), `INativeUsageClient`. Free of app model types so the probe can link it. |
| `Services/Native/NativeHttp.cs` | One shared `HttpClient`: `SocketsHttpHandler`, no redirects (a redirect could carry the bearer token elsewhere), no cookies, gzip/brotli, 90 s idle timeout, host allow list (HTTPS only, the five hosts below). |
| `Services/Native/NativeCredentialHelpers.cs` | Credential file reads with `FileShare.ReadWrite \| FileShare.Delete` into a pooled buffer that is zeroed on dispose; JWT claim reads (`exp`, ChatGPT account id) with `Utf8JsonReader`; Go RFC 3339 timestamps (9 fractional digits). |
| `Services/Native/NativeJsonContext.cs` | Source-generated (reflection-free) response models for all three providers. |
| `Services/Native/ClaudeNativeClient.cs` | Claude reader, plan label port, 429 cooldown, the shim's 10-minute last-good cache. |
| `Services/Native/CodexNativeClient.cs` | Codex reader, window normalizer and extra-limit mapper ports. |
| `Services/Native/AntigravityCredentials.cs` | Read-only Credential Manager source (`CredReadW` and `CredFree` only) and the blob parser. |
| `Services/Native/AntigravityNativeClient.cs` | Antigravity reader plus the shim's Gemini reshaping. |
| `Services/Native/AntigravityOAuthClientDiscovery.cs` | Finds the OAuth client inside the installed Windows binaries (for the opt-in refresh). |
| `Services/Native/AntigravityTokenRefresher.cs` | Opt-in, in-memory token refresh. Off by default. |
| `Services/Native/NativeUsageDtoMapper.cs` | Maps a result to the existing `UsageDataDto`, with the shim's meaning of `cached`, `authRequired`/`authError` and the CLI error object. App and tests only (it needs the app's model graph). |
| `CodexBarWin.Tests.Unit/Services/Native/*Tests.cs` | 7 test classes, synthetic tokens and responses only. |
| `tools/NativeProbe/` | Console probe (net10.0-windows, Release). Not in the solution; `CodexBarWin.csproj` now removes `tools\**` from Compile, Content, EmbeddedResource and None. |

Every client takes `ILogger<T>`, a `TimeProvider`, a cancellation token and per-request timeouts, resolves its
provider id through `ProviderConstants.ValidateAndNormalize`, and never throws for provider, network or sign-in
failures. No token value is logged, returned or put in a message (tests assert this for the messages).

## Per provider

### Claude

- Endpoint: `GET https://api.anthropic.com/api/oauth/usage` (upstream `ClaudeOAuthUsageFetcher`). Upstream adds
  `?cedar_ember=1` (reset-credit inventory) and retries without it on 400/403; the app does not use reset credits,
  so the native client calls the plain path once.
- Headers: `Authorization: Bearer <token>`, `Accept: application/json`, `anthropic-beta: oauth-2025-04-20`,
  `User-Agent: claude-code/2.1.0`. Upstream detects the version by running `claude --version`; the native client
  never runs it and sends upstream's fallback version. Accepted live today.
- Credential source: `%USERPROFILE%\.claude\.credentials.json` (or `%CLAUDE_CONFIG_DIR%\.credentials.json`). Only
  the `claudeAiOauth` object is read (`accessToken`, `expiresAt` in ms, `subscriptionType`, `rateLimitTier`); every
  other key, including the other tokens in the 36 KB file, is skipped by the reader without being materialized.
- Mapping (upstream `mapOAuthUsage`): primary = `five_hour` (300 min), secondary = `seven_day` or
  `seven_day_oauth_apps` (10080 min), tertiary = `seven_day_sonnet` or `seven_day_opus`, extra windows = scoped weekly
  `limits` ("Fable only" style, id `claude-weekly-scoped-<slug>`) then Daily Routines. Plan label ported from
  `ClaudePlan` ("Claude Team", "Claude Max 20x").
- Expired token: `SignInExpired` with how long ago ("expired 42 minutes ago"), no call, no refresh.
- 429: the client blocks itself until the later of now + 10 minutes (the shim's cooldown; upstream uses 5) and
  `Retry-After`, and serves the last good reading marked cached, as the shim does. A last good reading younger than
  10 minutes is served without calling unless one of its windows has reset since (the shim's cache rule).
- Live today: the token was valid for about 350 more minutes at 06:59.

### Codex

- Endpoint: `GET https://chatgpt.com/backend-api/wham/usage` (upstream `CodexOAuthUsageFetcher.fetchUsage`).
- Headers: `Authorization: Bearer <token>`, `Accept: application/json`, `User-Agent: CodexBar` (upstream's value),
  `ChatGPT-Account-Id: <account id>` when known.
- Credential source: `%USERPROFILE%\.codex\auth.json` (or `%CODEX_HOME%\auth.json`). Reads `tokens.access_token`,
  `tokens.account_id` (falling back to the id/access token's ChatGPT account claim), `last_refresh`. The refresh
  token and API key are never materialized (an API-key-only file is reported as "needs a ChatGPT sign-in").
- Expiry: the access token's JWT `exp` claim, as upstream does for `~/.codex`. Live today: valid for about 7.8 more
  days at 06:59 (`last_refresh` 2.2 days earlier).
- Mapping: windows by length (300 = session, 10080 = weekly, upstream `CodexRateWindowNormalizer`);
  `additional_rate_limits` as extra windows (upstream `CodexAdditionalRateLimitMapper`, stable Spark ids). Plan is the
  raw `plan_type` ("team"), because that is what the CLI route the app uses today reports (cache.json shows "team").
- Expired token: `SignInExpired`, no call, no refresh. 401/403: `Unauthorized`; 429: `RateLimited`.

### Antigravity (shown as Gemini)

- Endpoints (upstream `AntigravityRemoteUsageFetcher`), all `POST` with a JSON body:
  1. `https://cloudcode-pa.googleapis.com/v1internal:loadCodeAssist`, body
     `{"metadata":{"ideType":"ANTIGRAVITY","platform":"PLATFORM_UNSPECIFIED","pluginType":"GEMINI"}}`, for the plan
     and the Cloud Code project.
  2. `https://daily-cloudcode-pa.googleapis.com/v1internal:retrieveUserQuotaSummary`, body `{"project":"<id>"}`.
  3. Fallback only: `https://cloudcode-pa.googleapis.com/v1internal:fetchAvailableModels`.
- Headers: `Authorization: Bearer <token>`, `Content-Type: application/json`,
  `User-Agent: antigravity/hub/2.9.1 linux/amd64`. Upstream pins the "hub" client family because Cloud Code only
  serves quota to it; the Linux variant is what the working WSL route sends. Accepted live from Windows today.
- Credential source: Windows Credential Manager, generic credential `gemini:antigravity`, read with `CredReadW`.
  The blob (602 bytes, written by agy) is a Go `oauth2.Token` under `token` (`access_token`, `token_type`,
  `refresh_token`, RFC 3339 `expiry`, `expires_in`) plus `auth_method` ("consumer"), `id_token_email` and
  `writer_id`. This differs from upstream's macOS file (`~/.codexbar/antigravity/oauth_creds.json`: flat keys,
  `expiry_date` in ms, `project_id`, client fields); the parser accepts both. The refresh token is only read when the
  refresh flag is on.
- Expired token (or within upstream's 60 s safety window): `SignInExpired` with how long ago, no call.
- Reshaping (the shim's Python, ported): primary = bucket `gemini-5h`, secondary = bucket `gemini-weekly` (both with
  "Resets in 3h 16m" style descriptions); extra windows = the non-Gemini buckets, with `3p-5h` and `3p-weekly`
  titled "Claude and GPT 5-hour" and "Claude and GPT weekly" (the app shortens them to "External 5h/7d"). When a
  Gemini bucket is missing, upstream's representative window is kept, as in the shim.
- Differences from upstream, on purpose: never calls `onboardUser` (it changes the account); never persists
  credentials; no project id is ever written back. Upstream gives the quota summary 2 s; the native client allows
  5 s, because the live summary call took roughly 2 s when its connection was new (see Measurements).
- Plan label: upstream `resolvePlan`. Without an `id_token` in the Windows blob, "Free" vs "Workspace" is inferred
  from the stored email's domain instead of the `hd` claim (approximation).

### Antigravity refresh design (built, off by default, never run for real)

`AntigravityNativeOptions.AllowInMemoryRefresh` (default `false`). When on and the token is expired:

1. Find the OAuth client: `AntigravityOAuthClientDiscovery` scans the installed binaries (WinGet `agy.exe`, then the
   IDE's `resources\bin\language_server.exe`) without running them. It is a port of upstream's amd64 Go record
   matcher (`mov [rax+8], len; lea rcx, [rip+id]; ...; lea rcx, [rip+secret]`), with RIP-relative addresses mapped
   through the PE section table instead of the Mach-O `__TEXT` segment. It prefers upstream's public consumer client
   id when several clients are present. It reads 1 MB chunks with `RandomAccess` (no whole-file load, no unsafe code).
2. `POST https://oauth2.googleapis.com/token` with `client_id`, `client_secret`, `refresh_token`,
   `grant_type=refresh_token`.
3. Keep the new access token in memory only, reuse it until 60 s before its expiry, never write Credential Manager,
   and keep agy's refresh token unchanged.

Verified with tools: discovery against the real installed binaries (probe `discover` mode, no network, only booleans
printed): all three files (`agy.exe` from WinGet Packages and Links, `language_server.exe`) yield the consumer client
with a well-formed secret, in 40 to 670 ms, 2 MB allocated at most. Unit tests cover discovery on a synthetic PE
image, the refresh request shape, refusal, and in-memory reuse.

Unverified (the refresh was never executed against Google): whether Google rotates or revokes agy's refresh token
when a second process refreshes it (for installed-app clients it normally returns no new refresh token, so agy's
stored one should stay valid, but this was not tested); whether Google treats this as client impersonation for terms
of service purposes; whether a future Go compiler changes the instruction pattern (then discovery returns nothing and
the client reports "sign-in expired" as it does with the flag off). ARM64 binaries are not handled (x64 only).

## Parity against cache.json

cache.json has no account identity fields (only `plan`), and the CLI readings come from the WSL sign-ins. Account
identity was therefore judged from per-account reset timestamps (to the second) plus the plan label: identical reset
times on independent windows mean the same account. Values below are usage numbers only.

Cache readings were 2.4 to 3.1 minutes old at comparison time (`fetchedAt`), taken while quota was in use.

| Provider | Account | Window | Native (07:25:25) | cache.json | Result |
|---|---|---|---|---|---|
| Codex | match (resets equal, plan "team") | session | 11.0%, resets 12:09:37 | 10%, resets 12:09:38 | OK (1 pt, 1 s) |
| | | weekly | 88.0%, resets 2026-11-28 19:46:27 | 88%, same | OK |
| | | labels | Session, Weekly, plan "team", no extra windows | same | OK |
| Gemini | match (four resets equal to the second) | 5-hour | 29.9%, resets 10:42:02 | 30.8%, same | OK (0.9 pt) |
| | | weekly | 7.5%, resets 2026-12-02 05:42:02 | 8.0%, same | OK (0.5 pt) |
| | | Claude and GPT 5-hour | 0.0%, resets 12:25:28 | 0%, resets 12:24:43 | OK (45 s; an unused window's reset floats at now + 5 h) |
| | | Claude and GPT weekly | 51.2%, resets 2026-11-26 12:28:51 | 51.2%, same | OK |
| | | labels | "Claude and GPT 5-hour/weekly" | "External 5h/7d" (same titles after `WindowTitles.Short`) | OK |
| | | plan | "Google AI Pro" | none | Differs: the WSL route reads agy's CLI report (source "cli"), which carries no plan |
| Claude | mismatch | session | 59%, resets 11:00:00 | 16%, resets 09:30:00 | Not comparable (different account) |
| | | weekly | 34%, resets 2026-11-30 13:00:00 | 68%, resets 2026-11-26 12:00:00 | Not comparable |
| | | labels | Session, Weekly, plan "Claude Team" | Session, Weekly, "Fable" extra, plan "Claude Team" | Window kinds match; the Windows account returned no scoped weekly limit |

**Correction (added 25 Nov, after the spike).** The Claude mismatch above is not a different Windows account. The
spike agent ran under a launcher that sets `CLAUDE_CONFIG_DIR` to another Claude seat, and `ClaudeNativeClient`
honored it, so the probe read that seat's sign-in. Neither `CLAUDE_CONFIG_DIR` nor `CODEX_HOME` is set at user or
machine level on this laptop. A rerun from a normal environment (NativeProbe `single`, 07:35:07Z, one Claude call,
HTTP 200, 953 ms wall, 422 ms CPU cold) read the default profile and matched the app's reading:

| Window | Native (07:35) | cache.json | Result |
|---|---|---|---|
| session | 18% | 16% (reading 11 minutes older, during active use) | OK within the gap |
| weekly | 68% | 68% | OK |
| Fable-only weekly (scoped) | 8% | 8% | OK: the scoped window, verified live |
| resets | equal | equal | OK |

This is one pair, taken 11 minutes apart. The owner decided (25 Nov) that the native clients ignore
`CLAUDE_CONFIG_DIR` and `CODEX_HOME` unless an app setting names another path; that change is on branch
`perf/native-fixes-usage`.

An earlier single-provider run (07:24) compared against a 4.7-minute-old cache gave Codex session 10% vs 6%; the
fresher cache above agrees. The `source` label also differs (native "oauth"/"remote" vs CLI "oauth"/"codex-cli"/
"cli"); the app only shows it as text on the detail page.

## Measurements

Probe: `tools/NativeProbe`, Release, .NET 10.0.12, workstation GC, x64, this laptop. Each run is a fresh process.
CPU is `Process.TotalProcessorTime` (Windows updates it in 15.6 ms steps, so small values are coarse); allocation is
`GC.GetTotalAllocatedBytes(true)`; memory is the whole probe process after the calls. Baseline before any call:
26.4 to 26.6 MB working set, 7.7 to 7.9 MB private bytes.

| Measurement | Requests | Wall | CPU | Allocated | Working set after | Private bytes after |
|---|---|---|---|---|---|---|
| Claude, cold | 1 | 2206 ms | 391 ms | 438 KB | 45.6 MB | 13.1 MB |
| Codex, cold | 1 | 1088 ms | 562 ms | 413 KB | 46.4 MB | 12.8 MB |
| Antigravity, cold | 2 | 1845 ms | 516 ms | 498 KB | 46.5 MB | 12.5 MB |
| Antigravity, warm (1 s later, connection reused) | 2 | 1103 ms | 31 ms | 29 KB | 48.3 MB | 13.5 MB |
| Refresh, 3 in parallel, cold | 4 | 3519 ms | 906 ms | 900 KB | 53.2 MB | 16.3 MB |
| Refresh, 3 in parallel, warm (130 s later) | 4 | 2606 ms | 188 ms | 353 KB | 57.1 MB | 17.6 MB |

In the parallel runs each provider's own wall time was: Claude 1827 / 515 ms, Codex 829 / 495 ms, Antigravity
3454 / 2606 ms (cold / warm). Antigravity dominates because its two calls are sequential and the quota summary on
`daily-cloudcode-pa` is slow: the two calls took 1.1 s on reused connections and 2.6 s on new ones, and the
request log order suggests the summary alone took roughly 2 s in the latter case, right at upstream's 2 s timeout
(inferred, not timed separately). The warm refresh came 130 s later, after the 90 s idle timeout had
closed the connections, so it includes fresh TLS handshakes, like the app's 5-minute refresh would.

Not measured, within the call budgets: a warm single-provider call for Claude and Codex (that would have needed a
fourth call each). Instead, the probe's `replay` mode measured the in-process cost with synthetic responses and
sign-ins and no network: after the first call, each read takes 1 to 2 ms and 5 to 25 KB (Claude 7 to 25 KB, Codex
5 KB, Antigravity 17 KB), and a parallel refresh of all three 1 to 4 ms and 28 to 51 KB; first calls cost 42 to
140 ms of JIT. In the app, the runtime, JIT and probably the TLS stack are already loaded (the app already uses
`HttpClient`), so the app's increase should be smaller than the probe's process figures above. That is an
inference, not measured.

## Credential Manager hash check

| When | SHA-256 prefix | Note |
|---|---|---|
| Before my first read (about 06:59) | `8adf2317` | Token had expired 6 minutes earlier |
| After the single-provider probe run (checked between 07:24:20 and 07:25:21) | `acb832bd` | Last written 07:14:46 by agy |
| After my last read (07:28:07) | `acb832bd` | Last written still 07:14:46 |

Before and after do not match: the owner's `agy.exe` (WinGet Links path, running at the time) rewrote the
credential at 07:14:46, between my first shape read and the first probe run, which is how its token became valid
again. Its last-written time (07:14:46) precedes the first probe read (07:24:16) and was unchanged at 07:28:07, so
the blob did not change across any probe read. The native code imports only `CredReadW` and `CredFree`
(checked with grep on `Services/Native`).

## Live call log

All calls went only to the five allowed hosts (enforced by the allow-list handler; the last run also had hard
per-host caps).

| Time | Run | Claude | Codex | Antigravity |
|---|---|---|---|---|
| 07:23:52 | single claude | 1 (200) | | |
| 07:24:05 | single codex | | 1 (200) | |
| 07:24:16 | single gemini, cold + warm | | | 4 (200, 200, 200, 200) |
| 07:25:22 | all, cold | 1 (200) | 1 (200) | 2 (200, 200) |
| about 07:27:35 | all, warm | 1 (200) | 1 (200) | 2 (200, 200) |
| Total | | 3 of 3 | 3 of 3 | 8 of 8 |
| 07:35:07 | parity rerun by the reviewing session, after the spike (not part of the spike's budget) | 1 (200) | | |

Budget breach: Claude calls 1 and 2 were about 90 s apart (07:23:52 and 07:25:22), not the required 2 minutes. I
misjudged the elapsed time instead of checking the clock before starting the parallel run. Calls 2 and 3 were 133 s
apart. No 429 occurred. Whether this added pressure on the running app's own Claude calls is not known.

## Tests

`dotnet test CodexBarWin.Tests.Unit -c Debug -p:Platform=x64`: 2092 passed, 0 failed, 0 skipped (2092 total,
including the new native tests). App build (`dotnet build CodexBarWin.csproj -c Release -p:Platform=x64
-p:PublishReadyToRun=false`): succeeded. New tests cover response parsing with recorded shapes (synthetic values),
expired tokens (no call made), missing sign-ins, API-key-only Codex, 401/429/5xx, the 429 cooldown and
Retry-After, the 10-minute cache and its reset rule, Antigravity reshaping to the shim's output, the consumer-tier
rejection, no onboarding, the fetchAvailableModels fallback, plan labels, the in-memory refresh (synthetic client and
host), the credential parsers, PE discovery on a synthetic image, the host allow list, and the DTO mapping through
the app's existing `UsageDataDto.ToUsageData`.

## Risks and unknowns

- Undocumented endpoints. None of the five are public APIs: Anthropic's OAuth usage endpoint (beta header), ChatGPT's
  `wham/usage`, and Cloud Code's `v1internal` methods can change shape or close without notice. Upstream CodexBar
  tracks them, so its changes are the early warning.
- Client pinning. Claude gets `claude-code/2.1.0` (a fixed version where upstream detects the real one), Codex gets
  `CodexBar`, Antigravity gets `antigravity/hub/2.9.1 linux/amd64` from a Windows process. All accepted today;
  any of them could be rejected later.
- Rate limits. Anthropic rate limits the Claude usage endpoint (that is why the shim exists). Today 3 calls in about
  4 minutes got no 429, which says little about the 5-minute steady state. In normal use the native reader and the
  running app read the same account (see the Correction under Parity), so two pollers would share whatever limit
  applies; the rollout uses one app reading both sources instead. Codex and Cloud Code limits were not hit and are
  unknown.
- Token lifetimes without refresh: Claude at least 6 hours (350 minutes were left at 06:59; issue time unknown),
  Codex about 10 days (inferred from `exp` and `last_refresh`), Antigravity about 1 hour (the token agy wrote at
  07:14:46 had about 50 minutes left at 07:25). With refresh off,
  each provider shows "sign-in expired" until its own app next runs on Windows: rarely for Codex, sometimes for
  Claude, often for Antigravity (it had expired at 06:59 today).
- (Corrected 25 Nov.) The spike reported the Windows Claude sign-in as a different account; it was the other seat
  selected by `CLAUDE_CONFIG_DIR`. The default Windows profile is the account the app shows today.
- The Claude scoped weekly window ("Fable only") was verified live in the 07:35Z rerun (8% vs 8%). Daily Routines
  were verified with synthetic data only. Codex extra limits (Spark) likewise: none were returned live.
- Antigravity fallback (`fetchAvailableModels`) is simplified (most used Gemini model as primary, most used
  Claude/GPT model as secondary) and refuses the all-100% placeholder instead of verifying it with
  `retrieveUserQuota`. Not exercised live.
- An Antigravity account without a Cloud Code project is never onboarded; it falls back to a project-less call, whose
  result is unverified. Today's account had a project (inferred: the "no Cloud Code project" log line never appeared).
- Not ported: Codex `chatgpt_base_url` from `config.toml`; Claude extra-usage cost (`extra_usage`) and reset
  credits; Antigravity buckets that are disabled or have no fraction are dropped (the shim would show them as 0%).
- Plan and source labels differ as noted under Parity (Gemini plan now shown; source text on the detail page).
- `AntigravityNativeClient` keeps its refreshed token in a plain field; concurrent fetches on one instance are not
  expected (one refresh at a time) but are not guarded.
- Measurements are from one laptop on one morning; network latency dominated wall time.

## Integration notes (for later, not done)

Construct one `HttpClient` with `NativeHttp.CreateClient()` and the three clients as singletons (the Claude client
holds the cooldown and cache state), call them in parallel per refresh, and map each result with
`NativeUsageDtoMapper.ToDto(result).ToUsageData(now)`. The sign-in hints in `AuthFailureDetector.GetSignInHint` still
point at WSL and would need Windows wording.
