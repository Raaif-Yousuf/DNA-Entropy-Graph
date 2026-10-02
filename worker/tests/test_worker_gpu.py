"""The worker reports its GPU identity (issue #74): ``status.json``'s ``vm.gpu`` and
``result.json``'s ``gpu``. No GPU and no torch needed: ``nvidia-smi`` is injected."""

from __future__ import annotations

import json
import subprocess
from pathlib import Path

import pytest

from dna_entropy.worker.blobstore import LocalBlobstore
from dna_entropy.worker.gpu import detect_gpu
from dna_entropy.worker.runner import MANIFEST_PATH, RESULT_PATH, run_job


def _smi(stdout: str = "", returncode: int = 0):
    def run(cmd, **kwargs):
        assert cmd[0] == "nvidia-smi"
        assert kwargs.get("timeout"), "nvidia-smi must never be able to hang the worker"
        return subprocess.CompletedProcess(cmd, returncode, stdout=stdout, stderr="")

    return run


def test_detect_gpu_parses_name_and_driver_and_reads_the_zone_from_the_env() -> None:
    gpu = detect_gpu(env={"DEG_ZONE": "us-central1-a"}, run=_smi("NVIDIA L4, 580.82.07\n"))
    assert (gpu.name, gpu.driver, gpu.zone) == ("NVIDIA L4", "580.82.07", "us-central1-a")


def test_detect_gpu_with_several_gpus_reports_the_first() -> None:
    gpu = detect_gpu(env={}, run=_smi("NVIDIA A100-SXM4-40GB, 580.1\nNVIDIA A100-SXM4-40GB, 580.1\n"))
    assert gpu.name == "NVIDIA A100-SXM4-40GB"


@pytest.mark.parametrize("stdout", ["", "\n", "garbage with no comma\n"])
def test_detect_gpu_unparseable_output_is_no_gpu_not_an_error(stdout: str) -> None:
    gpu = detect_gpu(env={"DEG_ZONE": "z"}, run=_smi(stdout))
    assert gpu.name is None and gpu.driver is None
    assert gpu.zone == "z"


def test_detect_gpu_nonzero_exit_is_no_gpu() -> None:
    assert detect_gpu(env={}, run=_smi("NVIDIA L4, 1\n", returncode=9)).name is None


@pytest.mark.parametrize(
    "exc",
    [
        FileNotFoundError("nvidia-smi"),
        subprocess.TimeoutExpired("nvidia-smi", 10),
        OSError("boom"),
        # text=True decoding nvidia-smi output that is not valid text (a ValueError subclass)
        UnicodeDecodeError("utf-8", b"\xff", 0, 1, "invalid start byte"),
    ],
)
def test_detect_gpu_never_raises(exc: Exception) -> None:
    def run(cmd, **kwargs):
        raise exc

    assert detect_gpu(env={}, run=run).name is None


def test_the_run_records_the_detected_gpu_in_status_result_and_the_first_notice(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    """The observable: with a (fake) L4 the job's status.json says so, result.json says so,
    and the first progress line names it. Wired to nothing would leave all three null."""
    from dna_entropy.worker import gpu as gpu_module

    monkeypatch.setenv("DEG_ZONE", "us-central1-a")
    monkeypatch.setenv("DEG_VM_NAME", "deg-job1")
    monkeypatch.setattr(
        "dna_entropy.worker.runner.detect_gpu",
        lambda: gpu_module.detect_gpu(env={"DEG_ZONE": "us-central1-a"}, run=_smi("NVIDIA L4, 580.82.07\n")),
    )
    store = LocalBlobstore(tmp_path)
    store.write_text(
        MANIFEST_PATH,
        json.dumps(
            {
                "schema": 1,
                "jobId": "gpu-job",
                "inputs": [{"id": "in1", "path": "input/a.fasta", "name": "a"}],
                "predictor": {"kind": "mock", "seed": 0},
                "analysis": {"contextLength": 128, "window": 256, "stride": 128, "direction": "forward-only"},
                "store": {"kind": "localdir", "root": "x"},
            }
        ),
    )
    store.write_text("input/a.fasta", ">s\n" + "ACGT" * 40 + "\n")

    run_job(store)

    status = json.loads(store.read_text("status.json"))
    assert status["vm"]["gpu"] == "NVIDIA L4"
    assert status["vm"]["driver"] == "580.82.07"
    assert status["vm"]["name"] == "deg-job1"
    assert status["vm"]["zone"] == "us-central1-a"
    result = json.loads(store.read_text(RESULT_PATH))
    assert result["gpu"]["name"] == "NVIDIA L4"
    assert result["gpu"]["zone"] == "us-central1-a"
    first = json.loads(store.read_text("progress.jsonl").splitlines()[0])
    assert "NVIDIA L4" in first["message"]


def test_a_cpu_run_reports_no_gpu_in_words_not_silence(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    from dna_entropy.worker.status import GpuInfo

    monkeypatch.setattr("dna_entropy.worker.runner.detect_gpu", lambda: GpuInfo())
    store = LocalBlobstore(tmp_path)
    store.write_text(
        MANIFEST_PATH,
        json.dumps(
            {
                "schema": 1,
                "jobId": "cpu-job",
                "inputs": [{"id": "in1", "path": "input/a.fasta", "name": "a"}],
                "predictor": {"kind": "mock", "seed": 0},
                "analysis": {"contextLength": 128, "window": 256, "stride": 128, "direction": "forward-only"},
                "store": {"kind": "localdir", "root": "x"},
            }
        ),
    )
    store.write_text("input/a.fasta", ">s\n" + "ACGT" * 40 + "\n")

    run_job(store)

    first = json.loads(store.read_text("progress.jsonl").splitlines()[0])
    assert "no GPU" in first["message"]
    assert json.loads(store.read_text("status.json"))["vm"]["gpu"] is None
