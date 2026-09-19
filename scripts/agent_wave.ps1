<#
.SYNOPSIS
    Plans, briefs, and reports on a wave of concurrent agents working disjoint
    paths in this repo -- the missing "set the wave up safely" half of what
    scripts/hooks/block_agent_dispatch_in_worktree.py and
    scripts/hooks/block_recursive_delete.py already defend once a wave exists.

.DESCRIPTION
    THE MODEL THIS SCRIPT BUILDS FOR, AND WHY
    ------------------------------------------
    Issue #300 named git worktrees, modelled on a donor script that gave
    each agent its own linked worktree. That is not what this repo's own
    overnight waves actually do, MEASURED across four real waves the night
    this script was written (an orchestrator's own session log, not
    committed to this repo, but the practice it recorded is): every agent
    works the SAME checkout, at the SAME time, with a disjoint set of owned
    paths, and the orchestrator is the only one who ever runs a `git`
    command. Nobody created a worktree. Nothing broke that a worktree would
    have prevented, because the paths never overlapped -- and this script's own
    `-Start` refuses to let them.

    That is a real, load-bearing decision, not a shortcut:
      - `scripts/hooks/block_agent_dispatch_in_worktree.py`'s own module
        docstring already assumes worktrees exist for orchestrator-level
        isolation (a *dispatched agent* must not itself dispatch a
        sub-agent from inside one), not for parallel same-wave agents.
      - A worktree needs `worker/.venv` (hundreds of files) duplicated,
        symlinked, or junctioned per agent, and `git worktree remove`
        needs `-Unlink` first and refuses to run from inside the very
        worktree it is removing -- real, measured failure modes for a
        benefit (filesystem-level isolation) this repo does not need when
        ownership is already disjoint by construction and this script
        checks it.
      - Every agent's own brief already states, in this repo's own words,
        "Never git add/commit/push/checkout/stash/reset" -- the orchestrator
        holds every git command. A workflow needing per-agent worktree
        creation and teardown implies per-agent git access to use it.

    So `-Isolation SharedTree` (the default) is a real command that creates
    no filesystem isolation at all: it validates the plan, renders briefs,
    and reserves a `--basetemp` per agent, because those are the things a
    real wave tonight actually needed and did not have.
    `-Isolation Worktree` is also implemented, for the rarer case a future
    wave genuinely wants filesystem isolation (an experimental change one
    agent should be able to blow away without touching the primary
    checkout) -- it creates one linked worktree per agent with a junction
    to the primary checkout's `worker/.venv` (never copied, never
    re-installed), and tears down with `-Unlink` before `git worktree
    remove`, refusing if run from inside a worktree being removed, per
    issue #300's own "Done when".

    THE WAVE SPEC
    -------------
    A JSON file (`-SpecFile`), shaped exactly like the `| Agent | Issues |
    Owned paths |` table every real wave the night this script was written
    was already planned in by hand:

        {
          "name": "wave-2026-09-19-round2",
          "globalForbiddenPaths": ["legacy/clair/", ".git/"],
          "agents": [
            { "name": "worker",  "issues": [39, 45, 36], "ownedPaths": ["worker/**"] },
            { "name": "scripts", "issues": [300],         "ownedPaths": ["scripts/**"] },
            { "name": "docs",    "issues": [],            "ownedPaths": ["docs/**"],
              "notes": "finish the closed-issue audit, then seed docs/ToTest.md" }
          ]
        }

    Each agent's forbidden paths are derived automatically: every OTHER
    agent's owned paths, plus `globalForbiddenPaths`. Nothing needs to be
    typed twice, which is exactly how the real waves were planned by hand.

.PARAMETER Start
    Validate the spec (always, including the overlap check) and, with
    -Apply, render one brief per agent and reserve one `--basetemp`
    directory per agent under -OutputDir. Without -Apply: report the plan,
    change nothing.

.PARAMETER Status
    Report the wave's current state: every agent's owned paths and issues,
    every uncommitted file under `git status`, bucketed by which agent owns
    its path (or "UNCLAIMED" if it matches nobody), and each assigned
    issue's real GitHub state (open/closed) with its latest comment. This
    is the recovery report: if a wave ends abruptly, this is what an
    orchestrator picking up the pieces reads first.

.PARAMETER Down
    Tear down. For `-Isolation SharedTree` (the default), this removes the
    rendered briefs and reserved basetemp directories under -OutputDir --
    there is no filesystem isolation to tear down, because none was
    created. For `-Isolation Worktree`, this also removes every agent's
    linked worktree, refusing (unless -Force) one with uncommitted changes
    or one this process is currently running inside.

.PARAMETER Apply
    Without this switch, -Start only reports the plan (the default is a dry
    run, matching scripts/sync_labels.ps1). With it, briefs are written and
    basetemp directories are created (and, under -Isolation Worktree,
    worktrees too).

.PARAMETER WhatIf
    Force dry-run reporting even if -Apply was also passed. Same shape as
    scripts/sync_labels.ps1's own -WhatIf.

.PARAMETER All
    With -Down: tear down every agent in the spec.

.PARAMETER AgentName
    With -Down: tear down only this one agent.

.PARAMETER Force
    With -Down -Isolation Worktree: remove a worktree even though it has
    uncommitted changes.

.PARAMETER SpecFile
    Path to the wave spec JSON. Required for -Start and -Status; required
    for -Down unless -OutputDir alone is enough to find what was recorded
    there.

