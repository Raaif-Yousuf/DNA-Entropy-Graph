"""Command-line entry point.

This layer only parses arguments and delegates; all logic lives in the pipeline and the
stage modules (CLAUDE.md). Sprint 0 wires up `version`; `run` and `validate` are stubs
filled in during Sprints 1-2.
"""

from __future__ import annotations

import re
from pathlib import Path
from typing import Optional

import typer

from . import __version__, pipeline
from .analysis.windowing import WindowingError
from .annotators.base import AnnotatorError
from .config import (
    DEFAULT_CONTEXT_LENGTH,
    DEFAULT_MAX_LEN,
    DEFAULT_MAX_TOTAL_LEN,
    Direction,
    PredictorKind,
    RunConfig,
    TrackFormat,
)
from .pipeline import load_and_validate
from .predictors.base import PredictorError
from .validation.validators import ValidationError
from .worker.blobstore import BlobstoreError, GcsBlobstore, LocalBlobstore
from .worker.manifest import ManifestError
from .worker.runner import run_job

app = typer.Typer(
    add_completion=False,
    help="Per-position DNA entropy via a genomic language model, exported for IGV.",
)


def _sanitize_name(raw: str) -> str:
    """Make a user-supplied name safe for a folder, file names, and an IGV contig id."""
    s = re.sub(r"\s+", "_", raw.strip())
    s = re.sub(r"[^A-Za-z0-9._-]", "_", s)
    return s.strip("._-")


@app.command()
def version() -> None:
    """Print the version and exit."""
    typer.echo(f"dna-entropy {__version__}")


@app.command()
def run(
    input: Optional[str] = typer.Option(None, "--input", "-i", help="Read sequence from file (FASTA/GenBank/plain; default: stdin)."),
    name: str = typer.Option(..., "--name", prompt="Name for this run (used for the folder and file names)", help="Output base name; prompts if omitted. (Pass it explicitly when piping the sequence via stdin.)"),
    informat: Optional[str] = typer.Option(None, "--informat", help="Force input format: genbank|fasta|paste (default: auto-detect by extension/content)."),
    predictor: str = typer.Option("mock", "--predictor", help="Predictor backend: mock|evo."),
    model: str = typer.Option("evo2_7b", "--model", help="Evo model id (evo predictor only)."),
    device: str = typer.Option("cuda", "--device", help="cuda|cpu (evo predictor only)."),
    out: Optional[str] = typer.Option(None, "--out", "-o", help="Base folder for outputs (default: your Downloads folder); files go in <out>/<name>/."),
    fmt: str = typer.Option("bedgraph", "--format", help="Entropy track format: bedgraph|wig."),
    start: int = typer.Option(1, "--start", help="Genomic start coordinate for the track."),
    max_len: int = typer.Option(DEFAULT_MAX_LEN, "--max-len", help="GPU ceiling for one model forward pass (window cap, nt); NOT a limit on total input length anymore (windowing tiles longer sequences)."),
    max_total_len: int = typer.Option(DEFAULT_MAX_TOTAL_LEN, "--max-total-len", help="Outer sanity bound on the whole input (nt), independent of --max-len/windowing."),
    context_length: int = typer.Option(DEFAULT_CONTEXT_LENGTH, "--context-length", "-k", help="K: sequence the model must have seen before a prediction is trusted (nt)."),
    direction: str = typer.Option("both-combined", "--direction", help="both-combined|both-averaged|both-separate|forward-only|reverse-only."),
    rna: bool = typer.Option(False, "--rna", help="Convert U->T (treat input as RNA)."),
    genes: bool = typer.Option(False, "--genes/--no-genes", help="Call gene boundaries (prokaryotic; needs [genes] extra)."),
    tsv: bool = typer.Option(True, "--tsv/--no-tsv", help="Also write <name>.entropy.tsv (position, base, entropy; spreadsheet-friendly)."),
    seed: int = typer.Option(0, "--seed", help="Mock predictor seed (reproducibility)."),
) -> None:
    """Run the full pipeline: validate -> predict -> entropy -> IGV files."""
    safe_name = _sanitize_name(name)
    if not safe_name:
        typer.secho("ERROR: that name has no usable characters (use letters/digits).", fg=typer.colors.RED, err=True)
        raise typer.Exit(code=1)
    base_dir = Path(out) if out else Path.home() / "Downloads"
    run_dir = base_dir / safe_name

    try:
        cfg = RunConfig(
            name=safe_name,
            input_path=input,
            informat=informat,
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
            rna=rna,
            genes=genes,
            include_tsv=tsv,
            seed=seed,
        )
    except ValueError as exc:  # bad --predictor/--format/--direction value
        typer.secho(f"ERROR: {exc}", fg=typer.colors.RED, err=True)
        raise typer.Exit(code=1)

    try:
        result = pipeline.run(cfg)
    except (ValidationError, PredictorError, AnnotatorError, WindowingError) as exc:
        typer.secho(f"ERROR: {exc}", fg=typer.colors.RED, err=True)
        raise typer.Exit(code=1)

    for notice in result.notices:
        typer.secho(f"  - {notice}", fg=typer.colors.YELLOW)
    v = result.all_values
    if result.contigs > 1:
        headline = f"OK: {result.total_nt} nt across {result.contigs} sequences analyzed (predictor={predictor})."
    else:
        headline = f"OK: {len(result.seq)} nt analyzed (predictor={predictor})."
    typer.secho(headline, fg=typer.colors.GREEN)
    typer.echo(
        f"  entropy (bits): mean={v.mean():.3f}  min={v.min():.3f}  max={v.max():.3f}"
    )
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


