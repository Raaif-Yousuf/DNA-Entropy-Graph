"""Model/GPU hardware gating — refuse Hopper-only Evo variants on non-Hopper devices.

Reproduces design section 5.4's model/hardware matrix and the ``MODEL_NEEDS_HOPPER``
error-taxonomy entry (section 5.9, D11/D16): only ``evo2_7b``/``evo2_7b_262k`` run bf16 on
24 GB+ (L4, A100 40/80); ``evo2_1b_base``, ``evo2_20b``, ``evo2_40b`` need FP8 on Hopper
(H100) — ``evo2_40b`` needs two H100 80 GB.

Gates on the device's ACTUAL reported compute capability
(``torch.cuda.get_device_capability()``, injected by the caller so this stays torch-free
and unit-testable with no GPU), never on the model name alone: a future GPU that happens
to share a name pattern with a Hopper part but reports a lower capability is still
correctly refused, and a real Hopper part is correctly accepted even for a model id this
table doesn't yet know about (see ``model_requirement``'s conservative default).
"""

from __future__ import annotations

from dataclasses import dataclass

from .base import PredictorError

# Compute capability floor for "Hopper" (H100 is sm_90; anything >= (9, 0) qualifies).
HOPPER_COMPUTE_CAPABILITY: tuple[int, int] = (9, 0)


class ModelNeedsHopperError(PredictorError):
    """``MODEL_NEEDS_HOPPER`` (design section 5.9): the requested model needs an
    H100-class (or newer) GPU; the offered device does not qualify. Always raised BEFORE
    any weight download begins."""

    code = "MODEL_NEEDS_HOPPER"


@dataclass(frozen=True)
class ModelRequirement:
    """Hardware a model id needs, from design section 5.4's model/hardware matrix."""

    precision: str  # "bf16" | "fp8"
    needs_hopper: bool
    min_gpu_count: int = 1
    note: str = ""


# Design section 5.4's model/hardware matrix, verbatim.
MODEL_REQUIREMENTS: dict[str, ModelRequirement] = {
    "evo2_7b": ModelRequirement(
        precision="bf16",
        needs_hopper=False,
        note="bf16, ~14 GB, runs on an L4 or A100 (24 GB+)",
    ),
    "evo2_7b_262k": ModelRequirement(
        precision="bf16",
        needs_hopper=False,
        note="bf16, ~14 GB, runs on an L4 or A100 (24 GB+)",
    ),
    "evo2_1b_base": ModelRequirement(
        precision="fp8",
        needs_hopper=True,
        min_gpu_count=1,
        note="FP8 on Hopper (H100) only",
    ),
    "evo2_20b": ModelRequirement(
        precision="fp8",
        needs_hopper=True,
        min_gpu_count=1,
        note="FP8 on Hopper (H100) only",
    ),
    "evo2_40b": ModelRequirement(
        precision="fp8",
        needs_hopper=True,
        min_gpu_count=2,
        note="FP8 on 2x H100 80 GB",
    ),
}

# The one model the error message always offers as a fallback (design: "say what to use
# instead"). It runs on the widest range of hardware (L4 or A100), so it is always safe
# to suggest regardless of which Hopper-only model was requested.
FALLBACK_MODEL = "evo2_7b"


def model_requirement(model_id: str) -> ModelRequirement:
    """Look up the hardware requirement for ``model_id``.

    An unrecognized model id is treated as bf16/no-Hopper-requirement — the same
    conservative default as "we don't know this one, so we don't block it here"; a
    genuinely new Hopper-only model should be added to :data:`MODEL_REQUIREMENTS`. This
    function is a lookup table, not itself the gate: :func:`require_hardware` is the
    capability check that actually matters.
    """
    return MODEL_REQUIREMENTS.get(
        model_id,
        ModelRequirement(precision="bf16", needs_hopper=False, note="model id not in the hardware matrix"),
    )


def require_hardware(
    model_id: str,
    *,
    device: str,
    compute_capability: tuple[int, int] | None,
    gpu_count: int | None = None,
) -> None:
    """Raise :class:`ModelNeedsHopperError` if ``model_id`` needs Hopper and the offered
    device does not qualify, or if it needs more GPUs than are visible. No-op for every
    other model.

    Args:
        model_id: the requested Evo model id.
        device: the predictor's configured device string (e.g. ``"cuda"``, ``"cpu"``).
        compute_capability: ``torch.cuda.get_device_capability()`` for the active CUDA
            device, or ``None`` when there is no CUDA device at all (``device != "cuda"``,
            or CUDA is unavailable) — callers pass the REAL value; this module never
            imports torch itself so it stays testable with a synthetic tuple.
        gpu_count: ``torch.cuda.device_count()``, or ``None`` when unknown. ``evo2_40b``
            needs TWO H100 80 GB GPUs (:data:`MODEL_REQUIREMENTS`'s own
            ``min_gpu_count``); without this check, a single-GPU H100 box would pass the
            compute-capability gate above and only fail later, deep inside multi-GPU
            model loading, with a confusing error instead of this module's own clear,
            named one. ``None`` skips this check entirely (a caller that does not know
            its GPU count yet gets the same behavior this function had before this
            parameter existed, never a false refusal).
    """
    req = model_requirement(model_id)
    if not req.needs_hopper:
        return

    if device != "cuda" or compute_capability is None:
        raise ModelNeedsHopperError(
            f"{model_id} needs {req.precision} precision on an H100-class GPU "
            f"({req.note}); no CUDA device is available (device={device!r}). Use "
            f"{FALLBACK_MODEL} instead — it runs on an L4 or A100."
        )
    if compute_capability < HOPPER_COMPUTE_CAPABILITY:
        got = f"sm_{compute_capability[0]}{compute_capability[1]}"
        want = f"sm_{HOPPER_COMPUTE_CAPABILITY[0]}{HOPPER_COMPUTE_CAPABILITY[1]}"
        raise ModelNeedsHopperError(
            f"{model_id} needs {req.precision} precision on an H100-class GPU "
            f"({req.note}); this device reports compute capability {got}, which is "
            f"below Hopper ({want}). Use {FALLBACK_MODEL} instead — it runs on an L4 "
            "or A100."
        )
    if gpu_count is not None and gpu_count < req.min_gpu_count:
        raise ModelNeedsHopperError(
            f"{model_id} needs {req.min_gpu_count} H100-class GPUs ({req.note}), but "
            f"only {gpu_count} {'is' if gpu_count == 1 else 'are'} visible to this "
            f"process. Use {FALLBACK_MODEL} instead — it runs on a single L4 or A100."
        )
