<#
.SYNOPSIS
    Renders this repo's canonical issue body template and, with -Apply,
    files it via `gh issue create`.

.DESCRIPTION
    docs/superpowers/specs/2026-09-18-appendix-c-repo-conventions.md section
    6 names an exact markdown shape for every tracked issue (Why / Done when
    / Observable that proves it is wired / Docs touched / Tests / Out of
    scope / Cloud money) and says, in these words, that
    `scripts/new_issue.ps1` renders it. Appendix C's own repo-layout tree
    lists this file next to `issue_precheck.py`, `sync_labels.ps1` and
    `sync_memory.py` -- but nothing had ever built it, and no issue tracked
    building it either. This is that script.

    It is deliberately narrower than an overnight agent's own quick-triage
    issues (see AGENT_BRIEF's own shorter What/Why it matters/Where/Done
    when/Observable shape, which stays exactly as useful as it already is
    for that purpose): this renders the FULLER template section 6 defines
    for real, project-tracked work -- the shape every issue opened at
    project start already uses.

    A missing section is rendered as a plain placeholder line naming what
    belongs there, not silently dropped, so a half-filled-in issue still
    reads as "half-filled-in" to the next person, not as "finished, three
    sections short".

.PARAMETER Title
    Required. `<area>: <imperative verb phrase>` (or `EPIC <area>: <noun
    phrase>`, or `DECISION: <question>?`). Not enforced, only warned about
    on a shape this script does not recognise -- the convention has three
    real shapes, and a warning that cannot be silenced would be the wrong
    kind of strict.

.PARAMETER Why
    One paragraph: the user-visible or money-visible problem, or the
    capability. If it claims a cause, say so yourself with `MEASURED
    <date>:` or `THEORY (unverified):` -- this script does not add that for
    you.

.PARAMETER DoneWhen
    One or more falsifiable checklist items (each becomes its own `- [ ]`
    line). At least one is required: an issue with no acceptance criteria is
    exactly what the `needs-criteria` label exists to catch, and this script
    would rather you notice before filing than after. For more than one from
    an interactive prompt or another .ps1, use PowerShell's own array
    literal: `-DoneWhen "first thing","second thing"` (repeating `-DoneWhen`
    as a flag errors: "specified more than once", it does not accumulate).
    Invoked instead via a raw argv list (no PowerShell parser in front of
    it -- a `subprocess.run([...])`-shaped call, for instance), only a
    single string ever binds; pass one `-DoneWhen` and add the rest as
    further Done-when lines to the rendered body by hand.

.PARAMETER Observable
    The one thing that differs between "this works" and "this compiles,
    passes tests and does nothing" -- name where to look.

.PARAMETER DocsTouched
    Zero or more doc paths this issue's own work will update. Rendered as a
    checklist; empty is valid (some issues touch no docs).

.PARAMETER Tests
    Zero or more `<path>: <what it asserts>` lines.

.PARAMETER OutOfScope
    What this issue deliberately does not do (and which issue does, if
    known).

.PARAMETER CloudMoney
    `none`, or the machine type, expected minutes, expected cost, and who
    pays. Defaults to `none` -- most issues spend nothing; say so explicitly
    rather than leaving the section blank.

.PARAMETER Labels
    Comma-separated labels for `gh issue create --label`.

.PARAMETER Milestone
    Optional `gh issue create --milestone` value.

.PARAMETER Repo
    `<owner>/<repo>` to file against. Defaults to this repo.

.PARAMETER Apply
    Without this switch, the rendered body is printed and nothing is filed
    (the default is a dry run, matching scripts/sync_labels.ps1 and
    scripts/agent_wave.ps1). With it, `gh issue create` actually runs.

.PARAMETER WhatIf
    Force dry-run output even if -Apply was also passed.

.PARAMETER SelfTest
    Exercise template rendering and title-shape checking against fixtures,
    with no `gh` call. Exit 0 on pass, 1 on failure.

.EXAMPLE
    pwsh scripts/new_issue.ps1 -Title "worker: run loop uploads progress while running" `
        -Why "..." -DoneWhen "a run left overnight shows progress.jsonl growing" -Observable "..."
    Prints the rendered body. Files nothing.

.EXAMPLE
    pwsh scripts/new_issue.ps1 -Title "..." -Why "..." -DoneWhen "..." -Observable "..." `
        -Labels "P2,area:worker" -Apply
    Files the issue for real via `gh issue create`.

