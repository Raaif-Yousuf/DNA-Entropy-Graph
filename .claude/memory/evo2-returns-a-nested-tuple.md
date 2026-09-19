# Evo 2 returns a nested tuple

MEASURED on a GCP L4, prototype Sprint 3. `evo2`'s forward call does not
return a bare logits tensor — it returns a nested tuple, and the prototype's
`_extract_logits` exists only to unwrap it correctly.

**Do not "simplify" `_extract_logits`.** The nesting looks redundant on a
skim and is not; removing a layer of unwrap silently changes what downstream
code treats as logits, with no exception raised anywhere.

Where it lives now: `worker/src/dna_entropy/predictors/evo.py` (Hard Rule 1:
all Evo-specific code stays there). Guarded by `check_probability_matrix` at
the `(L, 4)` contract boundary (Hard Rule 3). See also
`.claude/skills/fixing-a-bug/bug-shapes.md`'s "predictor-boundary contract"
section, and `tokenizer-ids-are-uint8.md` / `evo2-tokenizer-adds-no-bos.md`
for the other two bugs at the same boundary.
