"""Orchestrates the pipeline stages. The CLI calls these; stages stay decoupled.

  input -> validate -> predict -> analyze -> export

Each stage is swappable; this module is the only place that knows the order.
"""

from __future__ import annotations

import re
from collections.abc import Callable
from dataclasses import dataclass, field

import numpy as np

from .analysis.direction import DirectionResult, analyze_direction
from .analysis.windowing import validate_context
from .annotators.base import GeneFeature
from .annotators.prodigal import ProdigalAnnotator
from .config import Direction, PredictorKind, RunConfig, TrackFormat
from .predictors.base import Predictor, PredictorError
from .predictors.mock import MockPredictor
from .readers import detect
from .readers.input import Contig, load_input
from .readers.paste import PasteReader
from .validation.validators import ValidatedSequence, validate_sequence
from .writers.base import Writer
from .writers.bedgraph import BedGraphWriter
from .writers.fasta import FastaWriter
from .writers.genbank import GenBankWriter
from .writers.geneious import GeneiousWriter
from .writers.gff import GffWriter
from .writers.summary import SummaryWriter
from .writers.tsv import TsvWriter
from .writers.wig import WigWriter


class PipelineError(RuntimeError):
    """Raised for a pipeline-level configuration problem (e.g. an unusable run name)."""


# Windows reserved device names (case-insensitive). Independently reproduced here rather
# than importing readers/input.py's private `_avoid_reserved_device_name` (issue #350):
# that module is owned by a different lane and a contig name has different validity
# rules from a run name (it also has to be a valid IGV chrom / GenBank LOCUS), so this is
# its own, simpler function for its own, simpler purpose.
_RESERVED_DEVICE_NAMES = frozenset(
    {"CON", "PRN", "AUX", "NUL"} | {f"COM{n}" for n in range(1, 10)} | {f"LPT{n}" for n in range(1, 10)}
)

# Generous but finite: the longest writer suffix today is ".entropy.geneious.gff3" (23
# chars); this stays comfortably under Windows' legacy 260-character MAX_PATH even inside
# a deeply nested output folder, while remaining generous enough for any real run name.
MAX_RUN_NAME_LENGTH = 100


def sanitize_run_name(name: str) -> str:
    """Make a run name safe to use as an output FILE NAME PREFIX (and, via ``cli.py``, a
    folder name).

    Issue #366, MEASURED 2026-09-19: every writer builds its output path as
    ``Path(cfg.out_dir) / f"{cfg.name}<suffix>"`` — ``cfg.name`` reached the filesystem
    with ZERO sanitization before this, whether it came from a CLI ``--name`` (previously
    given an ad hoc, CLI-only pass through a now-removed local helper) or straight from a
    manifest's ``inputs[].name`` (``worker/manifest.py::build_run_config``, never
    sanitized at all). :func:`run` calls this unconditionally at its own top, so both
    entry points — and any future one — are protected regardless of whether the caller
    remembers to sanitize first.

    Deliberately NOT a reuse of ``readers/input.py``'s private contig-name sanitizer:
    that function also has to produce a valid IGV ``chrom`` and GenBank ``LOCUS`` name,
    and numbers multiple records off one base name — different rules for a different
    job. A run name only has to be a safe path component.

    Hard Rule 14 (the user's files are read-only to us; outputs go only to the chosen
    output folder) is what actually matters here: a name of ``../../evil`` is that rule
    broken by a string, so this REFUSES or NEUTRALISES a traversal rather than merely
    tidying the name for cosmetics —

    - every character outside ``[A-Za-z0-9._-]`` (this includes ``/`` and ``\\``, so a
      value can never re-assemble into more than one path segment) becomes ``_``;
    - independently of that, any surviving run of two or more literal dots is also
      collapsed to ``_`` (defense in depth: a traversal segment can never appear even as
      inert-looking text);
    - a leading/trailing ``.``/``_``/``-`` is stripped (a Windows trailing dot/space
      quirk, same reasoning as issue #350's contig-name hardening);
    - the result is capped at :data:`MAX_RUN_NAME_LENGTH`;
    - a bare or case-insensitive Windows reserved device name (``CON``, ``NUL``,
      ``COM1``..``9``, ``LPT1``..``9``, with or without an extension) gets a harmless
      ``_run`` suffix appended rather than being refused outright, since it is
      recoverable without losing the user's intent.

    Raises:
        PipelineError: if nothing usable survives (empty, whitespace-only, or entirely
            path separators/dots) — refuses rather than silently writing to an unusable
            or surprising name.
    """
    if not isinstance(name, str):
        raise PipelineError(f"run name must be a string, got {type(name).__name__}")
    collapsed = re.sub(r"\s+", "_", name.strip())
    safe = re.sub(r"[^A-Za-z0-9._-]", "_", collapsed)
    safe = re.sub(r"\.\.+", "_", safe)  # defense in depth: no literal ".." survives either
    safe = safe.strip("._-")
    if not safe:
        raise PipelineError(
            f"run name {name!r} has no usable characters after removing unsafe ones "
            "(path separators, control characters, or only dots/dashes/underscores). "
            "Use letters, digits, '.', '_', or '-'."
        )
    if len(safe) > MAX_RUN_NAME_LENGTH:
        safe = safe[:MAX_RUN_NAME_LENGTH].rstrip("._-") or "run"
    base = safe.split(".", 1)[0].upper()
    if safe.upper() in _RESERVED_DEVICE_NAMES or base in _RESERVED_DEVICE_NAMES:
        safe = f"{safe}_run"
    return safe


