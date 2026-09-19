<#
.SYNOPSIS
    Plans (and, once -Apply is real, would create) a single labelled,
    time-boxed GCP GPU VM for a worker smoke/acceptance run -- the tool Hard
    Rule 15 names as the ONLY way GPU tests run for this project.

.DESCRIPTION
    Issue #317: this path is named in CLAUDE.md (Hard Rule 15, the stack
    table, Critical Pitfalls), four skills (`working-on-gcp`, `tests-first`,
    `fixing-a-bug`, `orchestrating-agents`), three `.claude/memory/*.md`
    seed files, `.claude/README.md`, and half a dozen `docs/*.md` files, plus
    issues #83/#84/#85 which all name it as their own test mechanism -- and
    until this file, none of that pointed at anything. This is the skeleton:
    the dry-run plan, the label/cost/duration math, and the leak-check and
    interrupt-safety contract every real `-Apply` run will have to satisfy,
    all provable today with zero cloud spend.

    THE ONE RULE THIS SCRIPT WILL NOT BREAK: it is dry-run by default. With
    no `-Apply`, it prints the plan -- machine type, zone, accelerator,
    estimated cost, estimated minutes -- and creates nothing. `-Apply` is
    NOT exercised by the session that wrote this script (no GCP project is
    configured yet -- `working-on-gcp` skill: "Project | Not yet set.
    Blocked on the owner (#24)") and is UNVERIFIED: read its own section
    below before trusting it.

    WHY -Apply DOES NOT WORK YET, ON PURPOSE
    -----------------------------------------
    Appendix C's own permission design denies `gcloud` to an agent on
    purpose, "because the app has no gcloud" and because
    `scripts/cloud_gpu_test.ps1` is supposed to use the app's own
    `DnaEntropyGraph.Cloud` via the `CloudCli` console host instead. Neither
    `CloudCli` (issue #60) nor the app it is built from (`app/`, issue #61)
    exists yet. Rather than quietly falling back to a raw `gcloud` call --
    which would defeat the exact reason `gcloud` is denied to agents in the
    first place -- `-Apply` here looks for a built `CloudCli` executable and
    FAILS CLEARLY, naming #60/#61/#24, when it is not found. This is
    intentional: a script that pretends to be the real thing by quietly
    substituting `gcloud` under the hood is a worse outcome than a script
    that says plainly "the thing I am supposed to call does not exist yet".

    LABELS AND maxRunDuration (Hard Rule 10)
    -------------------------------------------
    `docs/job_contract.md` section 2 is the source of truth: every VM,
    disk, and bucket object a job touches carries `app=dna-entropy-graph`,
    `job-id=<jobId>`, `installation-id=<installationId>`, `model=<modelId>`,
    `app-version=<semver>`, `lifecycle=stop|delete|keepalive`,
    `purpose=job|smoke`. `New-LabelSet` below builds exactly that set and
    validates every value against GCP's own label-value shape
    (`^[a-z0-9_-]{1,63}$`) before anything downstream can use it, matching
    `VmSpec`'s own job (Hard Rule 10) and `scripts/hooks/
    block_unlabelled_vm_create.py`'s mechanical enforcement at the tool-call
    level. `maxRunDuration` and `instanceTerminationAction=DELETE` are
    always part of the printed plan and, per `docs/cloud_design.md` section
    8, are the BACKSTOP only -- `worker/vm/startup.sh` calling the Compute
    API on itself is the primary cleanup mechanism, because
    `instanceTerminationAction` fires only when Compute Engine itself stops
    the VM at the duration deadline, never on a guest `shutdown`.

    "NOTHING LEFT RUNNING" AND INTERRUPT SAFETY
    ----------------------------------------------
    `Invoke-LeakCheck` is the script's last real step on any `-Apply` path:
    it asserts the resource list for this run's `installation-id` is EMPTY,
    and if it is not, it prints exactly what was left, in which zone, and
    the exact command to delete it -- "always tell the user exactly how to
    stop paying" (the prototype keeper's own discipline, carried forward
    here). A half-finished `-Apply` run (Ctrl+C mid-create) is handled the
    same way: `Invoke-Apply` wraps its own body in try/finally, so even an
    interrupted run prints the labelled VM's name, its zone, and the exact
    `gcloud`/`CloudCli` command to find and delete it by hand -- printing
    that fallback command for a HUMAN to run themselves is not the same
    thing as this script shelling out to `gcloud` on its own, which is the
    thing the section above explains this script deliberately does not do.

.PARAMETER ProjectId
    The GCP project to plan (and, once real, create) against. No default:
    `working-on-gcp` records the real dev project as "Not yet set", and
    inventing one here would be worse than requiring it explicitly. Dry-run
    planning works without it (prints "NOT SET (blocked on #24)" in the
    plan); `-Apply` refuses to proceed without a real value.

.PARAMETER Zone
    A specific zone to plan against. If omitted, `Get-ZoneCandidate` picks
    the first of a small, explicitly-labelled-as-unverified candidate list
    (`working-on-gcp`: "Default zone(s) | Not yet set" -- the real zone
    ladder is issue #86's job, not this script's).

.PARAMETER MachineType
    Default `g2-standard-8` (1x L4), the project's own default tier per
    CLAUDE.md's stack table ("g2-standard-8 (1x L4) first, A100 fallback by
    click"). `-MachineType a2-highgpu-1g` (A100-40) or `a2-ultragpu-1g`
    (A100-80) select the documented fallback tiers.

.PARAMETER AcceleratorType
    Default `nvidia-l4`, paired with the default machine type above.

.PARAMETER AcceleratorCount
    Default 1.

.PARAMETER Model
    The `model` label value (e.g. `evo2_7b`). Default `evo2_7b`, the
    project's own default tier.

.PARAMETER DiskSizeGb
    Boot disk size. Default 150 (the 7B-tier default per
    `docs/cloud_design.md` section 10; the 40B tier's own default is 300).

.PARAMETER MaxRunDurationSeconds
    `scheduling.maxRunDuration`, in seconds. Default 14400 (4 hours, the
    cost model's own default; max sane value per that same section is 24
    hours / 86400 seconds -- this script warns, but does not refuse, above
    that, since a future real GPU-tier or job type may legitimately need
    more).

.PARAMETER Lifecycle
    One of `stop`, `delete`, `keepalive` -- the `lifecycle` label value.
    Default `delete`.

.PARAMETER Purpose
    One of `job`, `smoke` -- the `purpose` label value. Default `smoke`,
    since this script's own designed job (issue #83) is a GPU smoke/
    acceptance run, not an end-user job.

.PARAMETER JobId
    Override the auto-generated job id (`docs/job_contract.md`'s own
    `{yyyyMMdd}-{HHmmss}-{6 base32}` shape, UTC). Auto-generated when
    omitted.

.PARAMETER InstallationId
    Override the auto-generated installation id. In the real app this is a
    GUID generated once at first launch and persisted locally
    (`docs/job_contract.md` section 2); this script has no app to read that
    from, so it generates a fresh placeholder every run unless one is
    passed explicitly, and says so in the plan.

.PARAMETER Image
    An explicit worker container image digest override (CLAUDE.md's
    Critical Pitfalls: "the VM runs released bytes, not your working
    tree... in dev, `scripts/cloud_gpu_test.ps1 -Image <digest>` overrides
    it and says so in the log"). Recorded in the plan and, on `-Apply`,
    would be passed to `CloudCli vm create`; not validated against any
    registry by this script.

.PARAMETER Apply
    Create a real resource. UNVERIFIED as of issue #317 -- no session has
    exercised this path, because no GCP project exists yet (#24) and
    `CloudCli` does not exist yet (#60). Without a built `CloudCli`, this
    exits 3 with a message naming the blocking issues rather than doing
    anything.

.PARAMETER SelfTest
    Run against synthetic inputs (label formatting, cost/duration math, the
    dry-run contract, the -Apply-without-CloudCli failure path) and exit.
    Creates nothing, needs no GCP credentials.

.PARAMETER Help
    Show this full help text and exit 0.

.EXAMPLE
    pwsh scripts/cloud_gpu_test.ps1
    Dry-run plan against the default tier, no project set: prints the plan
    and exits 0 having created nothing.

.EXAMPLE
    pwsh scripts/cloud_gpu_test.ps1 -ProjectId my-dev-project -Zone us-central1-a
    Dry-run plan against a specific project/zone.

.EXAMPLE
    pwsh scripts/cloud_gpu_test.ps1 -SelfTest

.EXAMPLE
    pwsh scripts/cloud_gpu_test.ps1 -ProjectId my-dev-project -Apply
    UNVERIFIED. As of issue #317, exits 3: no built CloudCli to call.
#>

param(
    [string]$ProjectId = '',
    [string]$Zone = '',
    [string]$MachineType = 'g2-standard-8',
    [string]$AcceleratorType = 'nvidia-l4',
    [int]$AcceleratorCount = 1,
    [string]$Model = 'evo2_7b',
    [int]$DiskSizeGb = 150,
    [int]$MaxRunDurationSeconds = 14400,
    [ValidateSet('stop', 'delete', 'keepalive')]
    [string]$Lifecycle = 'delete',
    [ValidateSet('job', 'smoke')]
    [string]$Purpose = 'smoke',
    [string]$JobId = '',
    [string]$InstallationId = '',
    [string]$Image = '',
    [switch]$Apply,
    [switch]$SelfTest,
    [switch]$Help
)

$ErrorActionPreference = 'Stop'

# `Write-Error` throws when $ErrorActionPreference is 'Stop', which silently
# breaks a "Write-Error ...; exit N" pattern (found and fixed elsewhere this
# session). [Console]::Error.WriteLine is the safe idiom, matching
# scripts/new_issue.ps1's own Write-UsageError.
function Write-UsageError {
    param([Parameter(Mandatory)][string]$Message)
    [Console]::Error.WriteLine($Message)
}

if ($Help) {
    Get-Help -Name $PSCommandPath -Full
    exit 0
}

# ---------------------------------------------------------------------------
# Pricing and duration estimates. Both THEORY (unverified) as of 2026-09-19
# -- see docs/research/2026-09-19-gpu-pricing-and-instances.md section 3 for
# the sourcing (fetch tool could not reach either vendor's live, JS-rendered
# pricing tables; every number is cross-checked against third-party
# aggregators, never read from the vendor page directly) and
# docs/cloud_design.md section 10 for the duration figures. Record a real
# `MEASURED <date>:` value in docs/cloud_design.md once a real run happens,
# and update these constants to match rather than leaving the two to drift
# apart, per this project's own "mark theories as theories" rule (Hard Rule
# 18).
# ---------------------------------------------------------------------------

$Script:PricingTable = @{
    'g2-standard-8|nvidia-l4'    = @{ HourlyUsd = 0.85; Label = '1x NVIDIA L4' }
    'a2-highgpu-1g|nvidia-a100'  = @{ HourlyUsd = 3.67; Label = '1x NVIDIA A100 40GB' }
    'a2-ultragpu-1g|nvidia-a100-80gb' = @{ HourlyUsd = 5.07; Label = '1x NVIDIA A100 80GB' }
}
$Script:DiskUsdPerGbMonth = 0.10
$Script:DurationEstimateMinutes = 12  # fresh pull; 5 min warm per cloud_design.md section 10

function Get-PricingEstimate {
    param(
        [Parameter(Mandatory)][string]$MachineType,
        [Parameter(Mandatory)][string]$AcceleratorType,
        [Parameter(Mandatory)][int]$DiskSizeGb,
        [Parameter(Mandatory)][int]$DurationMinutes
    )
    $key = "$MachineType|$AcceleratorType"
    $entry = $Script:PricingTable[$key]
    if (-not $entry) {
        return [pscustomobject]@{
            HourlyUsd       = $null
            AcceleratorNote = "no pricing entry for '$key' -- add one to `$Script:PricingTable"
            RunCostUsd      = $null
            DiskMonthlyUsd  = [math]::Round($DiskSizeGb * $Script:DiskUsdPerGbMonth, 2)
        }
    }
    $runCost = [math]::Round(($entry.HourlyUsd / 60.0) * $DurationMinutes, 2)
    [pscustomobject]@{
        HourlyUsd       = $entry.HourlyUsd
        AcceleratorNote = $entry.Label
        RunCostUsd      = $runCost
        DiskMonthlyUsd  = [math]::Round($DiskSizeGb * $Script:DiskUsdPerGbMonth, 2)
    }
}

