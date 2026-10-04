# Hardening verification — 2026-10-05

## Build and automated checks

- Baseline before hardening: 378 unit tests passed. Final result: **400 passed, zero failures or skips**, using `dotnet test CodexBarWin.Tests.Unit -c Debug -p:Platform=x64`.
- New regressions exercise the real SettingsService, including 40 overlapping ordinary/quiet saves, failed writes that preserve the previous file, and malformed persisted settings. The previous copied settings implementation in the tests was removed.
- Service tests cover cost being disabled while waiting for a scan or while a scan runs, canceled usage results being discarded before cache updates, and developer samples staying out of the real cache. Two additional regressions reproduced cancellation creating an empty 15-minute cost-cache entry, then passed after the fix: canceled manual scans retain previous totals, and a canceled first scan can retry immediately when cost is re-enabled.
- MainViewModel is linked into the test project. Deliberately uncooperative backends test settings changes during usage/cost fetches, developer-mode changes, Stop/restart, and cancellation plus history flush before the exit event.
- Release MSIX build passed. The existing trusted certificate signed validation version **0.9.5.8**; signature verification passed and that package was installed and launched. The source manifest was restored to `0.0.0.0`, including its original encoding.
- Two existing build warnings remain: the missing `win-x64.pubxml` publish profile and missing `mspdbcmf.exe` symbol-packaging tool. The isolated fixture also reports that its scratch copy has no Git version information.
- The only added dependency is CommunityToolkit.Mvvm in the unit project, matching the existing application dependency; its package license is MIT.

## Native visual coverage

Checks used Windows computer use with screenshots and real clicks at the machine's **150% display scaling**. Live checks began on 0.9.5.5 before the interruption. The full page/navigation checks were performed on 0.9.5.7; the final 0.9.5.8 contains the additional service cache fix and received a startup/flyout smoke test. Windows Application events showed no production-app crash during the final smoke test.

| Area | Result and evidence |
|---|---|
| Live flyout | Claude, Codex and Gemini cards render usage, reset times and both wrapping cost periods. Operational providers have green badges; Gemini without reported status has a grey badge. Refresh and Settings have meaningful accessibility names. |
| Live detail pages | All three providers open and navigate back. History, projections, current-window captions, local timestamps and provider-specific extra windows render. Real history survived package updates and restarts; the final file contained 331 stored samples at the check. |
| Cost controls | Live daily bars and top models render. The 7-day range and bar tooltip were exercised on 0.9.5.5; the 30-day and 14-day ranges were checked again on 0.9.5.7, including updated date labels and bars. A refreshed WSL scan with Windows CODEX_HOME returned real totals. Ongoing sessions and the 15-minute cost cadence make differently timed totals differ; an exact same-instant comparison was not claimed. |
| Status fixtures | Major and maintenance banners, red/amber card badges and grey unknown status render. A deliberately unsafe status URL produces no link. Gemini's extra limits fit on its card. Status-page navigation itself was covered by the earlier user-supplied Claude review, rather than repeated here. |
| History fixtures | Seven current-window samples display a readable light-theme guide and a clipped projection. Single-sample windows use singular captions and show collecting-data text without a guide behind it. Dark populated charts were also checked on real data. |
| Cost fixtures | Priced and unpriced model rows render. Turning costs off hides cards and details; turning them on brings totals back. |
| In-flight cost race | The fixture intentionally ignores cancellation and returns cost after 90 seconds. Cost was saved as off **49.1 seconds into the Claude scan**. After 122.2 seconds, the scan had completed, the queued refresh had finished, and both cards and the Claude detail footer still had no cost section. |
| Settings save failures | A directory at `settings.json.tmp` forces a write failure. Settings stays open with an error, and the previous settings file remains byte-for-byte intact. Cancel restores the original theme. Retrying after the blocker was renamed succeeds. A failed save while scrolled to the bottom now scrolls to the visible error. |
| Settings layout | Appearance, provider controls, cost toggle, refresh/startup options, overlay/strip options, alerts and developer mode were inspected. Long descriptions now clear the switches in light and dark themes. Production accessibility inspection confirms names for the selectors, reorder buttons and switches. |
| Provider configuration | Reordering providers persists and changes card order. Cancel leaves provider edits unapplied. Disabling every provider gives a readable recovery message and keeps Settings available; Retry remains responsive. |
| Empty/error fixtures | Empty responses show compact provider headers without stale totals or charts. Provider failures show a compact warning and readable detail error log. |
| Setup fixtures | Missing/stopped/broken WSL, missing distro and missing CLI instructions were inspected through Recheck. Ready shows Start, Recheck and Guide without clipping. Completing setup creates one main window before closing the wizard and leaves the process running. |
| Compact fixture controls | Incident dots and usage colors render. Right-click menus open; Hide actions close the overlay/strip, and clicking the strip opens the flyout. The fixture's altered geometry/focus flags limit what these checks establish, as explained below. |
| Notification button | The production test-notification button was clicked and the app remained responsive. Windows did not expose a toast window to the tool, so delivery is unverified. |

## Fixture isolation and limits

The signed `CodexBarWinHardeningFixture` package used a separate package identity, single-instance mutex, settings, cache and history. Its injected setup/usage/cost backends supplied the states above. It never replaced the production CLI bridge or wrote fixture samples into real history.

To make compact windows targetable, the last fixture variants exposed them in switchers, retained their native window styles, enlarged their outer bounds and disabled fullscreen hiding. The rendered controls and their menu actions were tested, but **normal-size placement, focus preservation, dragging/position restoration, fullscreen hiding and compact hover tooltips were not verified** by those variants.

Earlier experimental fixture versions 9.9.9.1 and 9.9.9.3 generated Windows XAML crash events. The setup transition failure observed in the first version was corrected and retested. The separate 9.9.9.3 crash's cause was not established; access to its Windows Error Reporting details was denied. It did not recur in the final fixture checks or the final production smoke test. This is a limitation of the compact-window testing, rather than evidence that all runtime paths are crash-free.

The fixture package was removed after testing. Production settings were backed up, startup visibility was temporarily changed to make the flyout reachable, and the original settings bytes were restored and verified by SHA-256. The installed production app remains running with the original saved preferences. Native captures are retained in this chat's local output artifacts.

## Remaining manual checks

- Actual tray popup interaction, including overlay toggle and clean Quit. The native-popup command wiring was checked against H.NotifyIcon 2.4.1's implementation; cancellation and forced history flush are covered by unit tests, but a real tray Quit was not exercised.
- Normal compact-window geometry/focus/fullscreen behavior and hover tooltips, including investigation if the earlier experimental XAML crash recurs.
- Windows toast delivery and startup after a real Windows sign-in.
- Display scales other than 150%, additional monitors and screen-reader use beyond the accessibility-tree inspection.
- Clipboard contents after the setup/error copy buttons. Their layouts were inspected, but clipboard output was not verified.

No push, tag or release was performed for this hardening pass.
