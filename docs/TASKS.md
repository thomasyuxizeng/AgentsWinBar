# Task board

The shared queue for coding agents (Codex, Claude Code) and people. One file per task in `docs/tasks/`.
Protocol and rules are in [`AGENTS.md`](../AGENTS.md).

## How a task moves

1. **Write the spec** as `docs/tasks/<id>.md` from [`_template.md`](tasks/_template.md). `<id>` is
   `NNN-short-name` (lower case, digits and hyphens). Add it to the table below as `queued`.
2. **Start it**: `pwsh tools/agents/run-codex-task.ps1 -Task <id>` (or open the worktree by hand). Status becomes `in progress`.
3. **Agent finishes**: it commits on `agent/<id>` and writes `docs/tasks/<id>.result.md`. Status becomes `review`.
4. **Review**: diff against the base branch, run the tests, check the result file's claims.
5. **Merge** (explicit, by a person): status becomes `done`. Delete the worktree and branch.

Keep specs small: one task, one outcome, checkable acceptance criteria. If it needs more than a day of work,
split it.

## Queue

| ID | Title | Status | Branch | Notes |
|----|-------|--------|--------|-------|
| [001-incident-notifications](tasks/001-incident-notifications.md) | Notify when a provider goes down | queued | `agent/001-incident-notifications` | Small; good first run of the Codex hand-off |
| 002 | Native Claude and Codex usage (no WSL) | idea | | Needs research first: see the comparison notes; hybrid, keep WSL for Gemini |

Statuses: `idea` (no spec yet), `queued`, `in progress`, `review`, `done`, `blocked`.

## Done

Earlier work was handed over through [`HANDOFF-cost-status-burndown.md`](HANDOFF-cost-status-burndown.md):
provider status badges, cost and token totals, and the usage history / burndown charts.