# ---------------------------------------------------------------------------
# Zone candidates. NOT a real zone ladder (issue #86 is that; it needs a
# live AggregatedList query against a real project this script does not
# have). A small, explicitly-labelled-candidate list so a dry-run plan has
# something concrete to show, never presented as measured or authoritative.
# ---------------------------------------------------------------------------

$Script:ZoneCandidates = @('us-central1-a', 'us-east4-a', 'us-west1-a')

function Get-ZoneCandidate {
    param([string]$Requested = '')
    if ($Requested) { return [pscustomobject]@{ Zone = $Requested; IsCandidate = $false } }
    return [pscustomobject]@{ Zone = $Script:ZoneCandidates[0]; IsCandidate = $true }
}

# ---------------------------------------------------------------------------
# Ids. docs/job_contract.md section 2's own shapes.
# ---------------------------------------------------------------------------

function New-JobId {
    $stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss')
    $alphabet = '0123456789abcdefghjkmnpqrstvwxyz'  # Crockford base32 alphabet (no i/l/o/u)
    $rand = New-Object System.Random
    $suffix = -join (1..6 | ForEach-Object { $alphabet[$rand.Next($alphabet.Length)] })
    "$stamp-$suffix"
}

function New-InstallationIdPlaceholder {
    # The real app generates this ONCE at first launch and persists it
    # locally (job_contract.md section 2). This script has no app to read
    # that from, so it mints a fresh placeholder every run -- callers who
    # want a stable value across runs pass -InstallationId explicitly.
    [guid]::NewGuid().ToString('N')
}

