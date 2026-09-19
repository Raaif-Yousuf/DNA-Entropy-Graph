"""CLI-shape tests for issue #277 (drop cloudrun/keep-gpu) and #278 (worker-run, real).

The approved design (docs/superpowers/specs/2026-09-18-dna-entropy-graph-design.md
section 3) forbids the worker shelling out to gcloud/SSH; `cloudrun` and `keep-gpu` (plus
`--prefer-local`) are gone along with `dna_entropy.cloud`. In their place, `worker-run` is
the real manifest-driven worker entrypoint (issue #278); see test_worker_runner.py for its
full end-to-end behaviour with `--root` against a `LocalBlobstore`.
"""

from __future__ import annotations

import json
from pathlib import Path

import typer.main
from typer.testing import CliRunner

from dna_entropy.cli import app

runner = CliRunner()


def test_help_lists_no_cloudrun_or_keep_gpu() -> None:
    result = runner.invoke(app, ["--help"])
    assert result.exit_code == 0
    assert "cloudrun" not in result.output
    assert "keep-gpu" not in result.output


def test_cloudrun_command_is_gone() -> None:
    result = runner.invoke(app, ["cloudrun", "--help"])
    assert result.exit_code != 0


def test_keep_gpu_command_is_gone() -> None:
    result = runner.invoke(app, ["keep-gpu", "--help"])
    assert result.exit_code != 0


def test_worker_run_requires_either_root_or_bucket_and_prefix() -> None:
    result = runner.invoke(app, ["worker-run"])
    assert result.exit_code == 2
    assert "--root" in result.output or "--bucket" in result.output


def test_worker_run_help_lists_local_and_gcs_options() -> None:
    # Asserted against the command's declared options, not its rendered help text.
    #
    # MEASURED 2026-09-19: the substring version of this test passed on a developer
    # terminal and failed on CI under two different widths, because Typer draws help
    # through Rich and what reaches `result.output` depends on the terminal, the Rich
    # version and the ANSI styling. Pinning COLUMNS did not fix it. The option names are
    # the contract worth testing; the box drawing around them is not, and a test that
    # asserts on it fails for reasons that have nothing to do with the CLI.
    result = runner.invoke(app, ["worker-run", "--help"])
    assert result.exit_code == 0

    worker_run = typer.main.get_command(app).commands["worker-run"]
    declared = {opt for param in worker_run.params for opt in param.opts}
    assert {"--root", "--bucket", "--prefix"} <= declared


def test_worker_run_missing_manifest_is_a_clean_error_not_a_crash(tmp_path: Path) -> None:
    empty_job_dir = tmp_path / "empty_job"
    empty_job_dir.mkdir()
    result = runner.invoke(app, ["worker-run", "--root", str(empty_job_dir)])
    assert result.exit_code == 2
    assert "ERROR" in result.output
    assert "Traceback" not in result.output  # a clean, reported error, not a raw crash


def test_worker_run_real_local_job_end_to_end(tmp_path: Path) -> None:
    """The CLI surface for the same acceptance bar test_worker_runner.py exercises
    directly: a real manifest, run through the actual `dna-entropy worker-run` command,
    produces a real result.json on disk."""
    job_dir = tmp_path / "job"
    job_dir.mkdir()
    (job_dir / "input").mkdir()
    (job_dir / "input" / "locus.fasta").write_text(
        ">seq\nACGTACGTACGTACGTACGTACGTACGTACGT\n", encoding="utf-8"
    )
    manifest = {
        "schema": 1,
        "jobId": "clitest-job",
        "inputs": [{"id": "in1", "path": "input/locus.fasta", "name": "locus"}],
        "predictor": {"kind": "mock", "seed": 0},
        "analysis": {"contextLength": 128, "window": 256, "stride": 128, "direction": "forward-only"},
        "store": {"kind": "localdir", "root": str(job_dir)},
    }
    (job_dir / "manifest.json").write_text(json.dumps(manifest), encoding="utf-8")

    result = runner.invoke(app, ["worker-run", "--root", str(job_dir)])
    assert result.exit_code == 0, result.output
    assert "done" in result.output
    assert (job_dir / "result.json").exists()
    result_doc = json.loads((job_dir / "result.json").read_text(encoding="utf-8"))
    assert result_doc["status"] == "done"


def test_no_tsv_flag_actually_omits_the_tsv_file(tmp_path) -> None:
    """Regression guard: --tsv/--no-tsv was declared as a CLI option but never passed into
    RunConfig, so --no-tsv silently did nothing (caught by inspection, not by a failing
    test, while wiring #281 — this test is what should have caught it)."""
    out_dir = tmp_path / "out"
    result = runner.invoke(
        app,
        ["run", "--name", "clitsv", "--out", str(out_dir), "--no-tsv"],
        input="ATGCATGCATGCATGCATGCATGCATGCATGC\n",
    )
    assert result.exit_code == 0, result.output
    written = {p.name for p in (out_dir / "clitsv").iterdir()}
    assert not any(name.endswith(".entropy.tsv") for name in written)


def test_tsv_flag_default_on_writes_the_tsv_file(tmp_path) -> None:
    out_dir = tmp_path / "out"
    result = runner.invoke(
        app,
        ["run", "--name", "clitsvon", "--out", str(out_dir)],
        input="ATGCATGCATGCATGCATGCATGCATGCATGC\n",
    )
    assert result.exit_code == 0, result.output
    written = {p.name for p in (out_dir / "clitsvon").iterdir()}
    assert any(name.endswith(".entropy.tsv") for name in written)
