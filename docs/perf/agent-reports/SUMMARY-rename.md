# Rename: CodexBarWin to AgentsWinBar

Branch `perf/rename`, worktree `C:\Users\ThomasFramework\worktrees\CodexBarWin\rename`. Nothing committed. Renames
were done with `git mv` (staged); the four new files are marked intent-to-add (`git add -N`) so `git diff` and
`git grep` see them.

## Results (final tree)

| Check | Result |
| --- | --- |
| `dotnet build AgentsWinBar.csproj -c Release -p:Platform=x64 -p:PublishReadyToRun=false -nodeReuse:false` | Build succeeded, 0 errors, 1 warning (NETSDK1198: the csproj names a `win-x64.pubxml` publish profile the repo does not contain; pre-existing) |
| `dotnet test AgentsWinBar.Tests.Unit -c Debug -p:Platform=x64 -nodeReuse:false` (with `CODEXBARWIN_SKIP_WSL_INTEGRATION=1` and `AGENTSWINBAR_SKIP_WSL_INTEGRATION=1`) | **2680 passed, 0 failed, 10 skipped** (2690 total); the 13 new importer tests pass |
| `pwsh -NoProfile -File tools/test-install.ps1` | Passed: WSL opt-in AST check (55 call sites), 332 assertions (324 before, 8 new) |
| `pwsh -NoProfile -File tools/test-release-security.ps1` | Passed |

The built output is `bin\x64\Release\...\win-x64\AgentsWinBar.exe`; its embedded resources are
`AgentsWinBar.Pricing.models-dev-prices.json` and `AgentsWinBar.Pricing.pricing-overlay.json`. `install.ps1` was not
run (not even `-DryRun`, which calls the GitHub API); its helpers and guards are covered by `tools/test-install.ps1`.

## What was renamed

- Files and folders: `AgentsWinBar.sln`, `AgentsWinBar.slnx`, `AgentsWinBar.csproj` (assembly and exe become
  `AgentsWinBar.exe`; no explicit AssemblyName), `AgentsWinBar.Tests.Unit/AgentsWinBar.Tests.Unit.csproj`.
- Code: RootNamespace, every `namespace` / `using` / `x:Class` / `using:` XAML namespace (`AgentsWinBar.*`).
- Embedded resource logical names `AgentsWinBar.Pricing.*` in `AgentsWinBar.csproj`, the test csproj,
  `tools/CostProbe/CostProbe.csproj` and `CostPriceCatalog.cs`.
- Strings: window titles (main, overlay, taskbar strip, setup), tray tooltip, taskbar strip automation name, settings
  ("Start AgentsWinBar when you sign in"), test toast title, setup text, dashboard admin-key hint, HTTP User-Agents
  (`AgentsWinBar/<version>`, `AgentsWinBar/1.0`), single-instance mutex `AgentsWinBar_SingleInstance` (and
  `_Test`), cost index writer mutex `AgentsWinBar.CostIndex.*`, startup task id `AgentsWinBarStartup` in both
  `Package.appxmanifest` and `SettingsViewModel`, `app.manifest` assembly identity, launch profiles.
- `Package.appxmanifest`: Identity Name, DisplayName, PublisherDisplayName (`AgentsWinBar`), VisualElements
  DisplayName and Description, startup task Executable and DisplayName. Identity Publisher stays `CN=CodexBarWin OSS`.
- `Directory.Build.props`: Authors, Company, Product, and the `MSBuildProjectName` conditions (needed for
  AllowUnsafeBlocks and the SDK arch fix to keep applying).
- Workflows: build/test/licenses use the new sln/csproj/test paths; build artifact `AgentsWinBar-<platform>-<config>`;
  release assets `AgentsWinBar-vX.Y.Z-<arch>.msix`, `AgentsWinBar-<arch>.msix`, `.sha256`,
  `AgentsWinBar-vX.Y.Z-Certificate.cer`, `AgentsWinBar-Certificate.cer`, `AgentsWinBar-vX.Y.Z-checksums.sha256`,
  `AgentsWinBar-checksums.sha256`; temporary `signing/AgentsWinBar.pfx`.
- `install.ps1`: package lookups, identity check (`AgentsWinBar` + unchanged publisher), asset names, staging folder
  `%LOCALAPPDATA%\AgentsWinBar-Setup`, data paths, messages, the final line `AGENTSWINBAR_SETUP=<status>`, default
  repo `Wave-OCE/AgentsWinBar`.
