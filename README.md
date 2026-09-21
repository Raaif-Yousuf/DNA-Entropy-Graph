# DNA Entropy Graph

I am building a Windows app for lab biologists who want a per-base Shannon entropy track
without opening a terminal. Researchers in a lab at Vanderbilt University Medical Center
already use the command-line prototypes this succeeds; their PI greenlit this app to
replace them. You drop in a GenBank or FASTA file, press Run, and get a number
from 0 to 2 bits at every position showing how predictable that base looks to the Evo 2
genomic language model, ready to open in IGV, Geneious, SnapGene or Benchling.

## How it works

- Evo 2 needs a GPU, so rather than asking you to own one or asking you to trust me with your
  sequences, the app rents a GPU machine for a few minutes inside your own Google Cloud
  project, runs the job there, downloads the results and cleans the machine up. There is no
  server of mine in the path and no account of mine on your project; the bill is yours and
  visible to you. [`docs/threat_model.md`](docs/threat_model.md) is the honest account of what
  that design does and does not expose.
- The science is a Python package in `worker/` that ships as a container image. It reads the
  sequence, runs it through the model over a sliding window in both directions, turns the
  per-base probabilities into Shannon entropy, and writes bedGraph, wig, GFF3 and TSV tracks.
- The Windows app in `app/` is C# and WinUI 3 and owns everything else: input validation, the
  job state machine, retry-safe cloud calls, cost tracking and a SQLite run history. It talks
  to Google Cloud through its own gateway layer, with an in-memory fake behind the same
  interfaces for tests.
- A run is usually well under a dollar. The cost people forget is that a stopped machine's
  disk keeps charging, so the app tracks every resource it created and offers to delete it.

## Where it stands

Not shipped. There is no installer and no v1. Measured on 2026-09-20, Windows 11:

| part | state | evidence |
| --- | --- | --- |
| worker pipeline, stand-in predictor | runs end to end, no GPU needed | 810 passed, 2 skipped |
| app shell, run history, run progress | builds and launches | 327 tests, 0 failing, 1 skipped |
| Evo 2 on a real GPU | never run | no GPU on the machine it is written on |
| any call to a real Google Cloud project | never run | every cloud path is covered against a fake only |

[`docs/ToTest.md`](docs/ToTest.md) is the queue of what has to be proven on real
infrastructure before anyone trusts it there, and the
[issues](https://github.com/Raaif-Yousuf/DNA-Entropy-Graph/issues) are the only tracker.

## Build and run

```
git clone https://github.com/Raaif-Yousuf/DNA-Entropy-Graph
cd DNA-Entropy-Graph\app && dotnet build -c Release -p:Platform=x64
py -m venv ..\worker\.venv && ..\worker\.venv\Scripts\pip install -e ..\worker[dev]
..\worker\.venv\Scripts\dna-entropy run --input ..\worker\tests\data\sample.gb --name demo --out out
```

Tests: `dotnet test -c Release -p:Platform=x64` from `app/`, and
`worker\.venv\Scripts\python -m pytest worker/tests -m "not gpu"` from the repo root.

To use the app rather than build it, start at
[`docs/user_guide/README.md`](docs/user_guide/README.md). To work on it, start at
[`docs/onboarding.md`](docs/onboarding.md) and [`docs/README.md`](docs/README.md).

This succeeds two command-line prototypes,
[dna-entropy](https://github.com/Raaif-Yousuf/dna-entropy) and
[DNA-Entropy-GenBank](https://github.com/Raaif-Yousuf/DNA-Entropy-GenBank); the Python science
package is ported from them, and the app and the direct-to-Google-Cloud design are new.
[`FEATURES.md`](FEATURES.md) has the prototypes' own feature inventory. MIT licensed, see
[LICENSE](LICENSE).
