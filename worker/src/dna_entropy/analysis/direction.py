"""Forward + reverse-complement prediction, tiled by window, combined per ``Direction``.

The owner's single most important ask (design section 5.6, point 3): predict the tail of
a sequence from a forward read and the HEAD from a reverse-complement read, so the first
bases are as accurate as the rest — a plain forward pass always starts with zero context.

Reverse means **reverse complement**, never reversed text: entropy of the complement
distribution equals entropy of the base distribution (complementing is just a relabelling
of the same 4 symbols: A<->T, C<->G), so the forward and reverse-complement entropy
tracks are directly comparable position-for-position. This is the single most expensive
mistake available in this codebase to get wrong — see ``test_reverse_uses_complement_not_reversed_text``.
"""

from __future__ import annotations

from collections.abc import Callable
from dataclasses import dataclass, field

import numpy as np

from ..config import Direction
from ..predictors.base import Predictor, PredictorOOMError, check_probability_matrix
from .entropy import shannon_entropy
from .windowing import WindowPlan, halved, plan_windows

_COMPLEMENT = str.maketrans("ACGT", "TGCA")


def reverse_complement(seq: str) -> str:
    """Reverse-complement a validated, uppercase A/C/G/T sequence.

    NOT the same as ``seq[::-1]`` (plain reversal) — that is not DNA and is never used
    here. ``A<->T``, ``C<->G``; the whole string is also reversed so it reads 5'->3'.
    """
    return seq.translate(_COMPLEMENT)[::-1]


@dataclass
class SinglePassResult:
    """One direction's stitched, whole-sequence result."""

    probs: np.ndarray  # (L, 4) — in this pass's own read order
    context: np.ndarray  # (L,) int64 — exact local context count behind each row
    window: int
    stride: int
    notices: list[str] = field(default_factory=list)


def _stitch_forward(window_probs: list[np.ndarray], plan: WindowPlan) -> tuple[np.ndarray, np.ndarray]:
    """Stitch per-window ``(w, 4)`` forward predictions into one ``(L, 4)`` array plus a
    per-position context count.

    ``context[i]`` is the number of preceding bases used to predict base ``i`` — its LOCAL
    index within whichever window produced it. When more than one window covers a
    position (only possible near the end, because of the final window's right-alignment),
    the window giving MORE context wins; by construction of the ``S = W - K`` stride this
    also happens to be the unique window whose "trusted" range ``[K, W)`` contains that
    position in the ordinary (non-final) case, so ties never actually occur in practice —
    but resolving by max-context keeps this correct even if they did.
    """
    length = plan.length
    probs = np.empty((length, 4), dtype=np.float32)
    context = np.full(length, -1, dtype=np.int64)
    for start, w in zip(plan.starts, window_probs, strict=True):
        width = w.shape[0]
        local = np.arange(width)
        global_idx = start + local
        better = local > context[global_idx]
        probs[global_idx[better]] = w[better]
        context[global_idx[better]] = local[better]
    return probs, context


def _run_plan(
    predictor: Predictor,
    seq: str,
    plan: WindowPlan,
    *,
    on_window: Callable[[], None] | None = None,
) -> list[np.ndarray]:
    out = []
    for start in plan.starts:
        window_seq = seq[start : start + plan.window]
        probs = predictor.predict(window_seq)
        check_probability_matrix(probs, len(window_seq))
        out.append(probs)
        if on_window is not None:
            # Called AFTER a window completes, never mid-window (one forward pass cannot
            # be interrupted partway) — the cooperative cancellation point
            # docs/job_contract.md §6 requires ("between windows and between contigs").
            # May raise (e.g. worker.cancel.JobCancelledError); propagates as-is.
            on_window()
    return out


def run_windowed(
    predictor: Predictor,
    seq: str,
    *,
    context_length: int,
    ceiling: int,
    on_window: Callable[[], None] | None = None,
) -> SinglePassResult:
    """Run ``predictor.predict`` once per window over ``seq`` and stitch forward-style.

    One model forward pass per WINDOW — never per base (a true per-base rolling window is
    rejected by design as too expensive). Retries exactly once, with a halved window, if
    the predictor raises :class:`~dna_entropy.predictors.base.PredictorOOMError`; a second
    OOM propagates. ``on_window``, if given, is called after every completed window (see
    :func:`_run_plan`) — a worker-layer cancellation check plugs in here without this
    module needing to know anything about ``control/cancel``.
    """
    plan = plan_windows(len(seq), context_length, ceiling)
    notices: list[str] = []
    try:
        window_probs = _run_plan(predictor, seq, plan, on_window=on_window)
    except PredictorOOMError as exc:
        new_context, new_ceiling = halved(context_length, ceiling)
        notices.append(
            f"Out of GPU memory at window={plan.window} ({exc}); retrying once with a "
            f"smaller window (context={new_context}, ceiling={new_ceiling})."
        )
        plan = plan_windows(len(seq), new_context, new_ceiling)
        window_probs = _run_plan(predictor, seq, plan, on_window=on_window)  # a 2nd OOM propagates
    probs, context = _stitch_forward(window_probs, plan)
    return SinglePassResult(
        probs=probs,
        context=context,
        window=plan.window,
        stride=plan.stride,
        notices=notices,
    )