# ---------------------------------------------------------------------------
# Labels: docs/job_contract.md section 2's exact set, validated against
# GCP's own label-value shape before anything downstream can use them.
# ---------------------------------------------------------------------------

function Test-LabelValue {
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Value)
    # GCP label values: lowercase letters, digits, underscore, hyphen; 1-63
    # chars. Empty is invalid for every label this script emits -- every one
    # of the seven is required (job_contract.md section 2), so an empty
    # value here is always a bug, never a legitimate "no label" state.
    return ($Value -cmatch '^[a-z0-9_-]{1,63}$')
}

function New-LabelSet {
    param(
        [Parameter(Mandatory)][string]$JobId,
        [Parameter(Mandatory)][string]$InstallationId,
        [Parameter(Mandatory)][string]$Model,
        [Parameter(Mandatory)][string]$AppVersion,
        [Parameter(Mandatory)][string]$Lifecycle,
        [Parameter(Mandatory)][string]$Purpose
    )
    $labels = [ordered]@{
        'app'             = 'dna-entropy-graph'
        'job-id'          = $JobId.ToLowerInvariant()
        'installation-id' = $InstallationId.ToLowerInvariant()
        'model'           = $Model.ToLowerInvariant()
        # GCP label values disallow '.', which a semver always has; '_' is
        # the closest allowed character and is what every other dotted value
        # here would also need if one ever appeared. The full semver stays
        # available in the plan's own -Image/version text elsewhere; only
        # the LABEL value is sanitised.
        'app-version'     = ($AppVersion.ToLowerInvariant() -replace '\.', '_')
        'lifecycle'       = $Lifecycle.ToLowerInvariant()
        'purpose'         = $Purpose.ToLowerInvariant()
    }
    $bad = @($labels.Keys | Where-Object { -not (Test-LabelValue $labels[$_]) })
    if ($bad.Count -gt 0) {
        throw "invalid label value(s) for: $($bad -join ', ') (GCP label values must match ^[a-z0-9_-]{1,63}$)"
    }
    return $labels
}

