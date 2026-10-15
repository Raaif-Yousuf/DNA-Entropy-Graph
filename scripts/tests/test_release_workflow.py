"""Structure test for .github/workflows/release.yml (#178, #518, #181, #165, #255).

The workflow cannot run locally (it needs a tag, signing secrets and a windows runner), so this pins what
makes it a REAL release gate rather than a pipeline that can quietly ship the wrong thing:

- it asserts every secret exists and fails before anything is built or signed (the Azure signing action
  silently no-ops without its secrets, #178);
- the secret-holding jobs run under the `release` environment (tag restriction, reviewers, scoped secrets);
- the version lockstep check runs with the tag and the tagged commit must be on main;
- every other job is gated on that check;
- the worker image digests are pinned into the app BEFORE it is published (#458);
- the OAuth client file is written by scripts/write_oauth_client.py (two fields only), never echoed, never in an
  uploaded artifact (#518);
- the app job order is pinned step by step BY NAME: sign, pack, sign Setup.exe, verify, stage, attest, hand off;
- the draft upload lives in its own job (the only one with contents: write besides publish) and needs the app job;
- the release is a DRAFT until every asset is attached, and only the last job publishes it;
- the clean-VM reminder issue is opened exactly once per version (#181).

Text based on purpose, like test_ci_notices_workflow.py: PyYAML is not in worker\\.venv. Comment-only lines
are dropped before any check so a word in a comment never satisfies one. Steps are addressed by their exact
`name:` (never by a substring of their body), so a needle that also occurs in a different step cannot pass.
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
SECRET_ENV_NAMES = [*REQUIRED_SECRETS, "GH_TOKEN", "GITHUB_TOKEN"]


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


def _name(step: str) -> str | None:
    m = re.search(r"(?m)^(?:      - |        )name: (.+?)\s*$", step)
    return m.group(1).strip("'\"") if m else None


def _by_name(steps: list[str], name: str) -> str:
    hits = [s for s in steps if _name(s) == name]
    assert len(hits) == 1, f"expected exactly one step named {name!r}, found {len(hits)}"
    return hits[0]


def _pos(steps: list[str], name: str) -> int:
    _by_name(steps, name)
    return [_name(s) for s in steps].index(name)


def _assert_order(steps: list[str], names: list[str]) -> None:
    for earlier, later in zip(names, names[1:], strict=False):
        assert _pos(steps, earlier) < _pos(steps, later), f"{earlier!r} must come before {later!r}"


def _needs(job: str) -> list[str]:
    m = re.search(r"^    needs:\s*(\[[^\]]*\]|\S+)\s*$", job, re.MULTILINE)
    if not m:
        return []
    return [n.strip() for n in m.group(1).strip("[]").split(",") if n.strip()]


def _all_needs(jobs: dict[str, str], name: str) -> set[str]:
    out: set[str] = set()
    todo = list(_needs(jobs[name]))
    while todo:
        n = todo.pop()
        if n not in out:
            out.add(n)
            todo.extend(_needs(jobs[n]))
    return out


def _run_body(step: str) -> str:
    """Everything from `run:` to the next sibling key of the step (the run text itself, comments already dropped)."""
    m = re.search(r"(?ms)^        run: (.*?)(?=^        [A-Za-z][\w-]*:|\Z)", step)
    return m.group(1) if m else ""


# ---------- the never-print-a-secret detector (tested against bad samples below) ----------

_PRINTERS = r"(?:echo|printf|write-host|write-output|write-error|cat|out-file|tee|set-content|add-content)"


def prints_a_secret(run_text: str) -> list[str]:
    bad: list[str] = []
    names = "|".join(SECRET_ENV_NAMES)
    for ln in run_text.splitlines():
        low = ln.strip().lower()
        prints = re.search(rf"(?<![\w-]){_PRINTERS}(?![\w-])", low)
        expands = re.search(r"\$\{!", ln) or re.search(rf"\$\{{?(?:env:)?(?:{names})\b", ln) or "${{ secrets." in ln
        if prints and expands:
            bad.append(ln.strip())
    if "${{ secrets." in run_text:
        bad.append("an inline ${{ secrets.* }} in a run block (hand it over with env:)")
    if re.search(r"(?i)set -x|Set-PSDebug|--debug\b", run_text):
        bad.append("shell tracing on")
    return bad


@pytest.mark.parametrize(
    "line",
    [
        'echo "${!name}"',
        'echo "$AZURE_CLIENT_SECRET"',
        "echo ${TRUSTED_SIGNING_ENDPOINT}",
        "Write-Host $env:OAUTH_CLIENT_JSON",
        'Write-Host "id: ${env:AZURE_CLIENT_ID}"',
        "printf '%s' \"$GH_TOKEN\"",
        'cat <<< "$AZURE_TENANT_ID"',
        "echo ${{ secrets.AZURE_CLIENT_SECRET }}",
        "set -x",
    ],
)
def test_the_detector_catches_each_way_of_printing_a_secret(line):
    assert prints_a_secret(line)


@pytest.mark.parametrize(
    "line",
    [
        'if [ -z "${!name}" ]; then missing="$missing $name"; fi',
        'echo "::error title=Release secrets missing::Set these:$missing"',
        'echo "OK: all release secrets are set"',
        "vpk upload github --token $env:GH_TOKEN --tag $env:GITHUB_REF_NAME",
    ],
)
def test_the_detector_leaves_names_and_passing_a_token_alone(line):
    assert prints_a_secret(line) == []


# ---------- triggers, permissions, gating ----------


def test_triggers_only_on_a_version_tag_push(text):
    on = text.split("\njobs:\n", 1)[0]
    assert re.search(r"(?m)^on:\n  push:\n    tags:\n      - 'v\*'\s*$", on)
    assert "pull_request" not in on
    assert "workflow_dispatch" not in on  # a manual run would have no tag to lockstep against


def test_top_level_permissions_are_read_only(text):
    head = text.split("\njobs:\n", 1)[0]
    assert re.search(r"(?m)^permissions:\n  contents: read\s*$", head)


def test_the_verify_job_asserts_every_secret_and_the_signer_subject(text):
    jobs = _jobs(text)
    step = _by_name(_steps(jobs["verify"]), "assert every release secret exists")
    for name in REQUIRED_SECRETS:
        assert f"secrets.{name}" in step, f"{name} is not asserted"
    assert "vars.RELEASE_SIGNER_SUBJECT" in step
    assert "exit 1" in step


def test_no_step_in_any_job_prints_a_secret(text):
    for job_name, job in _jobs(text).items():
        for step in _steps(job):
            assert prints_a_secret(_run_body(step)) == [], f"{job_name}: {_name(step)}"


def test_lockstep_runs_with_the_tag(text):
    steps = _steps(_jobs(text)["verify"])
    step = _by_name(steps, "the tag, worker/pyproject.toml and app/Directory.Build.props carry one version")
    assert 'check_version_lockstep.py --tag "${GITHUB_REF_NAME}"' in step


def test_the_tagged_commit_must_be_on_main(text):
    steps = _steps(_jobs(text)["verify"])
    step = _by_name(steps, "the tagged commit is on main")
    assert 'git branch -r --contains "$GITHUB_SHA"' in step
    assert "origin/main" in step and "exit 1" in step
    assert re.search(r"if ! git branch -r --contains .*grep -q", step)
    assert "fetch-depth: 0" in steps[0], "the whole history must be there for the question to mean anything"


def test_every_other_job_is_gated_on_verify(text):
    jobs = _jobs(text)
    for name in jobs:
        if name != "verify":
            assert "verify" in _all_needs(jobs, name), f"{name} can run before the secrets are asserted"


def test_no_job_is_allowed_to_fail_softly(text):
    assert "continue-on-error: true" not in text


# ---------- secrets, environment, tokens ----------


def test_secrets_are_read_only_in_verify_and_app_and_both_run_under_the_release_environment(text):
    jobs = _jobs(text)
    holders = {n for n, j in jobs.items() if "secrets." in j.replace("secrets.GITHUB_TOKEN", "")}
    assert holders == {"verify", "app"}
    for n in holders:
        assert re.search(r"(?m)^    environment: release\s*$", jobs[n]), f"{n} holds secrets outside the release environment"
    for n in set(jobs) - holders:
        assert "environment:" not in jobs[n], f"{n} needs no environment; the approval gate stays on the secret holders"


def test_the_oauth_secret_is_only_read_in_verify_and_app(text):
    holders = [n for n, j in _jobs(text).items() if "secrets.OAUTH_CLIENT_JSON" in j]
    assert set(holders) == {"verify", "app"}


def test_every_checkout_drops_the_token(text):
    seen = 0
    for job_name, job in _jobs(text).items():
        for step in _steps(job):
            if "actions/checkout@" in step:
                seen += 1
                assert re.search(r"(?m)^          persist-credentials: false\s*$", step), f"{job_name} checkout keeps the token"
    assert seen >= 4


def test_the_build_job_has_no_write_token_and_the_upload_job_is_separate(text):
    jobs = _jobs(text)
    app_perm = re.search(r"(?m)^    permissions:\n((?:      .*\n)+)", jobs["app"]).group(1)
    assert "contents: read" in app_perm and "contents: write" not in app_perm
    writers = {n for n, j in jobs.items() if "contents: write" in j}
    assert writers == {"upload", "publish"}
    assert "app" in _needs(jobs["upload"])
    assert "vpk upload github" not in jobs["app"]
    assert "vpk upload github" in jobs["upload"]
    assert "secrets." not in jobs["upload"].replace("secrets.GITHUB_TOKEN", "")
    assert "upload" in _needs(jobs["publish"])


# ---------- images ----------


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


def test_images_run_the_pin_checker_on_the_variants_dockerfile_before_building(text):
    steps = _steps(_jobs(text)["images"])
    pin = _by_name(steps, "the base image is pinned by digest")
    assert 'python3 scripts/check_dockerfile_base_pinned.py "worker/Dockerfile.${{ matrix.variant }}"' in pin
    assert _pos(steps, "the base image is pinned by digest") < _pos(steps, "build and push")


def test_every_matrix_variant_has_a_dockerfile_the_checker_reads():
    root = WORKFLOW.parent.parent.parent / "worker"
    for variant in ("cuda", "cpu"):
        assert (root / f"Dockerfile.{variant}").is_file()


def test_images_job_exposes_both_digests(text):
    images = _jobs(text)["images"]
    assert "digest" in images and "upload-artifact@" in images


# ---------- the app job ----------

APP_ORDER = [
    "pin the worker image digests into the app",
    "publish self-contained",
    "write oauth_client.local.json from the secret",
    "list the binaries to sign",
    "sign the app binaries",
    "vpk pack",
    "sign Setup.exe",
    "verify the signatures",
    "stage this release's files",
    "provenance attestation for the installer",
    "SBOM attestation for the installer",
    "hand the staged files to the upload job",
]


def test_the_app_job_runs_its_steps_in_the_pinned_order(text):
    _assert_order(_steps(_jobs(text)["app"]), APP_ORDER)


def test_the_oauth_client_is_written_by_the_script_from_the_secret(text):
    step = _by_name(_steps(_jobs(text)["app"]), "write oauth_client.local.json from the secret")
    assert re.search(r"(?m)^          OAUTH_CLIENT_JSON: \$\{\{ secrets\.OAUTH_CLIENT_JSON \}\}\s*$", step)
    assert "python scripts/write_oauth_client.py --out app/publish/oauth_client.local.json" in step
    assert "WriteAllText" not in step and "Set-Content" not in step and "Out-File" not in step


def test_python_is_set_up_before_the_scripts_the_app_job_runs(text):
    steps = _steps(_jobs(text)["app"])
    setup = [i for i, s in enumerate(steps) if "actions/setup-python@" in s]
    assert setup and setup[0] < _pos(steps, "pin the worker image digests into the app")


def test_signing_actions_get_their_values_from_secrets_only(text):
    app = _jobs(text)["app"]
    for secret in ("AZURE_TENANT_ID", "AZURE_CLIENT_ID", "AZURE_CLIENT_SECRET"):
        assert f"secrets.{secret}" in app
    steps = _steps(_jobs(text)["app"])
    assert "azure/artifact-signing-action@" in _by_name(steps, "sign the app binaries")
    assert "azure/artifact-signing-action@" in _by_name(steps, "sign Setup.exe")


def test_signing_covers_the_exe_and_every_own_dll(text):
    steps = _steps(_jobs(text)["app"])
    own = _by_name(steps, "list the binaries to sign")
    assert "DnaEntropyGraph.*.dll" in own and "DnaEntropyGraph.App.exe" in own
    assert "steps.own.outputs.files" in _by_name(steps, "sign the app binaries")


def test_the_signature_check_covers_every_signed_file_and_the_signer_subject(text):
    step = _by_name(_steps(_jobs(text)["app"]), "verify the signatures")
    assert "verify /pa" in step
    assert "steps.own.outputs.files" in step, "the exes and every DnaEntropyGraph.*.dll, not just one file"
    assert "DnaEntropyGraph.Desktop-win-Setup.exe" in step
    assert "Get-AuthenticodeSignature" in step and "SignerCertificate.Subject" in step
    assert "vars.RELEASE_SIGNER_SUBJECT" in step
    assert "-ne 'Valid'" in step
    assert step.count("exit 1") >= 3


def test_pack_uses_main_exe_and_the_tag_version(text):
    pack = _by_name(_steps(_jobs(text)["app"]), "vpk pack")
    assert "--mainExe DnaEntropyGraph.App.exe" in pack
    assert "--packId DnaEntropyGraph.Desktop " in pack
    assert "--packVersion" in pack and "GITHUB_REF_NAME" in pack


APP_DATA_ROOT_CS = Path(__file__).resolve().parent.parent.parent / "app" / "src" / "DnaEntropyGraph.Core" / "AppDataRoot.cs"
CI_APP = WORKFLOW.parent / "ci-app.yml"


def _data_folder_name() -> str:
    src = APP_DATA_ROOT_CS.read_text(encoding="utf-8")
    m = re.search(r'SpecialFolder\.LocalApplicationData\)\s*,\s*"([^"]+)"\)', src)
    assert m, "AppDataRoot.Default no longer builds <LocalApplicationData>\\<name>; update this test"
    return m.group(1)


def _collides(pack_id: str, data_folder: str) -> bool:
    return pack_id.casefold() == data_folder.casefold()


@pytest.mark.parametrize("path", [WORKFLOW, CI_APP], ids=["release.yml", "ci-app.yml"])
def test_pack_id_never_equals_the_app_data_folder_name(path):
    """#712: Velopack installs to %LOCALAPPDATA%\\<packId> and uninstall empties that folder. NTFS is case-insensitive,
    so a packId equal to the data folder name (any case) would share the user's history, settings and saved inputs."""
    data_folder = _data_folder_name()
    ids = re.findall(r"--packId\s+(\S+)", _code(path.read_text(encoding="utf-8")))
    assert ids, f"no --packId in {path.name}"
    for pack_id in ids:
        assert not _collides(pack_id, data_folder), (
            f"{path.name}: --packId {pack_id} is the same folder as the app data folder {data_folder} (case-insensitive)"
        )


