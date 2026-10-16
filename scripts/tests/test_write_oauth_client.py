"""scripts/write_oauth_client.py writes a minimal, validated oauth_client.local.json from a secret (#518, #178)."""

from __future__ import annotations

import json
import os
import subprocess
import sys
from pathlib import Path

SCRIPTS_DIR = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(SCRIPTS_DIR))

import write_oauth_client as woc  # noqa: E402

ID = "1234-abc.apps.googleusercontent.com"
SECRET = "GOCSPX-not-a-real-secret"


def _google_download() -> str:
    return json.dumps(
        {
            "installed": {
                "client_id": ID,
                "client_secret": SECRET,
                "project_id": "owner-project",
                "auth_uri": "https://accounts.google.com/o/oauth2/auth",
                "token_uri": "https://oauth2.googleapis.com/token",
                "redirect_uris": ["http://localhost"],
                "refresh_token": "1//must-not-pass",
            },
            "web": {"client_id": "x", "client_secret": "y"},
            "refresh_token": "1//also-must-not-pass",
        }
    )


def test_only_client_id_and_secret_survive():
    out = woc.minimal_document(_google_download())
    assert out == {"installed": {"client_id": ID, "client_secret": SECRET}}


def test_no_other_field_can_pass_through_anywhere_in_the_output():
    text = json.dumps(woc.minimal_document(_google_download()))
    for leaked in ("project_id", "auth_uri", "token_uri", "redirect_uris", "refresh_token", "owner-project", "1//", "web"):
        assert leaked not in text


def test_invalid_json_is_refused():
    for bad in ("", "not json", "[]", "null", '{"installed": []}', '{"installed": {}}'):
        try:
            woc.minimal_document(bad)
        except ValueError:
            continue
        raise AssertionError(f"{bad!r} was accepted")


def test_missing_empty_or_non_string_values_are_refused():
    cases = [
        {"installed": {"client_id": ID}},
        {"installed": {"client_secret": SECRET}},
        {"installed": {"client_id": "", "client_secret": SECRET}},
        {"installed": {"client_id": ID, "client_secret": "  "}},
        {"installed": {"client_id": 5, "client_secret": SECRET}},
        {"web": {"client_id": ID, "client_secret": SECRET}},
    ]
    for doc in cases:
        try:
            woc.minimal_document(json.dumps(doc))
        except ValueError:
            continue
        raise AssertionError(f"{doc!r} was accepted")


def test_error_messages_never_contain_the_secret_text():
    try:
        woc.minimal_document('{"installed": {"client_id": "' + ID + '", "client_secret": 7, "x": "' + SECRET + '"}}')
    except ValueError as exc:
        assert SECRET not in str(exc) and ID not in str(exc)
    else:
        raise AssertionError("accepted")


def _run(out: Path, secret_json: str | None) -> subprocess.CompletedProcess:
    env = {k: v for k, v in os.environ.items() if k != "OAUTH_CLIENT_JSON"}
    if secret_json is not None:
        env["OAUTH_CLIENT_JSON"] = secret_json
    return subprocess.run(
        [sys.executable, str(SCRIPTS_DIR / "write_oauth_client.py"), "--out", str(out)],
        capture_output=True,
        text=True,
        env=env,
    )


def test_cli_masks_before_anything_else_and_writes_utf8_lf_without_bom(tmp_path):
    out = tmp_path / "oauth_client.local.json"
    r = _run(out, _google_download())
    assert r.returncode == 0, r.stdout + r.stderr
    lines = r.stdout.splitlines()
    assert lines[0] == f"::add-mask::{ID}" and lines[1] == f"::add-mask::{SECRET}"
    raw = out.read_bytes()
    assert not raw.startswith(b"\xef\xbb\xbf") and b"\r" not in raw
    assert json.loads(raw) == {"installed": {"client_id": ID, "client_secret": SECRET}}


def test_cli_failure_writes_nothing_and_prints_no_secret(tmp_path):
    out = tmp_path / "oauth_client.local.json"
    bad = json.dumps({"installed": {"client_id": ID, "client_secret": ""}, "refresh_token": SECRET})
    r = _run(out, bad)
    assert r.returncode == 1 and not out.exists()
    assert SECRET not in r.stdout + r.stderr and ID not in r.stdout + r.stderr
    assert "ERROR" in r.stdout


def test_cli_without_the_env_var_fails(tmp_path):
    out = tmp_path / "x.json"
    r = _run(out, None)
    assert r.returncode == 1 and not out.exists()
