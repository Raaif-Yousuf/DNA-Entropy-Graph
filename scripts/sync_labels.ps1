<#
.SYNOPSIS
    Applies .github/labels.yml (this repo's label source of truth) to GitHub.

.DESCRIPTION
    .github/labels.yml names every label this repo uses -- its color and its
    description -- and this script is what makes that file mean something
    instead of just documenting intent. It reads the file, reads GitHub's
    current label set via `gh api`, and reports (or applies) exactly three
    kinds of change: CREATE a label the file names but GitHub doesn't have,
    UPDATE a label whose color or description drifted from the file, and
    PRUNE a label GitHub has that the file doesn't name (only when asked).

    A label whose color and description already match the file is left
    alone -- untouched, not re-sent -- which is what makes a second run with
    no changes to labels.yml a genuine no-op, not just a script that reports
    "nothing to do" while still making 22 API calls to say so.

    Never a hand-rolled HTTP client: every GitHub call goes through `gh api`,
    so auth, retries-on-transient-failure and pagination are `gh`'s problem,
    not this script's.

.PARAMETER Apply
    Without this switch, the script only reports what it would do (the
    default is a dry run). With it, CREATE and UPDATE actions are sent to
    GitHub. Pruning still additionally requires -Prune.

.PARAMETER Prune
    Also consider labels GitHub has that .github/labels.yml does not list.
    Without -Apply, this only changes the dry-run report (PRUNE lines
    instead of a one-line "N unmanaged label(s)" note). With -Apply, a
    pruneable label is actually deleted -- UNLESS it is still applied to at
    least one issue or pull request, in which case it is refused and
    reported, not silently skipped: see -Force.

.PARAMETER Force
    Only meaningful with -Apply -Prune: delete a label even though it is
    still applied to at least one issue or pull request. Without -Force, an
    in-use label is never deleted, because the label existing on real issues
    is exactly the case where deleting it silently loses information (which
    issues used to carry it becomes unrecoverable from the API afterward).

.PARAMETER WhatIf
    Force dry-run reporting even if -Apply was also passed. Named to match
    the PowerShell convention (and issue #29's own "sync_labels.ps1 -WhatIf
    shows no drift" test line) without adopting SupportsShouldProcess's
    per-item confirmation prompts, which would be the wrong shape for a
    batch label sync.

.PARAMETER Repo
    "<owner>/<repo>" to sync. Defaults to this repo.

.PARAMETER LabelsFile
    Path to the source-of-truth YAML. Defaults to .github/labels.yml next to
    this script's own repo root.

.PARAMETER SelfTest
    Exercise the YAML reader and the diffing logic against fixtures, with no
    network access and no `gh` calls at all. Exit 0 on pass, 1 on failure.

.PARAMETER ParseOnly
    Read -LabelsFile, print it as JSON, and exit -- no `gh` call, no network.
    This is what proves the real .github/labels.yml file itself parses
    correctly (name/color/description on all 22 entries, no duplicates)
    without needing GitHub reachable; scripts/tests/test_sync_labels.py uses
    it for exactly that.

.EXAMPLE
    pwsh scripts/sync_labels.ps1
    Dry run: prints the diff against the real repo, changes nothing, exits
    1 if anything would change (0 if the repo already matches the file).

.EXAMPLE
    pwsh scripts/sync_labels.ps1 -Apply
    Creates and recolors/redescribes labels to match .github/labels.yml.
    Never deletes anything.

.EXAMPLE
    pwsh scripts/sync_labels.ps1 -Apply -Prune
    As above, and also deletes any label not listed in the file, except one
    still applied to a real issue or PR.

.EXAMPLE
    pwsh scripts/sync_labels.ps1 -Apply -Prune -Force
    As above, and deletes an in-use label too.

.EXAMPLE
    pwsh scripts/sync_labels.ps1 -SelfTest
    Proves the parser and the diff logic work, without touching GitHub.
#>

param(
    [switch]$Apply,
    [switch]$Prune,
    [switch]$Force,
    [switch]$WhatIf,
    [switch]$SelfTest,
    [switch]$ParseOnly,
    [switch]$Help,
    [string]$Repo = 'Raaif-Yousuf/DNA-Entropy-Graph',
    [string]$LabelsFile = (Join-Path (Split-Path -Parent $PSScriptRoot) '.github' 'labels.yml')
)

$ErrorActionPreference = 'Stop'

if ($Help) {
    Get-Help -Name $PSCommandPath -Full
    exit 0
}

# ---------------------------------------------------------------------------
# .github/labels.yml reader. Deliberately hand-rolled rather than a YAML
# module dependency: the file's own shape is fixed and simple (a flat list
# of `- name:` / `  color:` / `  description:` entries, values optionally
# double- or single-quoted, comments and blank lines ignored), and that is
# the only shape this script needs to understand.
# ---------------------------------------------------------------------------

function ConvertFrom-YamlScalar {
    param([Parameter(Mandatory)][string]$Value)
    $v = $Value.Trim()
    if ($v.Length -ge 2) {
        $first = $v.Substring(0, 1)
        $last = $v.Substring($v.Length - 1, 1)
        if (($first -eq '"' -and $last -eq '"') -or ($first -eq "'" -and $last -eq "'")) {
            $v = $v.Substring(1, $v.Length - 2)
        }
    }
    return $v
}

function Read-LabelsYaml {
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "labels file not found: $Path"
    }

    $labels = [System.Collections.Generic.List[object]]::new()
    $current = $null

    foreach ($line in (Get-Content -LiteralPath $Path)) {
        $trimmed = $line.Trim()
        if ([string]::IsNullOrWhiteSpace($trimmed) -or $trimmed.StartsWith('#')) {
            continue
        }
        if ($line -match '^- name:\s*(.+?)\s*$') {
            if ($null -ne $current) { $labels.Add($current) }
            $current = [pscustomobject]@{
                Name        = ConvertFrom-YamlScalar $Matches[1]
                Color       = $null
                Description = $null
            }
            continue
        }
        if ($null -eq $current) {
            continue  # a stray property line before any `- name:` -- ignore
        }
        if ($line -match '^\s+color:\s*(.+?)\s*$') {
            $current.Color = ConvertFrom-YamlScalar $Matches[1]
            continue
        }
        if ($line -match '^\s+description:\s*(.+?)\s*$') {
            $current.Description = ConvertFrom-YamlScalar $Matches[1]
            continue
        }
    }
    if ($null -ne $current) { $labels.Add($current) }

    foreach ($label in $labels) {
        if ([string]::IsNullOrWhiteSpace($label.Name) -or
            [string]::IsNullOrWhiteSpace($label.Color) -or
            [string]::IsNullOrWhiteSpace($label.Description)) {
            throw "malformed entry in ${Path}: $($label | ConvertTo-Json -Compress) -- every label needs name, color and description"
        }
    }
    $dup = $labels | Group-Object -Property { $_.Name.ToLowerInvariant() } | Where-Object { $_.Count -gt 1 }
    if ($dup) {
        throw "duplicate label name(s) in ${Path}: $($dup.Name -join ', ')"
    }

    return $labels
}

# ---------------------------------------------------------------------------
# The plan: desired (from the file) vs. current (from GitHub) -> one of
# Create / Update / UpToDate / Prune / Unmanaged per label. Pure and
# network-free on purpose, so -SelfTest can exercise it directly.
# ---------------------------------------------------------------------------

function Get-LabelPlan {
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Desired,
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Current,
        [switch]$Prune
    )

    $plan = [System.Collections.Generic.List[object]]::new()
    $byName = @{}
    foreach ($c in $Current) { $byName[$c.Name.ToLowerInvariant()] = $c }

    foreach ($d in $Desired) {
        $key = $d.Name.ToLowerInvariant()
        if ($byName.ContainsKey($key)) {
            $c = $byName[$key]
            $colorDiffers = ($c.Color.ToLowerInvariant() -ne $d.Color.ToLowerInvariant())
            $descDiffers = ($c.Description -ne $d.Description)
            if ($colorDiffers -or $descDiffers) {
                $plan.Add([pscustomobject]@{
                    Action = 'Update'; Name = $d.Name; Color = $d.Color; Description = $d.Description
                    OldColor = $c.Color; OldDescription = $c.Description
                })
            } else {
                $plan.Add([pscustomobject]@{ Action = 'UpToDate'; Name = $d.Name; Color = $d.Color; Description = $d.Description })
            }
            $byName.Remove($key)
        } else {
            $plan.Add([pscustomobject]@{ Action = 'Create'; Name = $d.Name; Color = $d.Color; Description = $d.Description })
        }
    }

    foreach ($remaining in $byName.Values) {
        $action = if ($Prune) { 'Prune' } else { 'Unmanaged' }
        $plan.Add([pscustomobject]@{ Action = $action; Name = $remaining.Name; Color = $remaining.Color; Description = $remaining.Description })
    }

    return $plan
}

