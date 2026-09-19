"""Analysis stage: turn probabilities into entropy, windowing, and direction combination."""

from .direction import DirectionResult, analyze_direction, reverse_complement, run_windowed
from .entropy import MAX_ENTROPY_BITS, EntropySummary, shannon_entropy, summarize
from .windowing import (
    MIN_CONTEXT_LENGTH,
    MIN_RECOMMENDED_CONTEXT_LENGTH,
    MIN_SEQUENCE_LENGTH,
    WindowingError,
    WindowPlan,
    compute_window,
    halved,
    plan_windows,
    validate_context,
)

__all__ = [
    "MAX_ENTROPY_BITS",
    "EntropySummary",
    "shannon_entropy",
    "summarize",
    "WindowPlan",
    "WindowingError",
    "MIN_CONTEXT_LENGTH",
    "MIN_RECOMMENDED_CONTEXT_LENGTH",
    "MIN_SEQUENCE_LENGTH",
    "compute_window",
    "plan_windows",
    "validate_context",
    "halved",
    "DirectionResult",
    "analyze_direction",
    "reverse_complement",
    "run_windowed",
]