function Get-WorkerAppVersion {
    # No app/ yet (issue #61), so there is no real app-version to read.
    # worker/pyproject.toml's own version, suffixed to say plainly this is
    # a stand-in, is closer to the truth than a made-up semver.
    $pyproject = Join-Path $PSScriptRoot '..' 'worker' 'pyproject.toml'
    if (Test-Path $pyproject) {
        $match = Select-String -Path $pyproject -Pattern '^version\s*=\s*"([^"]+)"' | Select-Object -First 1
        if ($match) { return "$($match.Matches[0].Groups[1].Value)-dev" }
    }
    return '0.0.0-dev'
}

# ---------------------------------------------------------------------------
# The plan: everything -Apply would need, printed, never executed unless
# -Apply is passed.
# ---------------------------------------------------------------------------

function Write-Plan {
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string]$ProjectId,
        [Parameter(Mandatory)][pscustomobject]$ZoneChoice,
        [Parameter(Mandatory)][string]$MachineType,
        [Parameter(Mandatory)][string]$AcceleratorType,
        [Parameter(Mandatory)][int]$AcceleratorCount,
        [Parameter(Mandatory)][pscustomobject]$Pricing,
        [Parameter(Mandatory)][int]$DurationMinutes,
        [Parameter(Mandatory)][int]$DiskSizeGb,
        [Parameter(Mandatory)][int]$MaxRunDurationSeconds,
        [Parameter(Mandatory)][System.Collections.Specialized.OrderedDictionary]$Labels,
        [string]$Image = ''
    )
    Write-Host '=== scripts/cloud_gpu_test.ps1 plan (dry run unless -Apply) ==='
    Write-Host "  project:              $(if ($ProjectId) { $ProjectId } else { 'NOT SET (blocked on #24 -- working-on-gcp skill)' })"
    Write-Host "  zone:                 $($ZoneChoice.Zone)$(if ($ZoneChoice.IsCandidate) { '  [candidate, unverified -- no real zone ladder yet, issue #86]' })"
    Write-Host "  machine type:         $MachineType"
    Write-Host "  accelerator:          ${AcceleratorCount}x $AcceleratorType $(if ($Pricing.AcceleratorNote) { "($($Pricing.AcceleratorNote))" })"
    Write-Host "  boot disk:            ${DiskSizeGb} GB pd-balanced"
    if ($null -ne $Pricing.HourlyUsd) {
        Write-Host ("  estimated hourly:     `${0:N2}/h  [THEORY (unverified) 2026-09-19, docs/research/2026-09-19-gpu-pricing-and-instances.md]" -f $Pricing.HourlyUsd)
        Write-Host ("  estimated minutes:    ~$DurationMinutes min  [docs/cloud_design.md section 10, fresh-pull figure]")
        Write-Host ("  estimated run cost:   `${0:N2}  (hourly rate x estimated minutes)" -f $Pricing.RunCostUsd)
    } else {
        Write-Host "  estimated cost:       $($Pricing.AcceleratorNote)"
    }
    Write-Host ("  disk cost (ongoing):  `${0:N2}/month while the disk exists, running or not" -f $Pricing.DiskMonthlyUsd)
    Write-Host "  maxRunDuration:       ${MaxRunDurationSeconds}s"
    Write-Host "  instanceTerminationAction: DELETE (backstop only -- worker/vm/startup.sh's own self-stop/delete call is primary, docs/cloud_design.md section 8)"
    if ($Image) { Write-Host "  image override:       $Image" }
    Write-Host "  labels:"
    foreach ($key in $Labels.Keys) { Write-Host "    $key=$($Labels[$key])" }
}

