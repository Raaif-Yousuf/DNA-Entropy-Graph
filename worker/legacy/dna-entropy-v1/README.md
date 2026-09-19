# dna-entropy-v1 legacy reference

`orchestrator.py` and `cli.py` in this folder are copied verbatim, unmodified, from the
older prototype repository `https://github.com/Raaif-Yousuf/dna-entropy` at commit
`c0f2375314a7d96be89c84d775c99110579a0582` (`src/dna_entropy/cloud/orchestrator.py` and
`src/dna_entropy/cli.py`). They are kept only as a reference for the **ephemeral VM
lifecycle** that prototype implemented and the current `DNA-Entropy-Genbank` prototype
removed in favor of an always-on keeper/client split: `cloudrun` created a per-run VM,
**deleted it by default** after the run completed, and offered a `--keep` flag to
stop-and-preserve the VM instead (with a double confirmation and a disk-cost warning), plus
a confirmation prompt before deleting a reused box. The approved design for
DNA-Entropy-Graph brings this per-job, non-singleton VM lifecycle back (one VM per job,
Stop by default, Delete option, Keep-alive option), so this code is retained purely as a
historical reference for that logic while the real implementation is ported to C# under
`app/src/DnaEntropyGraph.Cloud/`. Nothing in this folder is imported or shipped by the
worker package.
