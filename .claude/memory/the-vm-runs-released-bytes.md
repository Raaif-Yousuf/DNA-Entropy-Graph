# The VM runs released bytes, not your working tree

The app pins the worker container image by **digest**, not by tag or
branch. A change to `worker/src/dna_entropy/` is not live on any VM until a
tagged release builds and pushes a new image, or, in dev,
`scripts/cloud_gpu_test.ps1 -Image <digest>` explicitly overrides the
pinned digest and says so in its own log.

If a cloud run behaves like it is running old code after a fix, the first
check is which image digest it actually pulled — not re-reading the fix for
a mistake that is not there.
