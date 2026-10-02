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
    EXIT_REQUEST_DELETE,
    EXIT_REQUEST_STOP,
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


# --- issue #44: exit 10/11 report an ALREADY-APPLIED stop/delete ---------------------------
#
# The worker applies the after-task lifecycle itself through the Compute API (lifecycle.py).
# Exit 10 / 11 tell the startup script "the VM was stopped / deleted by the worker, do not
# apply it again"; the job's own done/failed/cancelled outcome is always in result.json.


def _gcs_kind_job(tmp_path: Path, lifecycle: dict) -> None:
    manifest = {
        "schema": 1,
        "jobId": "cli-lifecycle-job",
        "inputs": [{"id": "in1", "path": "input/locus.fasta", "name": "locus"}],
        "predictor": {"kind": "mock", "seed": 0},
        "analysis": {"contextLength": 128, "window": 256, "stride": 128, "direction": "forward-only"},
        "lifecycle": lifecycle,
        "store": {"kind": "gcs", "bucket": "b", "prefix": "jobs/cli-lifecycle-job/"},
    }
    (tmp_path / "manifest.json").write_text(json.dumps(manifest), encoding="utf-8")
    _seed_input(tmp_path)


@pytest.fixture
def lifecycle_calls(monkeypatch: pytest.MonkeyPatch, tmp_path: Path):
    """Run the real runner against a LocalBlobstore whose manifest says kind=gcs, with the
    Compute API call replaced by a recorder (no network, no cloud)."""
    from dna_entropy.worker import cli as worker_cli
    from dna_entropy.worker import runner
    from dna_entropy.worker.blobstore import LocalBlobstore

    calls: list[str] = []
    monkeypatch.setattr(worker_cli, "_build_store", lambda args: LocalBlobstore(tmp_path))

    def _fake_apply(action: str, **_kw):
        calls.append(action)
        return {"name": "operation-1"}

    monkeypatch.setattr(runner, "apply_lifecycle", _fake_apply)
    return calls


def test_run_exits_10_when_the_worker_stopped_the_vm(tmp_path: Path, lifecycle_calls: list[str]) -> None:
    _gcs_kind_job(tmp_path, {"afterTask": "stop"})
    assert main(["run", "--store", "gcs", "--bucket", "b", "--prefix", "p"]) == EXIT_REQUEST_STOP
    assert lifecycle_calls == ["stop"]


def test_run_exits_11_when_the_worker_deleted_the_vm(tmp_path: Path, lifecycle_calls: list[str]) -> None:
    _gcs_kind_job(tmp_path, {"afterTask": "delete"})
    assert main(["run", "--store", "gcs", "--bucket", "b", "--prefix", "p"]) == EXIT_REQUEST_DELETE
    assert lifecycle_calls == ["delete"]


def test_run_exits_10_for_keep_that_degraded_to_stop(tmp_path: Path, lifecycle_calls: list[str]) -> None:
    _gcs_kind_job(tmp_path, {"afterTask": "keep"})  # afterKeepAlive defaults to "stop" (#93 not built)
    assert main(["run", "--store", "gcs", "--bucket", "b", "--prefix", "p"]) == EXIT_REQUEST_STOP
    assert lifecycle_calls == ["stop"]


def test_run_exit_code_reports_the_lifecycle_even_when_the_job_failed(
    tmp_path: Path, lifecycle_calls: list[str]
) -> None:
    _gcs_kind_job(tmp_path, {"afterTask": "delete"})
    (tmp_path / "input" / "locus.fasta").unlink()  # the input is missing: the job fails
    code = main(["run", "--store", "gcs", "--bucket", "b", "--prefix", "p"])
    assert code == EXIT_REQUEST_DELETE
    result = json.loads((tmp_path / "result.json").read_text(encoding="utf-8"))
    assert result["status"] == "done"  # per-input failure is isolated; the input itself says failed
    assert result["inputs"][0]["status"] == "failed"


def test_run_falls_back_to_the_normal_exit_code_when_the_lifecycle_call_failed(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch, lifecycle_calls: list[str]
) -> None:
    from dna_entropy.worker import runner
    from dna_entropy.worker.lifecycle import LifecycleError

    def _boom(action: str, **_kw):
        raise LifecycleError("compute refused")

    monkeypatch.setattr(runner, "apply_lifecycle", _boom)
    _gcs_kind_job(tmp_path, {"afterTask": "stop"})
    # Not applied -> the worker must NOT claim 10: the startup script's own cleanup() is the
    # backstop and needs to see an ordinary exit code.
    assert main(["run", "--store", "gcs", "--bucket", "b", "--prefix", "p"]) == EXIT_DONE


def test_localdir_runs_never_report_a_lifecycle_exit_code(tmp_path: Path) -> None:
    _write_manifest(tmp_path)  # kind=localdir: no VM to manage
    _seed_input(tmp_path)
    assert main(["run", "--store", "localdir", "--root", str(tmp_path)]) == EXIT_DONE
