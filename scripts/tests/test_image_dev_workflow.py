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
    assert "fromJSON(needs.plan.outputs.matrix)" in text
    assert "push: true" in text or "push=true" in text
    tags = re.findall(r"^\s*tags:\s*(.+)$", text, re.MULTILINE)
    assert len(tags) == 1 and "steps.sha.outputs.tag" in tags[0] and "${{ matrix.variant }}" in tags[0], tags
    assert "tag=dev-${GITHUB_SHA::7}-r" in text, "the tag step must produce a dev-<sha>-r<run> name"
    assert ":latest" not in text
    assert "github.ref_name" not in tags[0], "a tag named after the ref could collide with a release tag"
    tag_step = re.search(r"- name: tag\n(?:.*\n)*?\s+run: (.+)\n", text)
    assert tag_step, "no `tag` step"
    assert "GITHUB_RUN_ID" in tag_step.group(1) and "GITHUB_RUN_ATTEMPT" in tag_step.group(1), (
        "the tag must carry the run id and attempt so a re-dispatch of the same sha never overwrites an earlier tag"
    )


def test_it_frees_disk_before_the_base_is_pulled(text):
    free = text.index("/usr/share/dotnet")
    assert free < text.index("build-push-action")


def test_the_digest_is_printed_to_the_job_summary_with_the_image_argument(text):
    assert "steps.build.outputs.digest" in text
    assert "GITHUB_STEP_SUMMARY" in text
    assert "-Image $ref" in text and "${IMAGE_REPO}@${DIGEST}" in text
    assert "ONLY the digest" in text


def test_every_action_is_pinned_by_commit_sha(text):
    uses = re.findall(r"^\s*(?:-\s+)?uses:\s*(\S+)", text, re.MULTILINE)
    assert len(uses) >= 4, f"vacuity guard: only {len(uses)} `uses:` lines found"
    for ref in uses:
        assert re.fullmatch(r"[^@\s]+@[0-9a-f]{40}", ref), f"{ref} is not pinned to a commit SHA"


def test_the_variant_input_is_validated_in_shell_before_the_matrix_and_never_interpolated_into_a_script(text):
    plan = re.search(r"^  plan:\n((?:    .*\n|\n)+)", text, re.MULTILINE)
    assert plan, "no plan job"
    body = plan.group(1)
    assert 'case "$VARIANT" in' in body and "cuda)" in body and "cpu)" in body and "both)" in body
    assert re.search(r"\*\) .*exit 1", body), "the case has no failing default arm"
    assert re.search(r"^  image:\n(?:    .*\n)*?    needs: plan$", text, re.MULTILINE), "image must need plan"
    # The input is read once, through env:, so it is never spliced into shell text.
    assert len(re.findall(r"inputs\.variant", text)) == 1
    assert re.search(r"env:\n\s+VARIANT: \$\{\{ inputs\.variant \}\}", body)


def test_no_expression_is_spliced_into_shell_text(text):
    blocks = re.findall(r"^\s+run: \|\n((?:\s{10,}.*\n|\n)+)", text, re.MULTILINE)
    assert len(blocks) >= 3, f"vacuity guard: only {len(blocks)} run blocks found"
    for block in blocks:
        assert "${{" not in block, f"a run: script interpolates an expression; pass it through env: {block.strip()[:80]!r}"
