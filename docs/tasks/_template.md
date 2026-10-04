# <id>: <short title>

**Status:** queued
**Base branch:** (the branch the worktree starts from, usually the current feature branch)
**Branch:** `agent/<id>`

## Goal

One or two sentences: what should be true when this is done, and why it matters to a user.

## Context

What the agent needs to know that it can't get from the code: relevant files, measured behaviour, constraints,
decisions already made. Link to docs rather than pasting them.

## Scope

- **Do:** the specific changes.
- **Don't:** things that are tempting but out of scope.

## Acceptance criteria

Checkable statements. Each should be verifiable by a test or by a described manual check.

- [ ] ...
- [ ] Unit tests added for new code in `Services/`, `Models/`, `Helpers/`; the full suite passes.
- [ ] `CHANGELOG.md` updated under `[Unreleased]`.

## Verification the agent cannot do

List what needs the real signed app or a screen, so the agent reports it as unverified instead of guessing.

## Result

Written by the agent to `docs/tasks/<id>.result.md`.
