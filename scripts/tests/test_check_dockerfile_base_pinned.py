"""scripts/check_dockerfile_base_pinned.py: every FROM is pinned by digest (#255, #178)."""

from __future__ import annotations

import subprocess
import sys
from pathlib import Path

SCRIPTS_DIR = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(SCRIPTS_DIR))

import check_dockerfile_base_pinned as cdp  # noqa: E402

D = "@sha256:" + "a" * 64


def _problems(text: str) -> list[str]:
    return cdp.find_unpinned(text)


def test_single_pinned_from_passes():
    assert _problems(f"FROM python:3.12-slim{D}\nRUN true\n") == []


def test_single_tag_only_from_fails():
    assert len(_problems("FROM python:3.12-slim\n")) == 1


def test_arg_templated_from_fails():
    assert len(_problems("ARG TAG=1\nFROM nvcr.io/nvidia/pytorch:${TAG}\n")) == 1


def test_multi_stage_with_only_one_pinned_fails():
    text = f"FROM golang:1.22{D} AS build\nFROM debian:12\nCOPY --from=build /a /a\n"
    found = _problems(text)
    assert len(found) == 1 and "debian:12" in found[0]


def test_multi_stage_pinned_first_and_unpinned_first_both_caught():
    assert len(_problems(f"FROM a:1 AS one\nFROM b:2{D}\n")) == 1


def test_from_an_earlier_stage_alias_is_allowed_case_insensitively():
    text = f"FROM a:1{D} AS Build\nFROM build AS test\nfrom BUILD\n"
    assert _problems(text) == []


def test_alias_not_yet_defined_is_not_exempt():
    assert len(_problems(f"FROM later\nFROM a:1{D} AS later\n")) == 1


def test_scratch_is_allowed():
    assert _problems("FROM scratch\n") == []


def test_platform_flag_lowercase_keyword_and_continuation_are_parsed():
    assert _problems(f"from --platform=linux/amd64 a:1{D} as x\n") == []
    assert len(_problems("FROM --platform=linux/amd64 a:1 AS x\n")) == 1
    assert len(_problems("FROM \\\n  a:1\n")) == 1


def test_comment_lines_and_run_text_do_not_count():
    text = f"# FROM bad:1\nFROM a:1{D}\nRUN echo FROM bad:2\n"
    assert _problems(text) == []


def test_short_or_non_hex_digest_is_not_a_pin():
    assert len(_problems("FROM a:1@sha256:abc\n")) == 1
    assert len(_problems("FROM a:1@sha256:" + "g" * 64 + "\n")) == 1


def test_a_file_with_no_from_is_a_failure_not_a_vacuous_pass():
    assert len(_problems("RUN true\n")) == 1


def _run(path: Path) -> subprocess.CompletedProcess:
    return subprocess.run(
        [sys.executable, str(SCRIPTS_DIR / "check_dockerfile_base_pinned.py"), str(path)],
        capture_output=True,
        text=True,
    )


def test_cli_exit_codes(tmp_path):
    good = tmp_path / "Dockerfile.good"
    good.write_text(f"FROM a:1{D}\n", encoding="utf-8", newline="\n")
    bad = tmp_path / "Dockerfile.bad"
    bad.write_text(f"FROM a:1{D} AS x\nFROM b:2\n", encoding="utf-8", newline="\n")
    assert _run(good).returncode == 0
    r = _run(bad)
    assert r.returncode == 1 and "ERROR" in r.stdout
    assert _run(tmp_path / "missing").returncode == 1
