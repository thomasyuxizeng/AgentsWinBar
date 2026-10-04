# Agent instructions (Codex and other coding agents)

CodexBarWin is a WinUI 3 / .NET 10 Windows 11 tray app that shows AI usage limits (Claude, Codex, Gemini).
It runs the CodexBar CLI inside WSL and draws the result. This file is for agents working on the code. It
mirrors `CLAUDE.md`, which Claude Code reads; the rules are the same for both.

## Read first

1. [`CLAUDE.md`](CLAUDE.md) and `.claude/rules/*.md`: code style, security, testing and PR rules. They apply to you.
2. [`README.md`](README.md): what the app does, how it is installed.
3. [`docs/TASKS.md`](docs/TASKS.md): the task board, and the protocol below.

## Build and test

```powershell
dotnet restore CodexBarWin.sln
dotnet build -c Release -p:Platform=x64
dotnet test CodexBarWin.Tests.Unit -c Debug -p:Platform=x64
```

The test project links source files individually (`<Compile Include=... Link="Linked\...">` in
`CodexBarWin.Tests.Unit.csproj`). A new pure file in `Services/`, `Models/` or `Helpers/` needs a test file and
a link entry. Run the full test suite before you finish; it must pass.

## Environment pitfalls

- **Unsigned builds do not run** on the maintainer's machine (Application Control blocks `bin\` output). To see
  the real app you need a signed MSIX: see "From source" in the README. Building and unit tests do not need that.
- The installed app's settings and history live under
  `%LOCALAPPDATA%\Packages\CodexBarWin_w05y65rx3mcz6\LocalCache\Local\CodexBarWin\`. Do not edit them.
- WSL (`Ubuntu-24.04`) idles out between commands, the first call after idle takes 5 to 10 s, and piping text
  from PowerShell adds CRLF (send scripts base64-encoded). The shim in `tools/wsl/codexbar-shim.sh` fronts the CLI.
- Do not use `Remove-Item` on temp paths in the maintainer's environment (a guard blocks it).
- Never put secrets, tokens or personal data in code, docs, commits or task files.

## Task protocol

Work arrives as a spec in `docs/tasks/<id>.md` (see `docs/tasks/_template.md`). When you are given a task:

1. Work **only on the branch you were started on** (normally `agent/<id>`, in its own git worktree). Do not
   switch branches, rebase, or touch other branches.
2. Make small, reviewable commits. English only. Follow the `Add:` / `Fix:` / `Update:` / `Refactor:` /
   `Test:` / `Docs:` / `Remove:` prefixes.
3. Update `CHANGELOG.md` under `[Unreleased]` for user-visible changes, and the README if behaviour it describes changes.
4. Write `docs/tasks/<id>.result.md` when you finish: what changed, how you tested it, anything you could not
   verify, follow-ups. Do not claim something works unless you ran it. If a check needs the real running app or
   a screen, say it is unverified.
5. **Do not** push, tag, release, merge, or change GitHub settings. A human (or the reviewing agent) does that.
6. If the spec is ambiguous or blocked, stop and say so in the result file instead of guessing.

## Review

Changes are reviewed by a person or by Claude Code in the main checkout: diff against the base branch, run the
tests, check the result file's claims. Merging is a separate, explicit step.

## Delegating from Claude Code to Codex

`tools/agents/run-codex-task.ps1 -Task <id>` creates the worktree and branch, starts `codex exec` with the
configured model (`gpt-6.1-sol`), runs the tests afterwards and prints a summary. See the script's header.
The reverse direction (Codex calling Claude, via `claude -p`) is not set up: the `claude` command is not on the
maintainer's PATH.
