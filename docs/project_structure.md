# Project structure

Two views below: the **target** layout (Appendix C §1 of the design spec,
the full shape this repo is converging on) and **what exists today**. Marked
`HAND` (written and maintained by a person or an agent), `GEN` (generated,
never hand-edited), or `GEN+HAND` (a generated part plus a hand-written
preamble/section).

## Target layout (Appendix C §1)

```
DNA-Entropy-Graph/
├── CLAUDE.md                     HAND   quick-reference card
├── AGENTS.md                     HAND   5-line stub pointing at CLAUDE.md
├── README.md                     HAND   what it is, quickstart, links
├── LICENSE                       HAND   MIT
├── FEATURES.md                   HAND   prototype capability inventory (frozen)
├── THIRD-PARTY-NOTICES.md        GEN+HAND  generated tables + hand preamble
├── OWNER_TODO.md                 HAND   things only the owner can do
├── NEXT_SESSION.md               HAND   point-in-time handoff, overwritten each session
├── .gitignore .gitattributes .editorconfig .ignore
│
├── app/                          C# side (pending issue #61)
│   ├── DnaEntropyGraph.sln  Directory.Build.props  Directory.Packages.props  global.json  nuget.config
│   ├── src/  Core/  Cloud/  Presentation/  App/  Persistence/  LocalEngine/
│   ├── tools/DnaEntropyGraph.CloudCli/
│   ├── tests/  *.Core.Tests/  *.Cloud.Tests/  *.Presentation.Tests/  *.Persistence.Tests/  *.Guards.Tests/  *.App.UiTests/
│   └── packaging/  velopack.json  icon.ico  README.md
│
├── worker/                       Python side, the `dna_entropy` package
│   ├── pyproject.toml            HAND
│   ├── uv.lock                   GEN
│   ├── README.md                 HAND
│   ├── src/dna_entropy/          ported package + worker/ + analysis/ (pending)
│   ├── vm/  startup.sh  README.md          (pending issue #45)
│   ├── Dockerfile.cuda  Dockerfile.cpu     (pending issue #36 for the CPU image)
│   ├── engine/requirements-win-cu12x.lock  (pending)
│   └── tests/                    mirrors src/; tests/data/ fixtures
│
├── docs/                         see docs/README.md
├── docs/contract/                manifest.schema.json etc., GEN from Python dataclasses (pending)
├── tests/contract-fixtures/      shared JSON vectors, read by pytest AND xUnit
├── .claude/                      see .claude/README.md
├── .github/
│   ├── workflows/ ci-app.yml  ci-worker.yml  ci-docs.yml  codeql.yml  (release.yml, cloud-canary.yml pending)
│   ├── ISSUE_TEMPLATE/ feature.yml  bug.yml  decision.yml  config.yml
│   ├── PULL_REQUEST_TEMPLATE.md  labels.yml  dependabot.yml
└── scripts/
    ├── hooks/ block_recursive_delete.py  block_git_stash.py  block_unlabelled_vm_create.py  block_agent_dispatch_in_worktree.py  README.md
    ├── compile_sprint_log.py  issue_precheck.py  sync_memory.py  triage_diagnostics.py
    └── check_*.py  gen_*.py  sync_labels.ps1  new_issue.ps1  dev_app.ps1  dev_worker.ps1  gcp_burn.ps1  cloud_gpu_test.ps1  (pending)
```

## What actually exists today (annotated, generated/transient paths collapsed)

