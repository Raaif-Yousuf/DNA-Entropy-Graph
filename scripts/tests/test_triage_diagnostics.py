"""Tests for scripts/triage_diagnostics.py.

The manifest/status/progress/result schema asserted against here is
documented in docs/superpowers/specs/2026-09-18-appendix-b-cloud-design.md
sections 3.2-3.4; the dna_entropy.worker subpackage that will actually write
these files (issue #278) does not exist in code yet, so every fixture below
is hand-built JSON matching the DOCUMENTED shape, not real worker output.
If issue #278 lands with different field names, `triage_diagnostics.py`'s
own `SCHEMA_FIELDS` block is the one place to update, and the fixtures here
would need the same rename to keep passing -- that coupling is deliberate,
see this module's own top docstring.
"""

from __future__ import annotations

import json
import os
import subprocess
import sys
import zipfile
from pathlib import Path

import pytest

SCRIPTS_DIR = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(SCRIPTS_DIR))

import triage_diagnostics as td  # noqa: E402


MANIFEST = {
    "schema": 1, "jobId": "20260918-142233-k7q2vx", "createdAt": "2026-09-18T14:22:33Z",
    "createdBy": {"installationId": "inst-1", "appVersion": "1.2.3", "accountSub": "sub-1"},
    "worker": {"image": "ghcr.io/raaif-yousuf/dna-entropy-worker@sha256:abc123", "version": "1.0.0"},
    "inputs": [{"id": "in1", "path": "input/SetTnpB-Evo.gb", "name": "SetTnpB"}],
    "predictor": {"kind": "evo", "model": "evo2_7b", "precision": "bf16", "device": "cuda", "seed": 0},
    "limits": {"maxRunSeconds": 14400, "cancelPollSeconds": 10, "heartbeatSeconds": 30},
    "lifecycle": {"afterTask": "stop", "keepAliveMinutes": 30, "afterKeepAlive": "stop"},
    "store": {"kind": "gcs", "bucket": "deg-123-abc", "prefix": "jobs/20260918-142233-k7q2vx/"},
}

STATUS_FAILED = {
    "schema": 1, "jobId": "20260918-142233-k7q2vx", "stage": "failed", "percent": 42.5,
    "detail": {"input": "in1", "contig": "SetTnpB_3"},
    "startedAt": "2026-09-18T15:00:00Z", "updatedAt": "2026-09-18T15:20:03Z", "heartbeatSeq": 118,
    "vm": {"name": "deg-abc", "zone": "us-central1-a", "gpu": "NVIDIA L4", "driver": "580.x"},
    "worker": {"version": "1.0.1", "image": "sha256:abc123"},
    "error": {"code": "MODEL_OOM", "message": "too big for GPU memory", "detail": "CUDA OOM",
              "retriable": False, "remediation": "Lower context length"},
}

STATUS_RUNNING = {**STATUS_FAILED, "stage": "running", "error": None}


def _progress_lines(stages: list[tuple[str, str]]) -> list[str]:
    """[(stage, level), ...] -> raw JSONL lines."""
    out = []
    for i, (stage, level) in enumerate(stages):
        data = {"code": "MODEL_OOM"} if level == "error" else None
        out.append(json.dumps({
            "seq": i, "ts": f"2026-09-18T15:{i:02d}:00Z", "stage": stage, "level": level,
            "percent": float(i * 10), "message": f"stage {stage}", "data": data,
        }))
    return out


DEFAULT_STAGES = [
    ("queued", "info"), ("provisioning", "info"), ("booting", "info"),
    ("installing", "info"), ("restoring-cache", "info"), ("model-loading", "info"),
    ("running", "info"), ("running", "info"), ("running", "info"), ("failed", "error"),
]


def _write_job(root: Path, manifest=MANIFEST, status=STATUS_FAILED, stages=DEFAULT_STAGES,
               result: dict | None = None) -> None:
    root.mkdir(parents=True, exist_ok=True)
    (root / "manifest.json").write_text(json.dumps(manifest), encoding="utf-8")
    (root / "status.json").write_text(json.dumps(status), encoding="utf-8")
    (root / "progress.jsonl").write_text("\n".join(_progress_lines(stages)) + "\n", encoding="utf-8")
    (root / "logs").mkdir(exist_ok=True)
    (root / "logs" / "worker.log").write_text("hello\n", encoding="utf-8")
    if result:
        (root / "result.json").write_text(json.dumps(result), encoding="utf-8")


# ---------------------------------------------------------------------------
# Pure helpers
# ---------------------------------------------------------------------------

