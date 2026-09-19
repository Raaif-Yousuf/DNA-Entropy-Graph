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
from .annotators.base import AnnotatorError
from .cloud import CloudConfig, GcloudError, LLM_HINT, run_in_cloud
from .config import DEFAULT_MAX_LEN, PredictorKind, RunConfig, TrackFormat
from .pipeline import load_and_validate
from .predictors.base import PredictorError
from .validation.validators import ValidationError

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
    input: Optional[str] = typer.Option(None, "--input", "-i", help="Read sequence from file (default: stdin)."),
    name: str = typer.Option(..., "--name", prompt="Name for this run (used for the folder and file names)", help="Output base name; prompts if omitted. (Pass it explicitly when piping the sequence via stdin.)"),
    predictor: str = typer.Option("mock", "--predictor", help="Predictor backend: mock|evo."),
    model: str = typer.Option("evo2_7b", "--model", help="Evo model id (evo predictor only)."),
    device: str = typer.Option("cuda", "--device", help="cuda|cpu (evo predictor only)."),
    out: Optional[str] = typer.Option(None, "--out", "-o", help="Base folder for outputs (default: your Downloads folder); files go in <out>/<name>/."),
    fmt: str = typer.Option("bedgraph", "--format", help="Entropy track format: bedgraph|wig."),
    start: int = typer.Option(1, "--start", help="Genomic start coordinate for the track."),
    max_len: int = typer.Option(DEFAULT_MAX_LEN, "--max-len", help="Single-pass context cap (nt)."),
    rna: bool = typer.Option(False, "--rna", help="Convert U->T (treat input as RNA)."),
    genes: bool = typer.Option(False, "--genes/--no-genes", help="Call gene boundaries (prokaryotic; needs [genes] extra)."),
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
            predictor=PredictorKind(predictor),
            model=model,
            device=device,
            out_dir=str(run_dir),
            track_format=TrackFormat(fmt),
            start=start,
            max_len=max_len,
            rna=rna,
            genes=genes,
            seed=seed,
        )
    except ValueError as exc:  # bad --predictor/--format value
        typer.secho(f"ERROR: {exc}", fg=typer.colors.RED, err=True)
        raise typer.Exit(code=1)

    try:
        result = pipeline.run(cfg)
    except (ValidationError, PredictorError, AnnotatorError) as exc:
        typer.secho(f"ERROR: {exc}", fg=typer.colors.RED, err=True)
        raise typer.Exit(code=1)

    for notice in result.notices:
        typer.secho(f"  - {notice}", fg=typer.colors.YELLOW)
    v = result.values
    typer.secho(
        f"OK: {len(result.seq)} nt analyzed (predictor={predictor}).",
        fg=typer.colors.GREEN,
    )
    typer.echo(
        f"  entropy (bits): mean={v.mean():.3f}  min={v.min():.3f}  max={v.max():.3f}"
    )
    if cfg.genes:
        typer.echo(f"  genes predicted: {len(result.genes)}")
    typer.echo(f"  folder: {run_dir}")
    typer.echo("  wrote:")
    for path in result.outputs:
        typer.echo(f"    {Path(path).name}")


@app.command()
def validate(
    input: Optional[str] = typer.Option(None, "--input", "-i", help="Read sequence from file (default: stdin)."),
    rna: bool = typer.Option(False, "--rna", help="Convert U->T (treat input as RNA)."),
    max_len: int = typer.Option(DEFAULT_MAX_LEN, "--max-len", help="Single-pass context cap (nt)."),
) -> None:
    """Validate a sequence without running a model."""
    cfg = RunConfig(input_path=input, rna=rna, max_len=max_len)
    try:
        result = load_and_validate(cfg)
    except ValidationError as exc:
        typer.secho(f"ERROR: {exc}", fg=typer.colors.RED, err=True)
        raise typer.Exit(code=1)

    for notice in result.notices:
        typer.secho(f"  - {notice}", fg=typer.colors.YELLOW)
    typer.secho(f"OK: valid sequence, {len(result)} nt", fg=typer.colors.GREEN)


@app.command()
def cloudrun(
    input: Optional[str] = typer.Option(None, "--input", "-i", help="Read sequence from file (default: stdin)."),
    name: str = typer.Option(..., "--name", prompt="Name for this run (used for the folder and file names)", help="Output base name; prompts if omitted."),
    out: Optional[str] = typer.Option(None, "--out", "-o", help="Base folder (default: your Downloads); files go in <out>/<name>/."),
    genes: bool = typer.Option(True, "--genes/--no-genes", help="Call gene boundaries (default on)."),
    rna: bool = typer.Option(False, "--rna", help="Convert U->T (treat input as RNA)."),
    keep: bool = typer.Option(False, "--keep", help="Keep the box (stopped) for fast reuse next time, instead of deleting it (double-confirmed; ~$10/mo disk)."),
    project: Optional[str] = typer.Option(None, "--project", help="GCP project (default: your active gcloud project)."),
    zone: Optional[str] = typer.Option(None, "--zone", help="Force a GPU zone (default: auto, retries on stockout)."),
    ssh_key_file: Optional[str] = typer.Option(None, "--ssh-key-file", help="Custom SSH key file (advanced/testing)."),
) -> None:
    """Run on a GPU in YOUR Google Cloud: spins up a VM, runs Evo 2, saves files, tears it down."""
    safe_name = _sanitize_name(name)
    if not safe_name:
        typer.secho("ERROR: that name has no usable characters (use letters/digits).", fg=typer.colors.RED, err=True)
        raise typer.Exit(code=1)
    base_dir = Path(out) if out else Path.home() / "Downloads"

    # Validate locally first so we never spend cloud time on bad input.
    try:
        validated = load_and_validate(RunConfig(name=safe_name, input_path=input, rna=rna))
    except ValidationError as exc:
        typer.secho(f"ERROR: {exc}", fg=typer.colors.RED, err=True)
        raise typer.Exit(code=1)
    for notice in validated.notices:
        typer.secho(f"  - {notice}", fg=typer.colors.YELLOW)

    cfg = CloudConfig(project=project, ssh_key_file=ssh_key_file)
    if zone:
        cfg.zones = [zone]

    try:
        local = run_in_cloud(
            seq=validated.seq, name=safe_name, base_dir=base_dir, genes=genes, cfg=cfg, keep=keep
        )
    except GcloudError as exc:
        typer.secho(f"\nERROR: {exc}", fg=typer.colors.RED, err=True)
        typer.secho(LLM_HINT, fg=typer.colors.CYAN, err=True)
        raise typer.Exit(code=1)
    except Exception as exc:  # never show a raw traceback to a lab user
        typer.secho(f"\nERROR: unexpected problem: {exc}", fg=typer.colors.RED, err=True)
        typer.secho(LLM_HINT, fg=typer.colors.CYAN, err=True)
        raise typer.Exit(code=1)

    typer.secho(f"OK: done. Your files are in {local}", fg=typer.colors.GREEN)


def main() -> None:
    """Console-script entry point (see pyproject `[project.scripts]`)."""
    app()


if __name__ == "__main__":
    main()
