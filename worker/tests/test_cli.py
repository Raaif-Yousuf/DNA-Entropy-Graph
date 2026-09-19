"""CLI-shape tests for issue #277 (drop cloudrun/keep-gpu) and its replacement stub.

The approved design (docs/superpowers/specs/2026-09-18-dna-entropy-graph-design.md
section 3) forbids the worker shelling out to gcloud/SSH; `cloudrun` and `keep-gpu` (plus
`--prefer-local`) are gone along with `dna_entropy.cloud`. In their place, `worker-run`
is the seed of the manifest-driven worker entrypoint issue #278 will flesh out — it must
exist and must fail loudly and specifically (pointing at #278), never silently succeed or
silently do nothing, so nobody mistakes the stub for a working feature.
"""

from __future__ import annotations

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


def test_worker_run_stub_exists_and_fails_loudly() -> None:
    """The stub must exit non-zero and name the tracking issue, not pretend to succeed."""
    result = runner.invoke(app, ["worker-run", "--manifest", "does-not-matter.json"])
    assert result.exit_code != 0
    assert "#278" in result.output


def test_worker_run_requires_a_manifest_argument() -> None:
    result = runner.invoke(app, ["worker-run", "--help"])
    assert result.exit_code == 0
    assert "--manifest" in result.output
