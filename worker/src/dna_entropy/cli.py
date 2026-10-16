"""Command-line entry point.

This layer only parses arguments and delegates; all logic lives in the pipeline and the
stage modules (CLAUDE.md). Sprint 0 wires up `version`; `run` and `validate` are stubs
filled in during Sprints 1-2.
"""

from __future__ import annotations

from pathlib import Path

import typer

from . import __version__, pipeline
from .analysis.compare import CompareError, difference, read_entropy_tsv
from .analysis.composition import DEFAULT_GC_WINDOW
from .analysis.regions import DEFAULT_MERGE_GAP, DEFAULT_MIN_LENGTH, DEFAULT_THRESHOLD_BITS
from .analysis.smoothing import DEFAULT_SMOOTHING_WINDOWS
from .analysis.variants import (
    DEFAULT_MAX_VARIANTS,
    VariantError,
    check_count,
    cost_estimate,
    parse_variants_tsv,
    positions_to_variants,
    scan,
)
from .analysis.windowing import WindowingError
from .annotators.base import AnnotatorError
from .config import (
    DEFAULT_CONTEXT_LENGTH,
    DEFAULT_MAX_LEN,
    DEFAULT_MAX_TOTAL_LEN,
    AmbiguityPolicy,
    Direction,
    PredictorKind,
    RunConfig,
    Topology,
    TrackFormat,
)
from .pipeline import PipelineError, build_predictor, load_and_validate, sanitize_run_name
from .predictors.base import PredictorError
from .readers import entrez
from .readers.input import load_input
from .validation.validators import ValidationError
from .worker.blobstore import BlobstoreError, GcsBlobstore, LocalBlobstore
from .worker.manifest import ManifestError
from .worker.runner import run_job
from .writers.compare import write_compare
from .writers.variants import write_variants

app = typer.Typer(
    add_completion=False,
    help="Per-position DNA entropy via a genomic language model, exported for IGV.",
)


@app.command()
def version() -> None:
    """Print the version and exit."""
    typer.echo(f"dna-entropy {__version__}")


_MOVED_MESSAGE = (
    "ERROR: {name!r} is gone from this build. It used to drive Evo 2 on a "
    "user-managed gcloud GPU box directly from this CLI; the approved design talks to "
    "Google's APIs from the DNA-Entropy-Graph app instead (no gcloud, no SSH -- "
    "docs/superpowers/specs/2026-09-18-dna-entropy-graph-design.md section 3). Use the "
    "app, or 'dna-entropy worker-run' if you are driving the worker directly from a "
    "manifest.json."
)


# issue #212: `cloudrun`/`keep-gpu` are gone (issue #277), but a user typing the old
# command out of habit got Typer's own generic "No such command" error before this --
# which renders with Unicode box-drawing characters (a real bug: FEATURES.md section
# 8.4's own documented rule is "ASCII-safe console output throughout ... because lab
# users run a Windows cp1252 console that crashes on Unicode glyphs", and that crash is
# exactly reproducible by piping Typer's default error through a cp1252 stdout).
# `hidden=True` keeps them out of `--help`'s command list (they are not a supported
# feature to advertise) while still answering with a clear, ASCII-only, ERROR-prefixed
# message -- matching every other error path in this CLI -- if someone does type them.
@app.command(hidden=True)
def cloudrun() -> None:
    """Removed. See the app instead."""
    typer.secho(_MOVED_MESSAGE.format(name="cloudrun"), fg=typer.colors.RED, err=True)
    raise typer.Exit(code=1)


@app.command("keep-gpu", hidden=True)
def keep_gpu() -> None:
    """Removed. See the app instead."""
    typer.secho(_MOVED_MESSAGE.format(name="keep-gpu"), fg=typer.colors.RED, err=True)
    raise typer.Exit(code=1)


def _parse_accession_range(text: str | None) -> tuple[int, int] | None:
    """``"100-500"`` -> ``(100, 500)``; ``None`` -> ``None``."""
    if text is None:
        return None
    parts = text.replace("..", "-").split("-")
    if len(parts) != 2 or not all(p.strip().isdigit() for p in parts):
        raise ValidationError("The range must look like 100-500. Correct --accession-range and try again.")
    return int(parts[0]), int(parts[1])


