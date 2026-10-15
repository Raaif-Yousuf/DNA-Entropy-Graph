"""Structure test for .github/workflows/release.yml (#178, #518, #181, #165, #255).

The workflow cannot run locally (it needs a tag, signing secrets and a windows runner), so this pins what
makes it a REAL release gate rather than a pipeline that can quietly ship the wrong thing:

- it asserts every secret exists and fails before anything is built or signed (the Azure signing action
  silently no-ops without its secrets, #178);
- the version lockstep check runs with the tag;
- every other job is gated on that check;
- the worker image digests are pinned into the app BEFORE it is published (#458);
- the OAuth client file is written from a secret, never echoed, and never in an uploaded artifact (#518);
- signing happens before `vpk pack` and the signature is verified;
- the release is a DRAFT until every asset is attached, and only the last job publishes it;
- the clean-VM reminder issue is opened exactly once per version (#181).

Text based on purpose, like test_ci_notices_workflow.py: PyYAML is not in worker\\.venv. Comment-only lines
are dropped before any check so a word in a comment never satisfies one.
"""

from __future__ import annotations

import re
from pathlib import Path

import pytest

WORKFLOW = Path(__file__).resolve().parent.parent.parent / ".github" / "workflows" / "release.yml"

REQUIRED_SECRETS = [
    "AZURE_TENANT_ID",
    "AZURE_CLIENT_ID",
    "AZURE_CLIENT_SECRET",
    "TRUSTED_SIGNING_ENDPOINT",
    "TRUSTED_SIGNING_ACCOUNT",
    "TRUSTED_SIGNING_PROFILE",
    "OAUTH_CLIENT_JSON",
]


def _code(text: str) -> str:
    return "\n".join(ln for ln in text.splitlines() if not ln.strip().startswith("#"))


@pytest.fixture(scope="module")
def text() -> str:
    assert WORKFLOW.is_file(), "release.yml does not exist"
    return _code(WORKFLOW.read_text(encoding="utf-8"))


def _jobs(text: str) -> dict[str, str]:
    body = text.split("\njobs:\n", 1)[1]
    parts = re.split(r"(?m)^  ([A-Za-z][\w-]*):\n", body)
    return {parts[i]: parts[i + 1] for i in range(1, len(parts), 2)}


def _steps(job: str) -> list[str]:
    steps_part = job.split("    steps:\n", 1)[1]
    return [s for s in re.split(r"(?m)^(?=      - )", steps_part) if s.startswith("      - ")]


def _needs(job: str) -> list[str]:
    m = re.search(r"^    needs:\s*(\[[^\]]*\]|\S+)\s*$", job, re.MULTILINE)
    if not m:
        return []
    return [n.strip() for n in m.group(1).strip("[]").split(",") if n.strip()]


def _index(steps: list[str], needle: str) -> int:
    for i, s in enumerate(steps):
        if needle in s:
            return i
    raise AssertionError(f"no step mentions {needle!r}")


def _all_needs(jobs: dict[str, str], name: str) -> set[str]:
    out: set[str] = set()
    todo = list(_needs(jobs[name]))
    while todo:
        n = todo.pop()
        if n not in out:
            out.add(n)
            todo.extend(_needs(jobs[n]))
    return out


def test_triggers_only_on_a_version_tag_push(text):
    on = text.split("\njobs:\n", 1)[0]
    assert re.search(r"(?m)^on:\n  push:\n    tags:\n      - 'v\*'\s*$", on)
    assert "pull_request" not in on
    assert "workflow_dispatch" not in on  # a manual run would have no tag to lockstep against


def test_top_level_permissions_are_read_only(text):
    head = text.split("\njobs:\n", 1)[0]
    assert re.search(r"(?m)^permissions:\n  contents: read\s*$", head)


def test_the_verify_job_asserts_every_secret(text):
    jobs = _jobs(text)
    assert "verify" in jobs
    step = next(s for s in _steps(jobs["verify"]) if "secrets" in s.lower() and "assert" in s.lower())
    for name in REQUIRED_SECRETS:
        assert f"secrets.{name}" in step, f"{name} is not asserted"
    # fail loudly: the step must exit non-zero when one is empty
    assert "exit 1" in step


def test_the_assert_step_never_prints_a_secret_value(text):
    step = next(s for s in _steps(_jobs(text)["verify"]) if "secrets." in s)
    # names only: no echo of "$env:..." / "${...}" values
    assert not re.search(r"(?i)(echo|write-host|write-output)[^\n]*\$(env:)?\{?[A-Z_]*(SECRET|JSON|CLIENT)", step)


def test_lockstep_runs_with_the_tag(text):
    verify = _jobs(text)["verify"]
    assert 'check_version_lockstep.py --tag "${GITHUB_REF_NAME}"' in verify


def test_every_other_job_is_gated_on_verify(text):
    jobs = _jobs(text)
    for name in jobs:
        if name != "verify":
            assert "verify" in _all_needs(jobs, name), f"{name} can run before the secrets are asserted"


def test_no_job_is_allowed_to_fail_softly(text):
    assert "continue-on-error: true" not in text