.PARAMETER OutputDir
    Where briefs, basetemp directories, and (for -Isolation Worktree)
    worktrees live. Defaults OUTSIDE the repo
    (`$env:TEMP\dna-entropy-graph-wave\<spec-name>\`), on purpose: wave
    state is ephemeral orchestration data, not something this script commits
    or expects anyone else to commit. Pass a path under the repo only if you
    intend to gitignore and manage it yourself.

.PARAMETER Isolation
    `SharedTree` (default) or `Worktree`. See the model note above.

.PARAMETER RepoRoot
    Defaults to this script's own repo root (one level up from `scripts/`).

.PARAMETER SelfTest
    Exercise spec validation, path-overlap detection, brief rendering, and
    (against a real, throwaway git repo -- never this one) worktree
    create/list/remove, with no network access. Exit 0 on pass, 1 on
    failure.

.EXAMPLE
    pwsh scripts/agent_wave.ps1 -Start -SpecFile wave.json
    Dry run: validates the spec (refuses on any path overlap), reports what
    -Apply would create. Changes nothing.

.EXAMPLE
    pwsh scripts/agent_wave.ps1 -Start -SpecFile wave.json -Apply
    Renders one brief per agent and reserves one --basetemp directory per
    agent, under a temp directory outside the repo.

.EXAMPLE
    pwsh scripts/agent_wave.ps1 -Status -SpecFile wave.json
    The recovery report: owned paths, issues and their real GitHub state,
    and every uncommitted file bucketed by owner (or UNCLAIMED).

.EXAMPLE
    pwsh scripts/agent_wave.ps1 -Down -SpecFile wave.json -All
    Removes the rendered briefs and basetemp directories (and, under
    -Isolation Worktree, every agent's worktree).

.EXAMPLE
    pwsh scripts/agent_wave.ps1 -SelfTest
#>

param(
    [switch]$Start,
    [switch]$Status,
    [switch]$Down,
    [switch]$SelfTest,
    [switch]$Help,
    [switch]$Apply,
    [switch]$WhatIf,
    [switch]$All,
    [string]$AgentName,
    [switch]$Force,
    [string]$SpecFile,
    [string]$OutputDir,
    [ValidateSet('SharedTree', 'Worktree')]
    [string]$Isolation = 'SharedTree',
    [string]$Repo = 'Raaif-Yousuf/DNA-Entropy-Graph',
    [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'

function Write-UsageError {
    # `Write-Error` under `$ErrorActionPreference = 'Stop'` (set above)
    # escalates to a TERMINATING error -- it never returns, so a `Write-Error
    # "..."; exit 2` right after it never reaches the `exit 2`: the process
    # ends with PowerShell's own default exit code (1) instead. Plain
    # console output sidesteps that entirely and lets the caller's chosen
    # exit code actually take effect.
    param([Parameter(Mandatory)][string]$Message)
    [Console]::Error.WriteLine($Message)
}

if ($Help) {
    Get-Help -Name $PSCommandPath -Full
    exit 0
}

# ---------------------------------------------------------------------------
# Wave spec: read, validate, derive forbidden paths.
# ---------------------------------------------------------------------------

function Read-WaveSpec {
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "wave spec not found: $Path"
    }
    $raw = Get-Content -LiteralPath $Path -Raw
    try {
        $data = $raw | ConvertFrom-Json
    } catch {
        throw "wave spec at ${Path}: not valid JSON ($($_.Exception.Message))"
    }

    if (-not $data.name) { throw "wave spec at ${Path}: missing top-level 'name'" }
    if (-not $data.agents -or @($data.agents).Count -eq 0) {
        throw "wave spec at ${Path}: 'agents' must be a non-empty array"
    }

    $globalForbidden = @()
    if ($data.PSObject.Properties.Name -contains 'globalForbiddenPaths') {
        $globalForbidden = @($data.globalForbiddenPaths)
    }

    $agents = @()
    $seenNames = @{}
    foreach ($a in @($data.agents)) {
        if (-not $a.name) { throw "wave spec at ${Path}: an agent entry is missing 'name'" }
        if ($seenNames.ContainsKey($a.name)) {
            throw "wave spec at ${Path}: duplicate agent name '$($a.name)'"
        }
        $seenNames[$a.name] = $true
        if (-not $a.ownedPaths -or @($a.ownedPaths).Count -eq 0) {
            throw "wave spec at ${Path}: agent '$($a.name)' has no ownedPaths"
        }
        $issues = @()
        if ($a.PSObject.Properties.Name -contains 'issues') { $issues = @($a.issues) }
        $notes = ''
        if ($a.PSObject.Properties.Name -contains 'notes') { $notes = [string]$a.notes }
        # A carve-out within an owned path, e.g. "scripts/**" owned by one
        # agent this round except "scripts/gen_manifest_schema.py", which a
        # DIFFERENT agent owns for this round only -- a real, measured case
        # the night this script was written (round 2: the scripts agent
        # owns scripts/** except gen_manifest_schema.py, which the worker
        # agent owns as part of #39). Without this, a single-file carve-out
        # inside a directory a whole agent owns cannot be expressed at all.
        $excluded = @()
        if ($a.PSObject.Properties.Name -contains 'excludedPaths') { $excluded = @($a.excludedPaths) }

        $agents += [pscustomobject]@{
            Name          = $a.name
            Issues        = $issues
            OwnedPaths    = @($a.ownedPaths)
            ExcludedPaths = $excluded
            Notes         = $notes
        }
    }

    return [pscustomobject]@{
        Name                 = $data.name
        GlobalForbiddenPaths = $globalForbidden
        Agents               = $agents
    }
}