# ---------------------------------------------------------------------------
# Leak check: the script's last real step. Asserts the resource list for
# this run's installation-id is empty; if not, says exactly what was left,
# where, and how to delete it -- "always tell the user exactly how to stop
# paying".
# ---------------------------------------------------------------------------

function Find-CloudCli {
    # Candidate publish locations for the not-yet-built CloudCli console
    # host (issue #60). None of these exist yet as of issue #317; this
    # function exists so the day CloudCli IS built, this script starts
    # finding it without anyone having to edit this function again.
    $candidates = @(
        (Join-Path $PSScriptRoot '..' 'app' 'artifacts' 'CloudCli' 'CloudCli.exe'),
        (Join-Path $PSScriptRoot '..' 'app' 'src' 'DnaEntropyGraph.CloudCli' 'bin' 'Release' 'net10.0' 'CloudCli.exe')
    )
    foreach ($c in $candidates) { if (Test-Path $c) { return $c } }
    $onPath = Get-Command 'CloudCli' -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }
    return $null
}

function Invoke-LeakCheck {
    param(
        [Parameter(Mandatory)][string]$InstallationId,
        [string]$CloudCliPath = ''
    )
    if (-not $CloudCliPath) {
        Write-Host "leak check: skipped -- no CloudCli found (issue #60), and nothing was created by this run to check for."
        return $true
    }
    $out = & $CloudCliPath 'resources' 'list' '--install' $InstallationId '--json' 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-UsageError "leak check: 'CloudCli resources list' failed: $out"
        return $false
    }
    $resources = @()
    try { $resources = $out | ConvertFrom-Json } catch { $resources = @() }
    if (@($resources).Count -eq 0) {
        Write-Host 'leak check: resource list is empty. Nothing left running.'
        return $true
    }
    Write-UsageError "leak check: $(@($resources).Count) resource(s) still exist for installation-id=$InstallationId :"
    foreach ($r in $resources) {
        Write-UsageError "  $($r.name) in $($r.zone) -- delete with: gcloud compute instances delete $($r.name) --zone=$($r.zone) --quiet"
    }
    return $false
}

