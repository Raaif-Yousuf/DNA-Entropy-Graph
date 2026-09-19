# GPU pricing and instance matrix (GCP now, AWS placeholder), researched 2026-09-19

> Snapshot; goes stale by design. Issues are the tracker (Hard Rule 17). Issue #214
> (pricing catalog service, live Billing Catalog API lookup with a shipped fallback
> table) is the thing that eventually makes this file's numbers unnecessary to
> hand-maintain; until it lands, this note is the source `pricing.json` and the cost
> estimator (#98) should trace their numbers back to.

## 1. The question and why now

Three places already carry GPU and cloud-storage prices written during tonight's docs
pass without an independently dated source of their own: `docs/tech_stack.md` (no
pricing at all, it turns out, see section 5), `docs/user_guide/06-costs-and-cleanup.md`'s
worked examples, and the design spec's own hardware matrix
(`docs/superpowers/specs/2026-09-18-appendix-b-cloud-design.md` section 4.1). Issue #266
asks for one dated place these numbers live, checked against real sources rather than
carried forward from memory, since a wrong number here becomes a wrong number in front of
a lab biologist deciding whether they can afford a run.

## 2. What was checked, and an honest account of what could not be

**Attempted, and failed, against the vendor's own pages** (Google Cloud), each tried more
than once with different prompts, on 2026-09-19: `cloud.google.com/compute/gpus-pricing`,
`cloud.google.com/compute/vm-instance-pricing`, `cloud.google.com/compute/all-pricing`,
`cloud.google.com/products/compute/pricing/accelerator-optimized`,
`cloud.google.com/compute/disks-image-pricing`, `cloud.google.com/storage/pricing`. Every
one of these came back from the fetch tool as `[Content truncated due to length...]` with
only the page title visible, never the actual pricing table. This looks like a real
limitation of the fetch tool against these specific large, JS-rendered pricing tables, not
a sign the pages lack the data. **Attempted, and failed the same way, against AWS's own
pages**: `aws.amazon.com/ec2/instance-types/g6/`, `aws.amazon.com/ec2/instance-types/g5/`,
`aws.amazon.com/ec2/pricing/on-demand/`, these returned real page content (specs were
visible for the G6/G5 pages) but the pricing tables themselves are loaded by
JavaScript after the page loads and were not present in what the fetch tool retrieved.

**What this means for every number below**: none of them come from the vendor's own page
directly, despite a genuine, repeated attempt to reach one. Per the shared brief's own
instruction, every figure in this note is therefore marked `THEORY (unverified)`, not
`MEASURED`, regardless of how it was obtained. Where a number is corroborated by several
independent third-party pricing aggregators that agree closely with each other (and, in
most cases, with the number the design spec had already recorded, suggesting the spec's
own authors read the same vendor data at some earlier point), that agreement is noted as
raising confidence, but it does not upgrade the tag. **The next person with real browser
access to `cloud.google.com`'s and `aws.amazon.com`'s live, rendered pricing pages should
replace every `THEORY` tag below with `MEASURED <date>` after reading the vendor page
directly**, or, better, wait for #214 to make this file's hand-maintenance unnecessary.

Third-party sources consulted (leads, not sources of record, per the brief's own
instruction to chase every one back to the vendor and mark it `THEORY` when that chase
fails): [CloudPrice.net](https://cloudprice.net), [Economize
Cloud](https://www.economize.cloud), [Vantage Instances](https://instances.vantage.sh),
[Holori Calculator](https://calculator.holori.com), [DevZero
Instances](https://www.devzero.io/instances), [Spare Cores](https://sparecores.com),
cross-checked against Google search result snippets on 2026-09-19.

## 3. Findings: per-GPU-tier table

Prices are per hour in USD, for `us-central1` (Iowa) unless noted. THEORY (unverified)
2026-09-19 for every cell in this table, per section 2 above.

| Machine type | Accelerator | VRAM | Quota metric | On-demand $/h | Spot $/h | Notes |
|---|---|---|---|---|---|---|
| `g2-standard-8` | 1x NVIDIA L4 | 24 GB | `NVIDIA_L4_GPUS` | ~$0.85 (bundled machine+GPU; the machine-type-only component alone is ~$0.29, the GPU is billed as a separate line item on the real invoice that sums to the bundled figure) | ~$0.18 | Default tier. Matches the design spec's own recorded $0.85/~0.18-0.30 range closely. |
| `a2-highgpu-1g` | 1x NVIDIA A100 40GB | 40 GB | `NVIDIA_A100_GPUS` | ~$3.67 | ~$0.43 | On-demand figure matches the spec's own recorded $3.67 exactly across every aggregator checked. **Spot figure does not match the spec**, see section 4. |
| `a2-ultragpu-1g` | 1x NVIDIA A100 80GB | 80 GB | `NVIDIA_A100_80GB_GPUS` | ~$5.07 | ~$0.58 | Same pattern: on-demand matches the spec exactly, Spot does not, see section 4. |
| `a3-highgpu-2g` | 2x NVIDIA H100 80GB | 160 GB total (80 GB per GPU) | `NVIDIA_H100_GPUS` | **not offered as standard on-demand** for this shape, per two independent sources describing the A3 family; the spec's own table already says exactly this ("not offered on-demand for small A3 shapes") | not reliably found; one aggregator returned $0.29/h for the whole 2-GPU node, which does not pass a sanity check against the same family's larger `a3-highgpu-8g` node (~$10.98/h per GPU on-demand, ~$87.84/h for 8 GPUs, found from the same search pass) and is excluded as likely a data error rather than a real Spot price | Spot/Flex-start-only, exactly as the spec already assumes. A believable Spot estimate, extrapolated from the 8-GPU node's per-GPU on-demand rate and a typical 60-80% Spot discount, is roughly $4 to $9/h for the 2-GPU shape, but this is arithmetic on a lead, not a reading, and should not go into `pricing.json` as-is. |

## 4. What this changes for us: two findings worth an issue

1. **The g2-standard-8 "machine + GPU are separate line items that sum to the headline
   figure" shape** is worth a note for whoever builds `pricing.json` (#98/#214): every
   number this project has used so far (the spec, `tech_stack.md`, the user guide) already
   uses the correct bundled ~$0.85/h figure, so nothing needs correcting here, but a
   pricing-catalog implementation reading raw Billing Catalog SKUs will see the machine
   type and the GPU as two separate SKUs and needs to sum them, not pick one.
2. **A100 Spot pricing looks meaningfully cheaper than the design spec assumed.** The
   spec's own table (Appendix B section 4.1) records A100-40 Spot at "~1.1 to 2.1" and
   A100-80 Spot at "~1.5 to 2.0" USD/hour. Every aggregator checked in this pass instead
   shows roughly $0.43 and $0.58, a difference of 2 to 4x, not a rounding difference. Both
   numbers are unverified against the vendor (section 2), so this note cannot say which
   one is right, only that they disagree by more than chance. Since Spot pricing feeds the
   cost estimator's "try Spot" savings message directly, using the wrong end of a 4x
   spread would materially mislead a user about how much Spot actually saves them. Filed
   as issue **#303** (see below) rather than silently picking a number.

No correction was needed in `docs/tech_stack.md` (it turns out to carry no cloud pricing
at all, only software dependency licences, see section 5) or in
`docs/user_guide/06-costs-and-cleanup.md` (its on-demand L4 figure, ~$0.85/h, already
matches what this research found within rounding; its worked examples are reproduced and
cross-checked in section 6 below without needing a correction).

## 5. A note on `docs/tech_stack.md`

The shared brief's assignment for this issue said to use "the rates in
`docs/tech_stack.md` and the spec." Checked this session: `docs/tech_stack.md` contains
**no cloud pricing whatsoever**, it is a dependency-and-licence inventory (numpy, torch,
evo2, CommunityToolkit.Mvvm, and so on), not a cost source. The only project-internal
pricing source that exists today is the design spec's own Appendix B section 4.1 table.
This is not a defect in `tech_stack.md`, it is simply not that file's job, but it is worth
recording here so the next person does not go looking for prices in the wrong file. No
issue filed for this; it is a one-line clarification, not a gap in the product.

## 6. The costs people forget

All THEORY (unverified) 2026-09-19, same sourcing caveat as section 3.

- **Boot disk (pd-balanced), while the VM is running or stopped**: ~$0.10/GB/month,
  consistent across every source checked. For the default 150 GB disk (the 7B model
  tier): **~$15.00/month**. For the 300 GB disk (the 40B tier): **~$30.00/month**. This
  cost is billed whether the VM is running or stopped, since it is a property of the disk,
  not the compute. **This is the number that surprises people a month later**: a run
  finishes, the default action is Stop (not Delete), and the ~$15/month keeps accruing
  silently until either the user deletes the VM by hand or the app's own 7-day
  stopped-VM sweep deletes it automatically. $15/month works out to roughly **$0.49/day,
  ~$3.45/week**.
- **Cloud Storage, Standard class, US multi-region**: ~$0.020/GB/month. For a typical
  lab user's usage (a handful of small sequence files plus one cached copy of the model's
  weights, ~14 GB for the 7B tier), this is well under a dollar a month; the model-weight
  cache is the only line item here big enough to notice at all, at roughly $0.28/month.
- **Egress, when the user downloads their results**: ~$0.12/GB for the first 1 TB of
  internet egress at the default (Premium) network tier, dropping at higher volumes. A
  typical result set (a FASTA, a GenBank file, a couple of small text tracks) is well
  under 1 MB, so this cost rounds to zero for a normal run; it would only start to matter
  for someone downloading very large batches repeatedly. Traffic **between the Cloud
  Storage bucket and the Compute Engine VM in the same region** is a different,
  much-lower-cost path than internet egress and is not the number above; the design spec
  already notes this distinction when discussing cross-continent zone fallback
  (Appendix B section 4.4).

## 7. A worked total, cross-checked against the user guide

Reproducing the two examples from `docs/user_guide/06-costs-and-cleanup.md` with the
figures from section 3, to confirm the guide and this note agree:

- **One plasmid, first run in a new project** (fresh VM, ~8 to 10 minutes at ~$0.85/h):
  `0.85 * (9/60)` ≈ **$0.13**. The user guide says "roughly 8 to 10 minutes, well under 20
  cents." Consistent.
- **The same plasmid, later run** (warm VM, ~5 minutes): `0.85 * (5/60)` ≈ **$0.07**. The
  user guide says "roughly 5 minutes, around 7 cents." Consistent, exact match.

No correction needed to the user guide from this pass.

## 8. AWS placeholder

AWS is milestone v1.1 (post-GCP); per the shared brief, an honest gap here is fine, a
guess is not. All figures below are THEORY (unverified), for `us-east-1` (N. Virginia),
and were **not** reachable on AWS's own pricing pages this session (section 2); they come
from third-party aggregators only and should be treated as considerably less trustworthy
than the GCP figures above, which at least cross-check tightly against the design spec's
own previously recorded numbers.

| Instance | Accelerator | VRAM | On-demand $/h | Notes |
|---|---|---|---|---|
| `g6.xlarge` | 1x NVIDIA L4 | 24 GB | ~$0.80 | Named in the AWS epic issues as the L4-equivalent tier. Close to, but not identical to, GCP's L4 on-demand price; not independently confirmed. |
| `g5.xlarge` | 1x NVIDIA A10G | 24 GB | ~$1.01 | Named as the A10G fallback tier. A10G is architecturally between L4 and A100 in performance; no GCP equivalent exists at this exact spec. |

Spot/preemptible pricing for both was **not found** in this pass (not merely unverified,
genuinely not located even as a third-party lead); mark as **not yet measured** rather
than guessing. No AWS storage or egress figures were researched this session, since no
AWS storage design exists yet to cost against (the AWS epic is still at the
gateway-abstraction stage, issue #227).

## 9. How to refresh this note

1. Re-read, with real browser access (not this agent's automated fetch tool, which could
   not render these specific pages, per section 2): `cloud.google.com/compute/gpus-pricing`,
   `cloud.google.com/products/compute/pricing/accelerator-optimized`,
   `cloud.google.com/compute/disks-image-pricing`, `cloud.google.com/storage/pricing`, and
   for AWS, the EC2 On-Demand and Spot pricing pages for the G5 and G6 families.
2. Update every `THEORY (unverified)` tag in this note to `MEASURED <date>` with the exact
   URL read, or correct the figure if it has changed.
3. Cross-check the corrected figures against `docs/user_guide/06-costs-and-cleanup.md`'s
   worked examples and `docs/superpowers/specs/2026-09-18-appendix-b-cloud-design.md`
   section 4.1; file an issue for either one if a real vendor number no longer matches
   what they say.
4. Once issue #214 (pricing catalog service, live Billing Catalog API lookup) ships, this
   file becomes the one-time seed for its shipped fallback table rather than something
   `pricing.json` needs re-reading against by hand.

## 10. What was deliberately not pursued, and why

- No GCP or AWS API call, no `gcloud`/`aws` CLI, no authentication of any kind, per the
  shared brief; every figure above came from public pricing pages and search results
  only.
- No attempt to price the 1B/20B variants of Evo 2 separately (spec D11: `evo2_1b_base`
  is hidden under Advanced, needs the same Hopper-only tier as the 40B model, so it
  shares `a3-highgpu-2g`'s pricing row above rather than needing its own).
- No Azure pricing researched; Azure does not appear anywhere in the current design or
  issue set as a target cloud.

## Related

[`docs/superpowers/specs/2026-09-18-appendix-b-cloud-design.md`](../superpowers/specs/2026-09-18-appendix-b-cloud-design.md)
section 4.1 (the design's own hardware matrix, what this note checks against),
[`docs/user_guide/06-costs-and-cleanup.md`](../user_guide/06-costs-and-cleanup.md) (the
reader-facing version of section 7 above), [`docs/tech_stack.md`](../tech_stack.md) (the
dependency-and-licence inventory this note is not a substitute for, see section 5).
