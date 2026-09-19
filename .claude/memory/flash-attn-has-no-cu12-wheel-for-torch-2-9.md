# flash-attn wheel availability is a standing risk, not yet MEASURED here

THEORY (unverified for this repo): `flash-attn`'s prebuilt wheels have
historically lagged the newest PyTorch/CUDA minor-version combinations,
sometimes for weeks, which forces either a source build (slow, needs a
matching CUDA toolkit and compiler in the build environment) or pinning to
an older, compatible torch/CUDA pair.

`worker/pyproject.toml` does not yet pin a torch or flash-attn version (as
of this memory seed being written — check the file directly, this note will
go stale the moment it does). **Before pinning `[evo]` extras' torch/CUDA
versions, check PyPI/the flash-attn release page for a wheel matching that
exact combination**, and record the real finding here as `MEASURED <date>:`
once checked, replacing this THEORY note rather than appending to it (Hard
Rule 18). If no matching wheel exists, the container build
(`worker/Dockerfile.cuda`) is the place to do a source build once, not
something to repeat in every dev environment.