@dataclass
class RunResult:
    """Outcome of a full run: the clean sequence, entropy track, and written files.

    ``seq``/``values`` refer to the first contig (single-sequence inputs have exactly one).
    ``all_values`` concatenates every contig's entropy for aggregate reporting; ``contigs``
    and ``total_nt`` describe multi-record GenBank runs.

    ``direction``/``context_length``/``window``/``stride`` are the windowing/direction
    provenance for the whole run (section 5.6) — DERIVED once here and recorded, not
    recomputed by every writer/future manifest reader. ``seam`` and
    ``reduced_context_count`` describe the first contig's combination (``seam`` is
    ``None`` when that contig is shorter than ``2 * context_length``, per section 5.6).
    """

    seq: str
    values: np.ndarray
    notices: list[str]
    outputs: list[str]
    genes: list[GeneFeature] = field(default_factory=list)
    contigs: int = 1
    total_nt: int = 0
    all_values: np.ndarray | None = None
    direction: Direction = Direction.BOTH_COMBINED
    context_length: int = 0
    window: int = 0
    stride: int = 0
    seam: int | None = None
    reduced_context_count: int = 0

    def __post_init__(self) -> None:
        if self.all_values is None:
            self.all_values = self.values
        if not self.total_nt:
            self.total_nt = len(self.seq)


def read_raw(cfg: RunConfig) -> str:
    """Read raw text from the configured input (file or stdin)."""
    return PasteReader(cfg.input_path).read()


def load_and_validate(cfg: RunConfig, raw: str | None = None) -> ValidatedSequence:
    """Read (unless ``raw`` is supplied) and validate into a clean sequence."""
    if raw is None:
        raw = read_raw(cfg)
    return validate_sequence(raw, max_len=cfg.max_total_len, rna=cfg.rna)


def build_predictor(cfg: RunConfig) -> Predictor:
    """Construct the predictor backend named by the config."""
    if cfg.predictor is PredictorKind.MOCK:
        return MockPredictor(seed=cfg.seed)
    if cfg.predictor is PredictorKind.EVO:
        # Lazy import: the Evo stack is heavy and GPU-only, so it must not be imported
        # on the mock path (CLAUDE.md hard rule #5).
        try:
            from .predictors.evo import EvoPredictor
        except ImportError as exc:  # not built / deps absent
            raise PredictorError(
                "Evo predictor is not available yet (arrives in Sprint 3, and needs "
                "the [evo] extra on a GPU box). Use --predictor mock for now."
            ) from exc
        # max_context must match the GPU ceiling windowing computed W against (cfg.max_len)
        # — otherwise a bigger-ceiling config (e.g. an A100 tier) would still be silently
        # capped at EvoPredictor's own 8192 default, rejecting perfectly valid windows.
        return EvoPredictor(model=cfg.model, device=cfg.device, max_context=cfg.max_len)
    raise PredictorError(f"Unknown predictor: {cfg.predictor!r}")


def _select_track_writer(cfg: RunConfig) -> Writer:
    return WigWriter() if cfg.track_format is TrackFormat.WIG else BedGraphWriter()


def _try_annotate(seq: str) -> list[GeneFeature]:
    """Best-effort Prodigal call for a bonus GenBank; never raises (returns [] on failure)."""
    try:
        return ProdigalAnnotator().annotate(seq)
    except Exception:  # missing [genes] extra, or Prodigal failure — GenBank genes are optional
        return []