- Docs: `README.md` (names and paths only), `INSTALL.md`, `CONTRIBUTING.md`, `SECURITY.md`, `CLAUDE.md`,
  `.claude/rules/testing.md`, `THIRD-PARTY-NOTICES.md` (this project's name), issue template label, `Samples/README.md`,
  `tools/**` (FakeProviders README and test identity `AgentsWinBarTest`, wsl README/scripts, perf scripts, CostProbe
  paths, cost-ledger report). Repo URLs `Wave-OCE/CodexBarWin` became `Wave-OCE/AgentsWinBar`.
- Environment variables naming this app: `CODEXBARWIN_TEST_*` (Debug-only test hooks) became `AGENTSWINBAR_TEST_*`.
  The WSL test switches are now `AGENTSWINBAR_RUN_WSL_INTEGRATION`, `AGENTSWINBAR_SKIP_WSL_INTEGRATION` and
  `AGENTSWINBAR_SKIP_BASH_INTEGRATION`, and the old `CODEXBARWIN_*` names are still honored
  (`AgentsWinBar.Tests.Unit/TestEnvironment.cs`), so either name skips WSL.
- `CHANGELOG.md`: one Unreleased "Changed" entry ("Renamed to AgentsWinBar ..."); history untouched.

## Deliberately kept (what `git grep -n CodexBarWin` still returns)

- `CN=CodexBarWin OSS`: Identity Publisher in `Package.appxmanifest`, signer checks in `install.ps1`, the test
  manifest in `tools/test-install.ps1`, the certificate commands in `README.md` and the `release.yml` comment. The
  certificate cannot change now. Because the publisher is unchanged the publisher hash is unchanged, so the new package
  family name is `AgentsWinBar_w05y65rx3mcz6` (used by `tools/CostProbe` and `tools/perf/Measure-Refresh.ps1`).
- Upstream attribution: `nek0der/CodexBarWin` links in `README.md` and `THIRD-PARTY-NOTICES.md`;
  `.github/ISSUE_TEMPLATE/config.yml` still points to the upstream discussions (it did before; pointing it at this
  repo is a separate decision).
- `LICENSE.txt` and `<Copyright>` in `Directory.Build.props` keep "Copyright (c) 2025 CodexBarWin Contributors": an MIT
  copyright notice must be retained, so the holder line was not rewritten.
- `CodexBarWin.appinstaller` (dead upstream file that releases no longer publish) and README's warning that names it.
- The migration itself: `Helpers/AppPaths.cs` (`LegacyAppName = "CodexBarWin"`, the single place the old name is
  defined for code), doc comments in `Services/LegacyDataImporter.cs` and `App.xaml.cs`, `LegacyDataImporterTests`,
  `install.ps1` (`Find-LegacyAppData`, `Get-LegacyAppNotice`, the old package lookup and messages), the new
  `tools/test-install.ps1` block, the `INSTALL.md` "Upgrading from CodexBarWin" section, and the `CHANGELOG.md` entry.
- History, untouched as instructed: `CHANGELOG.md` release entries, `docs/perf/**`, `SPIKE-native-usage.md`; also kept
  as records: `SUMMARY-neon-perf.md`, `docs/HANDOFF-cost-status-burndown.md`, `docs/VERIFICATION-hardening.md`, and this
  file.
- Named after the upstream CodexBar CLI, not renamed: the "CodexBar CLI" wording, `tools/wsl/codexbar-shim.sh`,
  `/opt/codexbar`, the `codexbar` command, `CodexBarService` / `ICodexBarService` (and their tests), `SetupStatus`
  `CodexBarInstalled` / `CodexBarVersion` / `CodexBarError`, `SetupStep.InstallCodexBar`,
  `SetupChecker.MinCodexBarVersion`, the `CodexBarGuideUrl` setting (also a persisted settings.json key), the
  `$CodexBarVersion` pin in `install-wsl-shim.ps1`, and the `CODEXBAR_*` environment variables (for example
  `CODEXBAR_GEMINI_SPEND_LOG`).

## Data folder in one place

`Helpers/AppPaths.cs` defines `DataFolderName = "AgentsWinBar"`, `DataDirectory` and `DataFile(name)`. `SettingsService`,
`CacheService`, `EarlyResetStore`, `ClaudeCooldownFile` and the crash log in `App.xaml.cs` use it. The other stores
(`CostLedger`, `CostCacheStore`, `UsageHistoryPersistence`, `SetupMemoService`, the native cost index and prices)
already derive their folder from `settings.json`'s folder. `AppPaths.cs` is linked into the test project and
`tools/NativeProbe` (which compiles `Services/Native/*.cs`).

## Migration design

`Services/LegacyDataImporter.cs`, called by `App.ImportLegacyDataAsync()` in `OnLaunched` after the single-instance
check and before `ISettingsService` is resolved (its constructor creates the folder) and before settings, caches,
history, ledger and early resets load. Runs on a worker thread and is awaited. Skipped when the Debug test hooks are
active (their data folder is fake).

1. If `legacy-import.json` (the marker) exists in the new folder: done (`AlreadyDone`).
2. If the new folder has `settings.json`: write the marker, copy nothing (`TargetHasSettings`).
3. Sources (injectable `Func<IEnumerable<string>>`; default `DefaultSources(localAppData)`): every
   `%LOCALAPPDATA%\Packages\CodexBarWin_*\LocalCache\Local\CodexBarWin` with `settings.json`, ordered by its most
   recently written top-level file (newest first), then `%LOCALAPPDATA%\CodexBarWin`. The first folder with
   `settings.json` is used; a source equal to the target is ignored. `CodexBarWinTest_*` dev identities do not match
   the pattern. None found: write the marker (`NoSource`).
4. Copy every top-level file, plus the `cost-index` subfolder. The cost index is not top level as the brief assumed: it
   is `cost-index\index.sqlite` with `-wal` and `-shm`. Each file is copied to `<name>.import.tmp`, then moved into
   place without overwrite. Existing files are skipped and never overwritten; the source is only read.
5. `settings.json` is copied last and is the commit point. If it is in place, the marker is written (`Imported`, or
   `Partial` when some other file failed). If it could not be copied, no marker is written (`Failed`) and the next
   start retries, skipping the files already copied.
6. Reporting: `ILogger<LegacyDataImporter>` gets counts (copied, already present, failed) and failed file names,
   never contents. Because Release builds register no logging provider, a `Partial` or `Failed` result (and any
   unexpected exception) is also passed to `App.LogHandled`, which writes `crash.log`. `Run()` never throws, and
   `ImportLegacyDataAsync` has its own catch, so startup is never blocked.

Packaged behavior: `%LOCALAPPDATA%\AgentsWinBar` is redirected into the new package's LocalCache. The old package's
LocalCache is read through its real path under `%LOCALAPPDATA%\Packages`. If the old app is running while the import
runs, the copied SQLite index can be inconsistent; the cost service already detects a damaged index and rebuilds it.

### Installer (`install.ps1`)

- Preflight records whether the `CodexBarWin` package is installed (`$legacyInstalled`) and finds old data with the new
  helper `Find-LegacyAppData` (same order as the app).
- Setup used to preseed a fresh `settings.json` before the first launch, which would have stopped the import. It now
  preseeds only when there is no old data; otherwise the app's first launch imports it (one implementation, the
  tested C# importer). The installer's provider selection is then not applied; the old settings win.
- When the old package is installed, "What the human still needs to do" says the data is imported on first launch and
  gives the exact command `Get-AppxPackage -Name CodexBarWin | Remove-AppxPackage`, through `Get-LegacyAppNotice`.
  Setup never removes or stops the old app. DryRun reports the plan and the same notice.
- `INSTALL.md` has an "Upgrading from CodexBarWin" section with the same command.

## Tests added

`AgentsWinBar.Tests.Unit/Services/LegacyDataImporterTests.cs` (13, temp folders only):
`Run_PackagedSourceHasSettings_CopiesEveryFileAndWritesMarker` (also checks the cost index files and that the source
is unchanged), `Run_OnlyUnpackagedSource_CopiesFromUnpackagedFolder`, `Run_PackagedAndUnpackagedSources_PrefersPackaged`,
`Run_PackagedFolderWithoutSettings_FallsBackToUnpackaged`,
`DefaultSources_SeveralPackages_ListsMostRecentlyWrittenFirstThenUnpackaged`, `Run_NoOldData_ReturnsNoSourceAndWritesMarker`,
`Run_TargetAlreadyHasSettings_CopiesNothing`, `Run_TargetFileAlreadyPresent_IsNotOverwritten`,
`Run_OneFileCannotBeCopied_ReturnsPartialAndReportsIt` (locked source file; crash-log callback gets names, not
contents), `Run_SettingsCannotBeCopied_FailsWithoutMarkerAndRetriesNextStart`, `Run_MarkerPresent_DoesNotImportAgain`,
`Run_SourceListThrows_ReturnsFailedWithoutThrowing`, `Run_SourceIsTheTargetFolder_IsIgnored`.

`tools/test-install.ps1` (8 assertions): `Find-LegacyAppData` with none, unpackaged only, and several packaged folders
(newest wins; no-settings and `CodexBarWinTest_*` ignored); `Get-LegacyAppNotice` ends with the exact removal command
in both forms; the installer contains no `Remove-AppxPackage` call; the settings preseed guard requires no old data.

## Follow-ups for the owner

- Rename (or create) the GitHub repository as `Wave-OCE/AgentsWinBar` before the next release. `install.ps1`, README,
  INSTALL and the default guide URL now use that slug, and older releases have only `CodexBarWin-*` assets, which the
  new installer does not look for.
- Existing users of the old setup stage folder `%LOCALAPPDATA%\CodexBarWin-Setup` start fresh under
  `%LOCALAPPDATA%\AgentsWinBar-Setup` (only saved selections for reruns; nothing to migrate).
- After the rename, a running old CodexBarWin and the new AgentsWinBar can both run, because the mutex names differ.
  Removing the old app (the printed command) also removes its startup task.