.EXAMPLE
    pwsh scripts/new_issue.ps1 -SelfTest
#>

param(
    [string]$Title,
    [string]$Why = '',
    [string[]]$DoneWhen = @(),
    [string]$Observable = '',
    [string[]]$DocsTouched = @(),
    [string[]]$Tests = @(),
    [string]$OutOfScope = '',
    [string]$CloudMoney = 'none',
    [string]$Labels = '',
    [string]$Milestone = '',
    [string]$Repo = 'Raaif-Yousuf/DNA-Entropy-Graph',
    [switch]$Apply,
    [switch]$WhatIf,
    [switch]$SelfTest,
    [switch]$Help
)

$ErrorActionPreference = 'Stop'

function Write-UsageError {
    param([Parameter(Mandatory)][string]$Message)
    [Console]::Error.WriteLine($Message)
}

if ($Help) {
    Get-Help -Name $PSCommandPath -Full
    exit 0
}

# ---------------------------------------------------------------------------
# Title shape: warn, never block. Three real shapes exist (Appendix C section 6).
# ---------------------------------------------------------------------------

function Test-TitleShape {
    # `-cmatch` throughout: PowerShell's plain `-match` is case-INSENSITIVE,
    # which would let "DECISION: ..." satisfy the lowercase "area: phrase"
    # pattern too (since [a-z] matches upper-case letters case-insensitively)
    # and defeat the point of telling the three shapes apart at all.
    param([Parameter(Mandatory)][string]$TitleText)
    if ($TitleText -cmatch '^[a-z][a-z0-9_-]*:\s+\S') { return $true }        # "area: imperative phrase"
    if ($TitleText -cmatch '^EPIC\s+[a-z][a-z0-9_-]*:\s+\S') { return $true } # "EPIC area: noun phrase"
    if ($TitleText -cmatch '^DECISION:\s+.+\?$') { return $true }             # "DECISION: question?"
    return $false
}

# ---------------------------------------------------------------------------
# Template rendering.
# ---------------------------------------------------------------------------

function New-IssueBody {
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string]$Why,
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]]$DoneWhen,
        [Parameter(Mandatory)][AllowEmptyString()][string]$Observable,
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]]$DocsTouched,
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]]$Tests,
        [Parameter(Mandatory)][AllowEmptyString()][string]$OutOfScope,
        [Parameter(Mandatory)][AllowEmptyString()][string]$CloudMoney
    )

    $whyText = if ($Why) { $Why } else { '<one paragraph: the user-visible or money-visible problem, or the capability>' }

    $doneWhenText = if ($DoneWhen.Count -gt 0) {
        ($DoneWhen | ForEach-Object { "- [ ] $_" }) -join "`n"
    } else {
        "- [ ] <falsifiable: a person runs ONE command or opens ONE screen and says yes or no>"
    }

    $observableText = if ($Observable) { $Observable } else {
        '<the single thing that differs between "this works" and "this compiles, passes tests and does nothing">'
    }

    $docsText = if ($DocsTouched.Count -gt 0) {
        ($DocsTouched | ForEach-Object { "- [ ] ``$_``" }) -join "`n"
    } else {
        "- [ ] *(none -- confirm this issue truly touches no docs before filing)*"
    }

    $testsText = if ($Tests.Count -gt 0) {
        ($Tests | ForEach-Object { "- $_" }) -join "`n"
    } else {
        "- <``app/tests/...`` or ``worker/tests/test_<module>.py``: what it asserts>`n" +
        "- ToTest row needed? <no, because ... | yes: Needs = app-dev | installer | gpu-vm | cpu-vm | two-accounts | local-gpu>"
    }

    $outOfScopeText = if ($OutOfScope) { $OutOfScope } else {
        '<what this issue deliberately does not do, with the issue number that does>'
    }

    return @"
## Why
$whyText

## Done when
$doneWhenText

## Observable that proves it is wired
$observableText

## Docs touched
$docsText

## Tests
$testsText

## Out of scope
$outOfScopeText

## Cloud money
$CloudMoney
"@
}

# ---------------------------------------------------------------------------
# Self-test
# ---------------------------------------------------------------------------