def test_the_collision_check_is_case_insensitive():
    assert _collides("dnaentropygraph", _data_folder_name())
    assert _collides("DnaEntropyGraph", "DNAEntropyGraph")
    assert not _collides("DnaEntropyGraph.Desktop", "DNAEntropyGraph")


def test_attest_covers_only_this_releases_staged_files(text):
    steps = _steps(_jobs(text)["app"])
    prov = _by_name(steps, "provenance attestation for the installer")
    assert "steps.assets.outputs.subjects" in prov
    assert "app/releases" not in prov and "*.nupkg" not in prov and "*.exe" not in prov
    stage = _by_name(steps, "stage this release's files")
    assert re.search(r"(?m)^        id: assets\s*$", stage)
    assert "scripts/release_assets.py stage" in stage and "--github-output" in stage
    sbom = _by_name(steps, "SBOM attestation for the installer")
    assert "stage/DnaEntropyGraph.Desktop-win-Setup.exe" in sbom and "app/releases" not in sbom


def test_app_sbom_and_provenance_cover_the_installer(text):
    app = _jobs(text)["app"]
    assert "anchore/sbom-action@" in app
    assert "attestations: write" in app and "id-token: write" in app
    assert "actions/attest@" in app
    assert "SHA256SUMS" in _jobs(text)["publish"]
    assert "gh release download" in _jobs(text)["publish"]


