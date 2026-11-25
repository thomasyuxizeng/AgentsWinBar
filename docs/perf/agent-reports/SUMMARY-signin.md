# Sign in button for signed-out providers

Branch `perf/signin`, uncommitted (no commits, pushes or branch switches).

## Result

- Release build (`dotnet build CodexBarWin.csproj -c Release -p:Platform=x64 -p:PublishReadyToRun=false -nodeReuse:false`):
  **succeeded, 0 errors, 1 warning** (NETSDK1198 "publish profile win-x64.pubxml not found". It was there before this change.)
- Full unit suite (`CODEXBARWIN_SKIP_WSL_INTEGRATION=1`, `dotnet test CodexBarWin.Tests.Unit -c Debug -p:Platform=x64 -nodeReuse:false`):
  **Total 2488: 2478 passed, 6 failed, 4 skipped.**
  - The 4 skipped are the WSL integration tests the environment variable turns off.
  - The 6 failures are all `WslBatchProtocolTests.BuildScript_RealBash*`. With WSL skipped they run the WSL batch
    script under Git Bash, which fails here (`"/0.out": No such file or directory`, `syntax error: operand expected`).
    They are **not caused by this change**: neither the file nor `WslBatchProtocol.cs` is touched, and on an untouched copy of
    HEAD (`git archive HEAD`, built and run separately, then deleted) the same 6 fail the same way. They can be marked
    inconclusive with `CODEXBARWIN_SKIP_BASH_INTEGRATION=1`, or they pass where WSL runs them.
- New tests: **63, all passing** (counted with a filtered run).

## What changed

### New files
| File | What it is |
|---|---|
| `Services/ISignInLauncher.cs` | `ISignInLauncher.Launch(provider)` returns `SignInLaunchResult(Route, Description)`. `SignInRoute` = None, WindowsTerminal, CommandPrompt, DesktopApp, Browser. `ISignInSystem` (PATH lookup, file exists, process start, folders) is injectable. `SignInProcess` (file, fixed arguments, working folder). |
| `Services/SignInLauncher.cs` | `SignInLauncher` (route choice and fallback) and `WindowsSignInSystem` (PATH plus PATHEXT lookup, `Process.Start` with shell execute). |
| `Services/SignInWatcher.cs` | `ISignInWatcher` / `SignInWatcher` (picks up the new sign-in), `ISignInSignals` / `WindowsSignInSignals` (file timestamps, FileSystemWatcher, Credential Manager LastWritten via `CredReadW`, timers). |
| `Helpers/SignInStatus.cs` | `SignInState` (Unknown, SignedIn, SignedOut, Expired), plus the state, button rule and texts for Settings. |
| `Views/SignInAction.cs` | What every Sign in button does: `watcher.Begin(provider)`, then `launcher.Launch(provider)`, cancelling the watch if nothing could start. Errors are logged. |
| Tests: `Services/SignInLauncherTests.cs`, `Services/SignInWatcherTests.cs`, `Helpers/SignInStatusTests.cs` | See "Tests added". |

### Changed files
| File | Change |
|---|---|
| `Controls/DashboardProviderPanel.xaml` (+ `.xaml.cs`) | Signed-out InfoBar gets `InfoBar.ActionButton` = **"Sign in"** button (automation name "Sign in to <Provider>"), click handler `OnSignInClicked` wrapped in try-catch. |
| `Views/ProviderDetailPage.xaml` (+ `.xaml.cs`) | `SignedOutBanner` gets the same "Sign in" action button (`SignInButton`, automation name set per provider), handler `OnSignInClicked`. |
| `Views/SettingsPage.xaml` (+ `.xaml.cs`) | New **"Sign-ins"** section (see below), `SignInRow` row type, live update from `MainViewModel.Providers`. |
| `Views/DashboardFormat.cs` | `SignInButtonName(provider)` for the button's automation name. |
| `Helpers/AuthFailureDetector.cs` | `GetSignInHint` now returns the native hints (below). It feeds both banners and the dashboard's attention list. |
| `Models/UsageData.cs` | New session-only `SignInExpired` (`[JsonIgnore]`, never written to the cache file). |
| `Models/AppJsonSerializerContext.cs` | `UsageDataDto.SignInExpired` (`"signInExpired"` in JSON), mapped to `UsageData.SignInExpired` unless the provider is signed out. |
| `Services/Native/NativeUsageDtoMapper.cs` | Sets `SignInExpired` for `NativeUsageStatus.SignInExpired`. It still never sets `AuthRequired` for an expired sign-in. |
| `Services/CacheService.cs` | Strips `SignInExpired` from in-memory cache entries, like `AuthRequired`. |
| `ViewModels/MainViewModel.cs` | Optional `ISignInWatcher` constructor parameter. On a refresh request it refreshes now, or queues a refresh after the running one. A Gemini re-check is skipped and the watch cancelled once the provider is no longer signed out. Any reading without a sign-in problem cancels that provider's watch. New `IsSignedOut(provider)`. |
| `App.xaml.cs` | DI: `ISignInSystem`, `ISignInLauncher`, `ISignInSignals`, `ISignInWatcher` singletons. |
| `Samples/README.md` | How to show signed-out and expired providers in Developer mode. |
| `CodexBarWin.Tests.Unit/CodexBarWin.Tests.Unit.csproj` | Links the four new source files. |
| Tests changed: `AuthFailureDetectorTests` (new hint texts), `NativeUsageDtoMapperTests`, `CodexBarServiceTests`, `MainViewModelTests` (added tests) | |

