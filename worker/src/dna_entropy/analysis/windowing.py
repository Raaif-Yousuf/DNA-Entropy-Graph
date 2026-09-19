"""Tile a sequence into overlapping context windows for one direction's model passes.

Pure NumPy/stdlib, no predictor coupling, fully unit-testable with no GPU. See
docs/superpowers/specs/2026-09-18-dna-entropy-graph-design.md section 5.6.

**Definitions** (the owner's exact terms):
- ``K`` = context length: the amount of sequence the model must have seen before a
  prediction is trusted.
- ``W`` = window = ``min(2K, ceiling)``, where ``ceiling`` is the GPU's per-pass cap.
- ``S`` = stride = ``W - K``.
- Windows start at ``0, S, 2S, ...``; the LAST window is right-aligned (never short).
- Passes per direction = ``ceil((L - W) / S) + 1``, or ``1`` when ``L <= W``.

This is the cheap equivalent of a true per-base rolling context window (one forward pass
per base, rejected by design: "one forward pass per contig [per window]").
"""

from __future__ import annotations

import math
from dataclasses import dataclass


class WindowingError(ValueError):
    """Raised when K/ceiling/length make windowing impossible, or a pushback rule refuses.

    A separate error class from :class:`~dna_entropy.validation.validators.ValidationError`
    on purpose: this is a *second*, windowing-specific validation pass that runs even
    though the app is expected to validate first (section 5.6: "worker re-validates").
    """


@dataclass(frozen=True)
class WindowPlan:
    """The derived window/stride/offsets for tiling a length-``L`` sequence, one direction."""

    length: int  # L
    context: int  # K
    ceiling: int  # GPU ceiling used to compute W
    window: int  # W = min(2K, ceiling)
    stride: int  # S = W - K
    starts: tuple[int, ...]  # 0-based start offsets of each window; last is right-aligned

    @property
    def num_windows(self) -> int:
        return len(self.starts)

    def width_at(self, index: int) -> int:
        """Width of window ``index`` (all windows are ``W`` wide except a short sequence's
        single window, which is exactly ``length`` when ``length < window``)."""
        start = self.starts[index]
        return min(self.window, self.length - start)


def compute_window(context_length: int, ceiling: int) -> tuple[int, int]:
    """Return ``(W, S)`` for a given ``K`` (context_length) and GPU ceiling.

    ``W = min(2K, ceiling)``; ``S = W - K``. Raises :class:`WindowingError` if the
    ceiling is smaller than ``K`` itself (``S`` would be <= 0: no room for even one new
    base of prediction beyond the required context).
    """
    if context_length <= 0:
        raise WindowingError(f"context_length (K) must be positive, got {context_length}")
    if ceiling <= 0:
        raise WindowingError(f"ceiling must be positive, got {ceiling}")
    window = min(2 * context_length, ceiling)
    stride = window - context_length
    if stride <= 0:
        raise WindowingError(
            f"The GPU ceiling ({ceiling}) is smaller than the context length K "
            f"({context_length}); lower the context length or use a bigger GPU."
        )
    return window, stride


def plan_windows(length: int, context_length: int, ceiling: int) -> WindowPlan:
    """Compute the window plan for a sequence of ``length`` bases, one direction.

    A single window at offset 0 when ``length <= W`` (matches the prototype's one-pass
    behaviour exactly — this is what makes Forward-only reproduce it bit-for-bit).
    Otherwise, windows start at ``0, S, 2S, ...`` with the final window right-aligned to
    ``length - W`` so it is never short.
    """
    if length <= 0:
        raise WindowingError(f"length must be positive, got {length}")
    window, stride = compute_window(context_length, ceiling)

    if length <= window:
        starts: tuple[int, ...] = (0,)
    else:
        # ceil((L - W) / S) + 1, using only stdlib integer/float math.
        num_passes = math.ceil((length - window) / stride) + 1
        starts = tuple(min(i * stride, length - window) for i in range(num_passes))

    return WindowPlan(
        length=length,
        context=context_length,
        ceiling=ceiling,
        window=window,
        stride=stride,
        starts=starts,
    )


# --- pushback rules (section 5.6) ----------------------------------------------------