# ---------------------------------------------------------------------------
# -Apply. UNVERIFIED (issue #317): no session has exercised this path.
# Refuses to fall back to a raw `gcloud` call -- see the module docstring's
# "WHY -Apply DOES NOT WORK YET, ON PURPOSE" section.
# ---------------------------------------------------------------------------

function Invoke-Apply {
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string]$ProjectId,
        [Parameter(Mandatory)][string]$Zone,
        [Parameter(Mandatory)][string]$MachineType,
        [Parameter(Mandatory)][string]$AcceleratorType,
        [Parameter(Mandatory)][int]$AcceleratorCount,
        [Parameter(Mandatory)][int]$DiskSizeGb,
        [Parameter(Mandatory)][int]$MaxRunDurationSeconds,
        [Parameter(Mandatory)][System.Collections.Specialized.OrderedDictionary]$Labels,
        [Parameter(Mandatory)][string]$JobId,
        [string]$Image = ''
    )
    if (-not $ProjectId) {
        Write-UsageError "-Apply requires -ProjectId (working-on-gcp skill: no default project is set; blocked on #24)."
        exit 2
    }
    $cloudCli = Find-CloudCli
    if (-not $cloudCli) {
        Write-UsageError "-Apply cannot proceed: no built CloudCli found (issue #60; CloudCli is built from app/, issue #61)."
        Write-UsageError "This script deliberately does not fall back to a raw 'gcloud' call -- see this file's own"
        Write-UsageError "module docstring, 'WHY -Apply DOES NOT WORK YET, ON PURPOSE'. Build CloudCli first."
        exit 3
    }

    $vmName = "deg-$JobId"
    $labelArg = ($Labels.Keys | ForEach-Object { "$_=$($Labels[$_])" }) -join ','
    $ghArgs = @(
        'vm', 'create', $vmName,
        '--project', $ProjectId, '--zone', $Zone, '--machine-type', $MachineType,
        '--accelerator-type', $AcceleratorType, '--accelerator-count', $AcceleratorCount,
        '--disk-size-gb', $DiskSizeGb,
        '--labels', $labelArg,
        '--max-run-duration', "${MaxRunDurationSeconds}s",
        '--instance-termination-action', 'DELETE'
    )
    if ($Image) { $ghArgs += @('--image', $Image) }

    # try/finally so even a Ctrl+C (or a real failure) mid-create prints how
    # to find and delete the VM by hand -- a half-finished -Apply run leaves
    # a LABELLED VM (Hard Rule 10 is satisfied even by a failed run, because
    # New-LabelSet validated the labels before this function was ever
    # called), so the fallback command below is always accurate.
    try {
        Write-Host "Creating $vmName via $cloudCli ..."
        & $cloudCli @ghArgs
        if ($LASTEXITCODE -ne 0) { throw "CloudCli vm create exited $LASTEXITCODE" }
        Write-Host "$vmName created. Polling for the job to finish is not yet implemented (issue #83)."
    } finally {
        Write-Host ''
        Write-Host "If $vmName exists and this run did not clean it up:"
        Write-Host "  find it:   gcloud compute instances list --project=$ProjectId --filter=`"labels.job-id=$JobId`""
        Write-Host "  delete it: gcloud compute instances delete $vmName --project=$ProjectId --zone=$Zone --quiet"
        Write-Host "(these are for YOU to run by hand -- an agent session does not shell out to gcloud directly, Appendix C)"
    }
}

# ---------------------------------------------------------------------------
# Self-test.
# ---------------------------------------------------------------------------

function Invoke-SelfTest {
    # $Script:SelfTestFailures (not a bare local $failures): a nested
    # function's `$script:` scope modifier resolves to the FILE's script
    # scope, not its enclosing function's local scope, so a plain
    # `$failures = 0` local here plus `$script:failures++` inside Check
    # silently increments a DIFFERENT variable than the one checked below --
    # every failure prints "FAIL" but the verdict still reads 0 and reports
    # PASS. That exact bug was caught here by temporarily breaking a real
    # assertion and watching this self-test report PASS anyway; the same
    # shape exists in scripts/new_issue.ps1 as committed and is fixed there
    # too, same reasoning, in the same round.
    $Script:SelfTestFailures = 0
    function Check($label, $condition) {
        if ($condition) { Write-Host "ok    $label" } else { Write-Host "FAIL  $label"; $Script:SelfTestFailures++ }
    }

    # Labels
    $labels = New-LabelSet -JobId '20260919-120000-ab3xy9' -InstallationId 'inst123' `
        -Model 'evo2_7b' -AppVersion '0.0.1-dev' -Lifecycle 'delete' -Purpose 'smoke'
    Check "New-LabelSet includes every required label key" (
        $labels.Contains('app') -and $labels.Contains('job-id') -and $labels.Contains('installation-id') -and
        $labels.Contains('model') -and $labels.Contains('app-version') -and $labels.Contains('lifecycle') -and
        $labels.Contains('purpose')
    )
    Check "New-LabelSet's app label is the fixed discovery constant" ($labels['app'] -eq 'dna-entropy-graph')
    Check "Test-LabelValue accepts a real label value" (Test-LabelValue 'evo2_7b')
    Check "Test-LabelValue rejects an empty value" (-not (Test-LabelValue ''))
    Check "Test-LabelValue rejects uppercase (GCP labels are lowercase only)" (-not (Test-LabelValue 'Evo2_7B'))
    Check "Test-LabelValue rejects a value over 63 chars" (-not (Test-LabelValue ('a' * 64)))
    $threw = $false
    try { New-LabelSet -JobId '' -InstallationId 'x' -Model 'x' -AppVersion 'x' -Lifecycle 'delete' -Purpose 'smoke' | Out-Null }
    catch { $threw = $true }
    Check "New-LabelSet throws on an invalid (empty) label value rather than emitting it" $threw

    # Ids
    $jobId = New-JobId
    Check "New-JobId matches docs/job_contract.md's own {yyyyMMdd}-{HHmmss}-{6 base32} shape" `
        ($jobId -cmatch '^\d{8}-\d{6}-[0-9a-hjkmnp-tv-z]{6}$')
    $inst = New-InstallationIdPlaceholder
    Check "New-InstallationIdPlaceholder produces a label-valid value" (Test-LabelValue $inst)

    # Zone candidates
    $z1 = Get-ZoneCandidate -Requested 'europe-west4-a'
    Check "Get-ZoneCandidate honours an explicit -Requested zone and marks it non-candidate" `
        ($z1.Zone -eq 'europe-west4-a' -and $z1.IsCandidate -eq $false)
    $z2 = Get-ZoneCandidate -Requested ''
    Check "Get-ZoneCandidate falls back to a candidate zone, marked as such" `
        ($Script:ZoneCandidates -contains $z2.Zone -and $z2.IsCandidate -eq $true)

    # Pricing math
    $p = Get-PricingEstimate -MachineType 'g2-standard-8' -AcceleratorType 'nvidia-l4' -DiskSizeGb 150 -DurationMinutes 12
    Check "Get-PricingEstimate finds the default L4 tier's hourly rate" ($p.HourlyUsd -eq 0.85)
    Check "Get-PricingEstimate's run cost is (hourly/60)*minutes, rounded" ($p.RunCostUsd -eq [math]::Round((0.85/60.0)*12, 2))
    Check "Get-PricingEstimate's disk cost is diskGb * usd-per-gb-month" ($p.DiskMonthlyUsd -eq 15.0)
    $pUnknown = Get-PricingEstimate -MachineType 'made-up' -AcceleratorType 'made-up' -DiskSizeGb 150 -DurationMinutes 12
    Check "Get-PricingEstimate reports (not crashes) on an unknown machine/accelerator pair" ($null -eq $pUnknown.HourlyUsd)

    # App version fallback never throws even if worker/pyproject.toml is unreachable from a weird cwd
    $ver = Get-WorkerAppVersion
    Check "Get-WorkerAppVersion returns a non-empty stand-in version" ([string]::IsNullOrEmpty($ver) -eq $false)

    # Dry-run end to end: no -Apply, must create nothing and exit 0, and the
    # plan text must show up.
    # A REAL child `pwsh` process, not `& $PSCommandPath` in-process: this
    # script's error paths use [Console]::Error.WriteLine (the safe idiom
    # for "print and exit N" under $ErrorActionPreference='Stop' -- see
    # Write-UsageError's own comment), which writes straight to the OS-level
    # stderr handle and is invisible to PowerShell's in-process `2>&1`/`6>&1`
    # stream redirection. A real external process's stderr IS a redirectable
    # OS handle, so only a genuine subprocess capture sees that text -- the
    # same reason scripts/tests/test_hooks.py drives the Python hooks via
    # subprocess.run rather than importing and calling them in-process.
    $dryOut = & pwsh -NoProfile -File $PSCommandPath -ProjectId '' 2>&1 | Out-String
    Check "a plain dry run exits 0" ($LASTEXITCODE -eq 0)
    Check "a plain dry run's output names the plan header" ($dryOut -match '=== scripts/cloud_gpu_test\.ps1 plan')
    Check "a plain dry run's output shows every required label key" (
        $dryOut -match 'app=dna-entropy-graph' -and $dryOut -match 'job-id=' -and $dryOut -match 'installation-id=' -and
        $dryOut -match 'model=' -and $dryOut -match 'app-version=' -and $dryOut -match 'lifecycle=' -and $dryOut -match 'purpose='
    )
    Check "a plain dry run's output names the unset project honestly" ($dryOut -match 'NOT SET \(blocked on #24')

    # -Apply without a project refuses before ever looking for CloudCli.
    & pwsh -NoProfile -File $PSCommandPath -Apply 2>&1 | Out-Null
    Check "-Apply with no -ProjectId exits 2 (usage error, not a crash)" ($LASTEXITCODE -eq 2)

    # -Apply with a project but no built CloudCli fails clearly, citing the
    # blocking issues, rather than falling back to gcloud. This is the one
    # real assertion this self-test makes about the -Apply path -- it does
    # NOT and cannot prove -Apply's real creation logic, because nothing
    # this session can reach has a built CloudCli to call.
    $applyOut = & pwsh -NoProfile -File $PSCommandPath -ProjectId 'fake-test-project' -Apply 2>&1 | Out-String
    Check "-Apply with a project but no CloudCli exits 3" ($LASTEXITCODE -eq 3)
    Check "-Apply's failure names the blocking issues (#60/#61)" ($applyOut -match '#60' -and $applyOut -match '#61')
    Check "-Apply's failure explicitly refuses the gcloud fallback" ($applyOut -match 'does not fall back to a raw')

    if ($Script:SelfTestFailures -eq 0) {
        Write-Host "`nPASS: cloud_gpu_test self-test"
        return $true
    }
    Write-Host "`nFAIL: $($Script:SelfTestFailures) self-test failure(s)"
    return $false
}

