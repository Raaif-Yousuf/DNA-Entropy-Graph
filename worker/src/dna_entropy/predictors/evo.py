"""EvoPredictor — the real Evo 2 (7B) backend.

THE ONLY module allowed to import ``torch`` / ``evo2`` (CLAUDE.md hard rule #1). Runs on
an NVIDIA GPU; see docs/EVO_SETUP.md. Everything here funnels into the same ``(L, 4)``
probability contract as MockPredictor, so it is a drop-in swap.

Untested on the dev laptop (no GPU). Validated by ``tests/test_evo_predictor.py``
(marked ``gpu``) on the A10 box. The pure alignment math is in ``logits.py`` and is
tested everywhere.
"""

from __future__ import annotations

import numpy as np
import torch  # noqa: F401  (GPU-only dep; isolated to this module)
from evo2 import Evo2

from .base import PredictorError, PredictorOOMError, check_probability_matrix
from .hardware import require_hardware
from .logits import aligned_acgt_probs, normalize_model_output


class EvoPredictor:
    """Wraps Evo 2 to produce per-position A/C/G/T probabilities."""

    def __init__(
        self,
        model: str = "evo2_7b",
        device: str = "cuda",
        max_context: int = 8192,
    ) -> None:
        self.model_name = model
        self.device = device
        self.max_context = max_context

        # MODEL_NEEDS_HOPPER (design section 5.9): gate BEFORE any weight download. Reads
        # the device's REAL compute capability — never trusts the model name alone. Also
        # reads the REAL gpu count: evo2_40b needs two H100s, and checking only ONE
        # device's compute capability would let a single-GPU H100 box through to fail
        # later, deep inside multi-GPU model loading, instead of here with a clear,
        # named error (issue found during the lane-B audit).
        capability = None
        gpu_count = None
        if device == "cuda" and torch.cuda.is_available():
            capability = tuple(torch.cuda.get_device_capability())
            gpu_count = torch.cuda.device_count()
        require_hardware(model, device=device, compute_capability=capability, gpu_count=gpu_count)

        try:
            self._model = Evo2(model)
        except Exception as exc:  # weights missing, OOM, etc.
            raise PredictorError(f"Failed to load Evo model {model!r}: {exc}") from exc
        # Ask the tokenizer for the exact ids of A, C, G, T rather than hard-coding
        # ASCII — robust to whatever scheme the model uses.
        nuc_ids = list(self._model.tokenizer.tokenize("ACGT"))
        if len(nuc_ids) != 4:
            raise PredictorError(
                f"Unexpected tokenization of 'ACGT' -> {nuc_ids}; "
                "the alignment in evo.py assumes one token per nucleotide."
            )
        # Cast to plain ints: the tokenizer returns uint8, which torch would otherwise
        # interpret as a boolean mask during column selection.
        self._nuc_ids = [int(x) for x in nuc_ids]

    @staticmethod
    def _extract_logits(raw: object) -> torch.Tensor:
        """Normalize the model's return into a 2D ``(L, vocab)`` logits tensor.

        Tolerates evo2 versions that return a tuple, an object with ``.logits``, or a
        bare tensor, with or without a batch dimension. The unwrap steps themselves live
        in the torch-free :func:`~dna_entropy.predictors.logits.normalize_model_output`
        (unit-tested on any machine, per CLAUDE.md's Critical Pitfalls -- do not
        "simplify" them); this wrapper only translates an unrecognized shape into a named
        :class:`PredictorError` instead of letting a bare ``AttributeError``/``TypeError``
        leak from three lines further down the call stack (issue #293).
        """
        try:
            return normalize_model_output(raw)
        except ValueError as exc:
            raise PredictorError(f"Evo model returned an unrecognized output shape: {exc}") from exc

    def predict(self, seq: str) -> np.ndarray:
        if self.max_context and len(seq) > self.max_context:
            # Defensive: callers (analysis/direction.py's windowed runner) must never
            # pass a slice longer than one window (<= the GPU ceiling); this guards the
            # invariant rather than implementing windowing itself, which now lives one
            # layer up so it works identically for every predictor, not just Evo.
            raise PredictorError(
                f"Sequence length {len(seq)} exceeds Evo single-pass context "
                f"{self.max_context}; the caller should have windowed this already."
            )

        token_ids = self._model.tokenizer.tokenize(seq)
        input_ids = torch.tensor(token_ids, dtype=torch.int).unsqueeze(0).to(self.device)

        try:
            with torch.no_grad():
                raw = self._model(input_ids)
        except torch.cuda.OutOfMemoryError as exc:
            # Translate into a typed signal analysis/direction.py's windowed runner can
            # catch specifically (halve the window and retry once — design section 5.6)
            # without importing torch itself (only this module may).
            raise PredictorOOMError(f"Out of GPU memory running a window of {len(seq)} nt: {exc}") from exc
        logits = self._extract_logits(raw)  # (L, vocab)

        nuc_logits = logits[:, self._nuc_ids].float().cpu().numpy()  # (L, 4)
        probs = aligned_acgt_probs(nuc_logits)
        # Guards the predictor boundary; also catches any tokenizer length surprise
        # (e.g. an unexpected BOS token) by failing the shape check loudly.
        return check_probability_matrix(probs, len(seq))
