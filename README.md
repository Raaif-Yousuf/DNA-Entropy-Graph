# DNA Entropy Graph

A native Windows 11 app for lab biologists. Drop a GenBank or FASTA file (or paste a
sequence), press Run, and get a per-base Shannon entropy track computed by the Evo 2
genomic language model on a GPU in **your own** Google Cloud project. View it in the
built-in genome viewer or open it in IGV, Geneious, SnapGene or Benchling.

No terminal. No gcloud. No SSH. Nothing runs on our servers.

## Status

**Planning.** The design is approved and the work is tracked as GitHub Issues.
Start here:

- [Design spec](docs/superpowers/specs/2026-09-18-dna-entropy-graph-design.md)
- [Prototype feature inventory](FEATURES.md) (what the two command-line prototypes already did)
- [Issues](https://github.com/Raaif-Yousuf/DNA-Entropy-Graph/issues) and
  [milestones](https://github.com/Raaif-Yousuf/DNA-Entropy-Graph/milestones)

## Lineage

This project succeeds two command-line prototypes:
[dna-entropy](https://github.com/Raaif-Yousuf/dna-entropy) and
[DNA-Entropy-GenBank](https://github.com/Raaif-Yousuf/DNA-Entropy-GenBank).
The Python science package (`dna_entropy`) is ported from them and runs on the GPU
machine; the Windows app is new.

## License

MIT. See [LICENSE](LICENSE).