@app.command()
def run(
    input: str | None = typer.Option(
        None,
        "--input",
        "-i",
        help="Read sequence from file (FASTA/GenBank/plain; default: stdin).",
    ),
    name: str = typer.Option(
        ...,
        "--name",
        prompt="Name for this run (used for the folder and file names)",
        help="Output base name; prompts if omitted. (Pass it explicitly when piping the sequence via stdin.)",
    ),
    informat: str | None = typer.Option(
        None,
        "--informat",
        help="Force input format: genbank|embl|snapgene|fasta|paste (default: auto-detect).",
    ),
    gff3: str | None = typer.Option(
        None,
        "--gff3",
        help="Pair a GFF3 annotation with a FASTA --input: its genes are used and Prodigal never "
        "runs (issue #135). Sequence names (first column) must match the FASTA header names.",
    ),
    accession: str | None = typer.Option(
        None,
        "--accession",
        help="Fetch the record from NCBI by accession (e.g. NC_045512.2) instead of reading --input "
        "(issue #133). Cached; the accession and retrieval date go into provenance.json.",
    ),
    accession_range: str | None = typer.Option(
        None, "--accession-range", help="With --accession: fetch only START-END (1-based, inclusive)."
    ),
    ncbi_api_key: str | None = typer.Option(
        None,
        "--ncbi-api-key",
        envvar="NCBI_API_KEY",
        help="Optional NCBI API key (raises NCBI's rate limit). Never stored.",
    ),
    cache_dir: str | None = typer.Option(
        None, "--cache-dir", help="Where fetched records are cached (default: a per-user cache folder)."
    ),
    predictor: str = typer.Option("mock", "--predictor", help="Predictor backend: mock|evo."),
    model: str = typer.Option("evo2_7b", "--model", help="Evo model id (evo predictor only)."),
    device: str = typer.Option("cuda", "--device", help="cuda|cpu (evo predictor only)."),
    out: str | None = typer.Option(
        None,
        "--out",
        "-o",
        help="Base folder for outputs (default: your Downloads folder); files go in <out>/<name>/.",
    ),
    fmt: str = typer.Option("bedgraph", "--format", help="Entropy track format: bedgraph|wig."),
    start: int = typer.Option(1, "--start", help="Genomic start coordinate for the track."),
    max_len: int = typer.Option(
        DEFAULT_MAX_LEN,
        "--max-len",
        help="GPU ceiling for one model forward pass (window cap, nt); NOT a limit on total "
        "input length anymore (windowing tiles longer sequences).",
    ),
    max_total_len: int = typer.Option(
        DEFAULT_MAX_TOTAL_LEN,
        "--max-total-len",
        help="Outer sanity bound on the whole input (nt), independent of --max-len/windowing.",
    ),
    context_length: int = typer.Option(
        DEFAULT_CONTEXT_LENGTH,
        "--context-length",
        "-k",
        help="K: sequence the model must have seen before a prediction is trusted (nt).",
    ),
    direction: str = typer.Option(
        "both-combined",
        "--direction",
        help="both-combined|both-averaged|both-separate|forward-only|reverse-only.",
    ),
    topology: str = typer.Option(
        "auto",
        "--topology",
        help="auto|linear|circular. Circular wraps the sequence around (a plasmid) so the first "
        "bases get real context; auto reads a GenBank LOCUS line that says circular.",
    ),
    rna: bool = typer.Option(False, "--rna", help="Convert U->T (treat input as RNA)."),
    ambiguity: str = typer.Option(
        "keep",
        "--ambiguity",
        help="What to do with an IUPAC ambiguity code (N, R, Y, ...): keep it as-is, mask "
        "it to N, or refuse the input outright. keep|mask|error (docs/science_and_formats.md).",
    ),
    genes: bool = typer.Option(
        False,
        "--genes/--no-genes",
        help="Call gene boundaries (prokaryotic; needs [genes] extra).",
    ),
    tsv: bool = typer.Option(
        True,
        "--tsv/--no-tsv",
        help="Also write <name>.entropy.tsv (position, base, entropy; spreadsheet-friendly).",
    ),
    surprisal: bool = typer.Option(
        True,
        "--surprisal/--no-surprisal",
        help="Also compute per-position surprisal (-log2 P(actual base)) alongside entropy: "
        "written to the bedgraph/wig/geneious tracks, an extra TSV column, and stats.txt "
        "(docs/science_and_formats.md; issue #123). Zero extra GPU cost.",
    ),
    probs: bool = typer.Option(
        False,
        "--probs/--no-probs",
        help="Also write <name>.probs.tsv.gz: the per-base A/C/G/T probabilities behind the entropy "
        "(large; off by default; issue #127).",
    ),
    probs_npy: bool = typer.Option(
        False,
        "--probs-npy/--no-probs-npy",
        help="Also write <name>.probs.npy: the same matrix as a float32 NumPy array (issue #127).",
    ),
    smoothed: bool = typer.Option(
        True,
        "--smoothed/--no-smoothed",
        help="Also write <name>.entropy.smooth<W>.bedgraph (or .wig): a rolling mean of the entropy "
        "track per --smoothing-window. The raw track is always written too (issue #126).",
    ),
    smoothing_window: list[int] | None = typer.Option(
        None,
        "--smoothing-window",
        help="Smoothing window in bases, an odd number; repeat for several "
        f"(default {', '.join(map(str, DEFAULT_SMOOTHING_WINDOWS))}).",
    ),
    regions: bool = typer.Option(
        True,
        "--regions/--no-regions",
        help="Also write <name>.regions.bed and <name>.regions.gff3: stretches of low and high "
        "entropy as a feature track (issue #125).",
    ),
    region_threshold: float = typer.Option(
        DEFAULT_THRESHOLD_BITS,
        "--region-threshold",
        help="Region caller: bases below this many bits are low-entropy (and above 2 minus this, "
        "high-entropy). Above 0, at most 1.",
    ),
    region_min_length: int = typer.Option(
        DEFAULT_MIN_LENGTH, "--region-min-length", help="Region caller: shortest region to report (bases)."
    ),
    region_merge_gap: int = typer.Option(
        DEFAULT_MERGE_GAP,
        "--region-merge-gap",
        help="Region caller: merge regions at most this many bases apart.",
    ),
    gene_summary: bool = typer.Option(
        True,
        "--gene-summary/--no-gene-summary",
        help="Also write <name>.genes.tsv and <name>.genes.csv: one row per gene with its mean, "
        "min and max entropy (needs genes: a GenBank input, or --genes; issue #124).",
    ),
    gc: bool = typer.Option(
        False,
        "--gc/--no-gc",
        help="Also write <name>.gc.bedgraph: GC percent over a rolling window; no model needed (issue #191).",
    ),
    gc_window: int = typer.Option(
        DEFAULT_GC_WINDOW, "--gc-window", help="GC window in bases, an odd number (with --gc)."
    ),
    codons: bool = typer.Option(
        False,
        "--codons/--no-codons",
        help="Also write <name>.codons.tsv: codon usage per gene (needs genes: a GenBank input, "
        "a --gff3, or --genes; issue #191).",
    ),
    seed: int = typer.Option(0, "--seed", help="Mock predictor seed (reproducibility)."),
) -> None:
    """Run the full pipeline: validate -> predict -> entropy -> IGV files."""
    # issue #366: the SAME sanitizer pipeline.run() itself applies (its own top, before
    # anything is read or written) is used here too, so the folder name (built here,
    # before pipeline.run() is even called) and every file name inside it (built by
    # pipeline.run() from cfg.name) always agree -- one rule, not two ad hoc copies.
    try:
        safe_name = sanitize_run_name(name)
    except PipelineError as exc:
        typer.secho(f"ERROR: {exc}", fg=typer.colors.RED, err=True)
        raise typer.Exit(code=1) from exc
    base_dir = Path(out) if out else Path.home() / "Downloads"
    run_dir = base_dir / safe_name

    # issue #133: everything that can be checked without the network is checked BEFORE the fetch,
    # so a run that could never start (a bad option, a conflicting flag, a malformed accession)
    # never costs a request. The fetch itself comes after the RunConfig is built, below.
    start_end: tuple[int, int] | None = None
    if accession is not None:
        try:
            if input is not None:
                raise ValidationError("Use either --accession or --input, not both. Drop one of them.")
            if informat not in (None, "genbank"):
                raise ValidationError(
                    f"--accession always gives a GenBank record, so --informat {informat} does not "
                    "apply. Drop --informat (or add the file with --input instead)."
                )
            start_end = _parse_accession_range(accession_range)
            entrez.validate_request(
                accession, start_end[0] if start_end else None, start_end[1] if start_end else None
            )
        except ValidationError as exc:
            typer.secho(f"ERROR: {exc}", fg=typer.colors.RED, err=True)
            raise typer.Exit(code=1) from exc
    elif accession_range is not None:
        typer.secho(
            "ERROR: --accession-range needs --accession. Add --accession.", fg=typer.colors.RED, err=True
        )
        raise typer.Exit(code=1)

    try:
        cfg = RunConfig(
            name=safe_name,
            input_path=input,
            informat=informat,
            annotation_path=gff3,
            predictor=PredictorKind(predictor),
            model=model,
            device=device,
            out_dir=str(run_dir),
            track_format=TrackFormat(fmt),
            start=start,
            max_len=max_len,
            max_total_len=max_total_len,
            context_length=context_length,
            direction=Direction(direction),
            topology=Topology(topology),
            rna=rna,
            ambiguity_policy=AmbiguityPolicy(ambiguity),
            genes=genes,
            include_tsv=tsv,
            include_surprisal=surprisal,
            include_probs=probs,
            include_probs_npy=probs_npy,
            include_smoothed=smoothed,
            smoothing_windows=(
                tuple(smoothing_window) if smoothing_window is not None else DEFAULT_SMOOTHING_WINDOWS
            ),
            include_regions=regions,
            region_threshold=region_threshold,
            region_min_length=region_min_length,
            region_merge_gap=region_merge_gap,
            include_gene_summary=gene_summary,
            include_gc=gc,
            gc_window=gc_window,
            include_codons=codons,
            seed=seed,
        )
    except ValueError as exc:  # bad --predictor/--format/--direction/--ambiguity value
        typer.secho(f"ERROR: {exc}", fg=typer.colors.RED, err=True)
        raise typer.Exit(code=1) from exc

    provenance_extra = None
    if accession is not None:
        try:
            pipeline.validate_run_options(cfg)
            fetched = entrez.fetch_genbank(
                accession,
                cache_dir=cache_dir or entrez.default_cache_dir(),
                start=start_end[0] if start_end else None,
                stop=start_end[1] if start_end else None,
                api_key=ncbi_api_key,
            )
        except (ValidationError, PipelineError) as exc:
            typer.secho(f"ERROR: {exc}", fg=typer.colors.RED, err=True)
            raise typer.Exit(code=1) from exc
        cfg.input_path = fetched.path
        cfg.informat = "genbank"
        provenance_extra = {"input_source": fetched.provenance()}

    try:
        result = pipeline.run(cfg, provenance_extra=provenance_extra)
    except (ValidationError, PredictorError, AnnotatorError, WindowingError, PipelineError) as exc:
        typer.secho(f"ERROR: {exc}", fg=typer.colors.RED, err=True)
        raise typer.Exit(code=1) from exc

    for notice in result.notices:
        typer.secho(f"  - {notice}", fg=typer.colors.YELLOW)
    v = result.all_values
    if result.contigs > 1:
        headline = (
            f"OK: {result.total_nt} nt across {result.contigs} sequences analyzed (predictor={predictor})."
        )
    else:
        headline = f"OK: {len(result.seq)} nt analyzed (predictor={predictor})."
    typer.secho(headline, fg=typer.colors.GREEN)
    typer.echo(f"  entropy (bits): mean={v.mean():.3f}  min={v.min():.3f}  max={v.max():.3f}")
    typer.echo(
        f"  context: K={result.context_length}  window={result.window}  stride={result.stride}"
        + (f"  seam@{result.seam}" if result.seam is not None else "")
        + f"  direction={result.direction.value}"
    )
    if result.reduced_context_count:
        typer.secho(
            f"  reduced-context positions: {result.reduced_context_count}",
            fg=typer.colors.YELLOW,
        )
    if result.genes:
        typer.echo(f"  genes: {len(result.genes)}")
    typer.echo(f"  folder: {run_dir}")
    typer.echo("  wrote:")
    for path in result.outputs:
        typer.echo(f"    {Path(path).name}")


