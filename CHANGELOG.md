# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Fixed
- Reset times read consistently as "Resets in 4h 59m" on the cards and detail page, and the detail page no longer squeezes or garbles the usage text
- Flyout not appearing over other programs: it now takes the foreground reliably and stays above windows that become topmost
- "Command failed (exit code 3)" for Claude: usage is now fetched through the Claude CLI instead of the rate-limited OAuth endpoint
- Gemini windows are labelled "5-hour" and "Weekly" instead of "Pro" and "Flash"

### Added
- Pace layer under every usage bar: a second, violet bar shows how much of the usage window's time has passed (full at the reset). Blue running past it means you're using usage fast; violet showing past the blue means you're using it slowly. Hover a bar for the exact numbers
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