# Below this K, predictions near a window edge are dominated by the model's prior.
MIN_RECOMMENDED_CONTEXT_LENGTH = 1024
# Below this K, refuse outright — there isn't enough context for a meaningful prediction.
MIN_CONTEXT_LENGTH = 128
# Below this input length, refuse outright (ported from the prototype's own minimum).
MIN_SEQUENCE_LENGTH = 10


def validate_context(*, context_length: int, ceiling: int, seq_len: int) -> list[str]:
    """Re-validate K/ceiling/input-length against section 5.6's pushback rules.

    The app is expected to validate these first; the worker re-validates so a
    misconfigured or bypassed client can never silently produce a bad run. Returns
    non-fatal notices (warnings the caller should surface); raises
    :class:`WindowingError` for the two refuse conditions. Never silently clamps.
    """
    if context_length < MIN_CONTEXT_LENGTH:
        raise WindowingError(
            f"Context length K={context_length} is below the minimum of "
            f"{MIN_CONTEXT_LENGTH}; there is not enough context for a meaningful "
            "prediction. Choose a larger context length."
        )
    if seq_len < MIN_SEQUENCE_LENGTH:
        raise WindowingError(
            f"Sequence length {seq_len} nt is below the minimum of "
            f"{MIN_SEQUENCE_LENGTH} nt for windowed analysis."
        )

    notices: list[str] = []
    if context_length < MIN_RECOMMENDED_CONTEXT_LENGTH:
        notices.append(
            f"Context length K={context_length} is below the recommended minimum of "
            f"{MIN_RECOMMENDED_CONTEXT_LENGTH}: predictions near a window edge are "
            "dominated by the model's prior; results may be noisy."
        )
    if seq_len < context_length:
        notices.append(
            f"This sequence ({seq_len} nt) is shorter than the context length "
            f"(K={context_length}), so no base reaches full context; results are still "
            "produced."
        )
    if 2 * context_length > ceiling:
        notices.append(
            f"Context length K={context_length} requests a preferred window of "
            f"2K={2 * context_length}, which exceeds the GPU ceiling ({ceiling}). "
            f"The window is clamped to {ceiling} (stride shrinks accordingly); use a "
            "smaller context length or a bigger GPU for full head-room."
        )
    return notices


def halved(context_length: int, ceiling: int) -> tuple[int, int]:
    """Halve the WINDOW after an out-of-memory retry (section 5.6: "halve W, keep K if
    possible, else halve both K and W"). Returns ``(new_context, new_ceiling)`` such that
    ``compute_window(new_context, new_ceiling)`` derives a window strictly smaller than
    the one that just OOM'd (or, if the plan was already at its smallest valid shape,
    the same one — a floor, not a crash).

    "Keep K if possible" means: keep K when the window that just OOM'd was
    **ceiling-bound** (``W == ceiling < 2K``) — halving the ceiling alone then genuinely
    shrinks W, since K was never the constraint ``min(2K, ceiling)`` picked. When the
    window was **K-bound** (``W == 2K <= ceiling``), halving the ceiling alone does
    nothing — ``min(2K, ceiling)`` still picks ``2K`` — so K itself must shrink too
    (issue #313: this was the actual bug. The previous implementation always halved the
    *ceiling* and let the caller re-derive W via a fresh ``min(2K, ceiling)``, which only
    moves W when W was already ceiling-bound. The default context length is K-bound on
    every GPU tier above an L4, so every OOM retry silently re-ran an identically shaped
    pass and OOM'd again). Never returns a ceiling or K below 1.
    """
    old_window, old_stride = compute_window(context_length, ceiling)
    ceiling_was_binding = old_window < 2 * context_length

    if ceiling_was_binding and old_stride >= 2:
        # K stays exactly as-is; shrink the ceiling as far as it can go while still
        # leaving K a positive stride. A ceiling-bound region always has
        # `ceiling // 2 < context_length` (ceiling < 2K by definition here), so this
        # floors at `context_length + 1` in practice: the tightest valid window that
        # still honours K, which is the most relief an OOM retry can get without
        # touching K at all.
        new_ceiling = max(context_length + 1, ceiling // 2)
        return context_length, new_ceiling

    # K-bound (halving the ceiling alone would not move W), or a ceiling-bound plan
    # already at its tightest possible stride (old_stride == 1, no room left to shrink
    # the ceiling without violating K): halve K, and the ceiling along with it.
    new_context = max(1, context_length // 2)
    new_ceiling = max(new_context + 1, ceiling // 2)
    return new_context, new_ceiling
