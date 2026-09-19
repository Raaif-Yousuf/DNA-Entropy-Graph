"""Unified input loading: route a file (or paste) to the right reader + validation.

Produces a :class:`LoadedInput` — one or more :class:`Contig`s to analyze, plus any
non-fatal notices and the source kind (which the pipeline uses to pick the output set).

A GenBank may hold multiple records; we return **all** of them as separate contigs so the
pipeline computes entropy for each. FASTA/paste yield a single contig.
"""

from __future__ import annotations

import os
import re
from dataclasses import dataclass, field

from ..annotators.base import GeneFeature
from ..config import RunConfig
from ..validation.validators import ValidationError, validate_sequence
from . import detect
from .fasta import read_fasta
from .genbank import read_genbank
from .paste import PasteReader


@dataclass
class Contig:
    """One sequence to analyze: an output-safe name, the bases, and any known genes."""

    name: str  # safe for a filename / IGV chrom / GenBank LOCUS
    seq: str
    features: list[GeneFeature] = field(default_factory=list)
    source_id: str = ""  # original record id (kept for provenance)


@dataclass
class LoadedInput:
    """One or more validated contigs plus input-derived context for the pipeline."""

    contigs: list[Contig]
    notices: list[str] = field(default_factory=list)
    source_kind: str = detect.PASTE

    @property
    def seq(self) -> str:
        """First contig's sequence (convenience for single-contig callers)."""
        return self.contigs[0].seq

    @property
    def features(self) -> list[GeneFeature]:
        """First contig's features (convenience for single-contig callers)."""
        return self.contigs[0].features


def _check_whole_input_cap(running_total: int, max_total_len: int) -> None:
    """#314: ``cfg.max_total_len`` bounds the WHOLE input (every record's length summed),
    not any one record checked in isolation. Without this, a multi-record file where
    every record is individually under the cap can still be many times over it in total
    -- ``config.py``'s own comment calls it "the outer sanity bound on total input
    length", and a per-record-only check never enforces that. ``cfg.max_len`` (the GPU
    per-window ceiling) is a different knob entirely and must stay out of this check:
    windowing already tiles a single record longer than ``max_len`` into multiple
    passes (Hard Rule 4), so rejecting a long record here would break that on purpose.
    """
    if running_total > max_total_len:
        over = running_total - max_total_len
        raise ValidationError(
            f"Combined input length {running_total} nt exceeds the whole-input cap of "
            f"{max_total_len} nt by {over} nt. Split the input into smaller files, or "
            "raise --max-total-len if you intend to analyze this much sequence at once."
        )


# issue #350: MS-DOS/Windows has long reserved these names as device names regardless
# of extension ("CON.fasta" traditionally as forbidden as "CON"). CORRECTED 2026-09-19
# (replacing #350's own original, unverified claim per Hard Rule 18): MEASURED directly
# on this dev box (Windows 11 Home 10.0.26200) that Python's Path.write/open, .NET's
# File.WriteAllText, PowerShell's New-Item, and even a bare "CON" directory all
# successfully create real, readable files/folders named exactly "CON", "CON.fasta" and
# "NUL.gb" -- the classic DOS-device-name block does NOT reproduce for ordinary file
# creation on this OS build (docs/entry_points.md section 3 carries the disproven-diagnosis
# row). Kept anyway as free insurance: it costs nothing for any ordinary name, this
# app's own contig name also becomes a GenBank LOCUS field and an IGV/WIG chrom= label
# (contexts with their own naming quirks), and a different Windows edition, a network
# share, or a third-party tool opening the output later could still choke on it even
# though this measurement did not reproduce it.
_RESERVED_DEVICE_NAMES = frozenset(
    {"CON", "PRN", "AUX", "NUL"} | {f"COM{i}" for i in range(1, 10)} | {f"LPT{i}" for i in range(1, 10)}
)

# Windows' legacy MAX_PATH (260) counts the full path: drive + every directory + the
# file name + extension. This app is Windows-only (CLAUDE.md) and does not assume
# long-path opt-in, so a contig name is capped to leave room for the configured output
# directory and the single longest suffix any writer appends. NOTE: this budget bounds
# what THIS function returns (Contig.name, used inside file contents -- FASTA headers,
# GenBank LOCUS, GFF3 seqid, WIG chrom=); it does not, and cannot from here, bound the
# actual output FOLDER/FILE name on disk, which pipeline.py builds from cfg.name
# directly (MEASURED 2026-09-19: `--name CON` really does write `CON.fasta`/`CON.gb`
# under a real `CON\` folder) -- see the filed follow-up issue for that gap, which is
# pipeline.py's/cli.py's file, not this lane's.
_MAX_PATH = 260
_LONGEST_WRITER_SUFFIX_LEN = len(".entropy.geneious.gff3")
_PATH_SAFETY_MARGIN = 16  # slack for a path separator, a job-id subfolder, etc.
_MIN_NAME_LEN = 16  # never cap a name down to something unusably short


def _path_budget(out_dir: str) -> int:
    """How many characters a contig name may use so ``<out_dir>/<name><suffix>`` stays
    comfortably under Windows' legacy MAX_PATH."""
    budget = _MAX_PATH - len(os.path.abspath(out_dir)) - 1 - _LONGEST_WRITER_SUFFIX_LEN - _PATH_SAFETY_MARGIN
    return max(budget, _MIN_NAME_LEN)


