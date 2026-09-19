# One forward pass per window, never per position

Evo 2 is autoregressive: **one forward pass yields every position in the
window**, so a "true" per-base rolling window (one forward pass per base) is
both unnecessary and forbidden (Hard Rule 4) — it burns roughly a window's
width more GPU time for no accuracy gain over the tiled approach.

Long sequences are tiled into overlapping windows of `2K` with stride `K`
(context length `K`), each run forward AND on the reverse complement, and
combined by `worker/src/dna_entropy/analysis/direction.py`. If a change
looks like it needs a per-base loop calling the predictor once per position,
that is the signal to stop and re-read this rule, not to implement the loop.

See `reverse-means-reverse-complement.md` for the reverse-pass half of
combined mode.