function Invoke-SelfTest {
    $failures = 0
    function Check($label, $condition) {
        if ($condition) { Write-Host "ok    $label" } else { Write-Host "FAIL  $label"; $script:failures++ }
    }

    Check "an 'area: phrase' title is recognised" (Test-TitleShape 'worker: run loop uploads progress')
    Check "an 'EPIC area: phrase' title is recognised" (Test-TitleShape 'EPIC cloud: preflight and quota')
    Check "a 'DECISION: question?' title is recognised" (Test-TitleShape 'DECISION: should X happen?')
    Check "a title with no area prefix is NOT recognised" (-not (Test-TitleShape 'fix the thing'))
    Check "a DECISION title missing the trailing '?' is NOT recognised" (-not (Test-TitleShape 'DECISION: should X happen'))

    $full = New-IssueBody -Why 'Users cannot do X.' -DoneWhen @('a runs shows Y', 'Z is true') `
        -Observable 'the history row appears' -DocsTouched @('docs/a.md') -Tests @('worker/tests/test_x.py: asserts Y') `
        -OutOfScope 'does not do W, see #12' -CloudMoney 'none'
    Check "a fully-specified body includes the Why text" ($full -match 'Users cannot do X\.')
    Check "a fully-specified body renders every Done-when item as a checklist line" `
        ($full -match '- \[ \] a runs shows Y' -and $full -match '- \[ \] Z is true')
    Check "a fully-specified body includes Docs touched" ($full -match '`docs/a\.md`')
    Check "a fully-specified body includes Tests" ($full -match 'worker/tests/test_x\.py')
    Check "a fully-specified body includes Out of scope and Cloud money" `
        ($full -match 'does not do W, see #12' -and $full -match '## Cloud money\r?\nnone')

    $empty = New-IssueBody -Why '' -DoneWhen @() -Observable '' -DocsTouched @() -Tests @() -OutOfScope '' -CloudMoney 'none'
    Check "an unfilled section renders a visible placeholder, never a blank line pretending to be filled in" `
        ($empty -match '<one paragraph' -and $empty -match '<falsifiable')
    Check "every one of the seven required headings is present even with nothing supplied" (
        ($empty -match '## Why') -and ($empty -match '## Done when') -and
        ($empty -match '## Observable that proves it is wired') -and ($empty -match '## Docs touched') -and
        ($empty -match '## Tests') -and ($empty -match '## Out of scope') -and ($empty -match '## Cloud money')
    )

    if ($failures -eq 0) {
        Write-Host "`nPASS: new_issue self-test"
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

if (-not $Title) { Write-UsageError "-Title is required (see -Help)."; exit 2 }
if ($DoneWhen.Count -eq 0) {
    Write-UsageError "-DoneWhen needs at least one item -- an issue with no acceptance criteria is what 'needs-criteria' is for; file it as a spike or add criteria first."
    exit 2
}

if (-not (Test-TitleShape $Title)) {
    Write-Host "WARNING: '$Title' does not match '<area>: <phrase>', 'EPIC <area>: <phrase>', or 'DECISION: <question>?'. Filing anyway; fix the title by hand if this was not intentional." -ForegroundColor Yellow
}

$body = New-IssueBody -Why $Why -DoneWhen $DoneWhen -Observable $Observable -DocsTouched $DocsTouched `
    -Tests $Tests -OutOfScope $OutOfScope -CloudMoney $CloudMoney

$effectiveApply = $Apply -and -not $WhatIf

if (-not $effectiveApply) {
    Write-Host "[dry-run] would file against ${Repo}:"
    Write-Host "  title:     $Title"
    if ($Labels) { Write-Host "  labels:    $Labels" }
    if ($Milestone) { Write-Host "  milestone: $Milestone" }
    Write-Host "`n--- body ---`n"
    Write-Host $body
    exit 0
}

$tmpFile = New-TemporaryFile
try {
    Set-Content -LiteralPath $tmpFile -Value $body
    $ghArgs = @('issue', 'create', '--repo', $Repo, '--title', $Title, '--body-file', $tmpFile.FullName)
    if ($Labels) { $ghArgs += @('--label', $Labels) }
    if ($Milestone) { $ghArgs += @('--milestone', $Milestone) }
    $out = gh @ghArgs 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-UsageError "gh issue create failed: $out"
        exit 1
    }
    Write-Host $out
} finally {
    Remove-Item -LiteralPath $tmpFile -Force -ErrorAction SilentlyContinue
}
exit 0
