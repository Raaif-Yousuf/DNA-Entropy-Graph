# Contributing to DNA Entropy Graph

Thank you for looking. This project is a Windows desktop app for lab biologists (`app/`, C# and
WinUI 3) plus the Python science package it runs in the cloud (`worker/`). Contributions are
welcome, from a typo fix to a feature. By taking part you agree to the
[Code of Conduct](CODE_OF_CONDUCT.md). To report a security problem, do not open an issue; use
[SECURITY.md](SECURITY.md).

## Before you start

1. **Find or file an issue.** GitHub Issues is the only tracker. Search first
   (`gh issue list --search "<words>" --state all --limit 50`), because the work may already be
   done or declined. Use the issue forms under *New issue*; each asks for a "Done when" list so
   anyone can tell when the work is finished.
2. **Read the rules.** [`CLAUDE.md`](CLAUDE.md) holds the 21 hard rules in short form and
   [`docs/hard_rules.md`](docs/hard_rules.md) holds the reasons. The ones that bite most often:
   tests first, no secrets, every user-visible string in `Resources.resw` with no em dashes,
   no logic in `*.xaml.cs`, and Google Cloud calls only through the interfaces in
   `DnaEntropyGraph.Core/Cloud/`.
3. **Set up.** [`docs/onboarding.md`](docs/onboarding.md) is the first hour on a fresh Windows 11
   machine; [`docs/dev_commands.md`](docs/dev_commands.md) lists every command.

## Tests first

A new class gets its test file before its code; a new worker module gets
`worker/tests/test_<module>.py` first. Show the red run, write the code, show the green run.
Both suites must be green before you open a pull request:

```
# from app/
dotnet test -c Release -p:Platform=x64
# from the repo root
worker\.venv\Scripts\python -m pytest worker/tests -m "not gpu"
```

Tests marked `gpu` never run on a laptop; they run on a labelled VM through
`scripts/cloud_gpu_test.ps1` and only the maintainer does that. If your change needs one, say
so in the pull request. `python scripts/premerge.py --fast` runs every other gate in one
command.

## The shape of a pull request

- One issue, one pull request, from a branch named `<type>/<slug>` (`feat/`, `fix/`, `docs/`,
  `ci/`, `refactor/`, `test/`, `chore/`). Nothing is pushed straight to `main`; see
  [`docs/branching_and_prs.md`](docs/branching_and_prs.md).
- Fill in the pull request template: the issue's "Done when" list ticked, the red and green
  output, and the one observable that would differ if your change were wired to nothing.
- **Docs in the same commit.** A behaviour change updates the matching file in `docs/`, and a
  change a user can see also updates `docs/user_guide/`. On a branch, write
  `docs/changelog.d/<branch-name-with-dashes>.md`; every line starts with `- `.
- Say what you did not verify, and what would prove it. A claim about a cause is either
  `MEASURED <date>:` with the observation, or `THEORY (unverified):`.
- Do not create cloud resources for a pull request. If a change needs real infrastructure,
  describe how to check it and the maintainer will.

## Licences and dependencies

Everything that ships is MIT, Apache or BSD. Do not add a GPL, LGPL or AGPL dependency. A new
dependency updates [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md) in the same commit.
Contributions are accepted under the project's [MIT licence](LICENSE).

## Writing for users

The user guide in `docs/user_guide/` is written for a lab biologist who has never opened a
cloud console: the plain phrase first, the technical term second, and every error names one
thing the reader can do. Its voice is described in
[`docs/user_guide/README.md`](docs/user_guide/README.md).

## Questions

Open an issue. There is no chat channel.