# ---------- artifacts ----------

_FORBIDDEN_ARTIFACT_PATHS = [
    r"app/releases",
    r"app/publish",
    r"(?:^|/)releases(?:/|$)",
    r"(?:^|/)publish(?:/|$)",
    r"\.nupkg",
    r"\.exe",
    r"oauth_client",
    r"\.zip",
]


def forbidden_artifact_path(path_text: str) -> list[str]:
    norm = re.sub(r"/+", "/", path_text.replace("\\", "/")).lower()
    return [p for p in _FORBIDDEN_ARTIFACT_PATHS if re.search(p, norm)]


@pytest.mark.parametrize(
    "path",
    [
        "app/releases/*",
        "app\\releases\\x",
        "app//publish/",
        "./app/publish/**",
        "**/*.nupkg",
        "dist/Setup.EXE",
        "app\\\\releases",
        "x/oauth_client.local.json",
        "releases/",
    ],
)
def test_the_artifact_path_detector_catches_every_slash_form(path):
    assert forbidden_artifact_path(path)


def _upload_artifact_steps(text: str) -> list[str]:
    out = []
    for job in _jobs(text).values():
        out += [s for s in _steps(job) if "actions/upload-artifact@" in s]
    return out


def _artifact_paths(step: str) -> str:
    m = re.search(r"(?ms)^          path:(.*?)(?=^          [A-Za-z][\w-]*:|\Z)", step)
    return m.group(1) if m else ""


