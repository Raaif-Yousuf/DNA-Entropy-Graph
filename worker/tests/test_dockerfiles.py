"""Structural tests for worker/Dockerfile.cpu and worker/Dockerfile.cuda (issue #36).

A real build+run smoke test happens in ``ci-worker.yml``'s ``contract`` job (CPU only) and
was also run directly against the real Docker daemon during this session where possible
(see the closing comment on #36 for what was and was not actually verified). These tests
are static/textual regression guards that do not need Docker installed at all, so they
run in every normal ``pytest`` invocation.
"""

from __future__ import annotations

from pathlib import Path

WORKER_DIR = Path(__file__).parent.parent
DOCKERFILE_CPU = WORKER_DIR / "Dockerfile.cpu"
DOCKERFILE_CUDA = WORKER_DIR / "Dockerfile.cuda"


def _code_only(path: Path) -> str:
    """A Dockerfile's text with full-line comments stripped, so assertions about what it
    actually DOES aren't fooled by explanatory prose that necessarily names the very
    things (torch, gcloud, ...) it explains the image does NOT include."""
    return "\n".join(
        ln for ln in path.read_text(encoding="utf-8").splitlines() if not ln.strip().startswith("#")
    )


def test_both_dockerfiles_exist() -> None:
    assert DOCKERFILE_CPU.is_file()
    assert DOCKERFILE_CUDA.is_file()


def test_cpu_base_is_pinned_by_digest() -> None:
    """Issue #255: reproducible builds require a pinned base digest."""
    text = DOCKERFILE_CPU.read_text(encoding="utf-8")
    from_lines = [ln for ln in text.splitlines() if ln.strip().startswith("FROM ")]
    assert len(from_lines) == 1
    assert "@sha256:" in from_lines[0]
    digest = from_lines[0].split("@sha256:")[1].split()[0]
    assert len(digest) == 64
    assert all(c in "0123456789abcdef" for c in digest)


def test_cpu_image_never_installs_gpu_dependencies() -> None:
    code = _code_only(DOCKERFILE_CPU)
    for forbidden in ("torch", "evo2", "flash-attn", "flash_attn"):
        assert forbidden not in code, f"{forbidden!r} must not appear in the CPU image's actual instructions"


def test_cpu_image_installs_the_genes_extra() -> None:
    text = DOCKERFILE_CPU.read_text(encoding="utf-8")
    assert "--extra genes" in text


def test_cpu_image_installs_from_a_locked_lockfile() -> None:
    """Issue #255's other half: pinning the base image digest is not enough for a
    reproducible build if `pip install ".[genes]"` re-resolves numpy/biopython/typer/
    pyrodigal against whatever is current on PyPI the day the image is built. `--locked`
    fails the build outright if uv.lock disagrees with pyproject.toml, rather than
    silently re-resolving -- verified for real (see #255's closing report): two
    independent `docker build --no-cache` runs installed byte-identical dependency
    versions (`pip list --format=freeze` diffed empty) even though the overall image
    digest still differed -- a real, meaningful, and honestly bounded claim, not a claim
    of full bit-for-bit image reproducibility, which this alone does not achieve."""
    text = DOCKERFILE_CPU.read_text(encoding="utf-8")
    assert "uv.lock" in text
    assert "uv export --locked" in text
    assert "pip install --no-deps -r requirements.lock.txt" in text


def test_uv_lock_file_exists() -> None:
    assert (WORKER_DIR / "uv.lock").is_file()


def test_uv_lock_is_current_with_pyproject_toml() -> None:
    """A stale lockfile is worse than no lockfile -- it looks pinned but silently isn't.
    `uv lock --check` fails if uv.lock disagrees with pyproject.toml. Skipped (not
    failed) if `uv` isn't on PATH in whatever environment runs this test, matching
    scripts/gen_manifest_schema.py's own graceful-degradation pattern for an optional
    tool it can't assume is installed everywhere."""
    import shutil
    import subprocess

    import pytest

    if shutil.which("uv") is None:
        pytest.skip("uv not on PATH in this environment")
    result = subprocess.run(
        ["uv", "lock", "--check"], cwd=WORKER_DIR, capture_output=True, text=True, check=False
    )
    assert result.returncode == 0, (
        f"uv.lock is stale relative to pyproject.toml:\n{result.stdout}\n{result.stderr}"
    )


def test_cuda_image_installs_evo_and_genes_extras() -> None:
    text = DOCKERFILE_CUDA.read_text(encoding="utf-8")
    assert '".[genes,evo]"' in text or '".[evo,genes]"' in text


def test_cuda_image_is_clearly_marked_unverified() -> None:
    """This build has never run on a real GPU (no CUDA on this laptop) — the file must
    say so loudly, not imply it works."""
    text = DOCKERFILE_CUDA.read_text(encoding="utf-8")
    assert "UNVERIFIED" in text
    assert "#37" in text  # the tracking spike issue


def test_neither_image_bakes_in_model_weights() -> None:
    """design D6: weights are cached in the user's bucket, never baked into the image."""
    for path in (DOCKERFILE_CPU, DOCKERFILE_CUDA):
        text = path.read_text(encoding="utf-8")
        assert "huggingface_hub" not in text
        assert "download" not in text.lower() or "not baked" in text.lower()


def test_both_images_run_as_non_root() -> None:
    for path in (DOCKERFILE_CPU, DOCKERFILE_CUDA):
        text = path.read_text(encoding="utf-8")
        assert "USER worker" in text


def test_both_images_declare_a_default_selftest_command_with_no_fixed_entrypoint() -> None:
    """Regression guard: a fixed ENTRYPOINT of dna-entropy-worker would double up with
    ci-worker.yml's `docker run ... dna-entropy-worker selftest` invocation into
    `dna-entropy-worker dna-entropy-worker selftest` and fail — caught by actually
    running that exact command against the real built image, not assumed."""
    for path in (DOCKERFILE_CPU, DOCKERFILE_CUDA):
        code = _code_only(path)
        assert "ENTRYPOINT" not in code
        assert 'CMD ["dna-entropy-worker", "selftest"]' in code


def test_neither_image_installs_gcloud_or_ssh() -> None:
    """docs/cloud_design.md section 9: no SSH anywhere in this design."""
    for path in (DOCKERFILE_CPU, DOCKERFILE_CUDA):
        code = _code_only(path)
        assert "gcloud" not in code
        assert "openssh" not in code.lower()
