# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Fixed
- Settings saves are serialized and replace the complete file atomically; failed saves restore uncommitted values, keep Settings open and scroll to a visible error. Descriptions clear their switches at 150% scaling. Malformed provider lists, timer intervals, timeouts and animation settings recover safely
- Settings changes cancel obsolete refreshes, including late cost results; canceled cost scans preserve prior totals and allow immediate retries instead of creating a 15-minute empty-cache backoff. Developer samples no longer overwrite real usage caches or leak into history. Quit stops refreshes and flushes history before exiting
- Setup completes once and creates the main window before closing the wizard, preventing an early exit. Setup guidance now uses the fork's WSL installer, and its footer fits at 150% scaling
- Start minimized now controls startup visibility, Settings reuses its existing window, and Settings and flyout action controls have explicit accessibility names. The tray menu uses the native popup path to avoid the secondary-window XamlRoot crash
- Detail-page "Last fetched" timestamps now use local time instead of UTC. History captions use "1 sample", the even-use guide has stronger light/dark contrast, and collecting-data text no longer overlaps chart lines. Card cost estimates show each period on a separate wrapping line instead of cutting off the 30-day totals
- Claude "Command timed out after 30 seconds": the app had a hidden 30-second cap on every WSL command that fired before the provider's own 45 to 60 second timeout, and Claude's CLI route had become slow (about 18 seconds, then failing). Claude now uses the fast OAuth route first (about 1 second), keeps the CLI as a fallback that is skipped for 6 hours after it fails, and backs off for 10 minutes if Anthropic rate limits; the WSL cap is never shorter than the provider timeouts
- Enabling, disabling or reordering providers in Settings now takes effect immediately; previously the list was only built at startup, so a newly enabled provider didn't appear until the app was restarted
- Reset times read consistently as "Resets in 4h 59m" on the cards and detail page, and the detail page no longer squeezes or garbles the usage text
- Flyout not appearing over other programs: it now takes the foreground reliably and stays above windows that become topmost
- "Command failed (exit code 3)" for Claude: results are cached for 5 minutes and the last good result is shown if Anthropic rate limits
- Gemini windows are labelled "5-hour" and "Weekly" instead of "Pro" and "Flash"

### Added
- Dashboard: a full window (resizable, maximizable, full screen with F11, optional keep-on-top) opened from the new button in the flyout footer. It shows everything the app knows at once: headline tiles (provider health, incidents, limits, most-used window, next reset, cost today and 30 days), a panel per provider with every usage window (pace, reset times, burndown), service status, cost and token mix, top models and history, plus a diagnostics column (CLI and app versions, effective settings, history store, cost scans, refresh log and alerts this session)
- In-memory activity log of refresh results and alerts, for the dashboard
- Dashboard data snapshots with usage and cost summaries, history, cached CLI diagnostics and session refresh/alert activity.
- Per-window usage history charts with an even-use guide, observed-rate projection, capture time and sample count. Snapshots persist atomically beside Settings for 14 days, deduplicated at half-point changes, ten-minute intervals and window resets. New installations show a collecting-data state until enough samples are available
- Estimated cost and token totals from local Windows Claude/Codex logs and WSL Antigravity logs, with a 15-minute scan cadence, manual refresh, daily cost bars and top models on the detail page, and a Settings toggle. Cost commands bypass the shim's usage cache so log-path changes cannot reuse a zero-cost result
- Provider service status badges: green operational, amber minor incidents or maintenance, red major or critical incidents, and grey when status is unavailable. Detail pages show the provider's report and a status-page link; compact displays show a small logo dot only during incidents
- Usage alerts also cover the extra windows (Claude and GPT quota, Fable only, ...) with the same three levels
- Extra bars are coloured by provider: Anthropic orange (#D97757) for Claude windows (e.g. Fable only), OpenAI green (#10A37F) for OpenAI, and an orange to teal gradient for the shared Claude and GPT quota (routed through gold and green so the middle stays clean instead of turning brown); the time-passed layer behind these bars takes the same colour, softened (so Fable looks orange even at 0% used); amber and red still apply
- Extra usage windows now appear on the main flyout cards too: Claude's "Fable only" bar, and Antigravity's Claude and GPT quota on Gemini's card. They are coloured differently from the main session and weekly bars
- The floating overlay and the taskbar strip now show each provider's most relevant two bars instead of always session and weekly: the window that changed most recently, or one running ahead of schedule, takes a slot; windows at or near their limit rank last, so you see the resources you still have (at most one extra window per row; hover a bar to see which window it is). The app remembers when each bar last moved to do this
- Gemini's detail page also shows Antigravity's separate Claude and GPT quota (5-hour and weekly) with the usual bars, pace and reset times. Any extra windows the CodexBar CLI reports are shown the same way
- Projected run-out time in every usage bar's hover tooltip, e.g. "At this pace you'll hit the limit in 1h 20m, 3h 10m before the reset" (or the usage you're on track for by the reset)
- Usage alerts: a Windows notification when usage is far ahead of pace (25+ points and the limit would be hit well before the reset), nearly out (90%+), or at the limit; once per usage window. Settings toggle (on by default) and a "Send test notification" button
- The floating overlay and the taskbar strip hide while a fullscreen game, video or presentation is running, and come back afterwards
- `tools/wsl`: installer for the CodexBar CLI and the shim that bridges Gemini (via Antigravity) and Claude (CLI route, cached) inside WSL, so a fresh machine or reinstalled distro can be set up in one step
- Pace layer under every usage bar: a soft violet layer shows how much of the usage window's time has passed (full at the reset); when it shows past the end of the blue you're using usage slowly. When usage is outpacing the schedule, a thin marker line appears at the time position instead. Hover a bar for the exact numbers
- Overlay and taskbar strip number columns size to the actual numbers (two digits as the typical case) so values sit snugly next to their bars and the "%" signs line up
- The taskbar strip sits flush with the taskbar: full height (starting just below the taskbar's top line), square left end, rounded right end, no border
- Taskbar display: a compact usage strip on the taskbar, bottom-left corner, with its own Settings toggle (independent of the floating overlay)
- Provider logos replace the text labels in the overlay and taskbar strip to save space
- Always-on overlay: a minimal, draggable usage readout that stays above other windows without taking focus (tray menu and Settings toggle; position is remembered)
- Crash log at `%LOCALAPPDATA%\CodexBarWin\crash.log`
- Developer mode for testing without CodexBar CLI
- Sample JSON data files for Claude, Gemini, and Codex providers
- Settings toggle to enable/disable developer mode

### Changed
- Shorter labels for the extra usage windows so the flyout bars get more room: "Fable only" is now "Fable", "Claude and GPT 5-hour" is "Claude+GPT 5h" and "Claude and GPT weekly" is "Claude+GPT 7d" (everywhere: cards, compact displays, detail pages, alerts and the dashboard)
- README rewritten for the fork: quick start (WSL, sign-ins, install), how to read the bars, what the fork adds, troubleshooting; new screenshots of the flyout, detail page, floating overlay, taskbar strip and Settings

## [0.1.0] - TBD

### Added
- System tray integration with Windows 11 native UI
- Multi-provider support (Claude, Codex, Gemini)
- Dark mode support with Mica backdrop
- Auto refresh functionality
- Settings page for provider configuration
- Window slide-in animation from system tray
- Setup wizard for first-time configuration

[Unreleased]: https://github.com/nek0der/CodexBarWin/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/nek0der/CodexBarWin/releases/tag/v0.1.0
