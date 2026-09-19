# Evo 2's tokenizer adds no BOS token

MEASURED on a GCP L4, prototype Sprint 3. The tokenizer adds no
beginning-of-sequence token, so the output array has exactly `L = len(seq)`
rows, not `L + 1`.

**In Forward-only mode this makes row 0 uniform (2.0 bits of entropy) by
design, not by bug.** Do not "fix" row 0 by inserting a synthetic BOS row,
and do not read a uniform row 0 alone as evidence of a broken forward pass —
check first whether combined (bidirectional) mode is in play, which resolves
row 0 from the reverse pass instead.

See `one-forward-pass-per-window.md` and
`.claude/skills/fixing-a-bug/bug-shapes.md`'s "predictor-boundary contract"
section for the other two bugs at the same boundary.
