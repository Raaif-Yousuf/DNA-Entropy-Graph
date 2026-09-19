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
        ln for ln in path.read_text(encoding="utf-8").splitlines()
        if not ln.strip().startswith("#")
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
    assert '".[genes]"' in text


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