# ---------------------------------------------------------------------------
# Entry point
# ---------------------------------------------------------------------------

if ($SelfTest) {
    if (Invoke-SelfTest) { exit 0 } else { exit 1 }
}

$effectiveJobId = if ($JobId) { $JobId } else { New-JobId }
$effectiveInstallationId = if ($InstallationId) { $InstallationId } else { New-InstallationIdPlaceholder }
$effectiveAppVersion = Get-WorkerAppVersion
$zoneChoice = Get-ZoneCandidate -Requested $Zone

$labels = New-LabelSet -JobId $effectiveJobId -InstallationId $effectiveInstallationId `
    -Model $Model -AppVersion $effectiveAppVersion -Lifecycle $Lifecycle -Purpose $Purpose

$pricing = Get-PricingEstimate -MachineType $MachineType -AcceleratorType $AcceleratorType `
    -DiskSizeGb $DiskSizeGb -DurationMinutes $Script:DurationEstimateMinutes

Write-Plan -ProjectId $ProjectId -ZoneChoice $zoneChoice -MachineType $MachineType `
    -AcceleratorType $AcceleratorType -AcceleratorCount $AcceleratorCount -Pricing $pricing `
    -DurationMinutes $Script:DurationEstimateMinutes -DiskSizeGb $DiskSizeGb `
    -MaxRunDurationSeconds $MaxRunDurationSeconds -Labels $labels -Image $Image

if (-not $Apply) {
    Write-Host ''
    Write-Host 'DRY RUN: nothing created. Pass -Apply to create a real, billable resource.'
    Write-Host '(As of issue #317, -Apply is UNVERIFIED and fails without a built CloudCli -- see -Help.)'
    exit 0
}

Invoke-Apply -ProjectId $ProjectId -Zone $zoneChoice.Zone -MachineType $MachineType `
    -AcceleratorType $AcceleratorType -AcceleratorCount $AcceleratorCount -DiskSizeGb $DiskSizeGb `
    -MaxRunDurationSeconds $MaxRunDurationSeconds -Labels $labels -JobId $effectiveJobId -Image $Image

$leakOk = Invoke-LeakCheck -InstallationId $effectiveInstallationId -CloudCliPath (Find-CloudCli)
if (-not $leakOk) { exit 1 }
exit 0
