# DNA Entropy Graph

A Windows app for lab biologists, being built now. You drop a GenBank or FASTA file (or
paste a sequence), press Run, and get a per-base Shannon entropy track: a number from 0
to 2 bits at every position, showing how predictable that position looks to a genomic
language model (Evo 2). You view it in a built-in genome viewer or open it in IGV,
Geneious, SnapGene, or Benchling.

The analysis runs on a rented computer with a graphics card, for a few minutes, inside
your own Google Cloud project, paid for by you. Nobody involved with this project can see
your account, your files, or your bill. No terminal, no `gcloud`, no SSH, nothing on our
servers. [`docs/threat_model.md`](docs/threat_model.md) has the full, honest account of
what that setup does and does not expose.

## What it needs

- A Windows 11 PC (the packaged app is Windows-only for now).
- A Google account, with a payment method you are comfortable putting on file. Runs are
  usually well under a dollar each; see
  [`docs/user_guide/06-costs-and-cleanup.md`](docs/user_guide/06-costs-and-cleanup.md)
  for real worked examples, including the cost people forget (a stopped rented
  computer's disk keeps costing a small amount until it is deleted).

## If you are here to use it

Start with [`docs/user_guide/README.md`](docs/user_guide/README.md), written for you, not
for a developer: what the app does, what it costs, and a page-by-page walkthrough from
install to your first run. **There is no installer yet** (see Status below); the guide
describes the app as designed, ahead of the first release, so you know what to expect once
one exists.

## Status

**Not shipped. No installer, no v0.1 release yet.** Being honest about exactly where
things stand, since a README that implies a working product wastes your time finding out
otherwise:

- The design is approved (the full spec is linked below) and the GitHub issue tracker is
  the single source of truth for what is done and what is left.
- The Windows app (`app/`, C#/WinUI 3) does not exist as code yet.
- The science package (`worker/`, Python) is ported, runs, and its tests pass on a laptop
  with no GPU (`worker\.venv\Scripts\python.exe -m pytest worker/tests -m "not gpu"`).
  It has not yet been run end to end against a real Google Cloud project; every cloud
  path is implemented and has only ever been exercised against an in-memory fake so far.
  See [`docs/ToTest.md`](docs/ToTest.md) for the exact queue of what still needs proving
  on real infrastructure before anyone should trust it there.
- A full developer documentation set exists under [`docs/`](docs/README.md): architecture,
  the app-worker contract, the science, cloud design, packaging, and more, indexed from
  [`docs/README.md`](docs/README.md).
- Milestones, in order: `v0.1 walking skeleton` (a full run with a stand-in model on a
  plain cloud computer) → `v0.2 real Evo on GPU` → `v0.3 daily-use polish` →
  `v1.0 lab release` (the first signed installer) → `v1.1 multi-cloud (AWS)`. See the
  [milestones page](https://github.com/Raaif-Yousuf/DNA-Entropy-Graph/milestones) for
  current progress; a number written here would go stale by the time you read it.

## If you are here to develop it

- [`docs/onboarding.md`](docs/onboarding.md): first hour on a fresh Windows 11 box, to a
  green worker test suite.
- [`docs/README.md`](docs/README.md): the full documentation index, reading order, and
  which doc owns which fact.
- [`CLAUDE.md`](CLAUDE.md): the project's own hard rules and stack, if you are an AI
  agent working in this repo, or a developer who wants the same quick reference.
- [Design spec](docs/superpowers/specs/2026-09-18-dna-entropy-graph-design.md): the
  approved design everything else is built from.
- [Issues](https://github.com/Raaif-Yousuf/DNA-Entropy-Graph/issues) and
  [milestones](https://github.com/Raaif-Yousuf/DNA-Entropy-Graph/milestones): the only
  tracker. No `ROADMAP.md`, no `TODO.md`.

## Lineage

This project succeeds two command-line prototypes:
[dna-entropy](https://github.com/Raaif-Yousuf/dna-entropy) and
[DNA-Entropy-GenBank](https://github.com/Raaif-Yousuf/DNA-Entropy-GenBank). The Python
science package (`dna_entropy`) is ported from them, largely unchanged, and runs inside
a container on the rented cloud computer or on a local NVIDIA GPU; the Windows app and
the direct-to-Google-Cloud design are new. See [`FEATURES.md`](FEATURES.md) for the
prototypes' own feature inventory, the starting point this project was scoped from.

## License

MIT. See [LICENSE](LICENSE).