def _pick_track(blocks: dict, wanted: str | None, which: str):
    """The one block to compare: the named contig, or the file's only one (issue #129)."""
    if wanted is not None:
        if wanted not in blocks:
            raise CompareError(
                f"The {which} file has no contig named {wanted!r}. Check the name or omit --contig."
            )
        return wanted, blocks[wanted]
    if len(blocks) != 1:
        raise CompareError(
            f"The {which} file holds {len(blocks)} records. Name the one to compare with --contig."
        )
    return next(iter(blocks.items()))


@app.command()
def compare(
    a: str = typer.Argument(..., help="First run's <name>.entropy.tsv (the reference)."),
    b: str = typer.Argument(..., help="Second run's <name>.entropy.tsv; the difference is B minus A."),
    name: str = typer.Option("compare", "--name", help="Output file prefix."),
    out_dir: str = typer.Option(
        ".", "--out-dir", help="Folder for the difference track and deviations table."
    ),
    offset: int = typer.Option(
        0, "--offset", help="B position p is compared with A position p + offset (default 0: same length)."
    ),
    contig: str | None = typer.Option(
        None, "--contig", help="Contig name in both files, for multi-record runs."
    ),
    top: int = typer.Option(10, "--top", help="How many largest deviations to list."),
) -> None:
    """Compare two runs or two sequences: a difference track (B minus A) and the largest deviations."""
    try:
        name_a, track_a = _pick_track(read_entropy_tsv(a), contig, "first")
        _, track_b = _pick_track(read_entropy_tsv(b), contig, "second")
        positions, diff = difference(track_a, track_b, offset)
        out = write_compare(
            name=sanitize_run_name(name),
            contig=name_a,
            positions=positions,
            diff=diff,
            out_dir=out_dir,
            top=top,
        )
    except (ValidationError, ValueError, OSError) as exc:
        typer.secho(f"ERROR: {exc}", fg=typer.colors.RED, err=True)
        raise typer.Exit(code=1) from exc
    typer.echo(
        f"OK: compared {len(diff)} positions, "
        f"largest change {max((abs(d) for _, d in out.top), default=0.0):.4f} bits"
    )
    for pos, d in out.top:
        typer.echo(f"  - position {pos}: {d:+.4f} bits")
    typer.echo(f"OK: wrote {out.bedgraph}")
    typer.echo(f"OK: wrote {out.deviations}")