@pytest.mark.skipif(
    os.name != "nt",
    reason=(
        "Windows path semantics: pathlib joins with / on POSIX, so the expected "
        "backslash path can never compare equal there. The behaviour under test is "
        "Windows-only by construction (LOCALAPPDATA)."
    ),
)
def test_default_local_data_dir_honours_localappdata(monkeypatch):
    monkeypatch.setenv("LOCALAPPDATA", r"C:\Users\someone\AppData\Local")
    assert td.default_local_data_dir() == Path(r"C:\Users\someone\AppData\Local\DNAEntropyGraph")


def test_job_phase_history_dedupes_consecutive_stages():
    bundle = {"status": STATUS_FAILED}
    lines = _progress_lines(DEFAULT_STAGES)
    phases = td.job_phase_history(bundle, lines)
    assert phases == [
        "queued", "provisioning", "booting", "installing", "restoring-cache",
        "model-loading", "running", "failed",
    ]


def test_job_phase_history_falls_back_to_status_stage_with_no_progress_file():
    bundle = {"status": STATUS_RUNNING}
    assert td.job_phase_history(bundle, []) == ["running"]


def test_unrecognised_phases_flags_unknown_stage_names():
    assert td.unrecognised_phases(["queued", "bogus-stage"]) == ["bogus-stage"]
    assert td.unrecognised_phases(["queued", "running", "done"]) == []


def test_cloud_error_classes_from_status_and_progress():
    bundle = {"status": STATUS_FAILED}
    lines = _progress_lines(DEFAULT_STAGES)
    codes = td.cloud_error_classes(bundle, lines)
    assert codes == ["MODEL_OOM"]  # deduped: same code from status AND the error progress line


def test_cloud_error_classes_empty_when_no_error():
    bundle = {"status": STATUS_RUNNING}
    assert td.cloud_error_classes(bundle, []) == []


def test_field_and_first_prefer_status_over_manifest_for_worker_version():
    bundle = {"manifest": MANIFEST, "status": STATUS_FAILED}
    # status.json's worker.version (1.0.1) is fresher than manifest's (1.0.0).
    assert td.first(bundle, "status_worker_version", "manifest_worker_version") == "1.0.1"


def test_field_and_first_falls_back_to_manifest_when_status_missing():
    bundle = {"manifest": MANIFEST}
    assert td.first(bundle, "status_worker_version", "manifest_worker_version") == "1.0.0"


def test_app_version_comes_from_manifest_createdby():
    bundle = {"manifest": MANIFEST}
    assert td.field(bundle, "app_version") == "1.2.3"


# ---------------------------------------------------------------------------
# Bundle loading: directory shapes
# ---------------------------------------------------------------------------

def test_load_bundle_dir_single_job_at_root(tmp_path):
    _write_job(tmp_path)
    jobs = td.load_bundle(tmp_path)
    assert len(jobs) == 1
    label, bundle, progress = jobs[0]
    assert bundle["manifest"]["jobId"] == "20260918-142233-k7q2vx"
    assert bundle["status"]["stage"] == "failed"
    assert len(progress) == 10


def test_load_bundle_dir_multi_job_tree(tmp_path):
    _write_job(tmp_path / "jobs" / "job-a", status=STATUS_FAILED)
    _write_job(tmp_path / "jobs" / "job-b", status=STATUS_RUNNING)
    jobs = td.load_bundle(tmp_path)
    labels = sorted(label for label, _b, _p in jobs)
    assert labels == ["job-a", "job-b"]


def test_load_bundle_dir_local_runs_layout(tmp_path):
    # Appendix B section 3.3: local store root is "...\runs\<jobId>".
    _write_job(tmp_path / "runs" / "20260918-142233-k7q2vx")
    jobs = td.load_bundle(tmp_path)
    assert len(jobs) == 1
    assert jobs[0][0] == "20260918-142233-k7q2vx"


def test_load_bundle_dir_with_no_manifest_degrades_gracefully(tmp_path):
    (tmp_path / "logs").mkdir()
    (tmp_path / "logs" / "worker.log").write_text("just a log\n", encoding="utf-8")
    jobs = td.load_bundle(tmp_path)
    assert len(jobs) == 1
    label, bundle, progress = jobs[0]
    assert bundle.get("manifest") is None
    assert progress == []


# ---------------------------------------------------------------------------
# Bundle loading: zip shapes
# ---------------------------------------------------------------------------

def _zip_dir(src: Path, dest: Path) -> Path:
    with zipfile.ZipFile(dest, "w") as z:
        for p in src.rglob("*"):
            if p.is_file():
                z.write(p, p.relative_to(src))
    return dest


def test_load_bundle_zip_single_job(tmp_path):
    job_dir = tmp_path / "job"
    _write_job(job_dir)
    zpath = _zip_dir(job_dir, tmp_path / "bundle.zip")

    jobs = td.load_bundle(zpath)
    assert len(jobs) == 1
    _label, bundle, progress = jobs[0]
    assert bundle["manifest"]["jobId"] == "20260918-142233-k7q2vx"
    assert len(progress) == 10


