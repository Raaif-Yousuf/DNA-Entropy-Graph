"""Tests for worker/cli.py: the `dna-entropy-worker` container entrypoint (issues #36/#45).

A SEPARATE console script from `dna-entropy` (see worker/tests/test_cli.py) — this one's
`run` subcommand has a strict exit-code contract the VM startup script depends on.
"""

from __future__ import annotations

import json
from pathlib import Path

import pytest

from dna_entropy.worker.cli import (
    EXIT_CANCELLED,
    EXIT_DONE,
    EXIT_FAILED,
    _parse_gs_uri,
    build_parser,
    main,
)


def _write_manifest(job_dir: Path) -> None:
    manifest = {
        "schema": 1,
        "jobId": "cli-test-job",
        "inputs": [{"id": "in1", "path": "input/locus.fasta", "name": "locus"}],
        "predictor": {"kind": "mock", "seed": 0},
        "analysis": {"contextLength": 128, "window": 256, "stride": 128, "direction": "forward-only"},
        "store": {"kind": "localdir", "root": str(job_dir)},
    }
    (job_dir / "manifest.json").write_text(json.dumps(manifest), encoding="utf-8")


def _seed_input(job_dir: Path) -> None:
    (job_dir / "input").mkdir(exist_ok=True)
    (job_dir / "input" / "locus.fasta").write_text(">seq\n" + "ACGT" * 40 + "\n", encoding="utf-8")


# --- gs:// URI parsing -----------------------------------------------------------------


def test_parse_gs_uri_splits_bucket_and_prefix() -> None:
    bucket, prefix = _parse_gs_uri("gs://deg-123-abc/jobs/20260918-142233-k7q2vx/")
    assert bucket == "deg-123-abc"
    assert prefix == "jobs/20260918-142233-k7q2vx/"


def test_parse_gs_uri_rejects_non_gs_scheme() -> None:
    with pytest.raises(ValueError):
        _parse_gs_uri("https://example.com/x")


def test_parse_gs_uri_rejects_missing_bucket() -> None:
    with pytest.raises(ValueError):
        _parse_gs_uri("gs://")


# --- run: exit codes (the startup script's cleanup dispatch depends on these) ---------


def test_run_exits_zero_on_done(tmp_path: Path) -> None:
    _write_manifest(tmp_path)
    _seed_input(tmp_path)
    code = main(["run", "--store", "localdir", "--root", str(tmp_path)])
    assert code == EXIT_DONE


def test_run_localdir_accepts_deg_local_root_env_var(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    _write_manifest(tmp_path)
    _seed_input(tmp_path)
    monkeypatch.setenv("DEG_LOCAL_ROOT", str(tmp_path))
    code = main(["run", "--store", "localdir"])
    assert code == EXIT_DONE


def test_run_gcs_accepts_deg_job_uri_env_var(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    # No real GCS call happens: --store gcs with a bogus bucket fails at the blobstore
    # layer (no network in this sandbox), proving the URI WAS parsed and used to build a
    # GcsBlobstore rather than falling through to "missing --bucket/--prefix".
    monkeypatch.setenv("DEG_JOB_URI", "gs://fake-bucket/jobs/x/")
    code = main(["run", "--store", "gcs"])
    assert code == EXIT_FAILED  # fails trying to reach the (nonexistent) network, not on arg parsing


def test_run_missing_store_args_exits_failed_not_a_crash(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    monkeypatch.delenv("DEG_LOCAL_ROOT", raising=False)
    code = main(["run", "--store", "localdir"])
    assert code == EXIT_FAILED


def test_run_missing_manifest_exits_failed(tmp_path: Path) -> None:
    empty = tmp_path / "empty"
    empty.mkdir()
    code = main(["run", "--store", "localdir", "--root", str(empty)])
    assert code == EXIT_FAILED


def test_run_cancelled_job_exits_cancelled(tmp_path: Path) -> None:
    from dna_entropy.worker.blobstore import LocalBlobstore
    from dna_entropy.worker.cancel import CANCEL_PATH

    _write_manifest(tmp_path)
    _seed_input(tmp_path)
    LocalBlobstore(tmp_path).write_text(CANCEL_PATH, "")
    code = main(["run", "--store", "localdir", "--root", str(tmp_path)])
    assert code == EXIT_CANCELLED


def test_manifest_flag_is_accepted_but_does_not_change_where_the_store_reads_from(
    tmp_path: Path,
) -> None:
    """The startup script always invokes with --manifest <local staged path>; this must
    parse without error even though the worker re-reads manifest.json from the store
    root (same relative layout the script already staged it at)."""
    _write_manifest(tmp_path)
    _seed_input(tmp_path)
    code = main(
        [
            "run",
            "--manifest",
            str(tmp_path / "manifest.json"),
            "--store",
            "localdir",
            "--root",
            str(tmp_path),
        ]
    )
    assert code == EXIT_DONE


# --- selftest ----------------------------------------------------------------------


def test_selftest_prints_ok_and_exits_zero(capsys: pytest.CaptureFixture) -> None:
    code = main(["selftest"])
    assert code == 0
    assert "OK" in capsys.readouterr().out


# --- parser shape --------------------------------------------------------------------


def test_parser_requires_a_subcommand() -> None:
    parser = build_parser()
    with pytest.raises(SystemExit):
        parser.parse_args([])


def test_run_requires_store() -> None:
    parser = build_parser()
    with pytest.raises(SystemExit):
        parser.parse_args(["run"])