@app.command()
def variants(
    input: str = typer.Option(..., "--input", "-i", help="The sequence (one record) the variants are on."),
    variants_file: str | None = typer.Option(
        None, "--variants", help="TSV of pos, ref, alt (one substitution per line)."
    ),
    positions: str | None = typer.Option(
        None, "--positions", help="Comma-separated 1-based positions: all three substitutions at each."
    ),
    max_variants: int = typer.Option(
        DEFAULT_MAX_VARIANTS, "--max-variants", help="Refuse more than this many (one forward pass each)."
    ),
    dry_run: bool = typer.Option(False, "--dry-run", help="Print the cost estimate and stop."),
    name: str = typer.Option("variants", "--name", help="Output file prefix."),
    out_dir: str = typer.Option(".", "--out-dir", help="Folder for <name>.variants.tsv."),
    predictor: str = typer.Option("mock", "--predictor", help="Predictor backend: mock|evo."),
    model: str = typer.Option("evo2_7b", "--model", help="Evo model id (evo predictor only)."),
    device: str = typer.Option("cuda", "--device", help="cuda|cpu (evo predictor only)."),
    context_length: int = typer.Option(
        DEFAULT_CONTEXT_LENGTH, "--context-length", help="K: each variant is scored on position +- K/2."
    ),
    seed: int = typer.Option(0, "--seed", help="Mock predictor seed (reproducibility)."),
) -> None:
    """Score in-silico substitutions: change in surprisal and local entropy, one pass per variant."""
    try:
        if (variants_file is None) == (positions is None):
            raise VariantError("Give exactly one of --variants and --positions.")
        cfg = RunConfig(
            input_path=input,
            name=sanitize_run_name(name),
            out_dir=out_dir,
            predictor=PredictorKind(predictor),
            model=model,
            device=device,
            context_length=context_length,
            seed=seed,
        )
        loaded = load_input(cfg)
        if len(loaded.contigs) != 1:
            raise VariantError(
                f"The input has {len(loaded.contigs)} records; the variant scan takes one. "
                "Give a file with one record."
            )
        seq = loaded.contigs[0].seq
        if variants_file is not None:
            found = parse_variants_tsv(variants_file, seq)
        else:
            try:
                wanted = [int(p) for p in (positions or "").split(",") if p.strip()]
            except ValueError:
                raise VariantError(
                    "--positions must be whole numbers separated by commas, like 12,40,77."
                ) from None
            found = positions_to_variants(wanted, seq)
        check_count(found, max_variants)
        typer.echo(f"Cost: {cost_estimate(len(found))}.")
        if dry_run:
            return
        results = scan(seq, found, build_predictor(cfg), context_length)
        path = write_variants(name=cfg.name, results=results, out_dir=out_dir)
    except (ValidationError, PredictorError, ValueError, OSError) as exc:
        typer.secho(f"ERROR: {exc}", fg=typer.colors.RED, err=True)
        raise typer.Exit(code=1) from exc
    typer.echo(f"OK: scored {len(results)} variant(s)")
    typer.echo(f"OK: wrote {path}")