@app.command()
def validate(
    input: Optional[str] = typer.Option(None, "--input", "-i", help="Read sequence from file (default: stdin)."),
    rna: bool = typer.Option(False, "--rna", help="Convert U->T (treat input as RNA)."),
    max_len: int = typer.Option(DEFAULT_MAX_TOTAL_LEN, "--max-len", help="Outer sanity bound on the whole input (nt) — this command only cleans/validates, it never runs windowing, so there is no separate per-pass ceiling to set here."),
) -> None:
    """Validate a sequence without running a model."""
    cfg = RunConfig(input_path=input, rna=rna, max_total_len=max_len)
    try:
        result = load_and_validate(cfg)
    except ValidationError as exc:
        typer.secho(f"ERROR: {exc}", fg=typer.colors.RED, err=True)
        raise typer.Exit(code=1)

    for notice in result.notices:
        typer.secho(f"  - {notice}", fg=typer.colors.YELLOW)
    typer.secho(f"OK: valid sequence, {len(result)} nt", fg=typer.colors.GREEN)


@app.command("worker-run")
def worker_run(
    root: Optional[str] = typer.Option(None, "--root", help="Local job directory containing manifest.json (LocalBlobstore; the local engine and every test in this repo use this path)."),
    bucket: Optional[str] = typer.Option(None, "--bucket", help="GCS bucket name (GcsBlobstore)."),
    prefix: Optional[str] = typer.Option(None, "--prefix", help="GCS job prefix, e.g. jobs/<jobId>/ (GcsBlobstore, used with --bucket)."),
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
            fg=typer.colors.RED, err=True,
        )
        raise typer.Exit(code=2)

    try:
        result = run_job(store)
    except (ManifestError, BlobstoreError) as exc:
        # ManifestError: manifest.json exists but is invalid/wrong schema. BlobstoreError:
        # manifest.json (or the store root) doesn't exist at all — both are "the job
        # cannot even start", reported the same clean way, never a raw traceback.
        typer.secho(f"ERROR: {exc}", fg=typer.colors.RED, err=True)
        raise typer.Exit(code=2)

    typer.secho(f"job {result.jobId}: {result.status}",
                fg=typer.colors.GREEN if result.status == "done" else typer.colors.RED)
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
