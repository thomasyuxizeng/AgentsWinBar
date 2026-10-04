<#
.SYNOPSIS
  Hands a task from docs/tasks/<id>.md to Codex (`codex exec`) in its own git worktree and branch.

.DESCRIPTION
  1. Creates a worktree next to the repo (..\CodexBarWin-agent-<id>) on a new branch agent/<id>, based on -Base.
  2. Runs `codex exec` there with the configured model, a workspace-write sandbox and the task protocol from
     AGENTS.md. The prompt goes in over stdin, so no quoting problems.
  3. Afterwards runs the unit tests in the worktree and prints a summary: commits, diff stat, result file.
  It never pushes, tags, merges or changes GitHub settings. Review and merging are separate, explicit steps.

.PARAMETER Task        Task id, e.g. 001-incident-notifications (the file docs/tasks/<id>.md must exist).
.PARAMETER Base        Commit or branch to start from. Default: HEAD of the repo you run this in.
.PARAMETER Model       Codex model. Default gpt-6.1-sol.
.PARAMETER Effort      Reasoning effort passed to Codex (low, medium, high, xhigh). Default xhigh.
.PARAMETER Sandbox     workspace-write (default) or read-only. Never danger-full-access from here.
.PARAMETER DryRun      Print the prompt and the exact command; create nothing, run nothing.
.PARAMETER PrepareOnly Create the worktree and branch, print the prompt, but do not start Codex.
.PARAMETER SkipTests   Do not run the unit tests afterwards.

.EXAMPLE
  pwsh tools/agents/run-codex-task.ps1 -Task 001-incident-notifications -DryRun
  pwsh tools/agents/run-codex-task.ps1 -Task 001-incident-notifications
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Task,
    [string]$Base = 'HEAD',
    [string]$Model = 'gpt-6.1-sol',
    [ValidateSet('low', 'medium', 'high', 'xhigh')][string]$Effort = 'xhigh',
    [ValidateSet('read-only', 'workspace-write')][string]$Sandbox = 'workspace-write',
    [switch]$DryRun,
    [switch]$PrepareOnly,
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'

if ($Task -notmatch '^[a-z0-9][a-z0-9-]*$') { throw "Task id '$Task' must be lower case letters, digits and hyphens." }

$repo = (git -C $PSScriptRoot rev-parse --show-toplevel).Trim()
$spec = Join-Path $repo "docs/tasks/$Task.md"
if (-not (Test-Path $spec)) { throw "No spec at docs/tasks/$Task.md (copy docs/tasks/_template.md)." }
if (-not (Get-Command codex -ErrorAction SilentlyContinue) -and -not $DryRun -and -not $PrepareOnly) {
    throw "The 'codex' command is not on PATH."
}

$branch = "agent/$Task"
$worktree = Join-Path (Split-Path $repo -Parent) ("{0}-agent-{1}" -f (Split-Path $repo -Leaf), $Task)
$gitCommon = (git -C $repo rev-parse --path-format=absolute --git-common-dir).Trim()
$baseSha = (git -C $repo rev-parse --verify "$Base^{commit}").Trim()

$prompt = @"
You are a coding agent working on CodexBarWin. Read AGENTS.md, CLAUDE.md and .claude/rules/*.md first and follow them.

Your task is specified in docs/tasks/$Task.md. Do exactly that task.

Rules for this run:
- You are in a dedicated git worktree on branch $branch. Work only on this branch; do not switch branches, rebase, push, tag, or merge.
- Commit your work in small commits with the repository's message prefixes.
- Run the full unit test suite (dotnet test CodexBarWin.Tests.Unit -c Debug -p:Platform=x64) before you finish; it must pass.
- When done, write docs/tasks/$Task.result.md: what changed, how you tested it, what you could not verify, follow-ups. Commit it.
- If the spec is ambiguous or blocked, stop and explain in the result file instead of guessing.
"@

$codexArgs = @(
    'exec', '-C', $worktree, '-m', $Model, '-s', $Sandbox,
    '-c', "model_reasoning_effort=`"$Effort`"",
    '--add-dir', $gitCommon,                                  # worktree commits write into the main repo's .git
    '-o', (Join-Path $worktree "docs/tasks/$Task.codex-last-message.txt"),
    '-'                                                       # prompt from stdin
)

Write-Host "Task:      $Task"
Write-Host "Base:      $Base ($($baseSha.Substring(0, 8)))"
Write-Host "Branch:    $branch"
Write-Host "Worktree:  $worktree"
Write-Host "Command:   codex $($codexArgs -join ' ')"

if ($DryRun) {
    Write-Host "`n--- prompt (stdin) ---`n$prompt`n--- end ---`nDry run: nothing created, nothing run."
    return
}

# Worktree and branch
if (Test-Path $worktree) {
    Write-Host "Reusing existing worktree."
} else {
    $exists = git -C $repo branch --list $branch
    if ($exists) { throw "Branch $branch already exists but its worktree does not. Remove the branch or check it out yourself." }
    git -C $repo worktree add -q $worktree -b $branch $baseSha
    if ($LASTEXITCODE -ne 0) { throw "git worktree add failed." }
}
# The spec may be uncommitted in the main checkout: make sure the worktree has it
$wtSpec = Join-Path $worktree "docs/tasks/$Task.md"
if (-not (Test-Path $wtSpec)) { Copy-Item $spec $wtSpec }

if ($PrepareOnly) {
    Write-Host "`nPrepared. Prompt that would be sent:`n$prompt"
    return
}

$log = Join-Path $worktree "docs/tasks/$Task.codex-run.log"
$started = Get-Date
Write-Host "`nStarting Codex at $($started.ToString('HH:mm:ss')) (log: $log) ..."
$prompt | & codex @codexArgs 2>&1 | Tee-Object -FilePath $log
$codexExit = $LASTEXITCODE
$minutes = [math]::Round(((Get-Date) - $started).TotalMinutes, 1)

Write-Host "`n=== Codex finished (exit $codexExit, $minutes min) ==="
if ($codexExit -ne 0) {
    $tail = (Get-Content $log -Tail 15) -join "`n"
    if ($tail -match '(?i)usage|limit|quota|rate') { Write-Warning "Looks like a usage or rate limit. Try again later; the worktree is kept." }
    Write-Warning "Codex exited with $codexExit. Last log lines:`n$tail"
}

# Summary
Write-Host "`n--- commits on $branch ---"
git -C $worktree log --oneline "$baseSha..HEAD"
Write-Host "`n--- diff stat vs base ---"
git -C $worktree diff --stat $baseSha
$dirty = git -C $worktree status --short
if ($dirty) { Write-Warning "Uncommitted changes in the worktree:`n$dirty" }

$result = Join-Path $worktree "docs/tasks/$Task.result.md"
if (Test-Path $result) { Write-Host "`nResult file: $result" } else { Write-Warning "No docs/tasks/$Task.result.md was written." }

if (-not $SkipTests -and $codexExit -eq 0) {
    Write-Host "`n--- running unit tests in the worktree ---"
    Push-Location $worktree
    try { dotnet test CodexBarWin.Tests.Unit -c Debug -p:Platform=x64 --nologo -v q } finally { Pop-Location }
}

Write-Host "`nNext: review (git -C `"$worktree`" diff $($baseSha.Substring(0,8))..HEAD), then merge $branch yourself. Nothing was pushed."
