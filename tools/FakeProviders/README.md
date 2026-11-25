# FakeProviders

A local stand-in for the three usage endpoints the native readers call, so a **Debug** build of CodexBarWin can be
tried against 401s, 429s, slow or broken answers without a real sign-in and without touching the real providers.

It listens on `http://127.0.0.1:<port>/` only (no admin rights needed) and serves:

| Provider | Route |
| --- | --- |
| Claude | `GET /api/oauth/usage` |
| Codex | `GET /backend-api/wham/usage` |
| Antigravity (the app's Gemini) | `POST /v1internal:loadCodeAssist`, `POST /v1internal:retrieveUserQuotaSummary`, `POST /v1internal:fetchAvailableModels` |

It never prints request headers (the app sends its fake bearer token in `Authorization`).

## Scenarios

`normal` (200 with plausible numbers: Claude 37 %, Codex 41 %, Gemini 80 %), `401`, `403`, `429` (no Retry-After),
`429-seconds` (Retry-After: 120), `429-date` (an HTTP date 10 minutes ahead), `429-absurd` (Retry-After: 999999999999,
which the app must cut to 24 h), `500`, `slow` (answers after 30 s; the app gives up first), `malformed` (cut-off JSON),
`empty` (200 with no body), `huge` (8 MiB, streamed; the app stops reading at 2 MiB).

## Run it

From the repository root:

```powershell
dotnet build tools\FakeProviders -c Debug -nodeReuse:false
.\tools\FakeProviders\bin\Debug\net10.0\FakeProviders.exe --port 18080 --scenario normal --exit-after 3600
```

Options: `--port` (default 18080), `--scenario` (all providers), `--claude` / `--codex` / `--antigravity` (one provider),
`--exit-after <seconds>` (stops by itself; always pass it for unattended runs).

While it runs, from another terminal:

```powershell
curl.exe "http://127.0.0.1:18080/__scenario"                                   # show
curl.exe "http://127.0.0.1:18080/__scenario?name=429-seconds&provider=claude"  # one provider
curl.exe "http://127.0.0.1:18080/__scenario?name=500"                          # every provider
curl.exe "http://127.0.0.1:18080/__stop"                                       # stop the server
```

## Point a Debug build of the app at it

The hooks exist only in Debug builds (`#if DEBUG`). A Release build has no code that reads these variables and its HTTP
allow list has no loopback exception, so setting them cannot change the installed app.

| Variable | Meaning |
| --- | --- |
| `CODEXBARWIN_TEST_PROFILE_ROOT` | Stands in for `%USERPROFILE%`: `.claude\.credentials.json`, `.codex\auth.json`, the cost logs (`.claude\projects`, `.codex\sessions`, `.codex\archived_sessions`) and `.claude\gemini-api\spend.jsonl` are read under it |
| `CODEXBARWIN_TEST_APPDATA` | Stands in for `%LOCALAPPDATA%\CodexBarWin`: settings, caches, cost index and prices, `claude-cooldown.json`, `crash.log` |
| `CODEXBARWIN_TEST_ANTIGRAVITY_CREDENTIAL_FILE` | A file with a fake Antigravity credential, read instead of Windows Credential Manager |
| `CODEXBARWIN_TEST_CLAUDE_BASE_URL`, `..._CODEX_BASE_URL`, `..._ANTIGRAVITY_BASE_URL` | `http://127.0.0.1:<port>` and nothing else |

Rules (the app refuses to start in Debug when one is broken, rather than fall back to the real profile or hosts):
paths must be absolute; a base URL must be exactly `http://127.0.0.1:<port>`; the Claude and Codex base URLs need
`PROFILE_ROOT`, and the Antigravity base URL needs `ANTIGRAVITY_CREDENTIAL_FILE`, so a real token never goes over
plain http; and every override needs `APPDATA`, so fake readings never land in the real data folder. While any variable is set, the app also uses the single-instance name `CodexBarWin_SingleInstance_Test`
(so it runs beside the installed app), never starts the real `claude` to renew a sign-in when the profile is fake, and
reads no Anthropic admin key or spend-log setting from the environment.

### 1. Make a fake profile (synthetic values only)

Keep it outside `%LOCALAPPDATA%`: writes there from a packaged app are redirected into the package's own folder.

```powershell
$fake = "$HOME\cbw-fake"
New-Item -ItemType Directory -Force "$fake\profile\.claude", "$fake\profile\.codex", "$fake\data" | Out-Null
$exp = [DateTimeOffset]::UtcNow.AddDays(30)
Set-Content "$fake\profile\.claude\.credentials.json" ('{"claudeAiOauth":{"accessToken":"fake-claude-token","expiresAt":' + $exp.ToUnixTimeMilliseconds() + '}}')
$payload = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes('{"exp":' + $exp.ToUnixTimeSeconds() + '}')).TrimEnd('=').Replace('+','-').Replace('/','_')
Set-Content "$fake\profile\.codex\auth.json" ('{"tokens":{"access_token":"eyJhbGciOiJub25lIn0.' + $payload + '.fake","account_id":"fake-account"}}')
Set-Content "$fake\antigravity.json" ('{"token":{"access_token":"fake-antigravity-token","expiry":"' + $exp.ToString('o') + '"},"auth_method":"consumer"}')
```

### 2. Set the variables

The app is a packaged (MSIX) app; it gets the user environment, not the environment of the shell that starts it, so
set them at user level. Only Debug builds read them.

```powershell
$vars = @{
  CODEXBARWIN_TEST_PROFILE_ROOT                = "$fake\profile"
  CODEXBARWIN_TEST_APPDATA                     = "$fake\data"
  CODEXBARWIN_TEST_ANTIGRAVITY_CREDENTIAL_FILE = "$fake\antigravity.json"
  CODEXBARWIN_TEST_CLAUDE_BASE_URL             = "http://127.0.0.1:18080"
  CODEXBARWIN_TEST_CODEX_BASE_URL              = "http://127.0.0.1:18080"
  CODEXBARWIN_TEST_ANTIGRAVITY_BASE_URL        = "http://127.0.0.1:18080"
}
$vars.GetEnumerator() | ForEach-Object { [Environment]::SetEnvironmentVariable($_.Key, $_.Value, 'User') }
```

### 3. Build and start a side-by-side Debug package

Running `CodexBarWin.exe` from `bin` directly fails (the app needs package identity). Register the Debug layout as a
development package under another identity, so the installed app is left alone. Developer Mode must be on.

```powershell
dotnet build CodexBarWin.csproj -c Debug -p:Platform=x64 -nodeReuse:false
$layout = "bin\x64\Debug\net10.0-windows10.0.19041.0\win-x64"
(Get-Content "$layout\AppxManifest.xml" -Raw) -replace '<Identity Name="CodexBarWin"', '<Identity Name="CodexBarWinTest"' |
  Set-Content "$layout\AppxManifest.xml"
Add-AppxPackage -Register "$layout\AppxManifest.xml"
$family = (Get-AppxPackage CodexBarWinTest).PackageFamilyName
explorer.exe "shell:AppsFolder\$family!App"
```

Every Debug build rewrites `AppxManifest.xml`, so repeat the rename before registering again. Keep the usage and cost
sources on Native (the default). The WSL route is not redirected.

### 4. Clean up

```powershell
curl.exe "http://127.0.0.1:18080/__stop"
Get-Process CodexBarWin -ErrorAction SilentlyContinue | Where-Object Path -notlike '*\WindowsApps\*' | Stop-Process
Get-AppxPackage CodexBarWinTest | Remove-AppxPackage
$vars.Keys | ForEach-Object { [Environment]::SetEnvironmentVariable($_, $null, 'User') }
Remove-Item -Recurse -Force $fake
```

## Not redirected

These still go to the real world in a Debug test run: the provider status pages (status.claude.com,
status.openai.com, the Google status feed), the daily price download from models.dev, and everything on the WSL route
(the CodexBar CLI inside WSL reads WSL's own home). The Anthropic admin-key billing reader sees no key while the hooks
are on, so it makes no calls.

## Tests

`CodexBarWin.Tests.Unit/Services/Native/NativeTestOverridesTests.cs` covers the variable parser (Debug), the Release
stubs (`#if !DEBUG`; CI runs the tests in Release), the loopback allow list and one end-to-end read of all three
providers over a loopback socket.
