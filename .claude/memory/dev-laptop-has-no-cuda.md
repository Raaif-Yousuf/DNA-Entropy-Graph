# The dev laptop has an Intel Arc GPU, not an NVIDIA one

MEASURED on this project's dev machine. There is no CUDA-capable GPU
locally, so any `gpu`-marked pytest test, or anything importing `torch` with
a CUDA device request, cannot run on the laptop at all — not "runs slowly,"
literally cannot find a device.

`pytest -m "not gpu"` is the standing local test command (Hard Rule 15).
**GPU tests run only via `scripts/cloud_gpu_test.ps1`** on a labelled VM in
the owner's own GCP project. Do not "just try it locally to see" — the
failure mode is not a slow run, it is an immediate device-not-found error
that tells you nothing about whether the GPU-path code is actually correct.