# ---------------------------------------------------------------------------
# GitHub, entirely through `gh api`. No hand-rolled HTTP client, no token
# handling here: `gh` already carries auth, retry and pagination.
# ---------------------------------------------------------------------------

function Get-RemoteLabels {
    param([Parameter(Mandatory)][string]$Repo)
    $raw = gh api "repos/$Repo/labels" --paginate 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "gh api failed listing labels for ${Repo}: $raw"
    }
    $json = $raw | ConvertFrom-Json
    return @($json | ForEach-Object {
        [pscustomobject]@{ Name = $_.name; Color = $_.color; Description = $_.description }
    })
}

function New-RemoteLabel {
    param([string]$Repo, [string]$Name, [string]$Color, [string]$Description)
    $out = gh api "repos/$Repo/labels" -X POST -f "name=$Name" -f "color=$Color" -f "description=$Description" 2>&1
    if ($LASTEXITCODE -ne 0) { throw "failed to create label '$Name': $out" }
}

function Update-RemoteLabel {
    param([string]$Repo, [string]$Name, [string]$Color, [string]$Description)
    $encoded = [System.Uri]::EscapeDataString($Name)
    $out = gh api "repos/$Repo/labels/$encoded" -X PATCH -f "color=$Color" -f "description=$Description" 2>&1
    if ($LASTEXITCODE -ne 0) { throw "failed to update label '$Name': $out" }
}