@dataclass
class DirectionResult:
    """The per-contig output of windowing + direction combination for one run."""

    values: np.ndarray  # (L,) bits — the SELECTED/combined track (always populated)
    direction: Direction
    context_length: int
    window: int
    stride: int
    seam: int | None  # position K, when L >= 2K makes the clean forward/reverse split apply
    reduced_context_count: int  # positions where NEITHER direction reached K (only L < 2K)
    forward_values: np.ndarray | None = None  # populated for BOTH_SEPARATE
    reverse_values: np.ndarray | None = None  # populated for BOTH_SEPARATE
    notices: list[str] = field(default_factory=list)
    # The GPU per-window ceiling this pass was run with (windowing.WindowPlan.ceiling,
    # threaded through rather than recomputed — see run_windowed/analyze_direction).
    # Default 0 for a hand-built DirectionResult (e.g. in a test) that never ran a real
    # windowed pass; a real run always sets this via analyze_direction's own `ceiling`
    # parameter. Recorded here so a report can show WHY window < 2*context_length (the
    # ceiling clamped it) without digging through run notices text.
    ceiling: int = 0


_NEEDS_FORWARD = {
    Direction.BOTH_COMBINED,
    Direction.BOTH_AVERAGED,
    Direction.BOTH_SEPARATE,
    Direction.FORWARD_ONLY,
}
_NEEDS_REVERSE = {
    Direction.BOTH_COMBINED,
    Direction.BOTH_AVERAGED,
    Direction.BOTH_SEPARATE,
    Direction.REVERSE_ONLY,
}


def _combine(
    fwd_entropy: np.ndarray,
    fwd_context: np.ndarray,
    rev_entropy: np.ndarray,
    rev_context: np.ndarray,
    context_length: int,
    *,
    averaged: bool,
) -> tuple[np.ndarray, int]:
    """Section 5.6's combination rule.

    Base ``i`` takes forward once it has ``>= K`` bases before it, else reverse once it
    has ``>= K`` bases after it, else (only possible when ``L < 2K``) whichever direction
    has more context — recorded as "reduced context". ``averaged`` additionally means:
    where BOTH directions independently reach ``>= K`` context, take their mean instead of
    preferring forward outright.
    """
    length = fwd_entropy.shape[0]
    values = np.empty(length, dtype=np.float32)

    fwd_ok = fwd_context >= context_length
    rev_ok = rev_context >= context_length
    both_ok = fwd_ok & rev_ok
    only_fwd = fwd_ok & ~rev_ok
    only_rev = rev_ok & ~fwd_ok
    neither = ~fwd_ok & ~rev_ok

    if averaged:
        values[both_ok] = (
            fwd_entropy[both_ok].astype(np.float64) + rev_entropy[both_ok].astype(np.float64)
        ) / 2.0
    else:
        values[both_ok] = fwd_entropy[both_ok]  # forward always wins when both qualify
    values[only_fwd] = fwd_entropy[only_fwd]
    values[only_rev] = rev_entropy[only_rev]

    reduced = int(neither.sum())
    if reduced:
        idx = np.nonzero(neither)[0]
        fwd_wins = fwd_context[idx] >= rev_context[idx]
        values[idx[fwd_wins]] = fwd_entropy[idx[fwd_wins]]
        values[idx[~fwd_wins]] = rev_entropy[idx[~fwd_wins]]

    return values, reduced


def analyze_direction(
    predictor: Predictor,
    seq: str,
    *,
    context_length: int,
    ceiling: int,
    direction: Direction,
    on_window: Callable[[], None] | None = None,
) -> DirectionResult:
    """Run the windowed forward and/or reverse-complement passes and combine them.

    Calls the predictor at most twice per contig regardless of sequence length (once per
    direction actually needed), each call itself tiled into one predictor.predict() per
    window (never per base). ``on_window`` is called after every completed window in
    every direction pass — see :func:`run_windowed`.
    """
    notices: list[str] = []
    window = stride = None
    fwd_entropy = fwd_context = None
    rev_entropy = rev_context = None

    if direction in _NEEDS_FORWARD:
        fwd = run_windowed(
            predictor,
            seq,
            context_length=context_length,
            ceiling=ceiling,
            on_window=on_window,
        )
        fwd_entropy = shannon_entropy(fwd.probs)
        fwd_context = fwd.context
        window, stride = fwd.window, fwd.stride
        notices += fwd.notices

    if direction in _NEEDS_REVERSE:
        rc = reverse_complement(seq)
        rev = run_windowed(
            predictor,
            rc,
            context_length=context_length,
            ceiling=ceiling,
            on_window=on_window,
        )
        # rev.probs/.context are in the REVERSE-COMPLEMENT's own read order (index j came
        # from rc[j], i.e. original position L-1-j); flip back to original coordinates.
        rev_entropy = shannon_entropy(rev.probs)[::-1].copy()
        rev_context = rev.context[::-1].copy()
        if window is None:
            window, stride = rev.window, rev.stride
        notices += rev.notices

    length = len(seq)
    seam: int | None = None
    reduced = 0

    if direction is Direction.FORWARD_ONLY:
        values = fwd_entropy
    elif direction is Direction.REVERSE_ONLY:
        values = rev_entropy
    else:
        values, reduced = _combine(
            fwd_entropy,
            fwd_context,
            rev_entropy,
            rev_context,
            context_length,
            averaged=(direction is Direction.BOTH_AVERAGED),
        )
        if length >= 2 * context_length:
            seam = context_length
        if reduced:
            notices.append(
                f"{reduced} position(s) near the middle of the sequence had reduced "
                f"context in BOTH directions (the sequence is shorter than 2xK={2 * context_length}); "
                "their entropy uses whichever direction had more context (recorded as "
                "reduced context)."
            )

    return DirectionResult(
        values=values,
        direction=direction,
        context_length=context_length,
        window=window,
        stride=stride,
        seam=seam,
        reduced_context_count=reduced,
        ceiling=ceiling,
        forward_values=fwd_entropy if direction is Direction.BOTH_SEPARATE else None,
        reverse_values=rev_entropy if direction is Direction.BOTH_SEPARATE else None,
        notices=notices,
    )
