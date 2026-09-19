# Tech stack: every dependency, version, licence, purpose

Feeds `THIRD-PARTY-NOTICES.md` (Hard Rule 21: adding a dependency updates
that file in the same commit as this one, once `scripts/gen_third_party_
notices.py` exists — issue #34). Every licence below is either checked
against the dependency's own repository this session (`MEASURED <date>:`)
or carried from a design document and not yet independently checked
(`THEORY (unverified):`) — see Hard Rule 18.

**One real conflict found while writing this file: see the "Flagged"
row under Worker below**, and `DECISION` issue #301.

## Worker (Python)

| Package | Version constraint | Licence | Purpose | Status |
| --- | --- | --- | --- | --- |
| numpy | `>=1.24` | BSD-3-Clause | Core numeric arrays; the `(L, 4)` contract's dtype | THEORY (unverified this session; long-standing, well-known licence) |
| typer | `>=0.9` | MIT | CLI framework (`dna_entropy.cli`) | THEORY (unverified this session; well-known licence) |
| biopython | `>=1.81` | Biopython License Agreement (permissive, BSD-style) | GenBank/FASTA parsing, now a core dep (not a GPU extra) | THEORY (unverified this session; well-known licence) |
| pytest | `>=7` (`[dev]` extra) | MIT | Test runner | THEORY (unverified this session; well-known licence) |
| torch | unpinned (`[evo]` extra) | BSD-3-Clause | Evo 2's runtime; container-only, never installed on the laptop | THEORY (unverified this session; well-known licence) |
| evo2 | unpinned (`[evo]` extra) | Apache-2.0 | The genomic language model package itself | **MEASURED 2026-09-19** (checked against `github.com/ArcInstitute/evo2`'s own `LICENSE` file) |
| flash-attn | unpinned, brought in transitively by `[evo]` | BSD-3-Clause | Attention kernel Evo 2 uses for speed; see the `flash-attn-has-no-cu12-wheel-for-torch-2-9` memory seed for the wheel-availability risk, separate from the licence question | **MEASURED 2026-09-19** (checked against `github.com/Dao-AILab/flash-attention`'s own `LICENSE` file) |
| pyrodigal | `>=3` (`[genes]` extra) | **GPL-3.0** | Prokaryotic gene-boundary calling for GFF3 output | **MEASURED 2026-09-19, FLAGGED: violates Hard Rule 21.** Checked against `github.com/althonos/pyrodigal`'s own README/licence badge/`COPYING` file. `worker/src/dna_entropy/annotators/prodigal.py` already imports it, lazily, behind the `[genes]` extra; `ci-worker.yml` already installs it in CI. **Do not let this reach a shipped container image** until issue #301 (`DECISION`) is resolved — see `docs/hard_rules.md` rule 21 |

## App (C#, WinUI 3) — planned, `app/` does not exist yet (issue #61)

| Package | Licence | Purpose | Status |
| --- | --- | --- | --- |
| CommunityToolkit.Mvvm | MIT | `[ObservableProperty]`, `[RelayCommand]` source generators | THEORY (unverified this session; well-known licence, per `CLAUDE.md`'s Stack table) |
| Microsoft.Extensions.DependencyInjection | MIT | DI container | THEORY (unverified) |
| igv.js | MIT | Embedded genome viewer, inside WebView2 | THEORY (unverified) |
| ScottPlot.WinUI | MIT | Overview chart | THEORY (unverified) |
| Google.Cloud.* / Google.Apis.Auth | Apache-2.0 | Every real Google Cloud call, confined to `DnaEntropyGraph.Cloud` (Hard Rule 7) | THEORY (unverified) |
| Velopack | MIT | Installer / self-update | THEORY (unverified) |
| Microsoft.Data.Sqlite + Dapper | MIT / Apache-2.0 | Run history | THEORY (unverified) |
| Serilog | Apache-2.0 | Logging | THEORY (unverified) |
| xUnit v3 | Apache-2.0 | Test framework | THEORY (unverified) |
| Shouldly | BSD-3-Clause | Assertion library — chosen specifically to avoid FluentAssertions 8+'s commercial licence, see `fluentassertions-8-is-commercial` memory seed | THEORY (unverified) |
| NSubstitute | BSD-3-Clause | Mocking, chosen to avoid Moq's SponsorLink history | THEORY (unverified) |

Every row above is carried from `CLAUDE.md`'s Stack table and Hard Rule 21's
own licence list, none independently re-checked this session (the worker
half was checked because `worker/pyproject.toml` already exists and could
be checked against a real installed dependency tree; the app half cannot be
checked the same way until `app/` and its `.csproj` files exist). Re-verify
each as `MEASURED <date>:` the first time `app/`'s `Directory.Packages.
props` is real and `dotnet list package --include-transitive` can be run
against it.

## Model weights

| Asset | Licence | Status |
| --- | --- | --- |
| Evo 2 model weights (7B / 1B / 20B / 40B) | Apache-2.0 | THEORY (unverified this session against the actual weight-hosting terms on Hugging Face; carried from `CLAUDE.md` Hard Rule 21, which is itself carried from the design spec) |

## How this file is meant to be kept current

Once `scripts/gen_third_party_notices.py` exists (issue #34), it generates
`THIRD-PARTY-NOTICES.md`'s tables from `dotnet list package
--include-transitive` (once `app/` exists) plus `uv pip list` /
`worker/uv.lock`, and `scripts/check_third_party_notices.py` fails CI if
that generated file is stale against the real dependency tree. This file
(`tech_stack.md`) is the hand-written companion — purpose and a licence
per package, in prose, for a reader who wants the "why," not just the
generated table. When a dependency is added, both this file and
`THIRD-PARTY-NOTICES.md` update in the same commit (Hard Rule 21).
