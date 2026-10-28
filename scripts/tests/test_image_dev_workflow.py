"""Structure test for .github/workflows/image-dev.yml (the branch-built dev worker image).

The workflow cannot run locally (a manual dispatch on a GitHub runner), so this pins what keeps it the SMALL
and SAFE path to a pinned worker image, as opposed to a second release pipeline:

- it is workflow_dispatch only (no tag, push or schedule trigger);
- it references no secret except GITHUB_TOKEN (via `github.token`), so it cannot sign or hold OAuth material;
- its only write permission is `packages: write`;
- it is concurrency-limited, never cancelling a push half way;
- it pushes a `dev-<shortsha>-<variant>` tag (never `latest`, never a version tag a release would own);
- it prints the digest and the ready-to-paste `-Image` reference in the job summary;
- every action is pinned by commit SHA (scripts/check_actions_pinned.py also covers the real tree).

Text based on purpose, like test_release_workflow.py: PyYAML is not in worker\\.venv. Comment-only lines are
dropped first so a word in a comment never satisfies a check.
"""

from __future__ import annotations

import re
from pathlib import Path

import pytest

WORKFLOW = Path(__file__).resolve().parent.parent.parent / ".github" / "workflows" / "image-dev.yml"


def _code(text: str) -> str:
    return "\n".join(ln for ln in text.splitlines() if not ln.strip().startswith("#"))


@pytest.fixture(scope="module")
def text() -> str:
    assert WORKFLOW.is_file(), "image-dev.yml does not exist"
    return _code(WORKFLOW.read_text(encoding="utf-8"))


def _on_block(text: str) -> str:
    m = re.search(r"^on:\n((?:[ ]+.*\n|\n)+)", text, re.MULTILINE)
    assert m, "no `on:` block found; the checks below would vacuously pass"
    return m.group(1)


def test_it_is_workflow_dispatch_only(text):
    on = _on_block(text)
    assert re.search(r"^  workflow_dispatch:", on, re.MULTILINE)
    for trigger in ("push", "pull_request", "pull_request_target", "schedule", "release", "workflow_run", "tags"):
        assert not re.search(rf"^\s*{trigger}:", on, re.MULTILINE), f"image-dev.yml must not trigger on {trigger}"


def test_no_secret_is_referenced_except_the_github_token(text):
    secrets = set(re.findall(r"secrets\.([A-Za-z0-9_]+)", text))
    assert secrets <= {"GITHUB_TOKEN"}, f"image-dev.yml references secrets it must not hold: {sorted(secrets)}"
    assert "secrets: inherit" not in text
    assert "environment:" not in text, "no `release` environment: that is where the signing secrets live"
    assert "${{ github.token }}" in text, "vacuity guard: the ghcr login must use the workflow token"
    for name in ("AZURE_", "TRUSTED_SIGNING", "OAUTH_CLIENT_JSON", "id-token"):
        assert name not in text, f"{name} belongs to release.yml, not to a dev image build"


def test_the_only_write_permission_is_packages(text):
    perms = re.findall(r"^\s*([a-z-]+): write$", text, re.MULTILINE)
    assert perms == ["packages"], perms
    assert re.search(r"^\s*contents: read$", text, re.MULTILINE)


def test_it_is_concurrency_limited_without_cancelling_a_push(text):
    m = re.search(r"^concurrency:\n((?:[ ]+.*\n)+)", text, re.MULTILINE)
    assert m, "no top-level concurrency block"
    assert "group: image-dev" in m.group(1)
    assert "cancel-in-progress: false" in m.group(1)


def test_it_builds_the_two_dockerfiles_and_pushes_a_dev_tag_only(text):
    assert "worker/Dockerfile.${{ matrix.variant }}" in text
    assert re.search(r"variant:.*cuda.*cpu", text, re.DOTALL)
    assert "push: true" in text or "push=true" in text
    tags = re.findall(r"^\s*tags:\s*(.+)$", text, re.MULTILINE)
    assert len(tags) == 1 and "dev-" in tags[0] and "${{ matrix.variant }}" in tags[0], tags
    assert ":latest" not in text
    assert "github.ref_name" not in tags[0], "a tag named after the ref could collide with a release tag"


def test_it_frees_disk_before_the_base_is_pulled(text):
    free = text.index("/usr/share/dotnet")
    assert free < text.index("build-push-action")


def test_the_digest_is_printed_to_the_job_summary_with_the_image_argument(text):
    assert "steps.build.outputs.digest" in text
    assert "GITHUB_STEP_SUMMARY" in text
    assert "-Image" in text and "@${{ steps.build.outputs.digest }}" in text


def test_every_action_is_pinned_by_commit_sha(text):
    uses = re.findall(r"^\s*(?:-\s+)?uses:\s*(\S+)", text, re.MULTILINE)
    assert len(uses) >= 4, f"vacuity guard: only {len(uses)} `uses:` lines found"
    for ref in uses:
        assert re.fullmatch(r"[^@\s]+@[0-9a-f]{40}", ref), f"{ref} is not pinned to a commit SHA"