@app.command()
def validate(
    input: str | None = typer.Option(None, "--input", "-i", help="Read sequence from file (default: stdin)."),
    rna: bool = typer.Option(False, "--rna", help="Convert U->T (treat input as RNA)."),
    max_len: int = typer.Option(
        DEFAULT_MAX_TOTAL_LEN,
        "--max-len",
        help="Outer sanity bound on the whole input (nt) — this command only cleans/validates, "
        "it never runs windowing, so there is no separate per-pass ceiling to set here.",
    ),
    ambiguity: str = typer.Option(
        "keep",
        "--ambiguity",
        help="What to do with an IUPAC ambiguity code (N, R, Y, ...): keep it as-is, mask "
        "it to N, or refuse the input outright. keep|mask|error. The default matches `run`, "
        "so `validate` reports what a real run would do (docs/science_and_formats.md).",
    ),
) -> None:
    """Validate a sequence without running a model."""
    try:
        cfg = RunConfig(
            input_path=input, rna=rna, max_total_len=max_len, ambiguity_policy=AmbiguityPolicy(ambiguity)
        )
        result = load_and_validate(cfg)
    except (ValidationError, ValueError) as exc:
        typer.secho(f"ERROR: {exc}", fg=typer.colors.RED, err=True)
        raise typer.Exit(code=1) from exc

    for notice in result.notices:
        typer.secho(f"  - {notice}", fg=typer.colors.YELLOW)
    typer.secho(f"OK: valid sequence, {len(result)} nt", fg=typer.colors.GREEN)