### Texts
- Banner hints (dashboard and provider page), replacing the WSL-era ones:
  - Claude: "Sign in to Claude Code to see your Claude limits."
  - Codex: "Sign in to Codex to see your Codex limits."
  - Gemini: "Sign in to Antigravity with Google to see your Gemini limits."
- Button: "Sign in" (accessible name "Sign in to Claude" / "Codex" / "Gemini").
- Settings: section header "Sign-ins". Card caption "Each provider's own app keeps its sign-in; this app only reads it".
  States: "Signed in", "Signed out", "Expired, waiting for Claude Code / Codex / Antigravity to renew it", and
  "Not checked yet" (no reading yet this session, or only a non-sign-in error). The "Sign in" button appears only for "Signed out".
- Gemini terminal first line: "Sign in with Google when Antigravity asks, then close this window."

### Only for missing or rejected sign-ins
The banners (and so the buttons) only exist when `AuthRequired` is true, which comes only from a missing or rejected sign-in.
An expired sign-in (`NativeUsageStatus.SignInExpired`) keeps `AuthRequired = false`: no banner, no button. Settings shows
it as "Expired, waiting for <tool> to renew it" with no button (`SignInStatus.CanSignIn` is true only for SignedOut).

## Launch logic per provider
The provider is validated with `ProviderConstants.ValidateAndNormalize()`. Routes are tried in order, and if starting one
throws, the next is tried. Every command line and URL is a constant; the only variable parts are system paths
(`System32\cmd.exe`, `wt.exe` from PATH, `%LOCALAPPDATA%`), never file or network data. The terminal's working folder is `%USERPROFILE%`.

| Provider | 1st | 2nd | 3rd |
|---|---|---|---|
| Claude | `claude` on PATH: terminal running `claude auth login` | `https://code.claude.com/docs/en/setup` in the browser | |
| Codex | `codex` on PATH: terminal running `codex login` | `https://developers.openai.com/codex/cli/` in the browser | |
| Gemini | `%LOCALAPPDATA%\Programs\antigravity\Antigravity.exe` exists: the Antigravity app | `agy` on PATH: terminal running `echo Sign in with Google when Antigravity asks, then close this window. & agy` | `https://antigravity.google` in the browser |

The terminal is Windows Terminal when `wt` is on PATH:
`wt.exe -w new new-tab --title "<Provider> sign-in" "C:\Windows\System32\cmd.exe" /k <command>`.
Otherwise it is `cmd.exe /k title <Provider> sign-in& <command>`, and if Windows Terminal fails to start, Command Prompt is tried next.

URLs, all confirmed in the tools' own local files:
- Claude: the installed Claude Code binary says "see code.claude.com/docs/en/setup".
- Codex: the bundled `codex.exe` contains `https://developers.openai.com/codex/cli/`. Its README also links `https://developers.openai.com/codex`.
- Antigravity: the app bundle (`resources\app.asar`) contains `https://antigravity.google`, the product's main page. No more specific sign-in page was found.

## Picking up the new sign-in
`SignInWatcher` runs only between a click and its end. With nothing pending there is no timer and no watcher.
- **Claude, Codex:** a FileSystemWatcher on `%USERPROFILE%\.claude\.credentials.json` or `%USERPROFILE%\.codex\auth.json`.
  The first change asks for **one** refresh 2 s later, so the tool has finished writing, and then watching stops. A 10 s
  check of the file's last-write time catches the case where the folder did not exist yet. The watch gives up after 15 minutes.
- **Gemini:** a 10 s check of the `gemini:antigravity` Credential Manager entry's LastWritten time. Only that metadata
  is used; the secret is never copied or read by this code. When it changes, one refresh. Because the Antigravity desktop
  app might keep its sign-in elsewhere, there is also one refresh a minute while Gemini still shows signed out, for 5 minutes (at most 4).
- The refresh is the normal all-provider refresh, without forcing a cost scan, since the app has no single-provider refresh.
  On the native route that is one local check per provider. If a refresh is running, one is queued after it. The first
  reading of the provider without a sign-in problem ends its watch.
- Nothing reads the credential files: only existence and last-write time are used.