function Get-PathRoot {
    # Normalise a pattern like "scripts/**", "scripts/*", or a bare
    # "AGENTS.md" to the directory/file prefix that actually matters for
    # overlap and ownership checks. Forward slashes only, no trailing slash.
    param([Parameter(Mandatory)][string]$Pattern)
    $p = $Pattern.Trim().Replace('\', '/')
    if ($p.EndsWith('/**')) { $p = $p.Substring(0, $p.Length - 3) }
    elseif ($p.EndsWith('/*')) { $p = $p.Substring(0, $p.Length - 2) }
    $p = $p.TrimEnd('/')
    return $p
}

function Test-PathOverlap {
    # True if RootA and RootB are the same directory/file, or one sits
    # inside the other -- segment-by-segment, so "scripts" and "scripts2"
    # (a real, different top-level entry) are correctly NOT an overlap.
    param([Parameter(Mandatory)][string]$RootA, [Parameter(Mandatory)][string]$RootB)
    if ($RootA -eq $RootB) { return $true }
    $segA = $RootA -split '/'
    $segB = $RootB -split '/'
    $n = [Math]::Min($segA.Count, $segB.Count)
    for ($i = 0; $i -lt $n; $i++) {
        if ($segA[$i] -ne $segB[$i]) { return $false }
    }
    return $true  # one is a strict prefix of the other
}

function Test-PathIsUnderOrEqual {
    # True if Target sits at or inside Root (Root is Target's ancestor or
    # itself) -- directional, unlike Test-PathOverlap: used to ask "does
    # this exclusion COVER that path", not "do these two merely collide".
    param([Parameter(Mandatory)][string]$Root, [Parameter(Mandatory)][string]$Target)
    if ($Root -eq $Target) { return $true }
    $segRoot = $Root -split '/'
    $segTarget = $Target -split '/'
    if ($segRoot.Count -gt $segTarget.Count) { return $false }
    for ($i = 0; $i -lt $segRoot.Count; $i++) {
        if ($segRoot[$i] -ne $segTarget[$i]) { return $false }
    }
    return $true
}

function Test-OverlapExcused {
    # An overlap between $Owner's root $OwnerRoot and $Other's root
    # $OtherRoot is excused when $Owner has explicitly excluded a path that
    # covers the overlapping region -- the "scripts/** minus
    # gen_manifest_schema.py" carve-out.
    param([Parameter(Mandatory)][object]$Owner, [Parameter(Mandatory)][string]$OwnerRoot, [Parameter(Mandatory)][string]$OtherRoot)
    foreach ($ex in $Owner.ExcludedPaths) {
        $exRoot = Get-PathRoot $ex
        # The exclusion must cover whichever side is the narrower (more
        # specific) one, since that is the actual region in dispute.
        $narrower = if ($OtherRoot.Length -ge $OwnerRoot.Length) { $OtherRoot } else { $OwnerRoot }
        if (Test-PathIsUnderOrEqual -Root $exRoot -Target $narrower) { return $true }
    }
    return $false
}

function Find-OwnershipOverlaps {
    param([Parameter(Mandatory)][object[]]$Agents)
    $findings = [System.Collections.Generic.List[object]]::new()
    for ($i = 0; $i -lt $Agents.Count; $i++) {
        for ($j = $i + 1; $j -lt $Agents.Count; $j++) {
            foreach ($pathA in $Agents[$i].OwnedPaths) {
                foreach ($pathB in $Agents[$j].OwnedPaths) {
                    $rootA = Get-PathRoot $pathA
                    $rootB = Get-PathRoot $pathB
                    if (-not (Test-PathOverlap $rootA $rootB)) { continue }
                    if (Test-OverlapExcused -Owner $Agents[$i] -OwnerRoot $rootA -OtherRoot $rootB) { continue }
                    if (Test-OverlapExcused -Owner $Agents[$j] -OwnerRoot $rootB -OtherRoot $rootA) { continue }
                    $findings.Add([pscustomobject]@{
                        AgentA = $Agents[$i].Name; PathA = $pathA
                        AgentB = $Agents[$j].Name; PathB = $pathB
                    })
                }
            }
        }
    }
    return $findings
}

function Get-AgentForbiddenPaths {
    param([Parameter(Mandatory)][object]$Spec, [Parameter(Mandatory)][string]$Name)
    $forbidden = [System.Collections.Generic.List[string]]::new()
    foreach ($a in $Spec.Agents) {
        if ($a.Name -ne $Name) { foreach ($p in $a.OwnedPaths) { $forbidden.Add($p) } }
    }
    foreach ($p in $Spec.GlobalForbiddenPaths) { $forbidden.Add($p) }
    $self = $Spec.Agents | Where-Object { $_.Name -eq $Name } | Select-Object -First 1
    if ($self) {
        # What this agent excluded from its own ownership is, by
        # definition, off-limits to it too -- someone else owns it instead.
        foreach ($p in $self.ExcludedPaths) { $forbidden.Add("$p (carved out of your own owned paths this round)") }
    }
    return $forbidden
}

# ---------------------------------------------------------------------------
# Output layout: one directory per wave, outside the repo by default.
# ---------------------------------------------------------------------------

function Get-DefaultOutputDir {
    param([Parameter(Mandatory)][string]$WaveName)
    return Join-Path $env:TEMP "dna-entropy-graph-wave/$WaveName"
}

function Get-BaseTempPath {
    param([Parameter(Mandatory)][string]$OutputDir, [Parameter(Mandatory)][string]$Name)
    return Join-Path $OutputDir "basetemp/$Name"
}

function Get-BriefPath {
    param([Parameter(Mandatory)][string]$OutputDir, [Parameter(Mandatory)][string]$Name)
    return Join-Path $OutputDir "briefs/$Name-brief.md"
}

# ---------------------------------------------------------------------------
# Brief rendering.
# ---------------------------------------------------------------------------

$BriefTemplate = @'
# Wave brief: {{AGENT_NAME}} ({{WAVE_NAME}})

The owner is not reading your narration. Do not narrate: spend no tokens on
progress prose, restating this brief, or announcing what you are about to
do. Report outcomes, numbers, file paths, and the sections this brief asks
for at the end.

## You do not touch git. Not read-only, not "carefully" -- not at all.

The orchestrator holds every `git` command (status, diff, log, add, commit,
push, checkout, stash, reset -- all of it) and reads the tree on your
behalf. If you need to know what changed, describe it in your report and
let the orchestrator check. Leave your work uncommitted in the working
tree; the orchestrator commits.

Never `git stash` (shared across every concurrent session in this tree; a
revert-check that stashes can swap two agents' working sets). Never a
recursive delete inside the repo (`.scratch/`-shaped directories, if this
run has one, are shared). Both are enforced by
`scripts/hooks/block_git_stash.py` / `block_recursive_delete.py` via
`scripts/hooks/run_hook.py`, which denies rather than silently allowing if
the guard itself cannot run -- but the instinct should be yours before the
hook ever has to fire.

## Isolation model for this wave: {{ISOLATION_MODE}}

{{ISOLATION_NOTE}}

## Paths you own

{{OWNED_PATHS}}

## Paths you must NOT touch

{{FORBIDDEN_PATHS}}

If a change genuinely belongs outside your paths, do not make it. Write it
to `{{HANDOFF_PATH}}` and say so in your report.

## Your assigned issues

{{ISSUES}}

{{NOTES_SECTION}}
## Skills to invoke, by name, with the Skill tool (not paraphrased from memory)

- `fixing-a-bug` -- before writing ANY fix code for a bug, regression or defect.
- `wired-to-nothing` -- before reporting ANYTHING as done.
- `working-an-issue` -- before starting OR closing any GitHub issue.
- `tests-first` -- before writing any feature, fix, or behaviour change.

## Testing

Run only tests covering what you touched. Your own reserved pytest temp
directory for this wave, so your run never collides with another agent's:

    worker\.venv\Scripts\python.exe -m pytest <your paths> -q --basetemp="{{BASETEMP_PATH}}"

Only the orchestrator runs the full suite. Never a GPU test on this laptop
(no CUDA here); GPU tests run only via `scripts/cloud_gpu_test.ps1`. Never
create a cloud resource outside that script unless this brief names the
budget explicitly (it does not, here).

## Docs

A changelog fragment for what you land, in the **same commit-worthy change**
(the orchestrator commits it): `docs/changelog.d/{{AGENT_NAME}}-{{WAVE_NAME}}.md`,
starting with `- ` (a literal hyphen and space) -- a heading of any depth is
silently dropped by the compiler. `scripts/compile_sprint_log.py --check`
(or `check_changelog_fragments.py`) catches the shape mistake; run it before
reporting done if you touched `docs/changelog.d/`.

## Before reporting anything done

Name the one observable that would differ if your change were wired to
nothing, and go check it -- that is what the `wired-to-nothing` skill means
by "before reporting done," not a synonym for "the tests pass."

## Your report back (short; no file dumps)

- Issues closed or commented, with numbers.
- Files created or modified, as a plain list of paths.
- Issues filed, with numbers and one-line titles.
- Tests you ran and their exact result line.
- Anything you deliberately did not do, and why.
- Anything the orchestrator must do outside your paths.
'@

function New-WaveBrief {
    param(
        [Parameter(Mandatory)][object]$Spec,
        [Parameter(Mandatory)][object]$Agent,
        [Parameter(Mandatory)][string]$BaseTempPath,
        [Parameter(Mandatory)][string]$HandoffPath,
        [Parameter(Mandatory)][string]$IsolationMode
    )

    $ownedList = ($Agent.OwnedPaths | ForEach-Object { "- ``$_``" }) -join "`n"
    if ($Agent.ExcludedPaths.Count -gt 0) {
        $exclNote = ($Agent.ExcludedPaths | ForEach-Object { "  - except ``$_`` (owned by someone else this round)" }) -join "`n"
        $ownedList = "$ownedList`n$exclNote"
    }
    $forbidden = Get-AgentForbiddenPaths -Spec $Spec -Name $Agent.Name
    $forbiddenList = if ($forbidden.Count -gt 0) {
        ($forbidden | ForEach-Object { "- ``$_``" }) -join "`n"
    } else {
        "*(none recorded -- still stay inside the paths you own above)*"
    }
    $issuesList = if ($Agent.Issues.Count -gt 0) {
        ($Agent.Issues | ForEach-Object { "- #$_ -- run ``gh issue view $_`` first" }) -join "`n"
    } else {
        "*(none assigned in the spec -- see Notes below, or the orchestrator's own message)*"
    }
    $notesSection = if ($Agent.Notes) { "## Notes from the orchestrator`n`n$($Agent.Notes)`n`n" } else { "" }
    $isolationNote = if ($IsolationMode -eq 'Worktree') {
        "You are working in your own linked git worktree. Its path and branch are named in the " +
        "orchestrator's dispatch message, not derived here -- confirm both before starting."
    } else {
        "A single shared checkout. Every agent in this wave works the SAME working tree, at the " +
        "SAME time, on disjoint paths. This is deliberate (see scripts/agent_wave.ps1's own header " +
        "comment for why), not a workaround -- do not ask to switch to a worktree."
    }

    $text = $BriefTemplate
    $text = $text.Replace('{{AGENT_NAME}}', $Agent.Name)
    $text = $text.Replace('{{WAVE_NAME}}', $Spec.Name)
    $text = $text.Replace('{{ISOLATION_MODE}}', $IsolationMode)
    $text = $text.Replace('{{ISOLATION_NOTE}}', $isolationNote)
    $text = $text.Replace('{{OWNED_PATHS}}', $ownedList)
    $text = $text.Replace('{{FORBIDDEN_PATHS}}', $forbiddenList)
    $text = $text.Replace('{{HANDOFF_PATH}}', $HandoffPath)
    $text = $text.Replace('{{ISSUES}}', $issuesList)
    $text = $text.Replace('{{NOTES_SECTION}}', $notesSection)
    $text = $text.Replace('{{BASETEMP_PATH}}', $BaseTempPath)
    return $text
}

# ---------------------------------------------------------------------------
# Status / recovery report.
# ---------------------------------------------------------------------------

function Get-PathOwner {
    # Which agent (if any) owns a git-status-relative path, by the same
    # root-prefix rule Test-PathOverlap uses. A path covered by one of that
    # agent's own ExcludedPaths is skipped for that agent (someone else's
    # match, if any, wins instead). Returns $null for UNCLAIMED.
    param([Parameter(Mandatory)][string]$RelativePath, [Parameter(Mandatory)][object[]]$Agents)
    $normalised = $RelativePath.Replace('\', '/')
    foreach ($a in $Agents) {
        $excludedHit = $false
        foreach ($ex in $a.ExcludedPaths) {
            $exRoot = Get-PathRoot $ex
            if ($normalised -eq $exRoot -or $normalised.StartsWith("$exRoot/")) { $excludedHit = $true; break }
        }
        if ($excludedHit) { continue }
        foreach ($owned in $a.OwnedPaths) {
            $root = Get-PathRoot $owned
            if ($normalised -eq $root -or $normalised.StartsWith("$root/")) {
                return $a.Name
            }
        }
    }
    return $null
}

function Get-GitStatusFiles {
    # git status --porcelain=v1 --untracked-files=all, parsed into a flat
    # list of repo-relative paths (handles renames' "old -> new" shape by
    # taking the new path). `--untracked-files=all` matters here: without
    # it, git collapses a whole new untracked DIRECTORY into one summary
    # line ("?? scripts/") instead of listing the files inside it, which
    # would still bucket correctly by owner (the directory is still under
    # that agent's root) but is far less useful for the actual recovery
    # question -- "what, specifically, is uncommitted".
    param([Parameter(Mandatory)][string]$RepoRoot)
    $raw = git -C $RepoRoot status --porcelain=v1 --untracked-files=all 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "git status failed in ${RepoRoot}: $raw"
    }
    $paths = @()
    foreach ($line in @($raw)) {
        if (-not $line -or $line.Length -lt 4) { continue }
        $rest = $line.Substring(3)
        if ($rest -match '^(.*) -> (.*)$') { $rest = $Matches[2] }
        $paths += $rest.Trim('"')
    }
    return $paths
}

function Get-IssueStatusLine {
    param([Parameter(Mandatory)][int]$Number, [Parameter(Mandatory)][string]$Repo)
    $raw = gh issue view $Number --repo $Repo --json state,title,comments 2>&1
    if ($LASTEXITCODE -ne 0) {
        return "#${Number}: could not read from GitHub ($raw)"
    }
    try {
        $data = $raw | ConvertFrom-Json
    } catch {
        return "#${Number}: unreadable response from gh"
    }
    $latest = ''
    $comments = @($data.comments)
    if ($comments.Count -gt 0) {
        $lastBody = [string]$comments[-1].body
        $latest = ($lastBody -split "`n")[0]
        if ($latest.Length -gt 100) { $latest = $latest.Substring(0, 100) + '...' }
    }
    $line = "#${Number} [$($data.state)] $($data.title)"
    if ($latest) { $line += " -- latest comment: $latest" }
    return $line
}

function Get-WaveStatusReport {
    param([Parameter(Mandatory)][object]$Spec, [Parameter(Mandatory)][string]$RepoRoot, [string]$Repo)

    $files = Get-GitStatusFiles -RepoRoot $RepoRoot
    $byOwner = @{}
    $unclaimed = @()
    foreach ($f in $files) {
        $owner = Get-PathOwner -RelativePath $f -Agents $Spec.Agents
        if ($owner) {
            if (-not $byOwner.ContainsKey($owner)) { $byOwner[$owner] = @() }
            $byOwner[$owner] += $f
        } else {
            $unclaimed += $f
        }
    }

    $report = [System.Collections.Generic.List[object]]::new()
    foreach ($a in $Spec.Agents) {
        $uncommitted = if ($byOwner.ContainsKey($a.Name)) { $byOwner[$a.Name] } else { @() }
        $issueLines = @()
        if ($Repo) {
            foreach ($n in $a.Issues) { $issueLines += Get-IssueStatusLine -Number $n -Repo $Repo }
        }
        $report.Add([pscustomobject]@{
            Agent             = $a.Name
            OwnedPaths        = $a.OwnedPaths
            Issues            = $a.Issues
            IssueStatus       = $issueLines
            UncommittedFiles  = $uncommitted
        })
    }

    return [pscustomobject]@{
        Agents    = $report
        Unclaimed = $unclaimed
    }
}

function Write-WaveStatusReport {
    param([Parameter(Mandatory)][object]$StatusReport)

    foreach ($a in $StatusReport.Agents) {
        Write-Host "=== $($a.Agent) ==="
        Write-Host "  owns: $($a.OwnedPaths -join ', ')"
        if ($a.IssueStatus.Count -gt 0) {
            Write-Host "  issues:"
            foreach ($line in $a.IssueStatus) { Write-Host "    $line" }
        }
        if ($a.UncommittedFiles.Count -gt 0) {
            Write-Host "  uncommitted under its paths ($($a.UncommittedFiles.Count)):"
            foreach ($f in $a.UncommittedFiles) { Write-Host "    $f" }
        } else {
            Write-Host "  uncommitted under its paths: none"
        }
        Write-Host ""
    }
    if ($StatusReport.Unclaimed.Count -gt 0) {
        Write-Host "=== UNCLAIMED (matches no agent's owned paths -- look here first on recovery) ==="
        foreach ($f in $StatusReport.Unclaimed) { Write-Host "  $f" }
    } else {
        Write-Host "UNCLAIMED: none -- every uncommitted file sits under some agent's owned paths."
    }
}

# ---------------------------------------------------------------------------
# Worktree isolation (the non-default path; issue #300's original ask).
# ---------------------------------------------------------------------------

function Get-WorktreePath {
    param([Parameter(Mandatory)][string]$RepoRoot, [Parameter(Mandatory)][string]$WaveName, [Parameter(Mandatory)][string]$AgentName)
    $repoName = Split-Path -Leaf $RepoRoot
    $wtRoot = Join-Path (Split-Path -Parent $RepoRoot) "$repoName-wt"
    return Join-Path $wtRoot "$WaveName-$AgentName"
}

function New-AgentWorktree {
    param([Parameter(Mandatory)][string]$RepoRoot, [Parameter(Mandatory)][string]$WorktreePath, [Parameter(Mandatory)][string]$Branch)

    $parent = Split-Path -Parent $WorktreePath
    if (-not (Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }

    $out = git -C $RepoRoot worktree add -q -b $Branch $WorktreePath 2>&1
    if ($LASTEXITCODE -ne 0) { throw "git worktree add failed for ${WorktreePath}: $out" }

    # Never copy or reinstall the venv: a directory junction to the primary
    # checkout's own worker/.venv, so `worker\.venv\Scripts\python.exe`
    # resolves identically in every worktree without a second install.
    $primaryVenv = Join-Path $RepoRoot 'worker\.venv'
    $wtVenv = Join-Path $WorktreePath 'worker\.venv'
    if ((Test-Path -LiteralPath $primaryVenv) -and -not (Test-Path -LiteralPath $wtVenv)) {
        New-Item -ItemType Junction -Path $wtVenv -Target $primaryVenv | Out-Null
    }
}

function Remove-AgentWorktree {
    param([Parameter(Mandatory)][string]$RepoRoot, [Parameter(Mandatory)][string]$WorktreePath, [switch]$Force)

    if (-not (Test-Path -LiteralPath $WorktreePath)) {
        Write-Host "  (already gone: $WorktreePath)"
        return
    }

    $resolvedWorktree = (Resolve-Path -LiteralPath $WorktreePath).Path
    $resolvedCwd = (Resolve-Path -LiteralPath (Get-Location)).Path
    if ($resolvedCwd -eq $resolvedWorktree -or $resolvedCwd.StartsWith("$resolvedWorktree$([IO.Path]::DirectorySeparatorChar)")) {
        throw "refusing to remove '$WorktreePath': the current directory is inside it. cd out first."
    }

    if (-not $Force) {
        $dirty = git -C $WorktreePath status --porcelain=v1 2>&1
        if ($LASTEXITCODE -eq 0 -and @($dirty | Where-Object { $_ }).Count -gt 0) {
            throw "refusing to remove '$WorktreePath': it has uncommitted changes. Pass -Force to remove anyway (they will be lost)."
        }
    }

    # The venv junction must be unlinked first, or `git worktree remove`
    # (and a plain recursive delete) will walk into it and either fail or,
    # worse, delete the PRIMARY checkout's real venv contents through the
    # junction. `-Unlink`-then-remove is issue #300's own stated trap.
    $wtVenv = Join-Path $WorktreePath 'worker\.venv'
    if (Test-Path -LiteralPath $wtVenv) {
        (Get-Item -LiteralPath $wtVenv).Delete()  # removes the junction point only, not its target
    }

    $removeArgs = @('-C', $RepoRoot, 'worktree', 'remove')
    if ($Force) { $removeArgs += '--force' }
    $removeArgs += $WorktreePath
    $out = git @removeArgs 2>&1
    if ($LASTEXITCODE -ne 0) { throw "git worktree remove failed for ${WorktreePath}: $out" }
}

# ---------------------------------------------------------------------------
# Self-test
# ---------------------------------------------------------------------------

function Invoke-SelfTest {
    $failures = 0
    function Check($label, $condition) {
        if ($condition) { Write-Host "ok    $label" } else { Write-Host "FAIL  $label"; $script:failures++ }
    }

    # --- Get-PathRoot / Test-PathOverlap ---
    Check "Get-PathRoot strips a /** suffix" ((Get-PathRoot 'scripts/**') -eq 'scripts')
    Check "Get-PathRoot strips a /* suffix" ((Get-PathRoot 'docs/*') -eq 'docs')
    Check "Get-PathRoot leaves a bare file alone" ((Get-PathRoot 'AGENTS.md') -eq 'AGENTS.md')

    Check "nested paths overlap (scripts/** vs scripts/hooks/**)" `
        (Test-PathOverlap (Get-PathRoot 'scripts/**') (Get-PathRoot 'scripts/hooks/**'))
    Check "identical roots overlap" (Test-PathOverlap 'worker' 'worker')
    Check "disjoint top-level paths do not overlap" `
        (-not (Test-PathOverlap (Get-PathRoot 'docs/**') (Get-PathRoot 'scripts/**')))
    Check "a real, different top-level entry is not a false-positive overlap" `
        (-not (Test-PathOverlap (Get-PathRoot 'scripts/**') (Get-PathRoot 'scripts2/**')))

    # --- Read-WaveSpec: valid spec ---
    $tmp = New-Item -ItemType Directory -Force -Path (Join-Path $env:TEMP ("wave-selftest-" + [Guid]::NewGuid()))
    try {
        $goodSpecPath = Join-Path $tmp 'good.json'
        @{
            name   = 'selftest-wave'
            agents = @(
                @{ name = 'a'; issues = @(1, 2); ownedPaths = @('scripts/**') },
                @{ name = 'b'; issues = @(); ownedPaths = @('docs/**') }
            )
        } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $goodSpecPath

        $spec = Read-WaveSpec -Path $goodSpecPath
        Check "Read-WaveSpec reads a well-formed spec" ($spec.Agents.Count -eq 2 -and $spec.Name -eq 'selftest-wave')

        $overlaps = Find-OwnershipOverlaps -Agents $spec.Agents
        Check "no overlap found in a genuinely disjoint spec" ($overlaps.Count -eq 0)

        $forbiddenForA = Get-AgentForbiddenPaths -Spec $spec -Name 'a'
        Check "forbidden paths for one agent are derived from the others' owned paths" `
            ($forbiddenForA -contains 'docs/**')

        # --- overlapping spec ---
        $badSpecPath = Join-Path $tmp 'overlap.json'
        @{
            name   = 'selftest-wave-overlap'
            agents = @(
                @{ name = 'worker-a'; issues = @(); ownedPaths = @('worker/**') },
                @{ name = 'worker-b'; issues = @(); ownedPaths = @('worker/src/dna_entropy/worker/**') }
            )
        } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $badSpecPath
        $badSpec = Read-WaveSpec -Path $badSpecPath
        $overlaps = Find-OwnershipOverlaps -Agents $badSpec.Agents
        Check "a real overlap (worker/** vs worker/src/.../worker/**) is caught" ($overlaps.Count -ge 1)

        # --- malformed specs ---
        $threw = $false
        try { Read-WaveSpec -Path (Join-Path $tmp 'does-not-exist.json') } catch { $threw = $true }
        Check "a missing spec file throws" $threw

        $dupPath = Join-Path $tmp 'dup.json'
        @{ name = 'x'; agents = @(@{ name = 'a'; ownedPaths = @('x/**') }, @{ name = 'a'; ownedPaths = @('y/**') }) } |
            ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $dupPath
        $threw = $false
        try { Read-WaveSpec -Path $dupPath } catch { $threw = $true }
        Check "a duplicate agent name throws" $threw

        $noOwnedPath = Join-Path $tmp 'noowned.json'
        @{ name = 'x'; agents = @(@{ name = 'a'; ownedPaths = @() }) } |
            ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $noOwnedPath
        $threw = $false
        try { Read-WaveSpec -Path $noOwnedPath } catch { $threw = $true }
        Check "an agent with no ownedPaths throws" $threw

        # --- brief rendering ---
        $brief = New-WaveBrief -Spec $spec -Agent $spec.Agents[0] -BaseTempPath 'C:\temp\bt\a' `
            -HandoffPath 'scratchpad\handoff-a.md' -IsolationMode 'SharedTree'
        Check "the rendered brief names the agent and the wave" `
            ($brief -match 'Wave brief: a \(selftest-wave\)')
        Check "the rendered brief lists the agent's owned paths" ($brief -match '`scripts/\*\*`')
        Check "the rendered brief lists the other agent's paths as forbidden" ($brief -match '`docs/\*\*`')
        Check "the rendered brief carries the reserved basetemp path" ($brief -match [regex]::Escape('C:\temp\bt\a'))
        Check "the rendered brief states the no-git rule" ($brief -match 'You do not touch git')
        Check "the rendered brief names all four required skills" `
            ($brief -match 'fixing-a-bug' -and $brief -match 'wired-to-nothing' -and `
             $brief -match 'working-an-issue' -and $brief -match 'tests-first')
        Check "the rendered brief lists both assigned issues" ($brief -match '#1' -and $brief -match '#2')

        # --- status report / path ownership ---
        $owner = Get-PathOwner -RelativePath 'scripts/hooks/run_hook.py' -Agents $spec.Agents
        Check "a file under an owned path resolves to that agent" ($owner -eq 'a')
        $owner = Get-PathOwner -RelativePath 'legacy/clair/CLAUDE.md' -Agents $spec.Agents
        Check "a file matching nobody's owned path is UNCLAIMED (null)" ($null -eq $owner)

        # --- excludedPaths: the real "scripts/** except gen_manifest_schema.py" carve-out ---
        $carveSpecPath = Join-Path $tmp 'carveout.json'
        @{
            name   = 'carveout-wave'
            agents = @(
                @{ name = 'scripts'; ownedPaths = @('scripts/**'); excludedPaths = @('scripts/gen_manifest_schema.py') },
                @{ name = 'worker'; ownedPaths = @('worker/**', 'scripts/gen_manifest_schema.py') }
            )
        } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $carveSpecPath
        $carveSpec = Read-WaveSpec -Path $carveSpecPath
        $carveOverlaps = Find-OwnershipOverlaps -Agents $carveSpec.Agents
        Check "a carve-out (excludedPaths) excuses what would otherwise be a real overlap" `
            ($carveOverlaps.Count -eq 0)

        $owner = Get-PathOwner -RelativePath 'scripts/gen_manifest_schema.py' -Agents $carveSpec.Agents
        Check "the carved-out file resolves to the agent that actually owns it, not the directory's agent" `
            ($owner -eq 'worker')
        $owner = Get-PathOwner -RelativePath 'scripts/sync_labels.ps1' -Agents $carveSpec.Agents
        Check "the rest of the directory still resolves to its real owner" ($owner -eq 'scripts')

        $scriptsForbidden = Get-AgentForbiddenPaths -Spec $carveSpec -Name 'scripts'
        Check "an agent's own carve-out is listed as forbidden to itself" `
            (($scriptsForbidden | Where-Object { $_ -like 'scripts/gen_manifest_schema.py*' }).Count -gt 0)
    } finally {
        Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue
    }

    # --- worktree create/remove against a REAL, throwaway git repo (never this one) ---
    $wtTmp = Join-Path $env:TEMP ("wave-selftest-wt-" + [Guid]::NewGuid())
    New-Item -ItemType Directory -Force -Path $wtTmp | Out-Null
    try {
        $primary = Join-Path $wtTmp 'primary'
        New-Item -ItemType Directory -Force -Path $primary | Out-Null
        git -C $primary init -q
        git -C $primary config user.email 'test@example.com'
        git -C $primary config user.name 'Test'
        Set-Content -LiteralPath (Join-Path $primary 'README.md') -Value 'hello'
        # Matches this repo's own .gitignore (worker/.venv/ is ignored): without
        # this, `git status` in the worktree would see the venv junction's
        # contents as untracked and the dirty-check below would refuse to
        # remove a worktree that has no REAL uncommitted work at all.
        Set-Content -LiteralPath (Join-Path $primary '.gitignore') -Value "worker/.venv/`n"
        git -C $primary add README.md .gitignore
        git -C $primary commit -q -m initial

        $venvDir = Join-Path $primary 'worker\.venv'
        New-Item -ItemType Directory -Force -Path $venvDir | Out-Null
        Set-Content -LiteralPath (Join-Path $venvDir 'marker.txt') -Value 'primary venv'

        $wtPath = Join-Path $wtTmp 'primary-wt\branch-a'
        New-AgentWorktree -RepoRoot $primary -WorktreePath $wtPath -Branch 'wave/selftest-a'
        Check "New-AgentWorktree creates a real linked worktree" (Test-Path -LiteralPath $wtPath)
        Check "the worktree's worker/.venv resolves through the junction, never copied" `
            (Test-Path -LiteralPath (Join-Path $wtPath 'worker\.venv\marker.txt'))

        Remove-AgentWorktree -RepoRoot $primary -WorktreePath $wtPath
        Check "Remove-AgentWorktree removes a clean worktree" (-not (Test-Path -LiteralPath $wtPath))
        $list = git -C $primary worktree list
        Check "git worktree list reports only the primary checkout afterward" (@($list).Count -eq 1)

        Check "worker/.venv's real content survives worktree teardown (junction, not the target, was removed)" `
            (Test-Path -LiteralPath (Join-Path $venvDir 'marker.txt'))
    } finally {
        Remove-Item -LiteralPath $wtTmp -Recurse -Force -ErrorAction SilentlyContinue
    }

    if ($failures -eq 0) {
        Write-Host "`nPASS: agent_wave self-test"
        return $true
    }
    Write-Host "`nFAIL: $failures self-test failure(s)"
    return $false
}

# ---------------------------------------------------------------------------
# Entry point
# ---------------------------------------------------------------------------

if ($SelfTest) {
    if (Invoke-SelfTest) { exit 0 } else { exit 1 }
}

$effectiveApply = $Apply -and -not $WhatIf

if ($Start) {
    if (-not $SpecFile) { Write-UsageError "-Start needs -SpecFile"; exit 2 }
    try {
        $spec = Read-WaveSpec -Path $SpecFile
    } catch {
        Write-Error $_
        exit 1
    }

    $overlaps = Find-OwnershipOverlaps -Agents $spec.Agents
    if ($overlaps.Count -gt 0) {
        Write-Host "REFUSING to start wave '$($spec.Name)': path assignments overlap."
        foreach ($o in $overlaps) {
            Write-Host "  $($o.AgentA) ('$($o.PathA)') overlaps $($o.AgentB) ('$($o.PathB)')"
        }
        Write-Host "`nSplit the overlapping agents into separate, sequential waves instead."
        exit 1
    }
    Write-Host "wave '$($spec.Name)': $($spec.Agents.Count) agent(s), no path overlaps."

    if (-not $OutputDir) { $OutputDir = Get-DefaultOutputDir -WaveName $spec.Name }

    foreach ($a in $spec.Agents) {
        $baseTemp = Get-BaseTempPath -OutputDir $OutputDir -Name $a.Name
        $briefPath = Get-BriefPath -OutputDir $OutputDir -Name $a.Name
        $handoffPath = "scratchpad\handoff-$($a.Name).md"

        if (-not $effectiveApply) {
            Write-Host "  [dry-run] would write brief:   $briefPath"
            Write-Host "  [dry-run] would reserve basetemp: $baseTemp"
            if ($Isolation -eq 'Worktree') {
                $wt = Get-WorktreePath -RepoRoot $RepoRoot -WaveName $spec.Name -AgentName $a.Name
                Write-Host "  [dry-run] would create worktree:  $wt"
            }
            continue
        }

        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $briefPath) | Out-Null
        New-Item -ItemType Directory -Force -Path $baseTemp | Out-Null
        $brief = New-WaveBrief -Spec $spec -Agent $a -BaseTempPath $baseTemp -HandoffPath $handoffPath -IsolationMode $Isolation
        Set-Content -LiteralPath $briefPath -Value $brief
        Write-Host "  wrote brief:      $briefPath"
        Write-Host "  reserved basetemp: $baseTemp"

        if ($Isolation -eq 'Worktree') {
            $wt = Get-WorktreePath -RepoRoot $RepoRoot -WaveName $spec.Name -AgentName $a.Name
            New-AgentWorktree -RepoRoot $RepoRoot -WorktreePath $wt -Branch "wave/$($spec.Name)-$($a.Name)"
            Write-Host "  created worktree:  $wt"
        }
    }
    exit 0
}

