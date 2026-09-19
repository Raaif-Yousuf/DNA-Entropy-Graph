"""Run configuration for a single DNA-Entropy invocation.

Uses stdlib dataclasses (no third-party dep) to keep the laptop/mock install light
per CLAUDE.md hard rule #5.
"""

from __future__ import annotations

from dataclasses import dataclass
from enum import Enum


class PredictorKind(str, Enum):
    """Which prediction backend to use."""

    MOCK = "mock"
    EVO = "evo"


class TrackFormat(str, Enum):
    """Output format for the per-position entropy track."""

    BEDGRAPH = "bedgraph"
    WIG = "wig"


class Direction(str, Enum):
    """How forward and reverse-complement predictions combine into one entropy track.

    See docs/superpowers/specs/2026-09-18-dna-entropy-graph-design.md section 5.6 (the
    owner's three points: user-settable context, pushback on bad sizes, and — the most
    important — predicting the first bases from a reverse-complement read so they are as
    accurate as the rest).
    """

    BOTH_COMBINED = "both-combined"  # default: forward once it has >=K context, else reverse
    BOTH_AVERAGED = "both-averaged"  # mean where BOTH directions have >=K context
    BOTH_SEPARATE = "both-separate"  # emits .fwd/.rev tracks *and* the combined track
    FORWARD_ONLY = "forward-only"  # reproduces the prototype's single-pass output bit-for-bit
    REVERSE_ONLY = "reverse-only"


# Default GPU ceiling for a single model forward pass: the largest window W a single call
# to the predictor may be asked to handle. The 7B model on an L4 supports 8,192 (design
# section 5.4); bigger GPU tiers raise this. This used to be a hard cap on the WHOLE input
# sequence (longer inputs were rejected outright); now that analysis/windowing.py tiles
# long sequences into multiple <= W passes, it caps only a single window, not the input.
DEFAULT_MAX_LEN = 8192

# Default context length K: the amount of sequence the model must have seen before a
# prediction at a given base is trusted (section 5.6). W = min(2K, max_len); the window
# and stride are DERIVED, not independently settable — see analysis/windowing.py.
DEFAULT_CONTEXT_LENGTH = 4096

# Outer sanity bound on total input length, independent of the per-window ceiling above:
# windowing tiles arbitrarily long sequences into multiple <= max_len passes, so this is
# NOT a "single pass" cap any more (that job now belongs to max_len alone) — it only
# guards against a pathologically huge paste/file crashing the run before a real,
# cost-based limit exists (tracked in issue #248: "input limits tied to cost"). Generous
# on purpose (10 Mnt is far beyond any single locus) rather than an arbitrary guess.
DEFAULT_MAX_TOTAL_LEN = 10_000_000


@dataclass
class RunConfig:
    """All knobs for one run. Populated by the CLI; consumed by the pipeline."""

    name: str = "user_locus"
    input_path: str | None = None  # None => read from stdin
    informat: str | None = None  # "genbank"|"fasta"|"paste" override; None => auto-detect
    predictor: PredictorKind = PredictorKind.MOCK
    model: str = "evo2_7b"
    device: str = "cuda"
    out_dir: str = "out"
    track_format: TrackFormat = TrackFormat.BEDGRAPH
    start: int = 1
    # GPU ceiling for one forward pass (W's cap); NOT a limit on total input length anymore.
    max_len: int = DEFAULT_MAX_LEN
    # Outer sanity bound on the WHOLE input (independent of max_len/windowing — see above).
    max_total_len: int = DEFAULT_MAX_TOTAL_LEN
    # K: user-settable context length (section 5.6, point 1).
    context_length: int = DEFAULT_CONTEXT_LENGTH
    direction: Direction = Direction.BOTH_COMBINED
    genes: bool = False
    rna: bool = False
    seed: int = 0
    # Entropy TSV (position, base, entropy) — a selectable extra output alongside the
    # IGV track/GenBank/summary files (design §4.3 "Entropy TSV (new)"). On by default,
    # like every other writer here; turn off with --no-tsv if a run doesn't need it.
    include_tsv: bool = True
