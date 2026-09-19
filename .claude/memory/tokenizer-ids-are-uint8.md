# The Evo 2 tokenizer returns uint8 ids

MEASURED on a GCP L4, prototype Sprint 3. The tokenizer's output ids come
back as `uint8`. torch silently reinterprets a `uint8` tensor as a **boolean
mask** in several indexing contexts instead of raising an error, so an
uncast `uint8` id array produces wrong-but-plausible output with no
exception anywhere in the chain.

**Cast to `int` explicitly** at the predictor boundary
(`worker/src/dna_entropy/predictors/evo.py`); do not rely on an implicit
numpy-to-torch conversion to do it for you.

See `.claude/skills/fixing-a-bug/bug-shapes.md`'s "predictor-boundary
contract" section for the other two bugs found at the same boundary
(`evo2-returns-a-nested-tuple.md`, `evo2-tokenizer-adds-no-bos.md`).