def _write_tsv(cfg: RunConfig, processed: list[tuple[Contig, DirectionResult]]) -> str:
    """Write ``<name>.entropy.tsv``, shared by both output paths (GenBank and standard).

    Uses the 5-column fwd/rev/combined shape for Direction.BOTH_SEPARATE (each contig's
    forward/reverse/combined tracks are all populated), the plain 3-column shape
    otherwise (docs/science_and_formats.md section 5).
    """
    if any(dr.forward_values is not None for _, dr in processed):
        return TsvWriter().write_multi_separate(
            name=cfg.name,
            blocks=[(c.name, c.seq, dr.forward_values, dr.reverse_values, dr.values) for c, dr in processed],
            start=cfg.start,
            out_dir=cfg.out_dir,
        )
    return TsvWriter().write_multi(
        name=cfg.name,
        blocks=[(c.name, c.seq, dr.values) for c, dr in processed],
        start=cfg.start,
        out_dir=cfg.out_dir,
    )


def _write_genbank_outputs(cfg: RunConfig, processed: list[tuple[Contig, DirectionResult]]) -> list[str]:
    """GenBank input -> ONE GenBank (all records, genes preserved + entropy notes), a
    FASTA + bedGraph + WIG + Geneious track (a block per record), and ONE stats.txt.

    Everything is sourced straight from the GenBank records (sequence and existing genes);
    Prodigal is never run on this path. ``processed`` is a list of ``(Contig, DirectionResult)``.

    Every writer below is gated by its own ``cfg.include_*`` flag (issue #304) so a
    manifest naming fewer desired ``outputs`` produces fewer files, not the full set
    regardless.
    """
    outputs: list[str] = []

    if cfg.include_genbank:
        outputs.append(
            GenBankWriter().write_multi(
                name=cfg.name,
                records=[(c.name, c.seq, c.features, dr.values, c.source_id) for c, dr in processed],
                out_dir=cfg.out_dir,
            )
        )
    if cfg.include_fasta:
        outputs.append(
            FastaWriter().write_multi(
                name=cfg.name,
                blocks=[(c.name, c.seq) for c, _ in processed],
                out_dir=cfg.out_dir,
            )
        )
    if cfg.include_track:
        outputs.append(
            BedGraphWriter().write_multi(
                name=cfg.name,
                blocks=[(c.name, dr.values) for c, dr in processed],
                start=cfg.start,
                out_dir=cfg.out_dir,
            )
        )
        outputs.append(
            WigWriter().write_multi(
                name=cfg.name,
                blocks=[(c.name, dr.values) for c, dr in processed],
                start=cfg.start,
                out_dir=cfg.out_dir,
            )
        )
    if cfg.include_geneious:
        outputs.append(
            GeneiousWriter().write_multi(
                name=cfg.name,
                blocks=[(c.name, dr.values) for c, dr in processed],
                start=cfg.start,
                out_dir=cfg.out_dir,
            )
        )
    if cfg.include_stats:
        outputs.append(
            SummaryWriter().write_multi(
                name=cfg.name,
                sections=[(c.name, dr.values) for c, dr in processed],
                start=cfg.start,
                out_dir=cfg.out_dir,
                filename="stats.txt",
                provenance=[dr for _, dr in processed],
            )
        )

    if cfg.include_tsv:
        outputs.append(_write_tsv(cfg, processed))

    # Direction.BOTH_SEPARATE: also emit the fwd/rev tracks (section 5.6), one block per
    # record, alongside the combined bedGraph above.
    if cfg.include_track and any(dr.forward_values is not None for _, dr in processed):
        outputs.append(
            BedGraphWriter().write_multi(
                name=cfg.name,
                blocks=[(c.name, dr.forward_values) for c, dr in processed],
                start=cfg.start,
                out_dir=cfg.out_dir,
                variant="fwd",
            )
        )
        outputs.append(
            BedGraphWriter().write_multi(
                name=cfg.name,
                blocks=[(c.name, dr.reverse_values) for c, dr in processed],
                start=cfg.start,
                out_dir=cfg.out_dir,
                variant="rev",
            )
        )

    # Gene track for IGV, straight from the GenBank's own genes (never Prodigal). Only
    # written when the records actually carry genes AND the caller still wants it.
    if cfg.include_genes_gff3 and any(c.features for c, _ in processed):
        genes_gff = GffWriter().write_multi(
            name=cfg.name,
            blocks=[(c.name, c.features, len(c.seq)) for c, _ in processed],
            start=cfg.start,
            out_dir=cfg.out_dir,
            source="genbank",
        )
        outputs.append(genes_gff)
    return outputs