function Remove-RemoteLabel {
    param([string]$Repo, [string]$Name)
    $encoded = [System.Uri]::EscapeDataString($Name)
    $out = gh api "repos/$Repo/labels/$encoded" -X DELETE 2>&1
    if ($LASTEXITCODE -ne 0) { throw "failed to delete label '$Name': $out" }
}

function Test-LabelInUse {
    # True if at least one issue or PR (open or closed) currently carries
    # this label. Deleting a label GitHub still has attached to real issues
    # is the one mistake -Prune must not make silently; see -Force.
    param([string]$Repo, [string]$Name)
    $encoded = [System.Uri]::EscapeDataString($Name)
    $raw = gh api "repos/$Repo/issues?labels=$encoded&state=all&per_page=1" 2>&1
    if ($LASTEXITCODE -ne 0) { throw "failed to check whether label '$Name' is in use: $raw" }
    $json = $raw | ConvertFrom-Json
    return (@($json).Count -gt 0)
}

# ---------------------------------------------------------------------------
# Self-test: the parser and the diff logic, no network at all.
# ---------------------------------------------------------------------------

function Invoke-SelfTest {
    $failures = 0

    $fixture = @'
# a comment line, and a blank line above should both be ignored

- name: P0
  color: "7B0000"
  description: "Blocks all other work"
- name: area:app
  color: 1D76DB
  description: C# app work
'@
    $tmp = [System.IO.Path]::GetTempFileName()
    try {
        Set-Content -LiteralPath $tmp -Value $fixture -NoNewline
        $parsed = Read-LabelsYaml -Path $tmp
    } finally {
        Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
    }

    if ($parsed.Count -ne 2) {
        Write-Host "FAIL Read-LabelsYaml: expected 2 labels, got $($parsed.Count)"
        $failures++
    } elseif ($parsed[0].Name -ne 'P0' -or $parsed[0].Color -ne '7B0000' -or $parsed[0].Description -ne 'Blocks all other work') {
        Write-Host "FAIL Read-LabelsYaml: quoted-value label parsed wrong: $($parsed[0] | ConvertTo-Json -Compress)"
        $failures++
    } elseif ($parsed[1].Name -ne 'area:app' -or $parsed[1].Color -ne '1D76DB' -or $parsed[1].Description -ne 'C# app work') {
        Write-Host "FAIL Read-LabelsYaml: unquoted-value label parsed wrong: $($parsed[1] | ConvertTo-Json -Compress)"
        $failures++
    } else {
        Write-Host "ok    Read-LabelsYaml parses quoted and unquoted scalars, skips comments/blanks"
    }

    $badFixture = "- name: only-a-name`n"
    $tmpBad = [System.IO.Path]::GetTempFileName()
    try {
        Set-Content -LiteralPath $tmpBad -Value $badFixture -NoNewline
        $threw = $false
        try { Read-LabelsYaml -Path $tmpBad | Out-Null } catch { $threw = $true }
        if ($threw) {
            Write-Host "ok    Read-LabelsYaml refuses an entry missing color/description"
        } else {
            Write-Host "FAIL Read-LabelsYaml: accepted an entry with no color/description"
            $failures++
        }
    } finally {
        Remove-Item -LiteralPath $tmpBad -Force -ErrorAction SilentlyContinue
    }

    $desired = @([pscustomobject]@{ Name = 'new-label'; Color = 'ABCDEF'; Description = 'desc' })

    $plan = Get-LabelPlan -Desired $desired -Current @()
    if ($plan.Count -eq 1 -and $plan[0].Action -eq 'Create') {
        Write-Host "ok    plan creates a label GitHub does not have"
    } else {
        Write-Host "FAIL plan: expected a single Create action"
        $failures++
    }

    $sameButDifferentCase = @([pscustomobject]@{ Name = 'new-label'; Color = 'abcdef'; Description = 'desc' })
    $plan = Get-LabelPlan -Desired $desired -Current $sameButDifferentCase
    if ($plan.Count -eq 1 -and $plan[0].Action -eq 'UpToDate') {
        Write-Host "ok    plan treats a same-value, different-case color as up to date (idempotent)"
    } else {
        Write-Host "FAIL plan: expected UpToDate for a case-only color difference, got $($plan[0].Action)"
        $failures++
    }

    $drifted = @([pscustomobject]@{ Name = 'new-label'; Color = '000000'; Description = 'old desc' })
    $plan = Get-LabelPlan -Desired $desired -Current $drifted
    if ($plan.Count -eq 1 -and $plan[0].Action -eq 'Update') {
        Write-Host "ok    plan updates a label whose color/description really drifted"
    } else {
        Write-Host "FAIL plan: expected Update for a real drift"
        $failures++
    }

    $stray = @([pscustomobject]@{ Name = 'stray'; Color = '111111'; Description = 'not in the file' })
    $planNoPrune = Get-LabelPlan -Desired @() -Current $stray
    $planPrune = Get-LabelPlan -Desired @() -Current $stray -Prune
    if ($planNoPrune[0].Action -eq 'Unmanaged' -and $planPrune[0].Action -eq 'Prune') {
        Write-Host "ok    plan only proposes pruning a stray label when -Prune is passed"
    } else {
        Write-Host "FAIL plan: expected Unmanaged without -Prune and Prune with it"
        $failures++
    }

    if ($failures -eq 0) {
        Write-Host "`nPASS: sync_labels self-test"
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

if ($ParseOnly) {
    try {
        $desired = Read-LabelsYaml -Path $LabelsFile
    } catch {
        Write-Error $_
        exit 1
    }
    $desired | ConvertTo-Json
    exit 0
}

$effectiveApply = $Apply -and -not $WhatIf

try {
    $desired = Read-LabelsYaml -Path $LabelsFile
    $current = Get-RemoteLabels -Repo $Repo
} catch {
    Write-Error $_
    exit 1
}

$plan = Get-LabelPlan -Desired $desired -Current $current -Prune:$Prune

$creates = @($plan | Where-Object Action -eq 'Create')
$updates = @($plan | Where-Object Action -eq 'Update')
$upToDate = @($plan | Where-Object Action -eq 'UpToDate')
$prunes = @($plan | Where-Object Action -eq 'Prune')
$unmanaged = @($plan | Where-Object Action -eq 'Unmanaged')

Write-Host "sync_labels: $($desired.Count) label(s) in $LabelsFile against $Repo"
foreach ($p in $creates) {
    Write-Host "  CREATE  $($p.Name)  color=$($p.Color)  `"$($p.Description)`""
}
foreach ($p in $updates) {
    Write-Host "  UPDATE  $($p.Name)  color: $($p.OldColor) -> $($p.Color)  description: `"$($p.OldDescription)`" -> `"$($p.Description)`""
}
if ($Prune) {
    foreach ($p in $prunes) {
        Write-Host "  PRUNE   $($p.Name)"
    }
} elseif ($unmanaged.Count -gt 0) {
    Write-Host "  ($($unmanaged.Count) label(s) on GitHub not listed in ${LabelsFile}: $(($unmanaged.Name) -join ', '); pass -Prune to consider removing them)"
}
Write-Host "  $($upToDate.Count) label(s) already up to date"

$hasDrift = ($creates.Count -gt 0) -or ($updates.Count -gt 0) -or ($Prune -and $prunes.Count -gt 0)

if (-not $effectiveApply) {
    if ($Apply -and $WhatIf) {
        Write-Host "(-WhatIf overrides -Apply: reporting only, nothing was changed)"
    }
    if ($hasDrift) { exit 1 } else { exit 0 }
}

$failures = 0

foreach ($p in $creates) {
    try {
        New-RemoteLabel -Repo $Repo -Name $p.Name -Color $p.Color -Description $p.Description
        Write-Host "  created $($p.Name)"
    } catch {
        Write-Error $_
        $failures++
    }
}
foreach ($p in $updates) {
    try {
        Update-RemoteLabel -Repo $Repo -Name $p.Name -Color $p.Color -Description $p.Description
        Write-Host "  updated $($p.Name)"
    } catch {
        Write-Error $_
        $failures++
    }
}
if ($Prune) {
    foreach ($p in $prunes) {
        try {
            $inUse = Test-LabelInUse -Repo $Repo -Name $p.Name
            if ($inUse -and -not $Force) {
                Write-Warning "refusing to prune '$($p.Name)': still applied to at least one issue/PR; pass -Force to delete it anyway"
                continue
            }
            Remove-RemoteLabel -Repo $Repo -Name $p.Name
            $forcedNote = if ($inUse) { ' (forced, was in use)' } else { '' }
            Write-Host "  pruned  $($p.Name)$forcedNote"
        } catch {
            Write-Error $_
            $failures++
        }
    }
}

if ($failures -gt 0) { exit 1 }
exit 0
