"""Tests for worker/lifecycle.py: stop/delete/keep via the Compute API — FAKE transport
only. Nothing here makes, or could make, a real network call (no cloud spend, no cloud
calls — the overnight brief's hard rule); this module is implemented and reviewed, not
exercised against real GCP tonight.
"""

from __future__ import annotations

import json

import pytest

from dna_entropy.worker.lifecycle import LifecycleError, apply_lifecycle, self_instance_identity


class _FakeResponse:
    def __init__(self, body: bytes = b"{}") -> None:
        self._body = body

    def read(self) -> bytes:
        return self._body

    def __enter__(self) -> _FakeResponse:
        return self

    def __exit__(self, *exc) -> bool:
        return False


class _RecordingOpener:
    def __init__(self, responses: list) -> None:
        self.requests: list = []
        self._responses = list(responses)

    def __call__(self, req):
        self.requests.append(req)
        resp = self._responses.pop(0)
        if isinstance(resp, Exception):
            raise resp
        return resp


def _text(s: str) -> _FakeResponse:
    return _FakeResponse(s.encode())


def _token() -> _FakeResponse:
    return _FakeResponse(json.dumps({"access_token": "fake-token", "expires_in": 3600}).encode())


# --- self_instance_identity ----------------------------------------------------------


def test_self_instance_identity_reads_project_zone_name() -> None:
    opener = _RecordingOpener(
        [
            _text("my-project-123"),
            _text("projects/999/zones/us-central1-a"),
            _text("deg-20260918-142233-k7q2vx"),
        ]
    )
    project, zone, name = self_instance_identity(opener=opener)
    assert project == "my-project-123"
    assert zone == "us-central1-a"  # last path segment only, not the full zone path
    assert name == "deg-20260918-142233-k7q2vx"
    for req in opener.requests:
        assert "metadata.google.internal" in req.full_url
        assert req.headers.get("Metadata-flavor") == "Google"


def test_self_instance_identity_transport_failure_raises_lifecycle_error() -> None:
    import urllib.error

    opener = _RecordingOpener([urllib.error.URLError("no network")])
    with pytest.raises(LifecycleError):
        self_instance_identity(opener=opener)


# --- apply_lifecycle -------------------------------------------------------------------


def test_apply_lifecycle_keep_is_a_no_op_and_makes_no_request() -> None:
    opener = _RecordingOpener([])
    apply_lifecycle("keep", opener=opener)
    assert opener.requests == []


def test_apply_lifecycle_stop_posts_to_the_compute_api() -> None:
    opener = _RecordingOpener(
        [
            _text("proj"),
            _text("projects/1/zones/us-central1-a"),
            _text("deg-job1"),
            _token(),
            _FakeResponse(b"{}"),
        ]
    )
    apply_lifecycle("stop", opener=opener)
    last = opener.requests[-1]
    assert last.get_method() == "POST"
    assert "compute.googleapis.com" in last.full_url
    assert "/projects/proj/zones/us-central1-a/instances/deg-job1/stop" in last.full_url
    assert last.headers.get("Authorization") == "Bearer fake-token"


def test_apply_lifecycle_delete_posts_the_delete_verb() -> None:
    opener = _RecordingOpener(
        [
            _text("proj"),
            _text("projects/1/zones/us-central1-a"),
            _text("deg-job1"),
            _token(),
            _FakeResponse(b"{}"),
        ]
    )
    apply_lifecycle("delete", opener=opener)
    last = opener.requests[-1]
    assert last.full_url.endswith("/instances/deg-job1/delete")


def test_apply_lifecycle_unknown_action_is_rejected_without_any_request() -> None:
    opener = _RecordingOpener([])
    with pytest.raises(LifecycleError):
        apply_lifecycle("reboot", opener=opener)  # type: ignore[arg-type]
    assert opener.requests == []


def test_apply_lifecycle_http_error_from_compute_api_raises_lifecycle_error() -> None:
    import urllib.error

    opener = _RecordingOpener(
        [
            _text("proj"),
            _text("projects/1/zones/us-central1-a"),
            _text("deg-job1"),
            _token(),
            urllib.error.HTTPError("url", 403, "Forbidden", {}, None),
        ]
    )
    with pytest.raises(LifecycleError):
        apply_lifecycle("stop", opener=opener)


def test_apply_lifecycle_never_shells_out() -> None:
    """Documentation-as-test: this module's only side effect is an HTTP POST to the
    Compute API — never a subprocess, never an ssh/scp/shutdown command
    (docs/cloud_design.md §9)."""
    import inspect

    source = inspect.getsource(apply_lifecycle)
    assert "subprocess" not in source
    assert "os.system" not in source