def _write_standard_outputs(
    cfg: RunConfig,
    processed: list[tuple[Contig, DirectionResult]],
) -> tuple[list[str], list[GeneFeature]]:
    """FASTA/paste input -> the existing files, plus a bonus GenBank when possible.

    One block per contig (design D14/#283: a multi-record FASTA is processed exactly like
    multi-record GenBank — every record, not just the first). A single-contig input (paste,
    or a single-record FASTA) produces byte-identical output to the pre-#283 code, since
    ``write_multi`` with one block is how ``write`` was already implemented for every
    writer here.

    Every writer below is gated by its own ``cfg.include_*`` flag (issue #304) so a
    manifest naming fewer desired ``outputs`` produces fewer files, not the full set
    regardless.
    """
    track_writer = _select_track_writer(cfg)
    outputs: list[str] = []

    if cfg.include_fasta:
        outputs.append(
            FastaWriter().write_multi(
                name=cfg.name,
                blocks=[(c.name, c.seq) for c, _ in processed],
                out_dir=cfg.out_dir,
            )
        )
    if cfg.include_track:
        outputs.append(
            track_writer.write_multi(
                name=cfg.name,
                blocks=[(c.name, dr.values) for c, dr in processed],
                start=cfg.start,
                out_dir=cfg.out_dir,
            )
        )
    if cfg.include_geneious:
        outputs.append(
            GeneiousWriter().write_multi(
                name=cfg.name,
                blocks=[(c.name, dr.values) for c, dr in processed],
                start=cfg.start,
                out_dir=cfg.out_dir,
            )
        )
    if cfg.include_stats:
        outputs.append(
            SummaryWriter().write_multi(
                name=cfg.name,
                sections=[(c.name, dr.values) for c, dr in processed],
                start=cfg.start,
                out_dir=cfg.out_dir,
                provenance=[dr for _, dr in processed],
            )
        )

    if cfg.include_tsv:
        outputs.append(_write_tsv(cfg, processed))

    # Direction.BOTH_SEPARATE: also emit the fwd/rev tracks (section 5.6), one block per
    # contig, alongside the combined track already written above.
    if cfg.include_track and any(dr.forward_values is not None for _, dr in processed):
        outputs.append(
            track_writer.write_multi(
                name=cfg.name,
                blocks=[(c.name, dr.forward_values) for c, dr in processed],
                start=cfg.start,
                out_dir=cfg.out_dir,
                variant="fwd",
            )
        )
        outputs.append(
            track_writer.write_multi(
                name=cfg.name,
                blocks=[(c.name, dr.reverse_values) for c, dr in processed],
                start=cfg.start,
                out_dir=cfg.out_dir,
                variant="rev",
            )
        )

    genes: list[GeneFeature] = []
    genes_by_contig: list[list[GeneFeature]] = [[] for _ in processed]
    if cfg.genes:
        for i, (c, _dr) in enumerate(processed):
            g = ProdigalAnnotator().annotate(c.seq)  # may raise AnnotatorError (explicit opt-in)
            genes_by_contig[i] = g
            genes += g
        if cfg.include_genes_gff3:
            outputs.append(
                GffWriter().write_multi(
                    name=cfg.name,
                    blocks=[
                        (c.name, g, len(c.seq)) for (c, _), g in zip(processed, genes_by_contig, strict=True)
                    ],
                    start=cfg.start,
                    out_dir=cfg.out_dir,
                    source="pyrodigal",
                )
            )

    # Bonus GenBank "if possible": reuse --genes features per contig, else best-effort
    # Prodigal per contig. One .gb file holding every record, like the GenBank input path.
    if cfg.include_genbank:
        try:
            records = []
            for (c, dr), g in zip(processed, genes_by_contig, strict=True):
                gb_features = g or _try_annotate(c.seq)
                records.append((c.name, c.seq, gb_features, dr.values, c.source_id))
            outputs.append(GenBankWriter().write_multi(name=cfg.name, records=records, out_dir=cfg.out_dir))
        except Exception:
            pass  # GenBank is a bonus on this path; never fail the core run over it
    return outputs, genes


