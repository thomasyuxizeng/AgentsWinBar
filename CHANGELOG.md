# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Fixed
- Enabling, disabling or reordering providers in Settings now takes effect immediately; previously the list was only built at startup, so a newly enabled provider didn't appear until the app was restarted
- Reset times read consistently as "Resets in 4h 59m" on the cards and detail page, and the detail page no longer squeezes or garbles the usage text
- Flyout not appearing over other programs: it now takes the foreground reliably and stays above windows that become topmost
- "Command failed (exit code 3)" for Claude: usage is now fetched through the Claude CLI instead of the rate-limited OAuth endpoint
- Gemini windows are labelled "5-hour" and "Weekly" instead of "Pro" and "Flash"

### Added
- Usage alerts also cover the extra windows (Claude and GPT quota, Fable only, ...) with the same three levels
- Extra bars are coloured by provider and kept subtle: faded Anthropic orange for Claude windows (e.g. Fable only), teal for OpenAI, and an orange to teal gradient for the shared Claude and GPT quota; amber and red still apply
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
- N/A

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
