"""Batch-level cost guardrails: max files per batch, max total nt (issue #248).

**Why this exists**: a limit here exists because of money, not because of a technical
ceiling (windowing already tiles a sequence of any length; there is no *correctness*
reason to cap a batch). The app is expected to validate this first, before ever creating
a job or spending a cent of GPU time — this module is the worker's re-validation, the
same shape as ``analysis.windowing.validate_context``'s re-check of context length: a
misconfigured or bypassed client must never silently produce an unbounded bill.

**The limits themselves live in the manifest** (``manifest.limits.maxInputs`` /
``maxTotalNt``, see ``worker.manifest.Limits``), not hardcoded here, so the app and the
worker read the exact same policy number rather than two independently-maintained ones
that could drift. The *defaults* below are what a manifest gets when it omits them.

**An honest limit on this module itself**: the per-GPU-tier hourly rates below come from
``docs/research/2026-09-19-gpu-pricing-and-instances.md``, and every one of them is
tagged ``THEORY (unverified)`` there (issue #266) — not read directly off Google's own
pricing pages (that note's section 2 explains why), corroborated instead by several
third-party aggregators agreeing closely with each other. There is, separately, **no
verified nt-per-second throughput figure for Evo 2 anywhere in this repository** — nothing
in ``docs/research/``, the design spec, or the job contract's own per-stage-deadline
language ("running computed from nt count and window/direction count") commits to an
actual number, only to the shape of a future computation. Inventing one here, even
hedged, would be materially less trustworthy than the pricing table above (which is at
least cross-checked against several independent sources); it would be a single guess with
nothing to check it against. So the refusal message below states real, measured facts
(the batch's own file count and total nt, both exact) and the one real cost fact this
repo has (the GPU's hourly rate), but deliberately does **not** project a dollar total for
the refused batch — doing so would be presenting a fabricated estimate as if it were real,
which the shared brief explicitly warns against. See this module's own issue (#248)
closing report for the recommended follow-up: benchmark real Evo 2 throughput on L4/A100
and record it as a dated research note, the same way the pricing figures were, so a real
time/dollar projection becomes possible.
"""

from __future__ import annotations

# A reasonable STARTING policy cap, not derived from a verified throughput benchmark (see
# the module docstring) — retune once real throughput numbers exist. 50 files keeps a
# batch well within what job_contract.md's per-input processing model was built around
# (worker/tests exercise up to a handful today); 20 Mnt is comfortably above a single
# bacterial genome (~5 Mb) times several, while still well short of "someone accidentally
# queued a whole chromosome set."
DEFAULT_MAX_INPUTS = 50
DEFAULT_MAX_TOTAL_NT = 20_000_000

# GCP on-demand $/hour by GPU tier, from docs/research/2026-09-19-gpu-pricing-and-instances.md
# section 3. THEORY (unverified) as of that note (issue #266) — see this module's own
# docstring. Keys match the design spec's own tier names (Appendix B section 4.1).
GPU_HOURLY_USD_THEORY: dict[str, float] = {
    "l4": 0.85,
    "a100-40": 3.67,
    "a100-80": 5.07,
}
DEFAULT_GPU_TIER = "l4"  # the design's own default tier (D-series defaults, appendix B §4.1)


class BatchLimitError(ValueError):
    """Raised when a batch's file count or total nt exceeds ``manifest.limits``."""

    code = "BATCH_LIMIT_EXCEEDED"


def format_batch_limit_message(
    *,
    n_inputs: int,
    total_nt: int,
    max_inputs: int,
    max_total_nt: int,
    gpu_tier: str = DEFAULT_GPU_TIER,
) -> str:
    """The user-facing refusal message: real numbers, not "too large."

    Names exactly which limit(s) were exceeded and by how much, and gives the one real
    cost fact available (the configured GPU tier's hourly rate, clearly marked as an
    unverified estimate) — but does not claim a specific dollar total for this batch; see
    the module docstring for why that would be dishonest rather than merely imprecise.
    """
    reasons = []
    if n_inputs > max_inputs:
        reasons.append(f"{n_inputs} files (limit {max_inputs})")
    if total_nt > max_total_nt:
        reasons.append(f"{total_nt:,} nt total (limit {max_total_nt:,})")
    over = " and ".join(reasons)

    rate = GPU_HOURLY_USD_THEORY.get(gpu_tier)
    if rate is not None:
        rate_note = (
            f"the default {gpu_tier.upper()} tier runs about ${rate:.2f}/hour "
            "(unverified estimate — docs/research/2026-09-19-gpu-pricing-and-instances.md)"
        )
    else:
        rate_note = "GPU hourly rates are in docs/research/2026-09-19-gpu-pricing-and-instances.md"

    return (
        f"This batch is {n_inputs} file(s), {total_nt:,} nt total — over the "
        f"configured limit ({over}). Cost is GPU time, not file count: {rate_note}. This "
        "worker build has no verified nt-per-second throughput figure to convert that "
        "into an exact time or dollar total (a real gap, not an omission — see "
        "batch_limits.py's module docstring), so it cannot quote a price for this batch. "
        "Split it into smaller batches, or raise manifest.limits.maxInputs/maxTotalNt if "
        "you intend to run this much at once."
    )


def check_batch_limits(
    *,
    n_inputs: int,
    total_nt: int,
    max_inputs: int,
    max_total_nt: int,
    gpu_tier: str = DEFAULT_GPU_TIER,
) -> None:
    """Raise :class:`BatchLimitError` if the batch exceeds either limit; otherwise no-op."""
    if n_inputs > max_inputs or total_nt > max_total_nt:
        raise BatchLimitError(
            format_batch_limit_message(
                n_inputs=n_inputs,
                total_nt=total_nt,
                max_inputs=max_inputs,
                max_total_nt=max_total_nt,
                gpu_tier=gpu_tier,
            )
        )