def run(
    cfg: RunConfig,
    raw: str | None = None,
    *,
    on_window: Callable[[], None] | None = None,
    on_contig: Callable[[Contig], None] | None = None,
) -> RunResult:
    """Run the full pipeline and write all output files (output set depends on input kind).

    Every contig gets windowed forward and/or reverse-complement passes (section 5.6),
    never a per-base rolling window (GenBank inputs may carry several contigs, each
    analyzed independently). The worker RE-VALIDATES the context length against each
    contig even though the app is expected to validate first (``validate_context`` may
    raise :class:`~dna_entropy.analysis.windowing.WindowingError`).

    ``on_window``/``on_contig`` are cooperative-cancellation hooks (docs/job_contract.md
    §6: "the worker checks control/cancel between windows and between contigs"), called
    after every completed window and after every completed contig respectively; the
    worker's ``CancelWatcher.check_or_raise`` plugs into either without this module
    needing to know anything about ``control/cancel``. ``None`` (the default) means no
    hook — every existing caller is unaffected.

    Issue #366: ``cfg.name`` is sanitized (:func:`sanitize_run_name`) FIRST, unconditionally,
    before anything is read or written — this is the one choke point every caller (a CLI
    ``--name``, or a manifest's ``inputs[].name`` via ``worker/manifest.py``) funnels
    through, so a hostile name is neutralized here regardless of which path called in.
    May raise :class:`PipelineError` if nothing usable survives sanitization.
    """
    cfg.name = sanitize_run_name(cfg.name)
    loaded = load_input(cfg, raw)
    # issue #306: fastaRecords="first" is the prototype-parity opt-out from #283/D14's
    # "all records" default — readers/input.py has no opinion on it (and must not: Lane A
    # owns that module), so the truncation happens here, right after loading, before any
    # window is planned or any file is written. GenBank multi-record input is untouched:
    # the field is documented (job_contract.md §3) as FASTA-specific.
    if cfg.fasta_records == "first" and loaded.source_kind == detect.FASTA and len(loaded.contigs) > 1:
        dropped = len(loaded.contigs) - 1
        loaded.contigs = loaded.contigs[:1]
        loaded.notices = loaded.notices + [
            f"fastaRecords='first': analyzing only the first record; {dropped} other "
            "record(s) in this FASTA were not processed"
        ]
    predictor = build_predictor(cfg)

    notices: list[str] = list(loaded.notices)
    processed: list[tuple[Contig, DirectionResult]] = []
    reduced_total = 0
    try:
        for contig in loaded.contigs:
            notices += validate_context(
                context_length=cfg.context_length,
                ceiling=cfg.max_len,
                seq_len=len(contig.seq),
            )
            dr = analyze_direction(
                predictor,
                contig.seq,
                context_length=cfg.context_length,
                ceiling=cfg.max_len,
                direction=cfg.direction,
                on_window=on_window,
            )
            notices += dr.notices
            reduced_total += dr.reduced_context_count
            processed.append((contig, dr))
            if on_contig is not None:
                on_contig(contig)
    except Exception:
        # A hook (typically a cooperative cancellation check) stopped the run early.
        # docs/job_contract.md §6: "partial results are always kept, never discarded" —
        # write whatever contigs DID complete (best-effort; a failure here must not mask
        # the original exception, which is what the caller actually needs to see) before
        # propagating it unchanged.
        if processed:
            try:
                if loaded.source_kind == detect.GENBANK:
                    _write_genbank_outputs(cfg, processed)
                else:
                    _write_standard_outputs(cfg, processed)
            except Exception:
                pass
        raise

    if loaded.source_kind == detect.GENBANK:
        outputs = _write_genbank_outputs(cfg, processed)
        genes = [f for c, _ in processed for f in c.features]
    else:
        outputs, genes = _write_standard_outputs(cfg, processed)

    first_contig, first_dr = processed[0]
    all_values = np.concatenate([dr.values for _, dr in processed])
    return RunResult(
        seq=first_contig.seq,
        values=first_dr.values,
        notices=notices,
        outputs=outputs,
        genes=genes,
        contigs=len(processed),
        total_nt=sum(len(c.seq) for c, _ in processed),
        all_values=all_values,
        direction=cfg.direction,
        context_length=cfg.context_length,
        window=first_dr.window,
        stride=first_dr.stride,
        seam=first_dr.seam,
        reduced_context_count=reduced_total,
    )
