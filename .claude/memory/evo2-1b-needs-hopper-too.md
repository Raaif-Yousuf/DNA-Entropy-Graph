# Evo 2's smaller variants still need Hopper, not just the biggest ones

Evo 2's **1B, 20B, and 40B** variants require FP8, which on this hardware
family only runs on Hopper-generation GPUs (H100) — the requirement is
**not** limited to the largest (40B) variant, which is the easy assumption
to make and the wrong one. Only the **7B** variant runs on the
L4/A100-class GPUs this app's v0.1 VM actually provisions
(`g2-standard-8`, 1x L4, per the Stack table).

**Model gating must reject 1B/20B/40B on anything but a Hopper-class VM**
(tracked as its own worker issue: "add model gating for Hopper-only Evo
variants"), not just warn. CUDA is required in every case — there is no CPU
fallback path for any real Evo 2 variant, only the MockPredictor used for
tests and the CPU walking skeleton (Hard Rule 6).