## Settings "Sign-ins" section
It sits between the Providers group (after "Credit renewal day") and "Behavior", and copies the Providers card: section header
`BodyStrongTextBlockStyle` with margin 4,20,0,8; card with padding 16, `CardBackgroundFillColorDefaultBrush`, a stroke border and corner radius 8;
a caption at the top; one row per enabled provider (saved settings, display order) like the provider rows (`SubtleFillColorSecondaryBrush`,
padding 12,10, corner 6). Each row has the 16 px provider icon, the name (`BodyTextBlockStyle`), and the state (`CaptionTextBlockStyle`, secondary,
wrapping). Because of the narrow (~350 DIP) window, the "Sign in" button always sits **under** the text, styled like
"Send test notification" (margin 0,8,0,0, padding 10,4). States update while the window is open.

## Developer mode
The sample JSON already parsed `"authRequired"`; `"signInExpired"` is new. To show the button for a README screenshot,
add `"authRequired": true` next to `"provider"` in a sample such as `Samples/codex.json` (the copy in the build output's
`Samples` folder, or the source before building), turn on Developer mode, and refresh. The provider shows the Signed
out banner with "Sign in", dimmed last-known numbers, and "Signed out" with a button in Settings. `"signInExpired": true` shows the
expired state. This is documented in `Samples/README.md`. The default samples are unchanged.

## Tests added (63)
- `SignInLauncherTests`:
  - Claude and Codex with the tool on PATH: Windows Terminal, and Command Prompt when there is no `wt`.
  - Tool not on PATH: browser, even when `wt` exists.
  - Gemini with the app installed (with or without `agy`): the app. Only `agy`: Windows Terminal or Command Prompt with the sign-in line. Neither: browser.
  - Fallbacks when a start fails (wt to cmd; app to agy to browser), and None when everything fails.
  - An invalid provider (`openai`, `claude & calc`, empty) throws and starts nothing.
  - Command lines contain no `;`, `|`, `<`, `>` or `%`.
- `SignInWatcherTests`:
  - Nothing runs with no sign-in pending. The right file is watched for Claude and Codex.
  - A file change gives one refresh after the settle delay, then everything stops.
  - A file that appears without a folder watcher is caught by polling. An unchanged timestamp gives no refresh.
  - The 15 minute give-up. Gemini uses the Credential Manager target. A rewritten Gemini entry gives one refresh.
  - The Gemini re-check is once a minute for 5 minutes (4 requests), then stops.
  - Cancel; cancelling one of two pending sign-ins; an unknown provider is ignored.
  - A restart ignores a late settle callback and keeps one timer. An invalid provider throws. Dispose stops everything.
- `SignInStatusTests`: state mapping (none, reading, signed out, expired, non-auth error), button only for signed out, texts and tool names.
- `NativeUsageDtoMapperTests`: expired (with or without a reading) sets `SignInExpired` but not `AuthRequired`; other statuses do not set it; signed out wins over expired.
- `CodexBarServiceTests`: a Developer mode sample with `authRequired` shows signed out with its numbers; `signInExpired` shows expired.
- `MainViewModelTests`: a credential-change request refreshes and ends the watch once signed in; a re-check when already signed in cancels without refreshing.
- Updated: `AuthFailureDetectorTests.GetSignInHint_*` checks the new texts.

## Not verified
- **The UI was not run or seen.** The app was not launched, because that would read the owner's settings, cache and
  sign-ins, and no sign-in tool was started. So these are unchecked:
  - The InfoBar layout. From the WinUI 1.8 template metrics, the action button sits beside the title, 8 px from the top. A default 32 px
    button would have pushed the title row down and run into the hint's `-8` top margin. So the button uses InfoBar's compact
    action size (MinHeight 24, padding 8,0,8,1, keeping the title row's height), and the hint's top margin in these two
    signed-out banners went from -8 to 0. **The hint therefore sits about 8 px lower than before.** That is the only
    change to the existing banners besides the button and text. It should be checked in a screenshot, and if the gap looks
    too large, try -2 or -4.
  - The Settings section's look at 150%.
- The real routes were not run: Windows Terminal or cmd actually opening `claude auth login`, `codex login` or `agy`; the
  Antigravity app starting; the browser opening. Only the route choice and command lines are tested, with fakes. What was checked
  on this machine without running anything: `claude`, `codex`, `agy` and `wt.exe` are on PATH (`File.Exists` sees the `wt.exe` app
  alias), and `Antigravity.exe` is at the expected path. So on this machine Claude and Codex would open in Windows Terminal, and Gemini would open the Antigravity app.
- Whether the Antigravity desktop app writes the same `gemini:antigravity` entry as `agy` is unknown. The once-a-minute
  re-check covers it for 5 minutes either way.
- The real FileSystemWatcher and `CredReadW` LastWritten read were not exercised by tests (fakes only). The struct maps
  `CREDENTIALW` only up to `LastWritten`.
- The Gemini terminal fallback assumes `agy`'s first interactive run asks for a Google sign-in, as the task states. It was not run.