def test_images_build_both_variants_scan_and_attest(text):
    jobs = _jobs(text)
    images = jobs["images"]
    assert re.search(r"variant:\s*\[cuda, cpu\]|- cuda\n\s+- cpu", images)
    assert "docker/build-push-action@" in images
    assert "provenance: mode=max" in images
    trivy = images[images.index("aquasecurity/trivy-action@") :]
    assert re.search(r"exit-code: '1'", trivy), "the Trivy gate must fail the job"
    assert re.search(r"severity: 'CRITICAL,HIGH'", trivy)
    assert "anchore/sbom-action@" in images
    assert "actions/attest@" in images
    assert "attestations: write" in images and "packages: write" in images


def test_images_refuse_a_base_not_pinned_by_digest(text):
    images = _jobs(text)["images"]
    assert "check_dockerfile_base_pinned" in images or "@sha256:" in images


def test_images_job_exposes_both_digests(text):
    images = _jobs(text)["images"]
    assert "digest" in images and "upload-artifact@" in images


def test_the_app_pins_the_image_digests_before_publishing(text):
    steps = _steps(_jobs(text)["app"])
    assert _index(steps, "write_worker_images.py") < _index(steps, "dotnet publish")


def test_the_oauth_client_is_written_from_the_secret_after_publish_and_before_pack(text):
    steps = _steps(_jobs(text)["app"])
    write = _index(steps, "oauth_client.local.json")
    assert _index(steps, "dotnet publish") < write < _index(steps, "vpk pack")
    step = steps[write]
    assert "secrets.OAUTH_CLIENT_JSON" in step
    assert "::add-mask::" in step, "the secret must be masked in the log"
    # next to the exe, in the publish folder (OAuthClientLoader.DefaultCandidates looks there first)
    assert re.search(r"publish[\\/]oauth_client\.local\.json", step)


def test_the_oauth_secret_is_only_read_in_the_two_steps_that_need_it(text):
    holders = [n for n, j in _jobs(text).items() if "secrets.OAUTH_CLIENT_JSON" in j]
    assert set(holders) == {"verify", "app"}


def test_no_uploaded_artifact_can_contain_the_publish_folder_or_the_oauth_file(text):
    for m in re.finditer(r"actions/upload-artifact@[^\n]*\n(?:(?!\n      - ).)*", text, re.DOTALL):
        block = m.group(0)
        assert "oauth_client" not in block
        assert not re.search(r"path:\s*\|?\s*[^\n]*app/publish", block)
    assert "app/publish/**" not in text


def test_the_installer_is_signed_before_pack_and_the_signature_verified(text):
    steps = _steps(_jobs(text)["app"])
    sign_files = _index(steps, "azure/artifact-signing-action@")
    assert sign_files < _index(steps, "vpk pack")
    sign_setup = max(i for i, s in enumerate(steps) if "azure/artifact-signing-action@" in s)
    assert sign_setup > _index(steps, "vpk pack")
    assert _index(steps, "signtool") > sign_setup
    assert "verify /pa" in steps[_index(steps, "signtool")]


def test_pack_uses_main_exe_and_the_tag_version(text):
    pack = _steps(_jobs(text)["app"])[_index(_steps(_jobs(text)["app"]), "vpk pack")]
    assert "--mainExe DnaEntropyGraph.App.exe" in pack
    assert "--packId DnaEntropyGraph" in pack
    assert "--packVersion" in pack and "GITHUB_REF_NAME" in pack


def test_app_sbom_and_provenance_cover_the_installer(text):
    app = _jobs(text)["app"]
    assert "anchore/sbom-action@" in app
    assert "attestations: write" in app and "id-token: write" in app
    assert "actions/attest@" in app
    # the installer is never an Actions artifact (its payload carries the OAuth client file, #518); the
    # checksums are computed in the publish job from the draft release's own assets
    assert "SHA256SUMS" in _jobs(text)["publish"]
    assert "gh release download" in _jobs(text)["publish"]


def test_signing_actions_get_their_values_from_secrets_only(text):
    app = _jobs(text)["app"]
    for secret in ("AZURE_TENANT_ID", "AZURE_CLIENT_ID", "AZURE_CLIENT_SECRET"):
        assert f"secrets.{secret}" in app


def test_the_release_stays_a_draft_until_the_last_job(text):
    jobs = _jobs(text)
    assert "publish" in jobs
    # the app job uploads as a draft: vpk upload github without --publish
    upload = _steps(jobs["app"])[_index(_steps(jobs["app"]), "vpk upload github")]
    assert "--publish" not in upload
    # only the publish job flips draft to false, and it needs images and app
    flippers = [n for n, j in jobs.items() if re.search(r"--draft=false", j)]
    assert flippers == ["publish"]
    assert {"images", "app"} <= set(_needs(jobs["publish"]))


def test_publish_attaches_every_asset_before_it_flips_the_draft(text):
    steps = _steps(_jobs(text)["publish"])
    flip = _index(steps, "--draft=false")
    for needle in ("SHA256SUMS", ".whl", "digests"):
        assert _index(steps, needle) < flip, f"{needle} is attached after the release is public"


def test_the_clean_vm_reminder_issue_is_opened_once_after_publish(text):
    jobs = _jobs(text)
    assert "publish" in _needs(jobs["verify-reminder"])
    body = jobs["verify-reminder"]
    assert "issues: write" in body
    assert "Verify ${GITHUB_REF_NAME} on a clean VM" in body or "Verify $GITHUB_REF_NAME on a clean VM" in body
    assert "area:packaging" in body
    # idempotent: it searches for an existing one first
    assert "gh issue list" in body and body.index("gh issue list") < body.index("gh issue create")
