---
name: working-on-gcp
description: Use before creating, inspecting or debugging ANY Google Cloud resource for this project, before running scripts/cloud_gpu_test.ps1, and before writing a brief for an agent that will. Also use when a cloud run looks stuck, when "no capacity" appears, when a VM will not go away, or when deciding whether a finding is cloud-specific.
---

# Working on GCP

Every job this app runs is a VM created, in one owner's own Google Cloud
project, on demand, for one run, and torn down after. There is no standing
cluster and no shared server: "working on GCP" here means creating and
verifying exactly one labelled, time-boxed resource, and proving it is gone
when you are done.

## The account

| Thing | Value |
| --- | --- |
| Project | **Not yet set.** Blocked on the owner (#24, `OWNER_TODO.md` item 2). Do not invent a project id or use a project you found some other way. |
| Default zone(s) | **Not yet set** — record here as `MEASURED <date>:` once #24 lands. Until then, treat every zone as a candidate and let the zone-ladder logic (Hard Rule 9's "never a shared singleton", Critical Pitfalls' quota-vs-stockout split) pick one. |
| Dev bucket | Named per job, not fixed (Hard Rule 9: no fixed-name cloud singleton — two lab members may share one Google account). Discover by label, never by a hardcoded name. |
| Budget alert | **Not yet set.** Confirm one exists on the owner's billing account before running anything that is not `$0`; ask rather than assume. |
| GPU quota | **Not yet set** — `NVIDIA_L4_GPUS >= 1` in at least one region, `GPUS_ALL_REGIONS >= 1`, is issue #24's own Done-when. Do not run a GPU-creating command until this is confirmed, and confirm it by checking the project's real quota, not by assuming the design doc's target is already true. |

**Until #24 is closed, do not create a GPU-backed resource in any project.**
The CPU walking-skeleton path (Hard Rule 6, MockPredictor) does not need a
GPU and is fine to exercise once #21 (OAuth) and a project exist; a real
Evo 2 run is blocked on #24 specifically.

## The one rule every VM this app creates must follow

Hard Rules 9 and 10, restated as a preflight checklist because this is where
they get skipped under time pressure:

- [ ] Carries every standard label: `app=dna-entropy-graph`,
      `installation-id`, `job-id`, `model`, `app-version`, `lifecycle`
- [ ] Carries `maxRunDuration` (a real, finite value — never omitted, never
      "big enough to not matter")
- [ ] Carries `instanceTerminationAction=DELETE`, not `STOP` — a
      terminated-but-not-deleted VM still bills for its boot disk
- [ ] Additionally, per Critical Pitfalls: the VM's own `startup.sh` calls
      the Compute API on itself (`stop` or `delete`, with the metadata
      token) at the end, because `instanceTerminationAction` only fires when
      *Compute Engine* stops the instance (max-run-duration expiry), never
      when the guest runs `shutdown`. `maxRunDuration` is the backstop for a
      script that dies before reaching its own cleanup, not a replacement
      for the self-delete call.

`VmSpec` rejects a spec missing any of the label/duration/termination fields
before the request is built (Hard Rule 10) and `scripts/hooks/
block_unlabelled_vm_create.py` is a second, mechanical line of defense at
the tool-call level — but neither catches a startup script that never calls
the API on itself, because that failure only shows up minutes later as a
VM sitting in TERMINATED. Check for the self-delete call by reading
`worker/vm/startup.sh`, not by trusting that the two required flags were
enough.

## Budget arithmetic

Approximate GCP on-demand list prices (not MEASURED against this project's
own billing yet — record a real `MEASURED <date>:` figure here once a run
has actually happened and you can read it off the billing console):

- `g2-standard-8` with 1x L4: roughly **$0.85/hour** while running
- A100 fallback: roughly **$3.70/hour** while running
- `pd-balanced` 150 GB boot disk: roughly **$15/month** while it exists,
  independent of whether the VM is running — this is the part a
  terminated-but-not-deleted VM keeps paying for

State your machine type and expected wall clock before you run anything,
and compute your own rough cost from the numbers above rather than assuming
a run is free because it is short. If the real billed number ever disagrees
noticeably with this estimate, record it here as `MEASURED <date>:` and
correct the estimate rather than leaving both to drift apart.

## Preflight order (Critical Pitfalls, CLAUDE.md)

Billing off, the Compute API disabled, and a genuine zonal stockout look
identical from the caller's side. Before any `Insert`, check in this order
and stop at the first failure with a named action:

1. Project `ACTIVE`
2. Billing enabled
3. Compute API enabled (enable it ourselves, idempotently, if not)
4. GPU quota > 0 in at least one region
5. Bucket exists

`billing`, `api_disabled`, `permission` and `org_policy` errors abort
immediately. Only `quota` and `stockout` are worth retrying or walking a
zone ladder for.

## The measured ways a cloud run wastes itself

These are the general shape this project already expects to hit, carried
forward from prior GCE experience rather than measured on this project yet
(mark each with its own `MEASURED <date>:` here once it actually happens):

1. **Disk exhaustion reads as a crash, not as disk-full.** Size
   `pd-balanced` for the job's real artifacts (the input file, the model
   weights if not baked into the image, the output track) and check
   available space in the startup log, not just at the end.