def test_no_upload_artifact_step_carries_packages_the_publish_folder_or_the_oauth_file(text):
    steps = _upload_artifact_steps(text)
    assert len(steps) >= 4
    for step in steps:
        if _name(step) == "hand the staged files to the upload job":
            continue  # the one deliberate hand-off, pinned by its own test below
        path = _artifact_paths(step)
        assert path.strip(), "an upload-artifact step without a readable path"
        assert forbidden_artifact_path(path) == [], f"{_name(step)} uploads {path!r}"


def test_the_release_files_hand_off_is_the_only_package_artifact_and_is_tightly_scoped(text):
    handoff = [s for s in _upload_artifact_steps(text) if _name(s) == "hand the staged files to the upload job"]
    assert len(handoff) == 1
    h = handoff[0]
    assert re.search(r"(?m)^          name: release-files\s*$", h)
    assert re.search(r"(?m)^          path: stage/\*\s*$", h)
    assert "retention-days: 1" in h and "if-no-files-found: error" in h
    assert "app/publish/**" not in text


# ---------- upload (draft) and publish ----------


def test_the_draft_upload_job_downloads_the_staged_files_then_uploads_without_publish(text):
    steps = _steps(_jobs(text)["upload"])
    _assert_order(steps, ["download the staged files", "install vpk", "vpk upload github (draft)"])
    upload = _by_name(steps, "vpk upload github (draft)")
    assert "--publish" not in upload and "--merge" not in upload
    assert "--outputDir app/releases" in upload and "--tag $env:GITHUB_REF_NAME" in upload
    dl = _by_name(steps, "download the staged files")
    assert "name: release-files" in dl and "path: app/releases" in dl