@app.command("worker-run")
def worker_run(
    root: str | None = typer.Option(
        None,
        "--root",
        help="Local job directory containing manifest.json (LocalBlobstore; the local "
        "engine and every test in this repo use this path).",
    ),
    bucket: str | None = typer.Option(None, "--bucket", help="GCS bucket name (GcsBlobstore)."),
    prefix: str | None = typer.Option(
        None,
        "--prefix",
        help="GCS job prefix, e.g. jobs/<jobId>/ (GcsBlobstore, used with --bucket).",
    ),
) -> None:
    """Run one job from manifest.json: read it, run the pipeline once per input, and
    write status.json/progress.jsonl/result.json (docs/job_contract.md).

    Local jobs: ``--root <dir>``. Cloud jobs: ``--bucket``/``--prefix`` together
    (GcsBlobstore is implemented but not exercised against real GCS by this command
    tonight — no cloud spend, no cloud calls; see worker/lifecycle.py).
    """
    if root:
        store = LocalBlobstore(root)
    elif bucket and prefix:
        store = GcsBlobstore(bucket, prefix)
    else:
        typer.secho(
            "ERROR: pass either --root (local) or --bucket AND --prefix (gcs).",
            fg=typer.colors.RED,
            err=True,
        )
        raise typer.Exit(code=2)

    try:
        result = run_job(store)
    except (ManifestError, BlobstoreError) as exc:
        # ManifestError: manifest.json exists but is invalid/wrong schema. BlobstoreError:
        # manifest.json (or the store root) doesn't exist at all — both are "the job
        # cannot even start", reported the same clean way, never a raw traceback.
        typer.secho(f"ERROR: {exc}", fg=typer.colors.RED, err=True)
        raise typer.Exit(code=2) from exc

    typer.secho(
        f"job {result.jobId}: {result.status}",
        fg=typer.colors.GREEN if result.status == "done" else typer.colors.RED,
    )
    for ir in result.inputs:
        line = f"  {ir.id}: {ir.status}"
        if ir.error:
            line += f" ({ir.error.get('message', ir.error)})"
        typer.echo(line)
    raise typer.Exit(code=0 if result.status == "done" else 1)


def main() -> None:
    """Console-script entry point (see pyproject `[project.scripts]`)."""
    app()


if __name__ == "__main__":
    main()