2. **RUNNING is not working (Critical Pitfalls).** Instance status says
   nothing about the job. `status.json`'s heartbeat is the only real health
   signal; `startup.sh` writes `nvidia-smi`'s result as its first progress
   line specifically so "no GPU" is distinguishable from "still booting".
3. **A tool call caps at roughly 600 seconds.** Create + boot + pull + run
   is 6 to 20 minutes end to end (Critical Pitfalls). Never block a single
   tool call on the whole run: launch `scripts/cloud_gpu_test.ps1` detached
   and poll the log, the same way the app's own runner is a polled state
   machine, not a blocking call.
4. **A process exit code of 0 is not a pass.** Read `result.json` / the
   verdict the worker actually wrote; do not infer success from an SSH
   session or a script returning cleanly.
5. **The VM runs the released image digest, not your working tree
   (Critical Pitfalls).** A worker source change is not on the VM until a
   tagged release builds the image, or `scripts/cloud_gpu_test.ps1 -Image
   <digest>` explicitly overrides it and says so in the log. If a run
   behaves like it is running old code, check the digest it actually pulled
   before doubting the fix.
6. **Quota is not stockout (Critical Pitfalls).** Quota is per-region,
   fixable by the user, and worth checking before you start, not after a
   failure. Stockout is per-zone and transient; it is the only one of the
   two worth an automatic retry. `GPUS_ALL_REGIONS=0` overrides a regional
   quota of 1 — check the project-wide ceiling, not just the region you
   happen to be looking at.

## End state

After any cloud-creating run, `CloudCli resources list --install <id>` (or
the equivalent `gcloud compute instances list --project <id>` if `CloudCli`
is not yet built) must show **either an empty list, or you saying in your
report exactly which resource you left running, why, and until when.** An
unlabelled resource is invisible to this check by construction (Hard Rule
10), which is exactly why the label requirement above is not optional.

## What to bring home

A cloud run that produces no committed artifact did not happen:

- A `docs/research/<topic>_<date>/README.md` (format: see `docs/README.md`'s
  template skeletons) stating the machine type, the wall clock, the exact
  command, and the headline numbers.
- The raw result files, or their bucket paths plus a summary small enough
  to read, if they are large.
- One GitHub issue per distinct defect, with the observed evidence quoted.
  A count of failures is not an issue; a mechanism is.

## Related

`docs/cloud_design.md` (preflight, error classes, labels, cost, termination
— the full spec this skill's checklist is drawn from), `CLAUDE.md`'s
Critical Pitfalls section, `OWNER_TODO.md` items 1 and 2 (#21, #24, both of
which block any real run this skill would otherwise walk you through).