def _avoid_reserved_device_name(name: str) -> str:
    """Disambiguate a name Windows reserves as a device name, checked case-insensitively
    against the part before the first ``.`` -- the part every ``<name>.<ext>`` output
    file shares, whatever the extension."""
    head = name.split(".", 1)[0].upper()
    return f"{name}_seq" if head in _RESERVED_DEVICE_NAMES else name


def _safe_contig_name(base: str, index: int, total: int, out_dir: str = "out") -> str:
    """Make an output-safe contig name (filenames / IGV chrom / GenBank LOCUS).

    Single record -> just ``base``; multiple -> ``base_1``, ``base_2``, ... . Geneious-style
    ids (``geneious|urn:local:...``) are unsafe, so we index off the run name instead.

    Issue #350 hardens this against three more real shapes: a name that sanitizes to a
    Windows-reserved device name (``CON``, ``NUL``, ``PRN``, ``COM1``..``9``,
    ``LPT1``..``9``); a name long enough that ``<out_dir>/<name><writer suffix>`` risks
    Windows' legacy MAX_PATH; and a dots/spaces-only or trailing-dot/trailing-space name
    (Windows silently strips a trailing dot or space from a real file, so the sanitizer
    strips it too, up front, rather than let a difference disappear invisibly at write
    time). The numeric ``_<n>`` suffix is truncated around, never through: it is the
    only thing that keeps two records in one multi-record file from colliding, so
    length-capping the BASE portion first (never the suffix) is what keeps this
    function collision-proof after truncation, not just before it.
    """
    safe = re.sub(r"[^A-Za-z0-9._-]", "_", base).strip("._-") or "seq"
    suffix = "" if total == 1 else f"_{index + 1}"

    budget = _path_budget(out_dir)
    if len(safe) + len(suffix) > budget:
        keep = max(budget - len(suffix), 1)
        safe = safe[:keep].rstrip("._-") or "seq"

    return _avoid_reserved_device_name(f"{safe}{suffix}")


def _assert_unique_contig_names(contigs: list[Contig]) -> None:
    """#350: a contig-name collision must never silently let one record's output
    overwrite another's. By construction (the index suffix is preserved through
    truncation above) this should be unreachable, but this is the load-bearing safety
    net if a future change to :func:`_safe_contig_name` ever lets one through anyway --
    not decoration.
    """
    seen: dict[str, int] = {}
    for c in contigs:
        seen[c.name] = seen.get(c.name, 0) + 1
    dupes = sorted(n for n, count in seen.items() if count > 1)
    if dupes:
        raise ValidationError(
            f"{len(dupes)} contig name(s) collide after sanitization ({', '.join(dupes)}); "
            "refusing to let one record's output silently overwrite another's. This "
            "should not happen; please file a bug naming the input file's shape (not "
            "its contents)."
        )


def load_input(cfg: RunConfig, raw: str | None = None) -> LoadedInput:
    """Read + validate the configured input into a :class:`LoadedInput`.

    GenBank/FASTA files are read from ``cfg.input_path``; ``raw`` (if given) is used only
    for the paste path. GenBank/FASTA tolerate IUPAC ambiguity codes (real files contain
    ``N``); the paste path stays strict A/C/G/T.
    """
    kind = cfg.informat or detect.detect_kind(cfg.input_path)

    if kind == detect.GENBANK:
        records, notices = read_genbank(cfg.input_path)
        contigs: list[Contig] = []
        running_total = 0
        for i, rec in enumerate(records):
            v = validate_sequence(
                rec.seq, max_len=cfg.max_total_len, rna=cfg.rna, ambiguity_policy=cfg.ambiguity_policy
            )
            notices += v.notices
            running_total += len(v.seq)
            _check_whole_input_cap(running_total, cfg.max_total_len)
            contigs.append(
                Contig(
                    name=_safe_contig_name(cfg.name, i, len(records), out_dir=cfg.out_dir),
                    seq=v.seq,
                    features=rec.features,
                    source_id=rec.record_id,
                )
            )
        _assert_unique_contig_names(contigs)
        return LoadedInput(contigs=contigs, notices=notices, source_kind=kind)

    if kind == detect.FASTA:
        records, notices = read_fasta(cfg.input_path)
        contigs: list[Contig] = []
        running_total = 0
        for i, rec in enumerate(records):
            v = validate_sequence(
                rec.seq, max_len=cfg.max_total_len, rna=cfg.rna, ambiguity_policy=cfg.ambiguity_policy
            )
            notices += v.notices
            running_total += len(v.seq)
            _check_whole_input_cap(running_total, cfg.max_total_len)
            contigs.append(
                Contig(
                    name=_safe_contig_name(cfg.name, i, len(records), out_dir=cfg.out_dir),
                    seq=v.seq,
                    source_id=rec.header,
                )
            )
        _assert_unique_contig_names(contigs)
        return LoadedInput(contigs=contigs, notices=notices, source_kind=kind)

    text = raw if raw is not None else PasteReader(cfg.input_path).read()
    v = validate_sequence(text, max_len=cfg.max_total_len, rna=cfg.rna, ambiguity_policy=cfg.ambiguity_policy)
    contig = Contig(name=_safe_contig_name(cfg.name, 0, 1, out_dir=cfg.out_dir), seq=v.seq)
    return LoadedInput(contigs=[contig], notices=v.notices, source_kind=detect.PASTE)
