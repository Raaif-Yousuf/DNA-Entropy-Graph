"""Torch-free helpers to turn a model's next-token logits into the (L, 4) contract.

This is the trickiest part of the Evo integration (softmax + position alignment), so it
lives here as pure NumPy and is unit-tested on any machine (no GPU/torch needed). The
GPU-only model call lives in ``evo.py`` and feeds this function.
"""

from __future__ import annotations

import numpy as np

from .base import NUM_NUCLEOTIDES


def _softmax_rows(logits: np.ndarray) -> np.ndarray:
    shifted = logits - logits.max(axis=1, keepdims=True)
    exp = np.exp(shifted)
    return (exp / exp.sum(axis=1, keepdims=True)).astype(np.float32)


def aligned_acgt_probs(nuc_logits: np.ndarray) -> np.ndarray:
    """Align next-token nucleotide logits to per-base probabilities.

    Evo is autoregressive: ``nuc_logits[i]`` is the model's prediction for the base at
    position ``i + 1`` (given bases ``0..i``). We return an array where row ``i`` is the
    distribution *for* base ``i`` (CLAUDE.md / DESIGN.md §4 "Position semantics").

    Args:
        nuc_logits: ``(L, 4)`` logits over ``[A, C, G, T]`` (already restricted to the
            four nucleotide token ids), one row per input position.

    Returns:
        ``(L, 4)`` float32 probabilities. Row 0 has no preceding context, so it is set to
        the uniform distribution (maximum entropy, 2.0 bits).
    """
    if nuc_logits.ndim != 2 or nuc_logits.shape[1] != NUM_NUCLEOTIDES:
        raise ValueError(f"nuc_logits must be (L, {NUM_NUCLEOTIDES}), got {nuc_logits.shape}")
    length = nuc_logits.shape[0]
    if length == 0:
        # A zero-length window trivially satisfies the (L, 4) contract (CLAUDE.md hard
        # rule #3: L can be 0) -- an empty array, not an IndexError on the "seed row 0
        # uniform" step below, which assumes at least one row exists (issue #292).
        # check_probability_matrix agrees: "each row sums to 1.0" is vacuously true when
        # there are no rows.
        return np.empty((0, NUM_NUCLEOTIDES), dtype=np.float32)
    # next_probs[i] = distribution for base i+1.
    next_probs = _softmax_rows(nuc_logits.astype(np.float32))
    aligned = np.empty((length, NUM_NUCLEOTIDES), dtype=np.float32)
    aligned[0] = 1.0 / NUM_NUCLEOTIDES  # no context for the first base
    if length > 1:
        aligned[1:] = next_probs[:-1]
    return aligned


def normalize_model_output(raw: object) -> object:
    """Normalize a predictor's raw forward-pass output into a 2D ``(L, vocab)`` object.

    Holds the exact unwrap steps :meth:`~dna_entropy.predictors.evo.EvoPredictor._extract_logits`
    needs (nested tuple/list -> ``.logits`` attribute -> drop a leading batch dimension),
    but expressed as pure duck typing (``isinstance``, ``hasattr``, ``.ndim``, indexing) with
    no torch call anywhere in it. That makes it possible to unit-test the exact shapes
    evo2 is MEASURED to have returned (a nested tuple, on a GCP L4) on any machine, with a
    plain stand-in object, per CLAUDE.md's Critical Pitfalls -- do not "simplify" this away.

    Args:
        raw: the model's raw output: a (possibly nested) tuple/list, an object exposing
            ``.logits``, or a bare tensor-like object, with or without a batch dimension.

    Returns:
        The unwrapped, batch-free object (still whatever type it started as -- a torch
        tensor when called from :mod:`~dna_entropy.predictors.evo`, or a stand-in in tests).

    Raises:
        ValueError: if, after unwrapping tuples/lists and a ``.logits`` attribute, the
            result still has no ``.ndim`` -- i.e. it is not tensor-like at all. Names the
            actual type so the caller's error message can say what it got.
    """
    out = raw
    # evo2 has returned a nested tuple like ((logits, inference_params), ...); unwrap to
    # the first element regardless of nesting depth.
    while isinstance(out, (tuple, list)):
        out = out[0]
    if hasattr(out, "logits"):
        out = out.logits
    if not hasattr(out, "ndim"):
        raise ValueError(
            f"model output normalized to a {type(out).__name__!r} object with no `.ndim` "
            "attribute; expected a tensor-like object, optionally wrapped in a tuple/list "
            "or behind a `.logits` attribute."
        )
    if out.ndim == 3:  # (batch, L, vocab)
        out = out[0]
    return out