def test_load_bundle_zip_multi_job(tmp_path):
    root = tmp_path / "root"
    _write_job(root / "jobs" / "job-a")
    _write_job(root / "jobs" / "job-b", status=STATUS_RUNNING)
    zpath = _zip_dir(root, tmp_path / "bundle.zip")

    jobs = td.load_bundle(zpath)
    labels = sorted(label for label, _b, _p in jobs)
    assert labels == ["job-a", "job-b"]


# ---------------------------------------------------------------------------
# CLI surface
# ---------------------------------------------------------------------------

def test_help_exits_zero():
    proc = subprocess.run(
        [sys.executable, str(SCRIPTS_DIR / "triage_diagnostics.py"), "--help"],
        capture_output=True, text=True, timeout=15,
    )
    assert proc.returncode == 0
    assert "--data-dir" in proc.stdout
    assert "--tail" in proc.stdout


def test_cli_no_targets_prints_help_and_exits_2():
    proc = subprocess.run(
        [sys.executable, str(SCRIPTS_DIR / "triage_diagnostics.py")],
        capture_output=True, text=True, timeout=15,
    )
    assert proc.returncode == 2


def test_cli_missing_target_only_exits_1():
    proc = subprocess.run(
        [sys.executable, str(SCRIPTS_DIR / "triage_diagnostics.py"), "definitely-does-not-exist.zip"],
        capture_output=True, text=True, timeout=15,
    )
    assert proc.returncode == 1


def test_cli_reports_app_worker_image_phases_errors_and_tail(tmp_path):
    _write_job(tmp_path)
    proc = subprocess.run(
        [sys.executable, str(SCRIPTS_DIR / "triage_diagnostics.py"), str(tmp_path)],
        capture_output=True, text=True, timeout=15,
    )
    assert proc.returncode == 0
    out = proc.stdout
    assert "app version    : 1.2.3" in out
    assert "worker version : 1.0.1" in out  # status.json wins over manifest.json
    assert "sha256:abc123" in out
    assert "queued -> provisioning" in out
    assert "MODEL_OOM" in out
    assert "#9" in out  # last progress line shown


def test_cli_tail_limits_progress_lines_shown(tmp_path):
    stages = [("running", "info")] * 60
    _write_job(tmp_path, status=STATUS_RUNNING, stages=stages)
    proc = subprocess.run(
        [sys.executable, str(SCRIPTS_DIR / "triage_diagnostics.py"), str(tmp_path), "--tail", "5"],
        capture_output=True, text=True, timeout=15,
    )
    assert proc.returncode == 0
    assert "5 of 60 line(s)" in proc.stdout
    assert "55 earlier line(s) omitted" in proc.stdout


def test_cli_full_shows_every_progress_line(tmp_path):
    stages = [("running", "info")] * 60
    _write_job(tmp_path, status=STATUS_RUNNING, stages=stages)
    proc = subprocess.run(
        [sys.executable, str(SCRIPTS_DIR / "triage_diagnostics.py"), str(tmp_path), "--full"],
        capture_output=True, text=True, timeout=15,
    )
    assert proc.returncode == 0
    assert "60 of 60 line(s)" in proc.stdout
    assert "omitted" not in proc.stdout


def test_cli_data_dir_path_scans_runs_subdirectory(tmp_path):
    data_dir = tmp_path / "DNAEntropyGraph"
    _write_job(data_dir / "runs" / "job-a")
    proc = subprocess.run(
        [sys.executable, str(SCRIPTS_DIR / "triage_diagnostics.py"),
         "--data-dir", "--data-dir-path", str(data_dir)],
        capture_output=True, text=True, timeout=15,
    )
    assert proc.returncode == 0
    assert "job-a" in proc.stdout


def test_cli_data_dir_with_no_local_runs_reports_and_exits_2():
    proc = subprocess.run(
        [sys.executable, str(SCRIPTS_DIR / "triage_diagnostics.py"), "--data-dir",
         "--data-dir-path", "Z:\\definitely\\not\\a\\real\\path"],
        capture_output=True, text=True, timeout=15,
    )
    # No local runs found and no other targets given -> same as "no targets".
    assert proc.returncode == 2


def test_cli_two_bundles_prints_delta_section(tmp_path):
    b1 = tmp_path / "b1"
    b2 = tmp_path / "b2"
    _write_job(b1, status=STATUS_RUNNING, stages=[("running", "info")])
    _write_job(b2, status=STATUS_FAILED)
    proc = subprocess.run(
        [sys.executable, str(SCRIPTS_DIR / "triage_diagnostics.py"), str(b1), str(b2)],
        capture_output=True, text=True, timeout=15,
    )
    assert proc.returncode == 0
    assert "DELTA across bundles" in proc.stdout
    assert "NEW ERROR appeared" in proc.stdout
