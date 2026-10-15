"""Tests for scripts/hooks/gcloud_read_only.py: read-only gcloud passes, anything else is denied."""

from __future__ import annotations

import json
import subprocess
import sys
from pathlib import Path

import pytest

HOOKS_DIR = Path(__file__).resolve().parents[1] / "hooks"
sys.path.insert(0, str(HOOKS_DIR))

import gcloud_read_only as hook  # noqa: E402


@pytest.mark.parametrize(
    "command",
    [
        "gcloud config list",
        "gcloud auth list",
        "gcloud config get-value project",
        "gcloud projects describe my-proj",
        "gcloud projects list --limit 5",
        "gcloud compute instances list --filter=labels.app=dna-entropy-graph",
        "gcloud compute zones list --project p",
        "gcloud services list --enabled",
        "gcloud storage ls gs://bucket/prefix",
        "gcloud billing projects describe p",
        "gcloud projects get-iam-policy p --format=json",
        "gcloud compute accelerator-types list",
        "gcloud version",
        "gcloud info",
        "& gcloud config list 2>&1 | Select-Object -First 8",
        "gcloud.cmd config list",
        "gsutil ls gs://bucket",
        "echo 'never run gcloud compute instances delete here'",
        "grep -n gcloud docs/cloud_design.md",
        "",
    ],
)
def test_allows_read_only_and_prose(command):
    assert hook.verdict(command) is None


@pytest.mark.parametrize(
    "command",
    [
        "gcloud compute instances create vm --zone z",
        "gcloud compute instances delete vm --quiet",
        "gcloud compute instances stop vm",
        "gcloud compute instances start vm",
        "gcloud config set project p",
        "gcloud services enable compute.googleapis.com",
        "gcloud projects add-iam-policy-binding p --member m --role r",
        "gcloud auth print-access-token",
        "gcloud auth login",
        "gcloud storage rm gs://b/o",
        "gcloud storage cp a gs://b/a",
        "gcloud compute ssh vm",
        "gsutil rm gs://b/o",
        "gsutil cp a gs://b",
        "bq rm -f ds.t",
        # an unknown verb fails toward refusing
        "gcloud alpha something-new",
        "gcloud",
        # command position after separators and substitutions
        "gcloud config list && gcloud compute instances delete vm",
        "x=$(gcloud compute instances create vm)",
        "cd /tmp; gcloud compute instances delete vm",
        "CLOUDSDK_CORE_PROJECT=p gcloud compute instances delete vm",
        # a read verb does not launder a mutating one in the same invocation
        "gcloud compute instances delete vm --format list",
    ],
)
def test_denies_everything_else(command):
    assert hook.verdict(command) is not None


def test_cat_heredoc_body_is_not_a_command():
    command = "git commit -m \"$(cat <<'EOF'\ndocs: gcloud compute instances delete is denied\nEOF\n)\""
    assert hook.verdict(command) is None


def test_end_to_end_denies():
    payload = json.dumps({"tool_name": "Bash", "tool_input": {"command": "gcloud compute instances delete vm"}})
    proc = subprocess.run(
        [sys.executable, str(HOOKS_DIR / "gcloud_read_only.py")],
        input=payload, capture_output=True, text=True, timeout=15,
    )
    assert proc.returncode == 0
    assert json.loads(proc.stdout)["hookSpecificOutput"]["permissionDecision"] == "deny"


def test_end_to_end_silent_on_read_only():
    payload = json.dumps({"tool_name": "PowerShell", "tool_input": {"command": "gcloud config list"}})
    proc = subprocess.run(
        [sys.executable, str(HOOKS_DIR / "gcloud_read_only.py")],
        input=payload, capture_output=True, text=True, timeout=15,
    )
    assert proc.returncode == 0
    assert proc.stdout.strip() == ""


def test_fails_open_on_malformed_stdin():
    proc = subprocess.run(
        [sys.executable, str(HOOKS_DIR / "gcloud_read_only.py")],
        input="not json", capture_output=True, text=True, timeout=15,
    )
    assert proc.returncode == 0
    assert proc.stdout.strip() == ""


def test_self_test_passes():
    proc = subprocess.run(
        [sys.executable, str(HOOKS_DIR / "gcloud_read_only.py"), "--self-test"],
        capture_output=True, text=True, timeout=60,
    )
    assert proc.returncode == 0, proc.stderr