```
DNA-Entropy-Graph/
├── CLAUDE.md  AGENTS.md  README.md  LICENSE  FEATURES.md  NEXT_SESSION.md   live
├── OWNER_TODO.md  .editorconfig  .ignore  .gitattributes  .gitignore        live
├── .pytest_cache/                                                          GEN, gitignored
│
├── docs/
│   ├── README.md  hard_rules.md  entry_points.md  onboarding.md            live (this pass)
│   ├── dev_commands.md  tests.md  project_structure.md                     live (this pass)
│   ├── environment.md  tech_stack.md  ToTest.md  sprint_log.md             live (this pass)
│   ├── changelog.d/README.md                                               live (this pass)
│   ├── migration/                                                          HAND, migration inventories (historical)
│   └── superpowers/specs/                                                  HAND, the approved design + appendices
│
├── tests/contract-fixtures/      cloud_error_classification.json, cloud_quota_parsing.json  live
│
├── worker/
│   ├── pyproject.toml            live, HAND
│   ├── .venv/                    GEN, gitignored — local only, uv venv --python 3.12
│   ├── src/dna_entropy/          live: analysis/  annotators/  predictors/  readers/  validation/  writers/  cli.py  config.py  pipeline.py
│   ├── tests/                    live: test_*.py, data/, contract-fixtures/ (mirrors tests/contract-fixtures/ above)
│   ├── legacy/                   FROZEN, tracked, excluded from Grep search by .ignore (superseded prototype code)
│   ├── docs-legacy/               FROZEN, tracked, excluded from Grep search by .ignore
│   ├── dna-entropy.spec  LICENSE.prototype  CLAUDE.legacy.md  README.legacy.md   FROZEN, REFERENCE-verdict, not live
│   └── packaging/  scripts/       FROZEN prototype packaging scripts, REFERENCE-verdict
│
├── app/                           DOES NOT EXIST YET — issue #61
│
├── .claude/
│   ├── settings.json              live
│   ├── README.md                  live
│   ├── skills/                    live: tests-first, wired-to-nothing, working-an-issue, fixing-a-bug (+bug-shapes.md), orchestrating-agents, working-on-gcp, winui-dev
│   ├── agents/cold-diff-reviewer.md   live
│   └── memory/                    live: MEMORY.md + 27 lesson files
│
├── .github/
│   ├── workflows/ ci-app.yml  ci-worker.yml  ci-docs.yml  codeql.yml        live
│   ├── ISSUE_TEMPLATE/  PULL_REQUEST_TEMPLATE.md  labels.yml  dependabot.yml   live
│
├── scripts/
│   ├── hooks/ block_recursive_delete.py  block_git_stash.py  block_unlabelled_vm_create.py  block_agent_dispatch_in_worktree.py  README.md   live
│   ├── compile_sprint_log.py  issue_precheck.py  sync_memory.py  triage_diagnostics.py   live
│   ├── tests/  test_compile_sprint_log.py  test_issue_precheck.py  test_sync_memory.py  test_hooks.py   live
│   └── check_*.py  gen_*.py  *.ps1                                          DOES NOT EXIST YET
│
└── legacy/clair/                  gitignored, never committed — the CLAIR conventions donor, local-only
```

## Never-commit list

From `.gitignore` (the authoritative source; this list mirrors it for
convenience and can go stale — check `.gitignore` directly if in doubt):

- `worker/.venv/`, `app/**/bin/`, `app/**/obj/`, `app/**/*.user`, `.vs/`,
  `*.suo`, `publish/`, `Releases/` — build/dependency output
- `__pycache__/`, `*.pyc`, `.pytest_cache/`, `.ruff_cache/`,
  `worker/dist/`, `worker/build/`, `*.egg-info/` — Python build artifacts
- `app/secrets/*.local.json`, `*.tok`, `.env` — secrets and local state
  (Hard Rule 12)
- `out/`, `runs/` — outputs
- `Thumbs.db`, `desktop.ini` — OS cruft
- `legacy/clair/` — the private conventions donor, copied onto the owner's
  PC only, never published (see `docs/migration/2026-09-19-clair-
  conventions-inventory.md` for what it is and why it is never committed)

## Generated vs hand-written, in the docs above

- **GEN, never hand-edited:** `worker/uv.lock`, `docs/contract/*.schema.json`
  (once they exist, from Python dataclasses), `THIRD-PARTY-NOTICES.md`'s
  tables (the preamble is hand-written).
- **HAND, but format-templated:** every file listed as `HAND` under
  `docs/`, `.claude/`, and the root — see `docs/README.md`'s template
  skeletons for the shape a new one of these should start from.
- **Frozen, tracked, not live:** `worker/legacy/`, `worker/docs-legacy/`,
  `worker/*.legacy.md`, `worker/LICENSE.prototype`, `worker/dna-entropy.
  spec`, `worker/packaging/`, `worker/scripts/` (the prototype's own dev
  scripts) — kept for reference per the worker migration inventory, excluded
  from content search by `.ignore`, never edited to "fix" anything, since
  editing a frozen copy falsifies what it is a copy of.
