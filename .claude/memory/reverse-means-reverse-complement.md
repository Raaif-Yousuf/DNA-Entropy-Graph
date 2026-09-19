# "Reverse" means reverse complement, not a reversed string

Feeding the model a DNA sequence spelled backwards is not DNA — it is a
different, biologically meaningless string that happens to share characters
with the real one. The reverse pass runs on the **reverse complement**
(complement each base, then reverse the order) and maps index `i -> L-1-i`
before combining with the forward pass; entropy itself is invariant to that
relabelling, which is why a silent reversal bug can look numerically
plausible while being biologically wrong.

Combined mode takes the first `K` bases from the reverse pass; the seam at
position `K` is recorded in provenance. Any change to
`worker/src/dna_entropy/analysis/direction.py` needs a test that the
combined track differs from Forward-only exactly in the first `K` bases on a
synthetic input where the answer is known by construction, per
`.claude/skills/wired-to-nothing/SKILL.md`'s "direction/windowing change"
row.