def test_the_vpk_version_is_pinned_identically_in_both_jobs(text):
    jobs = _jobs(text)
    for j in ("app", "upload"):
        assert "dotnet tool install -g vpk --version 1.2.161" in jobs[j]


def test_the_release_stays_a_draft_until_the_last_job(text):
    jobs = _jobs(text)
    flippers = [n for n, j in jobs.items() if re.search(r"--draft=false", j)]
    assert flippers == ["publish"]
    assert {"images", "app", "upload"} <= set(_needs(jobs["publish"]))


PUBLISH_ORDER = [
    "the draft release holds the installer",
    "image digests file and notes",
    "collect the wheel and the SBOMs",
    "SHA256SUMS over every asset",
    "attach the extra assets to the draft",
    "every expected asset is attached",
    "make the release public",
]


def test_publish_runs_its_steps_in_the_pinned_order_ending_with_the_flip(text):
    steps = _steps(_jobs(text)["publish"])
    _assert_order(steps, PUBLISH_ORDER)
    assert _pos(steps, "make the release public") == len(steps) - 1
    assert [_name(s) for s in steps if "--draft=false" in s] == ["make the release public"]


def test_the_asset_check_uses_the_script_with_the_tag_version(text):
    step = _by_name(_steps(_jobs(text)["publish"]), "every expected asset is attached")
    assert "python3 scripts/release_assets.py check" in step
    assert '--version "${GITHUB_REF_NAME#v}"' in step
    assert "grep" not in step, "substring matching is what the anchored script replaces"


def test_publish_checks_out_the_scripts_without_a_token(text):
    steps = _steps(_jobs(text)["publish"])
    assert any("actions/checkout@" in s and "persist-credentials: false" in s for s in steps)


def test_publish_attaches_the_whl_sums_and_digests(text):
    attach = _by_name(_steps(_jobs(text)["publish"]), "attach the extra assets to the draft")
    for needle in ("assets/*.whl", "SHA256SUMS", "image-digests.txt", "assets/*.cdx.json"):
        assert needle in attach


# ---------- reminder ----------


def test_the_clean_vm_reminder_issue_is_opened_once_after_publish(text):
    jobs = _jobs(text)
    assert "publish" in _needs(jobs["verify-reminder"])
    body = jobs["verify-reminder"]
    assert "issues: write" in body
    assert "Verify ${GITHUB_REF_NAME} on a clean VM" in body or "Verify $GITHUB_REF_NAME on a clean VM" in body
    assert "area:packaging" in body
    assert "gh issue list" in body and body.index("gh issue list") < body.index("gh issue create")


def test_the_reminder_passes_the_title_to_jq_as_an_argument(text):
    step = _by_name(_steps(_jobs(text)["verify-reminder"]), "open the issue if there is not one already")
    assert 'jq --arg t "$title"' in step
    assert ".title == $t" in step
    assert not re.search(r'select\(\.title == \\?"\$', step), "the title must not be interpolated into the jq program"