if ($Status) {
    if (-not $SpecFile) { Write-UsageError "-Status needs -SpecFile"; exit 2 }
    try {
        $spec = Read-WaveSpec -Path $SpecFile
    } catch {
        Write-Error $_
        exit 1
    }
    $reportRepo = if ($Repo) { $Repo } else { $null }
    $report = Get-WaveStatusReport -Spec $spec -RepoRoot $RepoRoot -Repo $reportRepo
    Write-WaveStatusReport -StatusReport $report
    exit 0
}

if ($Down) {
    if (-not $SpecFile) { Write-UsageError "-Down needs -SpecFile"; exit 2 }
    try {
        $spec = Read-WaveSpec -Path $SpecFile
    } catch {
        Write-Error $_
        exit 1
    }
    if (-not $OutputDir) { $OutputDir = Get-DefaultOutputDir -WaveName $spec.Name }
    if (-not $All -and -not $AgentName) { Write-UsageError "-Down needs -All or -AgentName <name>"; exit 2 }

    $targets = if ($All) { $spec.Agents } else { $spec.Agents | Where-Object { $_.Name -eq $AgentName } }
    if (@($targets).Count -eq 0) { Write-UsageError "no matching agent(s) to tear down"; exit 2 }

    $failures = 0
    foreach ($a in $targets) {
        if ($Isolation -eq 'Worktree') {
            $wt = Get-WorktreePath -RepoRoot $RepoRoot -WaveName $spec.Name -AgentName $a.Name
            try {
                Remove-AgentWorktree -RepoRoot $RepoRoot -WorktreePath $wt -Force:$Force
                Write-Host "  removed worktree: $wt"
            } catch {
                Write-Error $_
                $failures++
                continue
            }
        }
        $baseTemp = Get-BaseTempPath -OutputDir $OutputDir -Name $a.Name
        $briefPath = Get-BriefPath -OutputDir $OutputDir -Name $a.Name
        Remove-Item -LiteralPath $baseTemp -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $briefPath -Force -ErrorAction SilentlyContinue
        Write-Host "  cleaned up:       $($a.Name)"
    }
    if ($failures -gt 0) { exit 1 }
    exit 0
}

Write-UsageError "Specify one of -Start, -Status, -Down, -SelfTest, or -Help."
exit 2
