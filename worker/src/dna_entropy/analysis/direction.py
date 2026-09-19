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
from .surprisal import surprisal as compute_surprisal
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
    # The run's NOMINAL, configured K -- the value analyze_direction() was called with,
    # unchanged by an OOM-halving retry (issue #313) on either direction's own pass.
    # Disproven theory (issue #407): this field itself is NOT a bug to "fix" by making it
    # track a post-halving value -- reporting the configured K is a legitimate, useful
    # thing on its own. The REAL bug #407 found was that `_combine`'s qualification
    # threshold used to be this same shared, nominal value for BOTH directions, which
    # silently broke after a halving on ONLY one side; `_combine` now takes each
    # direction's own actually-achieved `window - stride` instead (see its docstring).
    # `window`/`stride` below already ARE the final, post-halving values when a halving
    # happened (threaded straight from whichever WindowPlan actually ran).
    context_length: int
    window: int
    stride: int
    seam: int | None  # position K, when L >= 2K makes the clean forward/reverse split apply
    reduced_context_count: int  # positions where NEITHER direction reached K (only L < 2K)
    forward_values: np.ndarray | None = None  # populated for BOTH_SEPARATE
    reverse_values: np.ndarray | None = None  # populated for BOTH_SEPARATE
    notices: list[str] = field(default_factory=list)
    # issue #123: per-position surprisal (-log2 P(actual base)), combined by the SAME rule
    # as `values` (forward/reverse preference by context sufficiency, or averaged under
    # BOTH_AVERAGED) -- computed from the SAME fwd/rev `probs` this dataclass's `values`
    # already came from, at zero extra GPU cost (no second predictor call). `None` only for
    # a hand-built DirectionResult in a test that never called analyze_direction(); a real
    # run always populates it, unconditionally -- cfg.include_surprisal (config.py) gates
    # only whether a WRITER emits it, never whether it is computed.
    surprisal_values: np.ndarray | None = None
    # NOT a forward_surprisal/reverse_surprisal pair here (unlike forward_values/
    # reverse_values above): the TSV's BOTH_SEPARATE 5-column shape does not yet grow
    # surprisal columns (docs/science_and_formats.md section 2b's "known scope limit"),
    # so a per-direction surprisal field would have no reader -- found by
    # `check_unused_fields.py` during this same session (both flagged UNREAD) and removed
    # rather than left "for later," same reasoning as `WindowPlan.context`'s removal
    # (test_windowing.py). Compute them locally in analyze_direction() instead, the day a
    # writer actually wants them.
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
    fwd_context_length: int,
    rev_context_length: int,
    *,
    averaged: bool,
) -> tuple[np.ndarray, int]:
    """Section 5.6's combination rule.

    Base ``i`` takes forward once it has ``>= K`` bases before it, else reverse once it
    has ``>= K`` bases after it, else (only possible when ``L < 2K``) whichever direction
    has more context — recorded as "reduced context". ``averaged`` additionally means:
    where BOTH directions independently reach ``>= K`` context, take their mean instead of
    preferring forward outright.

    ``fwd_context_length``/``rev_context_length`` are deliberately TWO separate values, not
    one shared ``context_length`` (issue #407, MEASURED 2026-09-19: a single shared
    threshold was a real bug, not just a theory). An OOM-halving retry (issue #313) can
    shrink the K a SINGLE direction's pass actually ran with — ``run_windowed`` halves
    independently per direction, since each direction's ``run_windowed`` call starts fresh
    from the caller's nominal ``context_length`` and only reacts to its OWN OOM. A shared,
    nominal ``context_length`` threshold then becomes impossible for the halved side to
    ever satisfy (its own ``*_context`` array is capped at its new, smaller ``window - 1``,
    which can be below the UNhalved side's nominal K), silently locking that direction out
    of "qualifies" for the rest of the sequence and collapsing combined/averaged mode to
    the other direction alone — with no error, no notice, just a quietly worse track.
    Each side must be judged against the K it ACTUALLY ran with.
    """
    length = fwd_entropy.shape[0]
    values = np.empty(length, dtype=np.float32)

    fwd_ok = fwd_context >= fwd_context_length
    rev_ok = rev_context >= rev_context_length
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
    fwd_surprisal = rev_surprisal = None

    if direction in _NEEDS_FORWARD:
        fwd = run_windowed(
            predictor,
            seq,
            context_length=context_length,
            ceiling=ceiling,
            on_window=on_window,
        )
        fwd_entropy = shannon_entropy(fwd.probs)
        # issue #123: surprisal from the SAME fwd.probs entropy was just computed from --
        # zero extra predictor calls, per analysis/surprisal.py's own module docstring.
        fwd_surprisal = compute_surprisal(fwd.probs, seq)
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
        # Surprisal against the REVERSE-COMPLEMENT sequence (rc), read in rc's own order,
        # THEN flipped back -- same coordinate-flip discipline as rev_entropy above (Hard
        # Rule "reverse means reverse complement": rc[j] is base L-1-j of seq).
        rev_surprisal = compute_surprisal(rev.probs, rc)[::-1].copy()
        if window is None:
            window, stride = rev.window, rev.stride
        notices += rev.notices

    length = len(seq)
    seam: int | None = None
    reduced = 0

    if direction is Direction.FORWARD_ONLY:
        values = fwd_entropy
        surprisal_values = fwd_surprisal
    elif direction is Direction.REVERSE_ONLY:
        values = rev_entropy
        surprisal_values = rev_surprisal
    else:
        # issue #407, MEASURED 2026-09-19: each direction's OWN actually-used K, not the
        # shared nominal `context_length` -- an OOM-halving retry (issue #313) can shrink
        # ONE direction's K independently (each `run_windowed` call only reacts to ITS OWN
        # OOM), and `window - stride` always recovers the K that pass really ran with,
        # regardless of whether a halving happened (see _combine's own docstring for why
        # a single shared threshold silently broke combined/averaged mode after exactly
        # this scenario).
        fwd_k_used = fwd.window - fwd.stride
        rev_k_used = rev.window - rev.stride
        values, reduced = _combine(
            fwd_entropy,
            fwd_context,
            rev_entropy,
            rev_context,
            fwd_k_used,
            rev_k_used,
            averaged=(direction is Direction.BOTH_AVERAGED),
        )
        # Combined by the IDENTICAL rule (same fwd_context/rev_context masks -> the same
        # `reduced` count as above, deliberately discarded here rather than reassigned):
        # surprisal is a second metric riding the SAME per-position forward/reverse
        # selection entropy already used, not a second, independent combination decision.
        surprisal_values, _ = _combine(
            fwd_surprisal,
            fwd_context,
            rev_surprisal,
            rev_context,
            fwd_k_used,
            rev_k_used,
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
        surprisal_values=surprisal_values,
    )
