# Native Windows evo2: desk research for D15, researched 2026-10-15

> Issue #119 (spike). This note is desk research only: nothing was installed (this laptop has an
> Intel Arc GPU, no NVIDIA). The issue's own "Done when" (an attempt on a real 24 GB NVIDIA PC with
> a 5 kb wall time) is NOT met; see section 5 for the open measurement and the ToTest row.

## 1. The question

Spec D15: ship local execution natively if torch (cu12x) + flash-attn + evo2 install on Windows,
otherwise through WSL2 running the same `-cuda` container image.

## 2. What the upstream sources say (fetched 2026-10-15)

MEASURED 2026-10-15 (upstream README, raw.githubusercontent.com/ArcInstitute/evo2/main/README.md):
- OS support is stated as "Linux (official) or WSL2 (limited support)". Native Windows is not listed.
- Python 3.11 or 3.12; torch 2.6.x or 2.7.x recommended; CUDA 12.1+, cuDNN 9.3+, GCC 9+ or Clang 10+.
- Full install needs Transformer Engine (`transformer-engine-torch=2.3.0`, from conda-forge) and
  `flash-attn==2.8.0.post2` with `--no-build-isolation`.
- "Light install" (7B only) skips Transformer Engine; the 7B family runs in bfloat16 on any supported
  GPU. `evo2_1b_base`, `evo2_20b` and `evo2_40b` need Transformer Engine and Hopper FP8
  (matches `worker/src/dna_entropy/predictors/hardware.py`).
- `pyproject.toml`: requires python >=3.11,<3.13; depends on biopython, huggingface_hub, torch, `vtx>=1.1.0`.
  flash-attn and Transformer Engine are NOT declared there; they are manual prerequisites.

MEASURED 2026-10-15 (Dao-AILab/flash-attention README): Linux is the listed platform. Windows "might work
starting v2.3.2", "compilation still requires more testing", and no official Windows wheels are mentioned.
A source build needs `ninja` and, without it, about 2 hours.

MEASURED 2026-10-15 (github.com/kingbri1/flash-attention/releases, a community fork): prebuilt
`win_amd64` wheels exist for flash-attn 2.8.3 against torch 2.6.0 (cu124, cp311) and torch 2.7.0 / 2.8.0
(cu128, cp310 to cp313), for example
`flash_attn-2.8.3+cu128torch2.8.0cxx11abiFALSE-cp312-cp312-win_amd64.whl`. No Windows wheel for torch 2.9.0
was listed. These are third-party binaries, so the install must hash-pin them (issue #172) and the
licence column of `THIRD-PARTY-NOTICES.md` must cover the fork (BSD-3 upstream; verify).

MEASURED 2026-10-15 (Zymrael/vortex README): Windows is not mentioned; Transformer Engine and flash-attn are
prerequisites, Docker is recommended for reproducibility, and 7B runs without Transformer Engine.

## 3. Findings and open questions

- Transformer Engine is the Windows blocker for FP8 models: THEORY (unverified): it has no Windows wheels,
  so native Windows can only ever serve the 7B family (bf16). That is acceptable: the app's local option
  is for the 7B model on a 24 GB card (hardware facts memory; `evo2_7b` is the L4/A100 model).
- THEORY (unverified): the light install (torch cu128 2.7.x + community flash-attn 2.8.3 wheel + `vtx` + evo2)
  installs natively on Windows 11 with Python 3.12. The evo2 README pins flash-attn 2.8.0.post2, so the
  2.8.3 wheel is a version drift whose effect is unknown.
- THEORY (unverified): `vtx` (vortex) may contain Linux-only kernels or Triton usage; Triton has no official
  Windows build (a community `triton-windows` exists). Unknown whether the 7B bf16 path imports it.
- The worker's own `[evo]` extra (torch + evo2 + flash-attn) must stay importable under the same pins:
  `worker/src/dna_entropy/predictors/evo.py` is the only importer (Hard Rule 1).

## 4. Decision for the design (agent-made, reversible)

DECISION (agent-made, reversible): build the installer (#172) around a native `uv` venv with a
hash-pinned lock, behind an `ILocalEngineInstaller` interface, so a WSL2 implementation can be added
later without touching callers. Native is chosen as the default because the 7B light install needs no
Transformer Engine, and a WSL2 + container route needs Docker Desktop (a heavier install for a lab user).
The choice flips to WSL2 if the measurement in section 5 fails.

## 5. What still has to be measured (needs an NVIDIA 24 GB Windows PC)

1. `uv venv --python 3.12`, then install torch 2.7.x cu128, the pinned flash-attn Windows wheel, `vtx`, `evo2`.
2. `python -c "import evo2, torch; assert torch.cuda.is_available()"`.
3. Run the mock, then `evo2_7b` on `sample.gb` through `LocalJobRunner`; record versions and the wall time
   for a 5 kb run here. If any step fails, repeat with WSL2 + Docker + the `-cuda` image and record that.

Tracked in `docs/ToTest.md` (Needs: local-gpu) and in issue #119, which stays open until the numbers are in.
