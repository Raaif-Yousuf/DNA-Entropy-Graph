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

    HOW -Apply TALKS TO THE CLOUD, ON PURPOSE
    -------------------------------------------
    Appendix C's own permission design denies `gcloud` to an agent on
    purpose, "because the app has no gcloud": this script uses the app's own
    `DnaEntropyGraph.Cloud` through the `CloudCli` console host
    (app/tools/DnaEntropyGraph.CloudCli, issue #60). It never falls back to a
    raw `gcloud` call: when no built CloudCli is found, -Apply FAILS CLEARLY
    (exit 3) and says how to build one. Pass -CloudCliPath to name the
    executable explicitly.

    The `vm create` argument list is New-VmCreateArgs: ONLY flags
    CloudCliParser accepts (--project --zone --install --job --model
    --machine-type --service-account --app-version --lifecycle
    --max-run-duration). CloudCli builds the six standard labels, DELETE and
    the duration itself through VmSpec, so this script cannot weaken them.
    Consequences (issue #619): the accelerator and boot disk come from the
    machine type and the gateway (-AcceleratorType, -AcceleratorCount and
    -DiskSizeGb are plan-only; -DiskSizeGb other than 150 is refused with
    -Apply), the `purpose` label is plan-only, and -Image is refused with
    -Apply because CloudCli has no flag for an image or startup metadata.
    `-PrintApplyArgs` prints the list as JSON with no cloud call; the
    CloudGpuTestScriptContractTests C# test feeds it to the real parser.

    WHAT -Apply DOES TODAY: create the VM, delete it in a `finally` (so a
    failed or interrupted run does not leave a billing VM), run the leak
    scan, and print the resource table as the LAST output line. It does NOT
    yet run `pytest -m gpu` on the VM or pull logs: CloudCli cannot set the
    startup metadata, and worker/vm/startup.sh only runs `dna-entropy-worker
    run`. The run says so ("pytest -m gpu: NOT RUN"); issue #83 stays open
    for that half.

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
    `scheduling.maxRunDuration`, in seconds. Default 3600 (1 hour): today's
    -Apply only creates and deletes a VM, and the VM has no startup script
    to clean itself up, so maxRunDuration is the only backstop if the delete
    fails; 4 hours of an L4 would be about $3.40 of idle billing. Raise it
    when issue #83's pytest step lands. (The cost model's own default is 4
    hours, docs/cloud_design.md section 10.)

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
    registry by this script. CloudCli has no image flag yet, so -Apply
    refuses (exit 2) when it is set.

.PARAMETER Apply
    Create a real, billable resource (and delete it again, see above).
    UNVERIFIED against a real project (#24): the argument list is proven
    against the real CloudCli parser, the flow against a stub. Needs
    -ProjectId and -ServiceAccount (exit 2 without them) and a built
    CloudCli (exit 3 without one).

.PARAMETER ServiceAccount
    The email of the worker service account the VM runs as (CloudCli's
    required --service-account). Required with -Apply; no default, because
    guessing one would boot a VM that cannot reach its bucket.

.PARAMETER CloudCliPath
    Explicit path to the CloudCli executable. Default: search the known
    build locations, then PATH.

.PARAMETER PrintApplyArgs
    Print the CloudCli argument lists -Apply would use, as one JSON object
    {create, delete, list, leak} of string arrays, and exit 0. Creates
    nothing and makes no cloud call.

.PARAMETER SettleSeconds
    After a FAILED `vm create`, -Apply retries `vm delete` up to 3 times,
    waiting this many seconds between tries while the VM is not found yet
    (CloudCli exit 5), because a create that errored may still materialise
    a VM. Default 10.

.PARAMETER SelfTest
    Run against synthetic inputs (label formatting, cost/duration math, the
    dry-run contract, the vm create argument list, the -Apply flow against a
    stub CloudCli) and exit.
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
    UNVERIFIED against a real project. Needs -ServiceAccount and a built
    CloudCli; creates, deletes and leak-scans one labelled VM.
#>

param(
    [string]$ProjectId = '',
    [string]$Zone = '',
    [string]$MachineType = 'g2-standard-8',
    [string]$AcceleratorType = 'nvidia-l4',
    [int]$AcceleratorCount = 1,
    [string]$Model = 'evo2_7b',
    [int]$DiskSizeGb = 150,
    [int]$MaxRunDurationSeconds = 3600,
    [ValidateSet('stop', 'delete', 'keepalive')]
    [string]$Lifecycle = 'delete',
    [ValidateSet('job', 'smoke')]
    [string]$Purpose = 'smoke',
    [string]$JobId = '',
    [string]$InstallationId = '',
    [string]$Image = '',
    [string]$ServiceAccount = '',
    [string]$CloudCliPath = '',
    [int]$SettleSeconds = 10,
    [string]$Bucket = '',
    [string]$LogDir = '',
    [int]$PollSeconds = 20,
    [int]$GpuTestTimeoutSeconds = 0,
    [switch]$PrintApplyArgs,
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
    # This script has no installed app to read an app-version from.
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
    if ($Image) { Write-Host "  worker image:         $Image  [-Apply runs pytest -m gpu in it on the VM (gpu-test mode)]" }
    Write-Host "  labels (CloudCli sets the six standard ones through VmSpec; purpose is plan-only):"
    foreach ($key in $Labels.Keys) { Write-Host "    $key=$($Labels[$key])" }
    if ($Image) {
        Write-Host "  -Apply steps:         bucket ensure, vm create (gpu-test mode), poll the result object, pull the log, vm delete, leak scan, resource table."
    } else {
        Write-Host "  -Apply steps:         vm create, vm delete (in finally), leak scan, resource table. pytest -m gpu runs only with -Image <digest> (issue #83)."
    }
}

# ---------------------------------------------------------------------------
# Leak check: the script's last real step. Asserts the resource list for
# this run's installation-id is empty; if not, says exactly what was left,
# where, and how to delete it -- "always tell the user exactly how to stop
# paying".
# ---------------------------------------------------------------------------

function Find-CloudCli {
    param([switch]$PathOnly)
    # Candidate build/publish locations of the CloudCli console host
    # (app/tools/DnaEntropyGraph.CloudCli, issue #60), then PATH. -CloudCliPath
    # on the script overrides all of this.
    $candidates = @(
        (Join-Path $PSScriptRoot '..' 'app' 'artifacts' 'CloudCli' 'DnaEntropyGraph.CloudCli.exe'),
        (Join-Path $PSScriptRoot '..' 'app' 'tools' 'DnaEntropyGraph.CloudCli' 'bin' 'Release' 'net10.0' 'DnaEntropyGraph.CloudCli.exe')
    )
    if (-not $PathOnly) {
        foreach ($c in $candidates) { if (Test-Path $c) { return $c } }
    }
    $onPath = Get-Command 'DnaEntropyGraph.CloudCli' -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }
    return $null
}

function Invoke-LeakCheck {
    param(
        [Parameter(Mandatory)][string]$InstallationId,
        [Parameter(Mandatory)][string]$ProjectId,
        [string]$CloudCliPath = ''
    )
    if (-not $CloudCliPath) {
        Write-Host "leak check: skipped -- no CloudCli found, and nothing was created by this run to check for."
        return $true
    }
    # --kind vm: the results bucket is meant to outlive a run (lifecycle=results), so only a VM left behind is a leak.
    # stdout alone is parsed (stderr is left to the console): merging them would put a warning line into the JSON.
    $leakArgs = New-ResourcesListArgs -ProjectId $ProjectId -InstallationId $InstallationId -Json
    $stdout = & $CloudCliPath @leakArgs
    $exitCode = $LASTEXITCODE
    $couldNotVerify = "leak check: could not verify that nothing is left running. List the VMs for installation-id=$InstallationId in the Google Cloud console and delete any deg- VM by hand."
    if ($exitCode -ne 0) {
        Write-UsageError "leak check: 'CloudCli resources list' exited $exitCode."
        Write-UsageError $couldNotVerify
        return $false
    }
    # Only a JSON array is a list: empty output, 'null' or a bare scalar must not read as "nothing left".
    $text = ($stdout | Out-String).Trim()
    $resources = @()
    $parsed = $false
    if ($text.StartsWith('[')) {
        try { $resources = @($text | ConvertFrom-Json -ErrorAction Stop); $parsed = $true } catch { $parsed = $false }
    }
    if (-not $parsed) {
        Write-UsageError "leak check: 'CloudCli resources list' did not print a JSON list."
        Write-UsageError $couldNotVerify
        return $false
    }
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

# The `vm create` flags CloudCliParser accepts (app/tools/DnaEntropyGraph.CloudCli/CloudCliParser.cs, VmCreateFlags). A recorded
# list so a drift fails the self-test; the C# CloudGpuTestScriptContractTests feed the real argument list to the real parser.
$Script:CloudCliVmCreateFlags = @(
    'project', 'zone', 'install', 'job', 'model', 'machine-type', 'service-account', 'app-version', 'lifecycle',
    'max-run-duration', 'instance-termination-action', 'worker-image', 'bucket', 'startup-mode', 'expect-gpu'
)

# A worker image the startup script can run: pinned by digest (StartupMetadata's own ImagePattern).
$Script:PinnedImagePattern = '^[a-z0-9][a-zA-Z0-9._/:-]{0,200}@sha256:[0-9a-f]{64}$'

function New-BucketEnsureArgs {
    param([Parameter(Mandatory)][string]$ProjectId)
    return @('bucket', 'ensure', '--project', $ProjectId, '--json')
}

function New-ObjectGetArgs {
    param([Parameter(Mandatory)][string]$ProjectId, [Parameter(Mandatory)][string]$Bucket, [Parameter(Mandatory)][string]$Key)
    return @('object', 'get', '--project', $ProjectId, '--bucket', $Bucket, '--key', $Key)
}
$Script:CloudCliBucketEnsureFlags = @('project')
$Script:CloudCliObjectGetFlags = @('project', 'bucket', 'key')
$Script:CloudCliVmDeleteFlags = @('project', 'zone', 'name')
$Script:CloudCliResourcesListFlags = @('project', 'install', 'kind')

# The most -Apply will ask for: the app's own 24 hour hard ceiling on a VM's life (issue #12).
$Script:MaxRunDurationCeilingSeconds = 86400

function New-VmDeleteArgs {
    param([Parameter(Mandatory)][string]$ProjectId, [Parameter(Mandatory)][string]$Zone, [Parameter(Mandatory)][string]$Name)
    return @('vm', 'delete', '--project', $ProjectId, '--zone', $Zone, '--name', $Name)
}

function New-ResourcesListArgs {
    # --kind vm: the results bucket is meant to outlive a run, so only a VM left behind is a leak. -Json is the machine-read scan.
    param([Parameter(Mandatory)][string]$ProjectId, [Parameter(Mandatory)][string]$InstallationId, [switch]$Json)
    $listArgs = @('resources', 'list', '--project', $ProjectId, '--install', $InstallationId, '--kind', 'vm')
    if ($Json) { $listArgs += '--json' }
    return $listArgs
}

# The boot disk CloudCli's gateway creates (ComputeVmShape.BootDiskGb). -DiskSizeGb is plan-only, so another value cannot be honoured.
$Script:CloudCliBootDiskGb = 150

function New-VmCreateArgs {
    # The argument list for `CloudCli vm create` (#619): only flags CloudCliParser accepts. The six labels, DELETE and the
    # duration are built by CloudCli through VmSpec from these values; none of them is a flag here.
    param(
        [Parameter(Mandatory)][string]$ProjectId,
        [Parameter(Mandatory)][string]$Zone,
        [Parameter(Mandatory)][string]$ServiceAccount,
        [Parameter(Mandatory)][string]$InstallationId,
        [Parameter(Mandatory)][string]$JobId,
        [Parameter(Mandatory)][string]$Model,
        [Parameter(Mandatory)][string]$MachineType,
        [Parameter(Mandatory)][string]$AppVersion,
        [Parameter(Mandatory)][string]$Lifecycle,
        [Parameter(Mandatory)][int]$MaxRunDurationSeconds,
        [string]$Image = '',
        [string]$Bucket = '',
        [bool]$ExpectGpu = $true
    )
    $createArgs = @(
        'vm', 'create',
        '--project', $ProjectId, '--zone', $Zone, '--install', $InstallationId, '--job', $JobId,
        '--model', $Model, '--machine-type', $MachineType, '--service-account', $ServiceAccount,
        '--app-version', $AppVersion, '--lifecycle', $Lifecycle,
        '--max-run-duration', "${MaxRunDurationSeconds}s"
    )
    if ($Image) {
        # gpu-test mode (issue #759): the VM runs `pytest -m gpu` in this image and writes the result object this script polls for.
        $createArgs += @('--worker-image', $Image, '--bucket', $Bucket, '--startup-mode', 'gpu-test', '--expect-gpu', "$($ExpectGpu.ToString().ToLowerInvariant())")
    }
    return $createArgs
}

function Get-CloudCliText {
    # Runs CloudCli and returns @{ Exit; Text }. stdout only: stderr stays on the console.
    param([Parameter(Mandatory)][string]$CloudCliPath, [Parameter(Mandatory)][string[]]$CliArgs)
    $out = & $CloudCliPath @CliArgs
    return [pscustomobject]@{ Exit = $LASTEXITCODE; Text = (($out | Out-String) -replace "`r`n", "`n") }
}

function Wait-GpuTestResult {
    # Polls jobs/<job>/logs/gpu-test-result.json (the file startup.sh's gpu_test_run writes LAST) until it exists or the deadline
    # passes, then pulls gpu-test.log into $LogDir. Returns the pytest exit code, or $null when no result arrived (or it was unreadable).
    param(
        [Parameter(Mandatory)][string]$CloudCliPath,
        [Parameter(Mandatory)][string]$ProjectId,
        [Parameter(Mandatory)][string]$Bucket,
        [Parameter(Mandatory)][string]$JobId,
        [Parameter(Mandatory)][string]$LogDir,
        [Parameter(Mandatory)][int]$TimeoutSeconds,
        [Parameter(Mandatory)][int]$PollSeconds,
        [string]$ExpectedImage = '',
        [string]$ExpectedVm = ''
    )
    $resultKey = "jobs/$JobId/logs/gpu-test-result.json"
    $logKey = "jobs/$JobId/logs/gpu-test.log"
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    Write-Host "Waiting for $resultKey in bucket $Bucket (up to ${TimeoutSeconds}s) ..."
    $result = $null
    while ($true) {
        $got = Get-CloudCliText -CloudCliPath $CloudCliPath -CliArgs (New-ObjectGetArgs -ProjectId $ProjectId -Bucket $Bucket -Key $resultKey)
        if ($got.Exit -eq 0) { $result = $got; break }
        if ($got.Exit -ne 5) {
            Write-UsageError "object get exited $($got.Exit) while waiting for the gpu-test result. Check the internet connection and that the app is signed in; the VM is deleted below."
            return $null
        }
        if ((Get-Date) -ge $deadline) { break }
        if ($PollSeconds -gt 0) { Start-Sleep -Seconds $PollSeconds }
    }
    if (-not $result) {
        Write-UsageError "no gpu-test result arrived within ${TimeoutSeconds}s. The VM is deleted below; read jobs/$JobId/logs/startup.log in bucket $Bucket to see how far it got."
        return $null
    }
    # The log is pulled whatever the verdict: a failing run's log is the whole point.
    $logGot = Get-CloudCliText -CloudCliPath $CloudCliPath -CliArgs (New-ObjectGetArgs -ProjectId $ProjectId -Bucket $Bucket -Key $logKey)
    if ($logGot.Exit -eq 0) {
        New-Item -ItemType Directory -Force -Path $LogDir | Out-Null
        $logPath = Join-Path $LogDir 'gpu-test.log'
        [IO.File]::WriteAllText($logPath, $logGot.Text, (New-Object System.Text.UTF8Encoding $false))
        Write-Host "gpu-test log pulled to $logPath"
    } else {
        Write-UsageError "object get for $logKey exited $($logGot.Exit): the log was not pulled. Read it in bucket $Bucket in the Google Cloud console."
    }
    try {
        $parsed = $result.Text | ConvertFrom-Json -ErrorAction Stop
        if ($null -eq $parsed.exitCode) { throw 'no exitCode' }
    } catch {
        Write-UsageError "the gpu-test result object was not readable JSON with an exitCode. Read $resultKey in bucket $Bucket in the Google Cloud console."
        return $null
    }
    # The result must be THIS run's: the image it ran and the VM that wrote it. (A reused job id is also refused before create.)
    if (($ExpectedImage -and $parsed.image -cne $ExpectedImage) -or ($ExpectedVm -and $parsed.vm.name -cne $ExpectedVm)) {
        Write-UsageError "the gpu-test result in $resultKey is not from this run (image '$($parsed.image)', VM '$($parsed.vm.name)'; expected '$ExpectedImage' on '$ExpectedVm'), so it was not trusted. Read it in bucket $Bucket, then run again with a new -JobId."
        return $null
    }
    if ($parsed.error) {
        Write-UsageError "the VM stopped before any test ran: $($parsed.error). Read $logKey (pulled above) and jobs/$JobId/logs/startup.log in bucket $Bucket."
    }
    return [int]$parsed.exitCode
}

function Invoke-Apply {
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string]$ProjectId,
        [Parameter(Mandatory)][string]$Zone,
        [Parameter(Mandatory)][AllowEmptyString()][string]$ServiceAccount,
        [Parameter(Mandatory)][string]$InstallationId,
        [Parameter(Mandatory)][string]$MachineType,
        [Parameter(Mandatory)][string]$Model,
        [Parameter(Mandatory)][string]$AppVersion,
        [Parameter(Mandatory)][string]$Lifecycle,
        [Parameter(Mandatory)][int]$DiskSizeGb,
        [Parameter(Mandatory)][int]$MaxRunDurationSeconds,
        [Parameter(Mandatory)][string]$JobId,
        [Parameter(Mandatory)][int]$AcceleratorCount,
        [string]$Image = '',
        [string]$Bucket = '',
        [string]$LogDir = '',
        [int]$PollSeconds = 20,
        [int]$GpuTestTimeoutSeconds = 0,
        [string]$CloudCliPath = '',
        [int]$SettleSeconds = 10
    )
    if (-not $ProjectId) {
        Write-UsageError "-Apply requires -ProjectId (working-on-gcp skill: no default project is set; blocked on #24)."
        exit 2
    }
    if (-not $ServiceAccount) {
        Write-UsageError "-Apply requires -ServiceAccount <email>: the VM must run as the worker service account (CloudCli's --service-account). Pass the worker account's email and run this again."
        exit 2
    }
    if ($Image -and $Image -cnotmatch $Script:PinnedImagePattern) {
        Write-UsageError "-Image must be a full reference pinned by digest (name@sha256:<64 hex digits>): the VM's startup script only runs a digest. Pass the digest of the released worker image and run this again."
        exit 2
    }
    if ($DiskSizeGb -ne $Script:CloudCliBootDiskGb) {
        Write-UsageError "-Apply cannot honour -DiskSizeGb ${DiskSizeGb}: CloudCli always creates a $($Script:CloudCliBootDiskGb) GB boot disk. Run without -DiskSizeGb."
        exit 2
    }
    if ($MaxRunDurationSeconds -lt 1 -or $MaxRunDurationSeconds -gt $Script:MaxRunDurationCeilingSeconds) {
        Write-UsageError "-Apply needs -MaxRunDurationSeconds between 1 and $($Script:MaxRunDurationCeilingSeconds) (24 hours), not $MaxRunDurationSeconds. Pass a value in that range."
        exit 2
    }
    $cloudCli = if ($CloudCliPath) { $CloudCliPath } else { Find-CloudCli }
    if (-not $cloudCli -or -not (Test-Path -LiteralPath $cloudCli)) {
        Write-UsageError "-Apply cannot proceed: no built CloudCli found. Build it from app/ (dotnet publish app/tools/DnaEntropyGraph.CloudCli -c Release) or pass -CloudCliPath."
        Write-UsageError "This script deliberately does not fall back to a raw cloud CLI call: Appendix C denies one to agents on purpose."
        exit 3
    }

    $Script:ApplyCreateFailed = $false
    $Script:GpuTestExit = $null
    $Script:GpuTestRan = [bool]$Image

    # gpu-test mode needs the bucket the startup script writes to: the given one, or the project's own (idempotent ensure, no VM yet).
    $effectiveBucket = $Bucket
    if ($Image -and -not $effectiveBucket) {
        $ensure = Get-CloudCliText -CloudCliPath $cloudCli -CliArgs (New-BucketEnsureArgs -ProjectId $ProjectId)
        $ensureName = $null
        if ($ensure.Exit -eq 0) { try { $ensureName = ($ensure.Text | ConvertFrom-Json -ErrorAction Stop).name } catch { $ensureName = $null } }
        if (-not $ensureName) {
            Write-UsageError "bucket ensure failed (exit $($ensure.Exit)) or printed no name, so no VM was created. Run it again, or pass -Bucket <name> of an existing DNA Entropy Graph bucket."
            $Script:ApplyCreateFailed = $true
            return
        }
        $effectiveBucket = [string]$ensureName
    }

    # A job id whose result object exists is a FINISHED run: its verdict would be read as this run's. Refuse before any VM exists.
    if ($Image) {
        $existing = Get-CloudCliText -CloudCliPath $cloudCli -CliArgs (New-ObjectGetArgs -ProjectId $ProjectId -Bucket $effectiveBucket -Key "jobs/$JobId/logs/gpu-test-result.json")
        if ($existing.Exit -eq 0) {
            Write-UsageError "jobs/$JobId/logs/gpu-test-result.json already exists in bucket ${effectiveBucket}: that job id was used by an earlier run, and its result would be read as this one. No VM was created. Run again with a new -JobId (or without -JobId)."
            exit 2
        }
        if ($existing.Exit -ne 5) {
            Write-UsageError "object get exited $($existing.Exit) while checking for an earlier result of job $JobId, so no VM was created. Check the internet connection and that the app is signed in, then run again."
            $Script:ApplyCreateFailed = $true
            return
        }
    }

    $vmName = "deg-$JobId"
    $createArgs = New-VmCreateArgs -ProjectId $ProjectId -Zone $Zone -ServiceAccount $ServiceAccount -InstallationId $InstallationId `
        -JobId $JobId -Model $Model -MachineType $MachineType -AppVersion $AppVersion -Lifecycle $Lifecycle -MaxRunDurationSeconds $MaxRunDurationSeconds `
        -Image $Image -Bucket $effectiveBucket -ExpectGpu ($AcceleratorCount -gt 0)

    # try/finally so even a Ctrl+C (or a real failure) mid-create deletes the VM again: a half-made VM bills. The delete runs even
    # when create failed (the create may have reached Google before the failure). A failed create is NOT a clean state: Google may
    # have accepted the request (CloudCli exit 4 says "check for anything half-made"), so the delete is retried while it finds
    # nothing, an exit 5 is reported (never read as "clean"), and the caller still runs the leak scan and the resource table.
    # In gpu-test mode the VM deletes ITSELF after the tests (lifecycle delete), so an exit 5 after a result arrived is expected.
    $deleteArgs = New-VmDeleteArgs -ProjectId $ProjectId -Zone $Zone -Name $vmName
    try {
        Write-Host "Creating $vmName via $cloudCli ..."
        & $cloudCli @createArgs
        if ($LASTEXITCODE -ne 0) {
            $Script:ApplyCreateFailed = $true
            Write-UsageError "vm create exited $LASTEXITCODE. Looking for a half-made $vmName to delete."
        } elseif ($Image) {
            Write-Host "$vmName created; it runs pytest -m gpu in $Image and deletes itself."
            $timeout = if ($GpuTestTimeoutSeconds -gt 0) { $GpuTestTimeoutSeconds } else { [math]::Max(60, $MaxRunDurationSeconds - 60) }
            $logTarget = if ($LogDir) { $LogDir } else { Join-Path ([IO.Path]::GetTempPath()) (Join-Path 'deg-gpu-test' $JobId) }
            $Script:GpuTestExit = Wait-GpuTestResult -CloudCliPath $cloudCli -ProjectId $ProjectId -Bucket $effectiveBucket -JobId $JobId `
                -LogDir $logTarget -TimeoutSeconds $timeout -PollSeconds $PollSeconds -ExpectedImage $Image -ExpectedVm $vmName
            if ($null -eq $Script:GpuTestExit) {
                Write-UsageError "pytest -m gpu: NO RESULT. Nothing was proven."
            } elseif ($Script:GpuTestExit -eq 0) {
                Write-Host "pytest -m gpu: PASSED"
            } else {
                Write-UsageError "pytest -m gpu: FAILED (exit code $($Script:GpuTestExit)). Read the pulled log under $logTarget."
            }
        } else {
            Write-Host "$vmName created."
            Write-Host "pytest -m gpu: NOT RUN. Pass -Image <worker image digest> to run the GPU tests (issue #83)."
        }
    } finally {
        $attempts = if ($Script:ApplyCreateFailed) { 3 } else { 1 }
        $deleteExit = 0
        for ($try = 1; $try -le $attempts; $try++) {
            Write-Host "Deleting $vmName ..."
            & $cloudCli @deleteArgs
            $deleteExit = $LASTEXITCODE
            if ($deleteExit -ne 5) { break }
            if ($try -lt $attempts -and $SettleSeconds -gt 0) { Start-Sleep -Seconds $SettleSeconds }
        }
        if ($deleteExit -eq 5 -and -not $Script:ApplyCreateFailed -and $null -ne $Script:GpuTestExit) {
            Write-Host "$vmName had already deleted itself (vm delete exit 5), as a finished gpu-test run does."
        } elseif ($deleteExit -eq 5) {
            Write-UsageError "vm delete did not find $vmName in zone $Zone (exit 5). That is not proof it is gone: the leak check below decides."
        } elseif ($deleteExit -ne 0) {
            Write-UsageError "vm delete exited $deleteExit. $vmName may still exist and bill: delete it in the Google Cloud console (project $ProjectId, zone $Zone), or run CloudCli 'vm delete --project $ProjectId --zone $Zone --name $vmName' yourself."
        }
    }
}

function Write-ResourceTable {
    # The run's last output: CloudCli's own table of this installation's VMs. Its footer is 'OK: <n> resource(s)'.
    param([Parameter(Mandatory)][string]$CloudCliPath, [Parameter(Mandatory)][string]$ProjectId, [Parameter(Mandatory)][string]$InstallationId)
    $tableArgs = New-ResourcesListArgs -ProjectId $ProjectId -InstallationId $InstallationId
    & $CloudCliPath @tableArgs | ForEach-Object { Write-Host $_ }
    if ($LASTEXITCODE -ne 0) {
        Write-UsageError "resource table: 'CloudCli resources list' exited $LASTEXITCODE, so the list above is not trustworthy. List the VMs for installation-id=$InstallationId in the Google Cloud console and delete any deg- VM by hand."
        return $false
    }
    return $true
}

# ---------------------------------------------------------------------------
# Self-test.
# ---------------------------------------------------------------------------

function New-StubCli {
    # A throwaway CloudCli.cmd in its own temp folder. $Body is the .cmd text after '@echo off'. Returns the .cmd path.
    param([Parameter(Mandatory)][string]$Directory, [Parameter(Mandatory)][string[]]$Body)
    New-Item -ItemType Directory -Force -Path $Directory | Out-Null
    $path = Join-Path $Directory 'DnaEntropyGraph.CloudCli.cmd'
    Set-Content -Path $path -Value (@('@echo off') + $Body) -Encoding ascii
    return $path
}

function Remove-StubCli {
    # Files first, then the (now empty) folder: no recursive delete.
    param([Parameter(Mandatory)][string]$Directory)
    if (Test-Path $Directory) {
        Get-ChildItem -Path $Directory -File | ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }
        Remove-Item -LiteralPath $Directory -Force
    }
}

function New-ApplyStub {
    # A stub CloudCli that logs every call to calls.txt next to it: `vm create` exits $CreateExit, `resources list --json` prints
    # $JsonBody, any other `resources list` prints an empty table, everything else exits 0.
    # Issue #759: `bucket ensure` prints {"name":"deg-test-bucket"}; `object get` of the result key prints $ResultBody (or exits
    # $ResultExit when that is non-zero, 5 meaning "not there yet"), any other `object get` prints a pytest summary line. The result
    # object only exists after `vm create` ran (the way a real run's does), unless -ResultPreexists (a reused job id).
    param(
        [Parameter(Mandatory)][string]$Directory, [int]$CreateExit = 0, [int]$DeleteExit = 0, [int]$TableExit = 0, [string]$JsonBody = '[]',
        [int]$ResultExit = 0, [string]$ResultBody = '{"exitCode":0,"timedOut":false}', [switch]$ResultPreexists
    )
    $resultLines = if ($ResultExit -eq 0) { @("echo $ResultBody", 'exit /b 0') } else { @("exit /b $ResultExit") }
    $body = @(
        '>>"%~dp0calls.txt" echo %*',
        ('echo %*|findstr /c:"vm create" >nul && (type nul>"%~dp0created.flag" & exit /b ' + $CreateExit + ')'),
        ('echo %*|findstr /c:"vm delete" >nul && exit /b ' + $DeleteExit),
        'echo %*|findstr /c:"bucket ensure" >nul && goto ensure',
        'echo %*|findstr /c:"object get" >nul && goto objget',
        'echo %*|findstr /c:"resources list" >nul || exit /b 0',
        'echo %*|findstr /c:"--json" >nul && goto json',
        'echo KIND  NAME  LOCATION  STATUS  INSTALLATION  JOB',
        'echo OK: 0 resource(s^)',
        "exit /b $TableExit",
        ':json',
        "echo $JsonBody",
        'exit /b 0',
        ':ensure',
        'echo {"name":"deg-test-bucket"}',
        'exit /b 0',
        ':objget',
        'echo %*|findstr /c:"gpu-test-result.json" >nul && goto objresult',
        'echo == 3 passed ==',
        'exit /b 0',
        ':objresult'
    ) + $(if ($ResultPreexists) { @() } else { @('if not exist "%~dp0created.flag" exit /b 5') }) + $resultLines
    return New-StubCli -Directory $Directory -Body $body
}

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

    # -Apply without a service account refuses (CloudCli's vm create requires --service-account) before any CLI call (#619).
    $noSaDir = Join-Path ([IO.Path]::GetTempPath()) ("deg-selftest-nosa-" + [guid]::NewGuid().ToString('N'))
    try {
        $noSaCli = New-ApplyStub -Directory $noSaDir
        $noSaOut = & pwsh -NoProfile -File $PSCommandPath -ProjectId 'fake-test-project' -Apply -CloudCliPath $noSaCli 2>&1 | Out-String
        Check "-Apply with no -ServiceAccount exits 2" ($LASTEXITCODE -eq 2)
        Check "-Apply with no -ServiceAccount names -ServiceAccount" ($noSaOut -match '-ServiceAccount')
        Check "-Apply with no -ServiceAccount never calls CloudCli" (-not (Test-Path (Join-Path $noSaDir 'calls.txt')))
    } finally {
        Remove-StubCli -Directory $noSaDir
    }

    # The vm create argument list (#619): only flags CloudCliParser accepts, every value follows a flag, nothing CloudCli sets itself.
    $printed = & pwsh -NoProfile -File $PSCommandPath -ProjectId 'p1' -ServiceAccount 'w@p1.iam.gserviceaccount.com' `
        -Zone 'us-central1-a' -JobId '20261015-120000-ab3xy9' -InstallationId 'inst123' -MaxRunDurationSeconds 1200 -PrintApplyArgs 2>&1 | Out-String
    Check "-PrintApplyArgs exits 0 and creates nothing" ($LASTEXITCODE -eq 0)
    $printedObj = $printed | ConvertFrom-Json
    $printedArgs = @($printedObj.create)
    $deleteArgs = @($printedObj.delete)
    $listArgs = @($printedObj.list)
    $leakArgs = @($printedObj.leak)
    foreach ($set in @(@{ N = 'vm delete'; A = $deleteArgs; Allowed = $Script:CloudCliVmDeleteFlags; Verb = 'vm delete' },
                       @{ N = 'resources list'; A = $listArgs; Allowed = $Script:CloudCliResourcesListFlags; Verb = 'resources list' },
                       @{ N = 'resources list (leak scan)'; A = $leakArgs; Allowed = $Script:CloudCliResourcesListFlags; Verb = 'resources list' })) {
        $verbOk = ("$($set.A[0]) $($set.A[1])" -eq $set.Verb)
        $bad = @($set.A | Select-Object -Skip 2 | Where-Object { $_ -is [string] -and $_.StartsWith('--') -and $_ -ne '--json' -and $set.Allowed -notcontains $_.Substring(2) })
        Check "$($set.N) args start with '$($set.Verb)' and use only flags CloudCliParser accepts (unknown: $($bad -join ', '))" ($verbOk -and $bad.Count -eq 0 -and $set.A.Count -ge 6)
    }
    Check "vm delete args name the deg-<job> VM" ($deleteArgs -contains 'deg-20261015-120000-ab3xy9')
    Check "the leak scan args are json and vm-only, the table args are not json" (($leakArgs -contains '--json') -and ($leakArgs -contains 'vm') -and -not ($listArgs -contains '--json'))
    Check "vm create args start with 'vm create' and no positional name" ($printedArgs[0] -eq 'vm' -and $printedArgs[1] -eq 'create' -and $printedArgs[2].StartsWith('--'))
    $flagsUsed = @($printedArgs | Where-Object { $_ -is [string] -and $_.StartsWith('--') })
    $unknownFlags = @($flagsUsed | Where-Object { $Script:CloudCliVmCreateFlags -notcontains $_.Substring(2) })
    Check "vm create args use only flags CloudCliParser accepts (unknown: $($unknownFlags -join ', '))" ($unknownFlags.Count -eq 0 -and $flagsUsed.Count -ge 6)
    $valueSlots = @(for ($i = 2; $i -lt $printedArgs.Count; $i++) { if (-not $printedArgs[$i].StartsWith('--')) { $printedArgs[$i - 1].StartsWith('--') } })
    Check "vm create args: every value directly follows a flag" (@($valueSlots | Where-Object { -not $_ }).Count -eq 0 -and $valueSlots.Count -ge 6)
    Check "vm create args carry the required --service-account, --install and --zone" (
        ($printedArgs -contains '--service-account') -and ($printedArgs -contains '--install') -and ($printedArgs -contains '--zone'))
    Check "vm create args carry the finite --max-run-duration as given" ($printedArgs[[array]::IndexOf($printedArgs, '--max-run-duration') + 1] -eq '1200s')
    Check "vm create args never try to set labels or the termination action themselves" (
        -not ($printedArgs -contains '--labels') -and -not ($printedArgs -contains '--instance-termination-action'))

    # -Apply end to end against a stub CloudCli: create, delete in finally, leak scan, resource table last.
    $applyDir = Join-Path ([IO.Path]::GetTempPath()) ("deg-selftest-apply-" + [guid]::NewGuid().ToString('N'))
    try {
        $stub = New-ApplyStub -Directory $applyDir
        $applyArgs = @('-NoProfile', '-File', $PSCommandPath, '-ProjectId', 'fake-test-project', '-ServiceAccount', 'w@fake-test-project.iam.gserviceaccount.com',
            '-Zone', 'us-central1-a', '-JobId', '20261015-120000-ab3xy9', '-InstallationId', 'inst123', '-Apply', '-CloudCliPath', $stub)
        $applyLines = @(& pwsh @applyArgs 2>&1 | ForEach-Object { "$_" })
        Check "-Apply against a clean stub exits 0" ($LASTEXITCODE -eq 0)
        $calls = @(Get-Content -Path (Join-Path $applyDir 'calls.txt'))
        Check "-Apply calls CloudCli in order: vm create, vm delete, resources list (json), resources list (table)" (
            $calls.Count -eq 4 -and $calls[0] -like 'vm create *' -and $calls[1] -like 'vm delete *' -and
            $calls[2] -like 'resources list *--kind vm*--json*' -and $calls[3] -like 'resources list *' -and $calls[3] -notlike '*--json*')
        Check "-Apply deletes exactly the VM it named (deg-<job id>) in the same zone" ($calls[1] -like '*--name deg-20261015-120000-ab3xy9*' -and $calls[1] -like '*--zone us-central1-a*')
        $nonBlank = @($applyLines | Where-Object { $_.Trim() })
        Check "-Apply's LAST output line is the resource table's footer (the empty list)" ($nonBlank[-1] -eq 'OK: 0 resource(s)')
        Check "-Apply says plainly that the pytest step did not run" (($applyLines -join "`n") -match 'pytest -m gpu: NOT RUN')

    } finally {
        Remove-StubCli -Directory $applyDir
    }

    # -Apply -Image (issue #759): the gpu-test run. Bucket ensure, create with the startup flags, poll the result object, pull the log,
    # delete, leak scan, table. The stub answers `object get` for the result and the log; -ResultExit 5 means "not there yet".
    $digest = 'ghcr.io/raaif-yousuf/dna-entropy-worker:0.1.0-cuda@sha256:' + ('ab' * 32)
    $okBody = '{"exitCode":0,"timedOut":false,"image":"' + $digest + '","vm":{"name":"deg-20261015-120000-ab3xy9"}}'
    $imgBase = @('-NoProfile', '-File', $PSCommandPath, '-ProjectId', 'fake-test-project', '-ServiceAccount', 'w@fake-test-project.iam.gserviceaccount.com',
        '-Zone', 'us-central1-a', '-JobId', '20261015-120000-ab3xy9', '-InstallationId', 'inst123', '-Apply', '-SettleSeconds', '0', '-PollSeconds', '0',
        '-Image', $digest)
    $imgDir = Join-Path ([IO.Path]::GetTempPath()) ("deg-selftest-img-" + [guid]::NewGuid().ToString('N'))
    $imgLogDir = Join-Path ([IO.Path]::GetTempPath()) ("deg-selftest-imglog-" + [guid]::NewGuid().ToString('N'))
    try {
        $imgStub = New-ApplyStub -Directory $imgDir -DeleteExit 5 -ResultBody $okBody
        $imgOut = @(& pwsh @imgBase -CloudCliPath $imgStub -LogDir $imgLogDir 2>&1 | ForEach-Object { "$_" })
        Check "-Apply -Image against a passing stub exits 0" ($LASTEXITCODE -eq 0)
        $imgCalls = @(Get-Content -Path (Join-Path $imgDir 'calls.txt'))
        $verbs = @($imgCalls | ForEach-Object { ($_ -split ' ')[0..1] -join ' ' })
        Check "-Apply -Image calls CloudCli in order: bucket ensure, result pre-check, vm create, object get x2, vm delete, resources list x2" (
            ($verbs -join ',') -eq 'bucket ensure,object get,vm create,object get,object get,vm delete,resources list,resources list')
        $createCall = @($imgCalls | Where-Object { $_ -like 'vm create *' })[0]
        Check "-Apply -Image passes the digest, the bucket, gpu-test mode and the GPU expectation to vm create" (
            $createCall -like "*--worker-image $digest*" -and $createCall -like '*--bucket deg-test-bucket*' -and
            $createCall -like '*--startup-mode gpu-test*' -and $createCall -like '*--expect-gpu true*')
        Check "-Apply -Image polls the result object and then pulls the log (keys under jobs/<job>/logs)" (
            $imgCalls[3] -like '*--key jobs/20261015-120000-ab3xy9/logs/gpu-test-result.json*' -and
            $imgCalls[4] -like '*--key jobs/20261015-120000-ab3xy9/logs/gpu-test.log*' -and $imgCalls[3] -like '*--bucket deg-test-bucket*')
        $pulled = Join-Path $imgLogDir 'gpu-test.log'
        Check "-Apply -Image writes the pulled log to -LogDir" ((Test-Path $pulled) -and ((Get-Content -Raw $pulled) -match '3 passed'))
        Check "-Apply -Image reports the pytest verdict and not 'NOT RUN'" (($imgOut -join "`n") -match 'pytest -m gpu: PASSED' -and ($imgOut -join "`n") -notmatch 'NOT RUN')
        Check "-Apply -Image still ends with the resource table footer" (@($imgOut | Where-Object { $_.Trim() })[-1] -eq 'OK: 0 resource(s)')
        Check "-Apply -Image treats the VM that deleted itself (delete exit 5) as expected, not a warning" (($imgOut -join "`n") -match 'deleted itself' -and ($imgOut -join "`n") -notmatch 'did not find')
    } finally {
        Remove-StubCli -Directory $imgDir
        Remove-StubCli -Directory $imgLogDir
    }

    # A reused -JobId whose result object already exists (a finished run of the same id) is refused BEFORE a VM is created: the old
    # result would be read as this run's verdict (stale PASS).
    $reDir = Join-Path ([IO.Path]::GetTempPath()) ("deg-selftest-reuse-" + [guid]::NewGuid().ToString('N'))
    try {
        $reStub = New-ApplyStub -Directory $reDir -ResultBody $okBody -ResultPreexists
        $reOut = & pwsh @imgBase -CloudCliPath $reStub -LogDir $imgLogDir 2>&1 | Out-String
        Check "-Apply -Image with a job id that already has a gpu-test result exits 2" ($LASTEXITCODE -eq 2)
        Check "-Apply -Image with a reused job id names the key and says to use a new -JobId" ($reOut -match 'gpu-test-result\.json' -and $reOut -match '-JobId')
        $reCalls = @(Get-Content -Path (Join-Path $reDir 'calls.txt'))
        Check "-Apply -Image with a reused job id creates no VM" (@($reCalls | Where-Object { $_ -like 'vm create *' }).Count -eq 0)
    } finally {
        Remove-StubCli -Directory $reDir
    }

    # A result that is not this run's (another image or another VM name) is not trusted as a verdict.
    $stDir = Join-Path ([IO.Path]::GetTempPath()) ("deg-selftest-stale-" + [guid]::NewGuid().ToString('N'))
    $stLogDir = Join-Path ([IO.Path]::GetTempPath()) ("deg-selftest-stalelog-" + [guid]::NewGuid().ToString('N'))
    try {
        $otherDigest = 'ghcr.io/raaif-yousuf/dna-entropy-worker:0.0.9-cuda@sha256:' + ('cd' * 32)
        $stStub = New-ApplyStub -Directory $stDir -ResultBody ($okBody -replace [regex]::Escape($digest), $otherDigest)
        $stOut = & pwsh @imgBase -CloudCliPath $stStub -LogDir $stLogDir 2>&1 | Out-String
        Check "-Apply -Image does not pass on a result written by another image" ($LASTEXITCODE -eq 1 -and $stOut -notmatch 'pytest -m gpu: PASSED' -and $stOut -match 'not from this run')
        Remove-StubCli -Directory $stDir
        $stStub = New-ApplyStub -Directory $stDir -ResultBody ($okBody -replace 'deg-20261015-120000-ab3xy9', 'deg-someone-elses-job')
        $stOut = & pwsh @imgBase -CloudCliPath $stStub -LogDir $stLogDir 2>&1 | Out-String
        Check "-Apply -Image does not pass on a result written by another VM" ($LASTEXITCODE -eq 1 -and $stOut -notmatch 'pytest -m gpu: PASSED' -and $stOut -match 'not from this run')
    } finally {
        Remove-StubCli -Directory $stDir
        Remove-StubCli -Directory $stLogDir
    }

    # An early failure result (GPU never visible, image pull failed) ends the wait at once and names the error.
    $efDir = Join-Path ([IO.Path]::GetTempPath()) ("deg-selftest-early-" + [guid]::NewGuid().ToString('N'))
    $efLogDir = Join-Path ([IO.Path]::GetTempPath()) ("deg-selftest-earlylog-" + [guid]::NewGuid().ToString('N'))
    try {
        $efBody = '{"exitCode":7,"timedOut":false,"error":"GPU_NOT_VISIBLE","image":"' + $digest + '","vm":{"name":"deg-20261015-120000-ab3xy9"}}'
        $efStub = New-ApplyStub -Directory $efDir -ResultBody $efBody
        $efOut = & pwsh @imgBase -CloudCliPath $efStub -LogDir $efLogDir 2>&1 | Out-String
        Check "-Apply -Image whose VM reported an early failure exits 1 and names the error" ($LASTEXITCODE -eq 1 -and $efOut -match 'GPU_NOT_VISIBLE')
        Check "-Apply -Image reports the early failure as FAILED, not as a pass" ($efOut -match 'pytest -m gpu: FAILED \(exit code 7\)')
    } finally {
        Remove-StubCli -Directory $efDir
        Remove-StubCli -Directory $efLogDir
    }

    # -Bucket skips `bucket ensure`.
    $bkDir = Join-Path ([IO.Path]::GetTempPath()) ("deg-selftest-bk-" + [guid]::NewGuid().ToString('N'))
    $bkLogDir = Join-Path ([IO.Path]::GetTempPath()) ("deg-selftest-bklog-" + [guid]::NewGuid().ToString('N'))
    try {
        $bkStub = New-ApplyStub -Directory $bkDir -ResultBody $okBody
        & pwsh @imgBase -CloudCliPath $bkStub -LogDir $bkLogDir -Bucket 'my-own-bucket' 2>&1 | Out-Null
        $bkCalls = @(Get-Content -Path (Join-Path $bkDir 'calls.txt'))
        Check "-Apply -Image -Bucket skips bucket ensure and uses the given bucket" (
            @($bkCalls | Where-Object { $_ -like 'bucket ensure*' }).Count -eq 0 -and @($bkCalls | Where-Object { $_ -like '*--bucket my-own-bucket*' }).Count -ge 3)
    } finally {
        Remove-StubCli -Directory $bkDir
        Remove-StubCli -Directory $bkLogDir
    }

    # A failing pytest (result exitCode 1) fails the run but still pulls the log and deletes the VM.
    $pfDir = Join-Path ([IO.Path]::GetTempPath()) ("deg-selftest-pf-" + [guid]::NewGuid().ToString('N'))
    $pfLogDir = Join-Path ([IO.Path]::GetTempPath()) ("deg-selftest-pflog-" + [guid]::NewGuid().ToString('N'))
    try {
        $pfStub = New-ApplyStub -Directory $pfDir -ResultBody ($okBody -replace '"exitCode":0', '"exitCode":1')
        $pfOut = & pwsh @imgBase -CloudCliPath $pfStub -LogDir $pfLogDir 2>&1 | Out-String
        Check "-Apply -Image whose pytest failed exits 1" ($LASTEXITCODE -eq 1)
        Check "-Apply -Image whose pytest failed says FAILED with the exit code" ($pfOut -match 'pytest -m gpu: FAILED \(exit code 1\)')
        $pfCalls = @(Get-Content -Path (Join-Path $pfDir 'calls.txt'))
        Check "-Apply -Image whose pytest failed still pulls the log and deletes the VM" (
            (Test-Path (Join-Path $pfLogDir 'gpu-test.log')) -and @($pfCalls | Where-Object { $_ -like 'vm delete *' }).Count -eq 1)
    } finally {
        Remove-StubCli -Directory $pfDir
        Remove-StubCli -Directory $pfLogDir
    }

    # No result before the deadline: exit 1 naming it, the VM is deleted (it may still be running), the table still prints.
    $toDir = Join-Path ([IO.Path]::GetTempPath()) ("deg-selftest-to-" + [guid]::NewGuid().ToString('N'))
    $toLogDir = Join-Path ([IO.Path]::GetTempPath()) ("deg-selftest-tolog-" + [guid]::NewGuid().ToString('N'))
    try {
        $toStub = New-ApplyStub -Directory $toDir -ResultExit 5
        $toOut = & pwsh @imgBase -CloudCliPath $toStub -LogDir $toLogDir -GpuTestTimeoutSeconds 1 2>&1 | Out-String
        Check "-Apply -Image with no result before the deadline exits 1" ($LASTEXITCODE -eq 1)
        Check "-Apply -Image with no result says no result arrived" ($toOut -match 'no gpu-test result')
        $toCalls = @(Get-Content -Path (Join-Path $toDir 'calls.txt'))
        Check "-Apply -Image with no result still deletes the VM and prints the table" (
            @($toCalls | Where-Object { $_ -like 'vm delete *' }).Count -ge 1 -and $toCalls[-1] -like 'resources list *' -and $toCalls[-1] -notlike '*--json*')
    } finally {
        Remove-StubCli -Directory $toDir
        Remove-StubCli -Directory $toLogDir
    }

    # An image not pinned by digest is refused before any CLI call (the startup script can only run a digest).
    $upDir = Join-Path ([IO.Path]::GetTempPath()) ("deg-selftest-up-" + [guid]::NewGuid().ToString('N'))
    try {
        $upStub = New-ApplyStub -Directory $upDir
        $upBase = @($imgBase[0..($imgBase.Count - 2)]) + @('ghcr.io/raaif-yousuf/dna-entropy-worker:latest')
        $upOut = & pwsh @upBase -CloudCliPath $upStub 2>&1 | Out-String
        Check "-Apply -Image with a tag instead of a digest exits 2 naming the digest" ($LASTEXITCODE -eq 2 -and $upOut -match 'sha256')
        Check "-Apply -Image with a tag made no CloudCli call" (-not (Test-Path (Join-Path $upDir 'calls.txt')))
    } finally {
        Remove-StubCli -Directory $upDir
    }

    # -PrintApplyArgs with -Image: the create list gains the startup flags, and the ensure/get lists parse like the rest.
    $printedImg = & pwsh -NoProfile -File $PSCommandPath -ProjectId 'p1' -ServiceAccount 'w@p1.iam.gserviceaccount.com' -Zone 'us-central1-a' `
        -JobId '20261015-120000-ab3xy9' -InstallationId 'inst123' -Image $digest -Bucket 'deg-p1' -PrintApplyArgs 2>&1 | Out-String
    $pi = $printedImg | ConvertFrom-Json
    $piCreate = @($pi.create)
    $piFlags = @($piCreate | Where-Object { $_ -is [string] -and $_.StartsWith('--') })
    $piUnknown = @($piFlags | Where-Object { $Script:CloudCliVmCreateFlags -notcontains $_.Substring(2) })
    Check "-PrintApplyArgs -Image: vm create still uses only flags CloudCliParser accepts (unknown: $($piUnknown -join ', '))" ($piUnknown.Count -eq 0)
    Check "-PrintApplyArgs -Image: vm create carries --worker-image, --bucket, --startup-mode gpu-test and --expect-gpu true" (
        $piCreate[[array]::IndexOf($piCreate, '--worker-image') + 1] -eq $digest -and $piCreate[[array]::IndexOf($piCreate, '--bucket') + 1] -eq 'deg-p1' -and
        $piCreate[[array]::IndexOf($piCreate, '--startup-mode') + 1] -eq 'gpu-test' -and $piCreate[[array]::IndexOf($piCreate, '--expect-gpu') + 1] -eq 'true')
    $piEnsure = @($pi.ensure)
    $piGet = @($pi.get)
    Check "-PrintApplyArgs: bucket ensure and object get lists use only CloudCliParser flags" (
        ($piEnsure[0..1] -join ' ') -eq 'bucket ensure' -and ($piGet[0..1] -join ' ') -eq 'object get' -and
        @($piEnsure | Where-Object { $_ -is [string] -and $_.StartsWith('--') -and $_ -ne '--json' -and $Script:CloudCliBucketEnsureFlags -notcontains $_.Substring(2) }).Count -eq 0 -and
        @($piGet | Where-Object { $_ -is [string] -and $_.StartsWith('--') -and $Script:CloudCliObjectGetFlags -notcontains $_.Substring(2) }).Count -eq 0)

    # A failed create still tries to delete the VM (a half-made VM bills), and a leftover VM in the scan fails the run.
    $failDir = Join-Path ([IO.Path]::GetTempPath()) ("deg-selftest-fail-" + [guid]::NewGuid().ToString('N'))
    $failBase = @('-NoProfile', '-File', $PSCommandPath, '-ProjectId', 'fake-test-project', '-ServiceAccount', 'w@fake-test-project.iam.gserviceaccount.com',
        '-Zone', 'us-central1-a', '-JobId', '20261015-120000-ab3xy9', '-InstallationId', 'inst123', '-Apply', '-SettleSeconds', '0')
    try {
        # create exit 4 (CloudCli: "check resources list for anything half-made"), the delete finds nothing (exit 5), and the scan
        # then finds a VM that materialised late: the leak must be reported, not hidden behind the create error.
        $failStub = New-ApplyStub -Directory $failDir -CreateExit 4 -DeleteExit 5 -JsonBody '[{"name":"deg-x","zone":"us-central1-a"}]'
        $failOut = & pwsh @failBase -CloudCliPath $failStub 2>&1 | Out-String
        Check "-Apply whose create fails exits 1" ($LASTEXITCODE -eq 1)
        $failCalls = @(Get-Content -Path (Join-Path $failDir 'calls.txt'))
        Check "-Apply whose create fails still attempts the delete (and retries while it finds nothing)" (@($failCalls | Where-Object { $_ -like 'vm delete *' }).Count -ge 2)
        Check "-Apply whose create fails still runs the leak scan" (@($failCalls | Where-Object { $_ -like 'resources list *--json*' }).Count -eq 1)
        Check "-Apply whose create fails prints the leak the scan found" ($failOut -match 'still exist' -and $failOut -match 'deg-x')
        Check "-Apply does not call a delete exit 5 clean after a failed create" ($failOut -match 'did not find')
        Check "-Apply whose create fails still ends with the resource table" ($failCalls[-1] -like 'resources list *' -and $failCalls[-1] -notlike '*--json*')
    } finally {
        Remove-StubCli -Directory $failDir
    }

    # create fails, nothing exists anywhere: still exit 1 (the create failed) and the scan still ran.
    $failDir2 = Join-Path ([IO.Path]::GetTempPath()) ("deg-selftest-fail2-" + [guid]::NewGuid().ToString('N'))
    try {
        $failStub2 = New-ApplyStub -Directory $failDir2 -CreateExit 1 -DeleteExit 5
        & pwsh @failBase -CloudCliPath $failStub2 2>&1 | Out-Null
        Check "-Apply whose create fails and finds nothing still exits 1" ($LASTEXITCODE -eq 1)
        Check "-Apply whose create fails and finds nothing still ran the leak scan" (@(Get-Content -Path (Join-Path $failDir2 'calls.txt') | Where-Object { $_ -like 'resources list *--json*' }).Count -eq 1)
    } finally {
        Remove-StubCli -Directory $failDir2
    }

    # The final table call failing must fail the run, not leave exit 0 with the wrong last line.
    $tableDir = Join-Path ([IO.Path]::GetTempPath()) ("deg-selftest-table-" + [guid]::NewGuid().ToString('N'))
    try {
        $tableStub = New-ApplyStub -Directory $tableDir -TableExit 1
        $tableOut = & pwsh @failBase -CloudCliPath $tableStub 2>&1 | Out-String
        Check "-Apply whose final resource table call fails exits 1" ($LASTEXITCODE -eq 1)
        Check "-Apply whose final resource table call fails says so" ($tableOut -match 'resource table')
    } finally {
        Remove-StubCli -Directory $tableDir
    }

    # -MaxRunDurationSeconds: a finite, positive value up to the 24 hour ceiling; anything else is refused before any CLI call.
    $ceilDir = Join-Path ([IO.Path]::GetTempPath()) ("deg-selftest-ceil-" + [guid]::NewGuid().ToString('N'))
    try {
        $ceilStub = New-ApplyStub -Directory $ceilDir
        foreach ($bad in @(86401, 0, -5)) {
            $ceilOut = & pwsh @failBase -CloudCliPath $ceilStub -MaxRunDurationSeconds $bad 2>&1 | Out-String
            Check "-Apply refuses -MaxRunDurationSeconds $bad (exit 2, names the limit)" ($LASTEXITCODE -eq 2 -and $ceilOut -match '86400')
        }
        Check "-Apply refused every bad duration before calling CloudCli" (-not (Test-Path (Join-Path $ceilDir 'calls.txt')))
        & pwsh @failBase -CloudCliPath $ceilStub -MaxRunDurationSeconds 86400 2>&1 | Out-Null
        Check "-Apply accepts exactly the 86400 ceiling" ($LASTEXITCODE -eq 0)
    } finally {
        Remove-StubCli -Directory $ceilDir
    }

    # Find-CloudCli's PATH fallback still finds the real executable name.
    $pathDir = Join-Path ([IO.Path]::GetTempPath()) ("deg-selftest-path-" + [guid]::NewGuid().ToString('N'))
    $savedPath = $env:PATH
    try {
        New-StubCli -Directory $pathDir -Body @('exit /b 0') | Out-Null
        $env:PATH = "$pathDir;$savedPath"
        $stubFound = Find-CloudCli -PathOnly
        Check "Find-CloudCli's PATH fallback finds DnaEntropyGraph.CloudCli (the real executable name)" ($stubFound -eq (Join-Path $pathDir 'DnaEntropyGraph.CloudCli.cmd'))
    } finally {
        $env:PATH = $savedPath
        Remove-StubCli -Directory $pathDir
    }

    # Leak check: a stub CloudCli stands in for the real one. Only a parsed, empty list on exit 0 is "clean"; anything unverifiable fails.
    $leakDir = Join-Path ([IO.Path]::GetTempPath()) ("deg-selftest-leak-" + [guid]::NewGuid().ToString('N'))
    try {
        $leakCases = @(
            @{ Name = 'an empty JSON list on exit 0'; Body = @('echo []', 'exit /b 0'); Clean = $true },
            @{ Name = 'an empty list with noise on stderr (stdout is parsed alone)'; Body = @('echo warning 1>&2', 'echo []', 'exit /b 0'); Clean = $true },
            @{ Name = 'garbage on stdout with exit 0'; Body = @('echo this is not json', 'exit /b 0'); Clean = $false },
            @{ Name = 'no output at all with exit 0'; Body = @('exit /b 0'); Clean = $false },
            @{ Name = 'JSON null with exit 0'; Body = @('echo null', 'exit /b 0'); Clean = $false },
            @{ Name = 'a valid empty list but a non-zero exit'; Body = @('echo []', 'exit /b 1'); Clean = $false },
            @{ Name = 'one leftover VM'; Body = @('echo [{"name":"deg-x","zone":"us-central1-a"}]', 'exit /b 0'); Clean = $false }
        )
        foreach ($case in $leakCases) {
            $cli = New-StubCli -Directory $leakDir -Body $case.Body
            $result = Invoke-LeakCheck -InstallationId 'inst123' -ProjectId 'fake-test-project' -CloudCliPath $cli 2>$null
            Check "leak check: $($case.Name) -> $(if ($case.Clean) { 'clean' } else { 'NOT clean' })" ($result -eq $case.Clean)
        }
        Check "leak check: vacuity guard, the case table has both outcomes" (($leakCases | Where-Object { $_.Clean }).Count -ge 1 -and ($leakCases | Where-Object { -not $_.Clean }).Count -ge 3)
    } finally {
        Remove-StubCli -Directory $leakDir
    }

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

if ($PrintApplyArgs) {
    $printBucket = if ($Bucket) { $Bucket } else { 'deg-example-bucket' }
    $printArgs = New-VmCreateArgs -ProjectId $ProjectId -Zone $zoneChoice.Zone -ServiceAccount $ServiceAccount `
        -InstallationId $effectiveInstallationId -JobId $effectiveJobId -Model $Model -MachineType $MachineType `
        -AppVersion $effectiveAppVersion -Lifecycle $Lifecycle -MaxRunDurationSeconds $MaxRunDurationSeconds `
        -Image $Image -Bucket $printBucket -ExpectGpu ($AcceleratorCount -gt 0)
    $printName = "deg-$effectiveJobId"
    [ordered]@{
        create = @($printArgs)
        delete = @(New-VmDeleteArgs -ProjectId $ProjectId -Zone $zoneChoice.Zone -Name $printName)
        list   = @(New-ResourcesListArgs -ProjectId $ProjectId -InstallationId $effectiveInstallationId)
        leak   = @(New-ResourcesListArgs -ProjectId $ProjectId -InstallationId $effectiveInstallationId -Json)
        ensure = @(New-BucketEnsureArgs -ProjectId $ProjectId)
        get    = @(New-ObjectGetArgs -ProjectId $ProjectId -Bucket $printBucket -Key "jobs/$effectiveJobId/logs/gpu-test-result.json")
    } | ConvertTo-Json -Compress
    exit 0
}

$pricing = Get-PricingEstimate -MachineType $MachineType -AcceleratorType $AcceleratorType `
    -DiskSizeGb $DiskSizeGb -DurationMinutes $Script:DurationEstimateMinutes

Write-Plan -ProjectId $ProjectId -ZoneChoice $zoneChoice -MachineType $MachineType `
    -AcceleratorType $AcceleratorType -AcceleratorCount $AcceleratorCount -Pricing $pricing `
    -DurationMinutes $Script:DurationEstimateMinutes -DiskSizeGb $DiskSizeGb `
    -MaxRunDurationSeconds $MaxRunDurationSeconds -Labels $labels -Image $Image

if (-not $Apply) {
    Write-Host ''
    Write-Host 'DRY RUN: nothing created. Pass -Apply (with -ProjectId and -ServiceAccount) to create, delete and leak-scan one real, billable VM.'
    Write-Host '(-Apply is UNVERIFIED against a real project and does not yet run pytest -m gpu, issue #83 -- see -Help.)'
    exit 0
}

Invoke-Apply -ProjectId $ProjectId -Zone $zoneChoice.Zone -ServiceAccount $ServiceAccount -InstallationId $effectiveInstallationId `
    -MachineType $MachineType -Model $Model -AppVersion $effectiveAppVersion -Lifecycle $Lifecycle -DiskSizeGb $DiskSizeGb `
    -MaxRunDurationSeconds $MaxRunDurationSeconds -JobId $effectiveJobId -AcceleratorCount $AcceleratorCount -Image $Image -Bucket $Bucket `
    -LogDir $LogDir -PollSeconds $PollSeconds -GpuTestTimeoutSeconds $GpuTestTimeoutSeconds -CloudCliPath $CloudCliPath -SettleSeconds $SettleSeconds

# Always runs, also after a failed create: the scan and the table are how a half-made VM gets reported.
$resolvedCli = if ($CloudCliPath) { $CloudCliPath } else { Find-CloudCli }
$leakOk = Invoke-LeakCheck -InstallationId $effectiveInstallationId -ProjectId $ProjectId -CloudCliPath $resolvedCli
$tableOk = Write-ResourceTable -CloudCliPath $resolvedCli -ProjectId $ProjectId -InstallationId $effectiveInstallationId
# In gpu-test mode (-Image) the run only passes when pytest exited 0: no result ($null) or a non-zero code fails it.
$gpuTestFailed = $Script:GpuTestRan -and ($null -eq $Script:GpuTestExit -or $Script:GpuTestExit -ne 0)
if ($Script:ApplyCreateFailed -or $gpuTestFailed -or -not $leakOk -or -not $tableOk) { exit 1 }
exit 0
