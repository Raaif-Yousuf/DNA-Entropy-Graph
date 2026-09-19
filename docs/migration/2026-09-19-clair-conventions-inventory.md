# CLAIR conventions inventory

**Date:** 2026-09-19
**Source repo:** CLAIR (private; `<private-org>/CLAIR` mirror), local checkout `<repo-root>\CLAIR`
**Source commit:** `e31f5b7dcdb1404a3bfd43bb0c4f1331de15991c`
**Copied into:** `legacy/clair/` in this repo, mirroring each file's original relative path under CLAIR's root
**Rule:** Nothing under `legacy/` is live. No file in `legacy/clair/` is referenced by `.claude/settings.json`, any hook, any CI workflow, or any script outside `legacy/`. It exists only to be read and mined by the P0 cleanup issues below, each of which ports a named piece into its live location with CLAIR's paths, repo name and guard scripts replaced. Once every P0 issue lands, `legacy/` is deleted (see `legacy/README.md`).

This inventory was drafted from `docs/superpowers/specs/2026-09-18-appendix-c-repo-conventions.md`, which is the adapted, paste-ready source for the live versions of these files.

---

## Copied files

62 files copied verbatim. Verdict values: **ADOPT** (port to the live path with the named changes), **TEMPLATE** (keep as a format example; content re-derived for this repo), **DROP** (CLAIR-specific; delete after this inventory is committed), **REFERENCE** (keep under `legacy/` for its lessons only, not ported as code).

| Path (`legacy/clair/...`) | Purpose (one line) | Verdict | Adaptation needed | Live target path |
|---|---|---|---|---|
| `CLAUDE.md` | CLAIR's quick-reference card: hard rules, stack table, pitfalls, where-to-look index | TEMPLATE | Full rewrite: CLAIR's 28 numbered rules -> the 21 in Appendix C §2 (science/split/cloud/user/process framing); FastAPI/React/DuckDB stack -> WinUI/worker/GCP stack; repo path and issue tracker slug | `CLAUDE.md` |
| `AGENTS.md` | not found in source (CLAIR has `CODEX_PROMPT.md` instead, not an `AGENTS.md`) | — | Author fresh: 5-line stub per Appendix C §1 ("Read CLAUDE.md. Rules and routing live there.") | `AGENTS.md` |
| `OWNER_TODO.md` | Things only the owner can do (installer signing checks, licence server URLs) | TEMPLATE | Replace CLAIR's Windows-signing/licence-server items with Azure Trusted Signing identity validation, OAuth consent screen, GPU quota | `OWNER_TODO.md` |
| `NEXT_SESSION.md` | Point-in-time session handoff, fully overwritten each session | TEMPLATE | Format only; content starts empty for the new repo | `NEXT_SESSION.md` |
| `THIRD-PARTY-NOTICES.md` | Generated third-party attribution tables + hand preamble | TEMPLATE | Regenerate from `dotnet list package --include-transitive` + `uv pip list` once `scripts/gen_third_party_notices.py` exists; drop CLAIR's demo-dataset attributions | `THIRD-PARTY-NOTICES.md` |
| `.ignore` | ripgrep excludes | ADOPT | Replace `clair_data`, `backend/`, `frontend/`, `node_modules`, `.scratch` entries with `worker/.venv`, `app/**/bin`, `app/**/obj`, `.vs`, `Releases/` | `.ignore` |
| `.editorconfig` | not found in source | — | Author fresh (C#/Python mixed conventions) | `.editorconfig` |
| `.gitattributes` | not found in source | — | Author fresh per Appendix C §1 (`* text=auto eol=lf`, `*.ps1 eol=crlf`, `*.sln eol=crlf`) | `.gitattributes` |
| `.claude/README.md` | Explains the `.claude/` directory: what's tracked vs. reinstallable, memory sync steps | ADOPT | Replace the 3 CLAIR-specific skill names with this repo's 7 skills; replace `C--Users-<repo-root>-CLAIR` memory-path slug and repo path throughout | `.claude/README.md` |
| `.claude/settings.json` | Permissions allowlist/denylist + PreToolUse/SessionStart/Stop hooks | ADOPT | `backend/.venv` -> `worker/.venv/Scripts/python.exe`; `dotnet`/`gh issue`/`gh pr` commands per Appendix C §4; deny `gcloud`/`gh release`/`git push --force`/`git stash`; wire the third hook (`block_unlabelled_vm_create.py`, new, doesn't exist in CLAIR) | `.claude/settings.json` |
| `.claude/agents/cold-diff-reviewer.md` | Framing-free second-opinion review agent | ADOPT | Retarget from "Python/FastAPI/DuckDB/Polars/React" to "C#/WinUI/Python diff"; swap CLAIR's bug-class checklist (route wiring, em dash, DI) for this repo's (binding/command/DI/startup-step/label/setting shapes, reverse-complement check) | `.claude/agents/cold-diff-reviewer.md` |
| `.claude/skills/tests-first/SKILL.md` | Write failing test first, prove red, then code, then green | ADOPT | `backend/tests/test_<module>.py` / `frontend/tests/unit` -> `worker/tests/test_<module>.py` / `app/tests/DnaEntropyGraph.<Proj>.Tests/`; venv activation path; neighbour-test list becomes empty contig / N-bearing GenBank / multi-record / L<2K / L>W / cloud-error-per-class | `.claude/skills/tests-first/SKILL.md` |
| `.claude/skills/wired-to-nothing/SKILL.md` | The house bug: code that compiles, runs, passes tests, does nothing | ADOPT | Replace the FastAPI-route/`execution/`-module/eslint-guard shape table with Appendix C §4's table (XAML binding, `[RelayCommand]`, DI service, startup-script step, bucket rule, label, setting, `CloudError` class, worker output file, `JobPhase` transition, direction/windowing change) | `.claude/skills/wired-to-nothing/SKILL.md` |
| `.claude/skills/working-an-issue/SKILL.md` | Precheck before starting/closing a GitHub issue | ADOPT | `gh issue close ... --repo <private-org>/CLAIR` -> `Raaif-Yousuf/DNA-Entropy-Graph`; branch naming and commit-type conventions already match Appendix C §6, keep as-is | `.claude/skills/working-an-issue/SKILL.md` |
| `.claude/skills/fixing-a-bug/SKILL.md` | Eight-step bug-fix discipline; diagnostics zip as field report | ADOPT | `frontend/`/`backend/routers/`/`backend/execution/schema_migrations*.py` file-touch triggers -> `app/src/**`/`worker/src/**` equivalents; `post_merge_check.py` reference -> the Guards project + `check_*.py` scripts | `.claude/skills/fixing-a-bug/SKILL.md` |
| `.claude/skills/fixing-a-bug/bug-shapes.md` | Hand-maintained bug-shape denominator table | ADOPT | Replace CLAIR examples (hardware-tier registry, tier-lock badge) with this repo's recorded classes: nested-tuple unwrap, uint8-as-bool-mask, no-BOS row 0, billing-vs-stockout misclassification | `.claude/skills/fixing-a-bug/bug-shapes.md` |
| `.claude/skills/orchestrating-agents/SKILL.md` | Dispatching subagents, writing briefs, merge discipline | ADOPT | `<repo-root>\CLAIR-wt\WAVE_BRIEF.md` path; `post_merge_check.py`/`run_tests.py` references -> this repo's guard commands; ban list keeps `git stash`/recursive-delete/closing issues/publishing releases, add "no cloud creates outside `cloud_gpu_test.ps1`" | `.claude/skills/orchestrating-agents/SKILL.md` |
| `.claude/skills/working-on-macos/SKILL.md` | Pre-flight discipline before touching the macOS box: which binary is running, port conflicts, worktree venv symlinks | TEMPLATE | Becomes `working-on-gcp`: keep the shape (pre-flight checklist, "which X is actually running", cost/budget arithmetic, end-state verification) but replace every fact (macOS bundle identity -> GCP project/labels; port 8000 -> VM heartbeat; `backend-macos.spec` -> `startup.sh`) | `.claude/skills/working-on-gcp/SKILL.md` |
| `.claude/skills/field-walkthrough/SKILL.md` | Manual field-test walkthrough discipline (screenshots, evidence archive) | REFERENCE | Not in Appendix C's skill list; the MEASURED-discipline and evidence-archiving lessons are reusable if a future manual lab-user test pass is added, but this is not a day-1 skill for DNA-Entropy-Graph | none |
| `.claude/tools/RTK.md` | Docs for the `rtk` token-optimizing CLI proxy | REFERENCE | Generic tool doc, no CLAIR-specific content, but `rtk` is already documented at the user's global `~/.claude/RTK.md`; redundant to keep repo-local | none |
| `.claude/tools/statusline-command.sh` | Claude Code statusline script | REFERENCE | Generic, no CLAIR-specific content; not part of Appendix C's `.claude/` set | none |
| `.claude/tools/subagent-statusline.jq` | jq filter for the statusline | REFERENCE | Generic; same as above | none |
| `.claude/tools/usage.md` | Docs for the usage-tracking tool | REFERENCE | Generic; same as above | none |
| `.claude/tools/usage.py` | Usage-tracking tool implementation | REFERENCE | Generic; same as above | none |
| `.claude/memory/MEMORY.md` | Index of 228 memory-seed files, one link per lesson | TEMPLATE | Format only (index that links `memory/<slug>.md` files with a one-line summary); the 27 memory seeds actually seeded are listed fresh in Appendix C §4 | `.claude/memory/MEMORY.md` |
| `scripts/hooks/README.md` | How the PreToolUse hooks work, example stdin payloads | ADOPT | Example payloads use `<repo-root>\CLAIR-wt\...`; swap for this repo's worktree convention | `scripts/hooks/README.md` |
| `scripts/hooks/block_agent_dispatch_in_worktree.py` | Refuses `Agent` tool dispatch from inside a worktree not under the recognized roots | ADOPT | Hard-codes `CLAIR-wt/` as the recognized worktree root; replace with this repo's worktree directory name | `scripts/hooks/block_agent_dispatch_in_worktree.py` |
| `scripts/hooks/block_git_stash.py` | PreToolUse hook: refuses `git stash` | ADOPT | Check for any hard-coded repo path (grep came back clean for this file specifically; verify on port) | `scripts/hooks/block_git_stash.py` |
| `scripts/hooks/block_recursive_delete.py` | PreToolUse hook: refuses a recursive delete inside the repo | ADOPT | Check for any hard-coded repo path (grep came back clean for this file specifically; verify on port) | `scripts/hooks/block_recursive_delete.py` |
| `scripts/compile_sprint_log.py` | Folds `changelog.d/<branch>.md` fragments into `sprint_log.md`, newest-first | ADOPT | `backend/docs/changelog.d/` path assumption -> `docs/changelog.d/`; `.gitignore`'d-tree list (`venv, node_modules, clair_data`) -> `worker/.venv`, `app/**/bin`, `app/**/obj` | `scripts/compile_sprint_log.py` |
| `scripts/issue_precheck.py` | "Has this issue already been done?" precheck against the GitHub repo | ADOPT | `REPO = "<private-org>/CLAIR"` -> `Raaif-Yousuf/DNA-Entropy-Graph`; path-prefix allowlist `("backend/", "frontend/", "src-tauri/", "scripts/")` -> `("app/", "worker/", "scripts/")` | `scripts/issue_precheck.py` |
| `scripts/sync_memory.py` | Pulls/pushes `.claude/memory/` to/from a per-machine path outside the repo | ADOPT | `CLAIR_MEMORY_DIR` env var and the `-Users-<repo-root>-CLAIR` path-slug derivation are repo-name-coupled; **this repo is public, so add the secret-pattern scan Appendix C §4 requires** (refuse to push a file containing an email, project number, token-shaped string, or billing account id) before wiring the SessionStart/Stop hooks | `scripts/sync_memory.py` |
| `scripts/triage_diagnostics.py` | Reads a diagnostics zip: app version, last states, error classes, last N log lines | ADOPT | CLAIR-specific fields (`backend/state.py:187`, `clair_data` live-dir default) -> this repo's manifest/status schema (app version, worker version, image digest, last `JobPhase`s, `CloudError` classes, last 50 progress lines) | `scripts/triage_diagnostics.py` |
| `scripts/post_merge_check.py` | The 85-guard whole-tree merge gate (routers, frontend baselines, sibling re-exports, etc.) | DROP | Entirely keyed to CLAIR's `backend/`+`frontend/`+`src-tauri/` layout and FastAPI/React-specific census checks; this repo's merge gate is `dotnet test app/tests/DnaEntropyGraph.Guards.Tests` + individually named `scripts/check_*.py`, not one monolith. The "guards accumulate over months, trust `--print-guards`/the count, never a written number" lesson is worth keeping in `hard_rules.md` prose, but the code does not port | none |
| `scripts/branch_gate_check.py` | Pre-commit subset of the merge gate (eslint, component-size baseline, API-mock completeness, etc.) | DROP | Same reasoning as `post_merge_check.py`: every one of its ~13 checks is a census over `backend/routers`, `frontend/src`, or `src-tauri/src` file shapes that don't exist here | none |
| `scripts/run_tests.py` | The one command for running CLAIR's backend suite in parallel, then isolating and re-running failures alone | REFERENCE | The "isolation-retry a failure once before calling it real, never diagnose a flake by hand" pattern is reusable if `worker/tests` grows large enough to need it; not adopted as-is because it is `pytest`+`backend/pytest.ini`-coupled and this repo's day-1 test surface (`dotnet test`, `pytest -m "not gpu"`) doesn't need it yet | none |
| `scripts/wiring_audit.py` | Census of routes/handlers to catch "wired to nothing" | REFERENCE | The concept is already carried forward as the `wired-to-nothing` skill (ADOPT); a standalone audit script over FastAPI routers doesn't translate to WinUI bindings/DI, so this file itself is reference-only | none |
| `scripts/route_reachability.py` | Verifies every backend route has a real frontend/script caller | REFERENCE | Same "wired to nothing for routes" concept; useful pattern if a future guard checks XAML `x:Bind`/DI reachability, but the FastAPI/React grep patterns are not portable | none |
| `scripts/gen_api_endpoints.py` | Generates `docs/api_endpoints.md` from a census of `backend/routers/*.py` | REFERENCE | Conceptually close to Appendix C §1's `gen_manifest_schema.py` (generate a checked-in artifact from source, drift-guarded), but the router-census implementation is FastAPI-specific | none |
| `scripts/agent_wave.ps1` | Sets up a wave of agent worktrees, guards `.venv`/`node_modules` from recursive delete | REFERENCE | The worktree-wave orchestration pattern is reused conceptually by the `orchestrating-agents` skill (ADOPT) and `using-git-worktrees`; the script hard-codes `CLAIR-wt`, `backend\.venv`, `frontend\node_modules` throughout and is not in Appendix C's script list | none |
| `scripts/is_it_alive.ps1` | Health-check script (presumably: is the CLAIR app/backend actually running) | REFERENCE | Not in Appendix C's script list; the "don't trust process-running, check the real health signal" pattern matches this repo's own "RUNNING is not working" pitfall, but the script itself checks CLAIR-specific processes/ports | none |
| `.github/workflows/ci.yml` | CLAIR's CI: backend pytest, frontend vitest/eslint, doc-drift checks | REFERENCE | This repo's CI is split into `ci-app.yml`/`ci-worker.yml`/`ci-docs.yml`; the Windows-job layout and matrix structure are reusable, the specific steps are not | none (informs `ci-app.yml`, `ci-worker.yml`, `ci-docs.yml`) |
| `.github/workflows/build-msi.yml` | Windows installer build | REFERENCE | The Windows job layout (setup, cache paths, build, package) is the reusable part; installer tech is MSI here vs. Velopack in the new repo | none (informs `release.yml`) |
| `.github/workflows/sign-artifact.yml` | Signs a published artifact via Azure Trusted Signing | REFERENCE | **The signing-secrets assertion pattern is exactly what CLAUDE.md's pitfalls section calls out** ("the step silently no-ops if the secret is unset, CLAIR #490"): `release.yml` must assert every signing secret exists before building, not just call the signing action | none (informs `release.yml`'s secret-assertion step) |
| `docs/README.md` | Docs index, topic map, skills table, house style | TEMPLATE | Rewrite topic map for this repo's doc set; port the "five status surfaces" house-style section from Appendix C §3 nearly verbatim (it already generalizes) | `docs/README.md` |
| `docs/hard_rules.md` | Rationale/carve-outs/history for each numbered CLAUDE.md rule | TEMPLATE | Rewrite per-rule rationale for the 21 rules in Appendix C §2; keep the "which rules a machine checks" table format | `docs/hard_rules.md` |
| `docs/entry_points.md` | Task-shaped index; disproven-diagnoses table | TEMPLATE | Rewrite entries; seed the disproven-diagnoses table with "cloud creates fail everywhere = no capacity -> actually billing off" per Appendix C §3 | `docs/entry_points.md` |
| `docs/onboarding.md` | First-hour setup on a fresh machine | TEMPLATE | Rewrite for Windows 11 + VS2022 + WinUI/WASDK + `uv venv` + MSYS2-python trap, per Appendix C §3 | `docs/onboarding.md` |
| `docs/project_structure.md` | Annotated repo tree, generated vs. hand-written, never-commit list | TEMPLATE | Rewrite tree for the `app/`+`worker/` split in Appendix C §1 | `docs/project_structure.md` |
| `docs/dev_commands.md` | Every dev command, PowerShell equivalents table, GPU test recipe | TEMPLATE | Rewrite for `dotnet`/`pytest`/`ruff`/`gh` commands and `cloud_gpu_test.ps1` recipe | `docs/dev_commands.md` |
| `docs/tests.md` | What runs where, guards list, fixtures, flaky-test policy | TEMPLATE | Rewrite for laptop/CI-windows/CI-ubuntu/GPU-VM matrix per Appendix C §3 | `docs/tests.md` |
| `docs/ToTest.md` | Verification queue: closed-but-unproven-on-a-real-build rows | TEMPLATE | Row format and drain rules (§3's "ToTest queue rules") port almost verbatim; content starts empty | `docs/ToTest.md` |
| `docs/sprint_log.head.md` | First 200 of 12,287 lines of CLAIR's append-only sprint log, to show the format | TEMPLATE | Format only (`## Recent changes`, newest-first, one entry per merge); full 12k-line file was deliberately not copied. New repo's `docs/sprint_log.md` starts empty | `docs/sprint_log.md` (format only) |
| `docs/changelog.d/README.md` | The changelog-fragment convention | TEMPLATE | Port near-verbatim: fragment path, must start with `- `, folded by `compile_sprint_log.py`, deleted at merge | `docs/changelog.d/README.md` |
| `docs/ui_conventions.md` | React/CSS UI patterns, copy rules, theme tokens | TEMPLATE | Rewrite for WinUI 3 (NavigationView, InfoBar, ContentDialog, `x:Bind`) per Appendix C §3; keep the copy-rules-with-examples and state-to-UI table format | `docs/ui_conventions.md` |
| `docs/threat_model.md` | Trust boundaries for CLAIR's local-first architecture | TEMPLATE | Rewrite trust boundaries for this repo: user's Google account, OAuth client, container, VM, bucket, laptop | `docs/threat_model.md` |
| `docs/packaging_design.md` | MSI/signing/update-channel design | TEMPLATE | Rewrite for Velopack vs. MSIX decision, self-contained publish, container images, version lockstep | `docs/packaging_design.md` |
| `docs/gce_wave_brief.md` | Brief for a GCE-based agent wave (cost/quota/preflight lessons) | TEMPLATE | Explicitly named in Appendix C §4 as one of the two source documents (with `working-on-macos`) that `working-on-gcp` is modelled on; content is re-derived into that skill, not copied as a doc | none (feeds `.claude/skills/working-on-gcp/SKILL.md`) |
| `docs/agent_wave_brief.md` | Brief format for a multi-agent implementation wave | TEMPLATE | Feeds the brief-writing guidance in `orchestrating-agents` (precheck every lane, name skills by name, one observable, worktree path, changelog fragment path) | none (feeds `.claude/skills/orchestrating-agents/SKILL.md`) |
| `docs/macos_next_build_runbook.md` | Runbook example: cut and verify a macOS build | TEMPLATE | Format example only for the runbook skeleton (Appendix C §3): Preconditions / What changed / Version line / Pre-flight gates / Command sequence / Verification / Rollback / DONE notes | `docs/release_runbook.md` (format only; content is DNA-Entropy-Graph's Velopack release process) |
| `docs/encryption_at_rest.md` | Decision record: whether/how to encrypt data at rest | TEMPLATE | Format example only for the decision-record skeleton (Appendix C §3) | `docs/cloud_design.md` or a standalone decision doc (format only) |
| `docs/branching_and_ci.md` | Decision record: CLAIR's branch/CI/merge-gate design | TEMPLATE | Format example only for the decision-record skeleton; also documents the exact signing-secret pitfall referenced in CLAUDE.md's Critical Pitfalls | `docs/packaging_design.md` (format only) |
| `docs/superpowers/specs/2026-08-08-file-model-and-ia-design.md` | Spec: CLAIR's file/IA model | TEMPLATE | Format example only for the spec skeleton (Appendix C §3: Why / Model / Behaviour / Contracts / Out of scope / Work packages / Open questions) | `docs/superpowers/specs/` (format only) |
| `docs/superpowers_plan_dataset_links_2026-09-03.md` | Plan: dataset-links feature work packages | TEMPLATE | Format example only for a work-package-shaped plan tied to sub-issues | `docs/superpowers/` (format only) |
| `docs/research/chat_declines_2026-09/README.md` | Research note: chat-decline taxonomy findings | TEMPLATE | Format example only for the research-note skeleton (Appendix C §3: Question / What was checked / Findings / What this changes / What was not pursued) | `docs/research/` (format only) |

**Files requested but not found in the CLAIR source (skipped, no `legacy/clair/` entry exists):** `AGENTS.md`, `.editorconfig`, `.gitattributes`, `.agents/` (directory does not exist), `.codex/` (directory does not exist).

---

## Hard-coded CLAIR specifics found

A grep of every copied file for `CLAIR|<private-org>|backend/|frontend/|clair_data|<repo-root>\CLAIR|\.venv|run_tests\.py|post_merge_check` returned **1,690 matches across 55 of the 62 copied files**. Per-file counts (highest first): `docs/project_structure.md` 250, `docs/dev_commands.md` 187, `docs/ToTest.md` 171, `docs/packaging_design.md` 132, `docs/tests.md` 122, `scripts/branch_gate_check.py` 92, `docs/ui_conventions.md` 85, `docs/threat_model.md` 85, `docs/macos_next_build_runbook.md` 66, `scripts/post_merge_check.py` 44, `docs/hard_rules.md` 40, `docs/onboarding.md` 35, `.github/workflows/ci.yml` 32, `docs/encryption_at_rest.md` 31, `docs/agent_wave_brief.md` 25, `docs/README.md` 23, `docs/entry_points.md` 22, `scripts/run_tests.py` 17, `CLAUDE.md` 17, `docs/superpowers/specs/2026-08-08-file-model-and-ia-design.md` 16, `docs/sprint_log.head.md` 16, `scripts/wiring_audit.py` 15, `scripts/route_reachability.py` 14, `.claude/skills/working-on-macos/SKILL.md` 14, `.github/workflows/build-msi.yml` 12, `.claude/skills/orchestrating-agents/SKILL.md` 12, `scripts/agent_wave.ps1` 11, `.claude/skills/wired-to-nothing/SKILL.md` 9, `.claude/settings.json` 7, `.claude/memory/MEMORY.md` 7, `.claude/README.md` 7, and 27 more files with 6 or fewer matches each (these TEMPLATE/large-doc files are the bulk of the count; the file:line detail below focuses on the files with an ADOPT or DROP verdict, where the exact line matters for the port).

The docs with the highest counts (`project_structure.md`, `dev_commands.md`, `ToTest.md`, `packaging_design.md`, `tests.md`, `ui_conventions.md`, `threat_model.md`) are TEMPLATE-verdict and will be rewritten wholesale rather than edited line-by-line, so their matches are not itemized below; grep them directly with the pattern above if a specific fact is needed during that rewrite.

### ADOPT-verdict files (every match matters for the port)

**`.claude/settings.json`**
- L13-16: `.venv/Scripts/python.exe` / `backend/.venv/Scripts/python.exe` -> `worker/.venv/Scripts/python.exe`
- L17: `../scripts/run_tests.py` -> replace with `dotnet test` / `pytest -m "not gpu"` commands
- L18: `backend/.venv/Scripts/python.exe scripts/compile_sprint_log.py` -> path only, script name unchanged
- L19: `scripts/sync_redaction.py` -> not in the new repo's script list, drop this allow entry

**`.claude/README.md`**
- L12: "The three CLAIR-specific skills: `wired-to-nothing`, `working-an-issue`, `field-walkthrough`" -> list this repo's 7 skills
- L25-26, L31: `~/.claude/projects/C--Users-<repo-root>-CLAIR/memory` path slug -> derived from this repo's own path
- L37: `<repo-root>\CLAIR` -> `<repo-root>\DNA-Entropy-Graph`
- L54-55: "CLAIR-specific and re-installable" framing -> restate for this repo's skill set

**`.claude/agents/cold-diff-reviewer.md`**
- L3: description names "Python/FastAPI/DuckDB/Polars/React diff in CLAIR" -> "C#/WinUI/Python diff in DNA-Entropy-Graph"
- L8: "reviewing a diff in CLAIR, a local-first FastAPI/React/DuckDB/Polars..." -> rewrite architecture description
- L17: "Check for CLAIR's real, recorded bug classes" -> point at this repo's recorded classes (nested-tuple, uint8 mask, no-BOS, billing-vs-stockout)

**`.claude/skills/tests-first/SKILL.md`**
- L3: description says "in CLAIR" -> "in DNA-Entropy-Graph, in C# or Python"
- L16: `backend/tests/test_<module>.py` -> `worker/tests/test_<module>.py`
- L18: `frontend/tests/unit/...` (vitest) -> `app/tests/DnaEntropyGraph.<Proj>.Tests/`
- L20: "From `backend/` with the venv active" -> `worker/` with `worker\.venv`
- L33: "`frontend/`, `backend/routers/`, or a schema migration" trigger list -> this repo's equivalent trigger paths

**`.claude/skills/wired-to-nothing/SKILL.md`**
- L3: description names CLAIR generically, keep the framing but drop CLAIR-specific issue numbers (#297, #480)
- L36-37: "A backend route" row (`rg` against `frontend/src/`, named router test files) -> replace with the XAML-binding/DI/startup-step table rows from Appendix C §4
- L79-80, L93: CLAIR-specific shipped-bug examples (`useSheetsAutosave`, "Figure 1" caption) -> replace with this repo's own once found, or drop the anecdotes
- L112-114: file-touch trigger paths (`frontend/src/`, `src-tauri/src/`, `backend/routers/`, `backend/execution/`) and the `post_merge_check.py` reference -> this repo's paths and guard commands

**`.claude/skills/working-an-issue/SKILL.md`**
- L3: description names CLAIR generically, framing carries over as-is
- L119: `gh issue close 123 --repo <private-org>/CLAIR ...` -> `--repo Raaif-Yousuf/DNA-Entropy-Graph`

**`.claude/skills/fixing-a-bug/SKILL.md`**
- L3: description names CLAIR generically, framing carries over
- L282-283: "touched anything under `frontend/`, `backend/routers/`, or `backend/execution/schema_migrations*.py`" -> this repo's file-touch triggers
- L288: "an unbaselined router BEFORE `post_merge_check.py` finds it" -> this repo's guard reference

**`.claude/skills/fixing-a-bug/bug-shapes.md`**
- L33, L43, L61: CLAIR-specific examples (hardware-registry test, tier-lock badge bug) -> replace with this repo's recorded bug shapes (nested-tuple unwrap, uint8-as-bool, no-BOS row-0, billing-vs-stockout) per the denominator table Appendix C §4 implies

**`.claude/skills/orchestrating-agents/SKILL.md`**
- L70: `<repo-root>\CLAIR-wt\WAVE_BRIEF.md` -> this repo's worktree convention and brief path
- L139: "from `backend/`" -> `worker/` or `app/` depending on which suite
- L223, L233, L248, L272-273, L277, L287, L291, L299: repeated `post_merge_check.py` / `scripts/run_tests.py` / `backend/docs/changelog.d/` references -> this repo's guard commands (`dotnet test .../Guards.Tests`, `check_*.py`) and `docs/changelog.d/` path

**`.claude/skills/working-on-macos/SKILL.md`** (TEMPLATE -> becomes `working-on-gcp`, but every line below is what must be replaced, not ported)
- L3, L8, L15, L28, L30, L38, L44, L48, L71, L108, L139-140, L158, L162: every macOS/CLAIR.app/`backend-macos.spec`/worktree-symlink fact needs a GCP-side equivalent (project id, VM labels, heartbeat check, `startup.sh`, budget arithmetic) per Appendix C §4's `working-on-gcp` outline

**`scripts/hooks/block_agent_dispatch_in_worktree.py`**
- L63-64: recognizes `CLAIR-wt/` and `.claude/worktrees/` as valid worktree roots -> add/replace with this repo's worktree directory convention

**`scripts/hooks/README.md`**
- L5: references `backend/tests/test_block_*_hook.py` -> this repo's test location
- L21, L38: example stdin payloads use `<repo-root>\CLAIR-wt\2352-rename` -> this repo's worktree path convention

**`scripts/compile_sprint_log.py`**
- L72: references `backend/tests/test_compile_sprint_log.py` -> this repo's test path
- L95: `.gitignore`'d trees listed as `venv, node_modules, clair_data` -> `worker/.venv`, `app/**/bin`, `app/**/obj`
- L117: `backend/docs/changelog.d/` -> `docs/changelog.d/`

**`scripts/issue_precheck.py`**
- L118: references `run_tests.py` truncation incident -> historical note, can stay or be dropped
- L127: `REPO = "<private-org>/CLAIR"` -> `REPO = "Raaif-Yousuf/DNA-Entropy-Graph"` (**the one line that must change for this script to point at the right tracker**)
- L200: path-prefix allowlist `("backend/", "frontend/", "src-tauri/", "scripts/")` -> `("app/", "worker/", "scripts/")`
- L825: `_NODE_ID_SCAN_DIRS = ("backend/tests", "backend/execution")` -> `("worker/tests", "worker/src/dna_entropy")` or drop this scan if unused
- L929: references `backend/tests/test_schedulers_precompute.py` -> historical note, drop or update

**`scripts/sync_memory.py`**
- L10, L15, L78, L88, L97: `CLAIR_MEMORY_DIR` env var name and the `<repo-root>/CLAIR -> -Users-<repo-root>-CLAIR` slug-derivation logic are repo-name-coupled by construction (the slug is *derived* from the repo path, so it will self-correct once run from `DNA-Entropy-Graph`, but the env var name and any CLAIR-specific comments should be renamed for clarity)
- **Not a line match but the required change:** this repo is public. Before wiring the SessionStart/Stop hooks, add the secret-pattern scan Appendix C §4 requires (refuse to push a file containing an email, project number, token-shaped string, or billing account id) and document it in `.claude/README.md`.

**`scripts/triage_diagnostics.py`**
- L25, L208: `backend/state.py:187` / `routers/system.py:1581` field-location references -> this repo's manifest/status schema field locations
- L58: `--data-dir` help text references "the LIVE clair_data" -> this repo's local run-history directory (`%LOCALAPPDATA%\DNAEntropyGraph\`)
- L460, L467: `clair_data` directory name and `com.clair.app` bundle id -> this repo's app data directory

**`CLAUDE.md`** (TEMPLATE, but the load-bearing facts to re-derive are at these lines)
- L1, L9, L13: title, name expansion, repo path (`<repo-root>\CLAIR`) -> this repo's name and path
- L21: names `post_merge_check.py` as the merge gate with a 85-guard count MEASURED history -> this repo's actual guard mechanism (`dotnet test .../Guards.Tests` + `check_*.py`), with its own count once it exists
- L29, L31: never-commit list (`clair_data/`) and venv-activation rule (`backend\.venv\Scripts\Activate.ps1`) -> `worker\.venv\Scripts\Activate.ps1`
- L38, L42: FastAPI-router rule and `apiFetch()`/`X-CLAIR-Token` convention -> not applicable; replace with this repo's Cloud-interface and `.resw` string rules
- L43: `python ..\scripts\run_tests.py` -> this repo's test commands
- L48: `gh issue create` on `<private-org>/CLAIR` -> `Raaif-Yousuf/DNA-Entropy-Graph`
- L50: em-dash ban scope note -> port as-is, this rule already generalizes (Appendix C Rule 13 keeps it)
- L58-59, L65: DuckDB/LanceDB stack table rows -> this repo's stack table (Appendix C §"Stack")
- L97, L117-119: `frontend/CLAUDE.md` cross-reference and the pre-commit branch-gate row -> not applicable in the C#/Python split; replace with this repo's Rule 21 (`branch_gate_check.py` equivalent, if any is built)

**`NEXT_SESSION.md`**
- L30: "Merge-gate whole-tree guards: 85 (`--print-guards`)" -> not applicable, this repo has no such counter yet
- L102, L114, L116: `backend/execution/`, `post_merge_check.py`, `cloud_run_tests.py --gate` references are historical CLAIR session notes; this file's content is fully overwritten each session so nothing here needs porting, only the *practice* of overwriting it

### DROP-verdict files (hard-coded specifics confirm why they don't port)

**`scripts/post_merge_check.py`** (44 matches) — every check is a census keyed to `backend/routers/`, `backend/execution/`, `backend/tests/`, or `frontend/src/**/*.jsx` (e.g. L116, L199, L397-398, L477, L641-642, L702, L730, L805, L837, L894, L954, L973, L1025, L1133, L1197-1199, L1253). None of these paths exist in `DNA-Entropy-Graph`.

**`scripts/branch_gate_check.py`** (92 matches, the highest of any script) — nearly every one of its ~13 checks is defined in terms of `frontend/src/`, `backend/routers/`, `backend/execution/`, or `backend/tests/` path prefixes (representative: L41-50, L73, L91-99, L122-197, L412-425, L484-485, L689-758, L806-861, L907-1121, L1152-1291, L1357-1414, L1441-1657, L1762, L1884). Confirms the DROP verdict: this file cannot be ported without being rewritten from scratch against a different file layout, which is a rewrite, not a migration.

---

## Proposed P0 cleanup issues

The same list is saved as JSON at `p0_clair.json` in this session's scratchpad (see task notes) for use by whatever files the actual GitHub issues.

1. **`docs: rewrite CLAUDE.md from the CLAIR template`**
   - Why: `legacy/clair/CLAUDE.md` is CLAIR's card verbatim (FastAPI/React rules, DuckDB stack, `<private-org>/CLAIR` tracker). The live `CLAUDE.md` needs the 21 rules, stack table and pitfalls from Appendix C §2 instead, or every session loads instructions that don't apply to this repo.
   - Done when:
     - [ ] `CLAUDE.md` exists at repo root with the 21 numbered rules from Appendix C §2 (science/split/cloud/user/process)
     - [ ] The stack table and Critical Pitfalls section match Appendix C §2, not CLAIR's
     - [ ] No occurrence of `CLAIR`, `backend/`, `frontend/`, or `<private-org>` remains in the file
   - Observable: `grep -c CLAIR CLAUDE.md` returns 0.
   - area: docs

2. **`docs: author a 5-line AGENTS.md stub`**
   - Why: CLAIR has no `AGENTS.md` (only a much longer `CODEX_PROMPT.md`), so there is no file to copy; Appendix C §1 specifies a 5-line stub that must exist so Codex-based agents find routing instructions.
   - Done when:
     - [ ] `AGENTS.md` exists at repo root, under 20 lines, reading "Read CLAUDE.md. Rules and routing live there."
     - [ ] `ci-docs.yml`'s line-count check (once it exists) passes against it
   - Observable: `wc -l AGENTS.md` is under 20.
   - area: docs

3. **`docs: port the five ADOPT skills into .claude/skills/`**
   - Why: `tests-first`, `wired-to-nothing`, `working-an-issue`, `fixing-a-bug` (+ `bug-shapes.md`), and `orchestrating-agents` carry real, reusable process discipline from CLAIR, but every one references CLAIR's file layout (`backend/`, `frontend/`, `backend/routers/`), its guard scripts (`post_merge_check.py`, `run_tests.py`), and `<private-org>/CLAIR`.
   - Done when:
     - [ ] Each of the 5 skills exists under `.claude/skills/<name>/SKILL.md` with every CLAIR-specific path, repo name, and guard-script reference replaced per the file:line list above
     - [ ] `bug-shapes.md`'s example rows are replaced with this repo's recorded bug classes (nested-tuple unwrap, uint8-as-bool-mask, no-BOS row 0, billing-vs-stockout)
     - [ ] `wired-to-nothing/SKILL.md`'s shape table matches Appendix C §4's table exactly
   - Observable: `grep -rl CLAIR .claude/skills/` returns nothing.
   - area: docs

4. **`docs: derive working-on-gcp from working-on-macos and gce_wave_brief`**
   - Why: Appendix C §4 explicitly models the new `working-on-gcp` skill on CLAIR's `working-on-macos` skill plus `gce_wave_brief.md`; neither the macOS pre-flight checklist nor the GCE brief's facts apply directly, but the shape (which-X-is-actually-running, cost arithmetic, end-state verification) does.
   - Done when:
     - [ ] `.claude/skills/working-on-gcp/SKILL.md` exists with the pre-flight order, budget arithmetic, and "empty `resources list` or say what you left" end-state check from Appendix C §4
     - [ ] `legacy/clair/.claude/skills/working-on-macos/SKILL.md` and `legacy/clair/docs/gce_wave_brief.md` are no longer needed and can be deleted
   - Observable: `.claude/skills/working-on-gcp/SKILL.md` exists and is loaded before `scripts/cloud_gpu_test.ps1` runs (per its own description trigger).
   - area: docs

5. **`scripts: port the three worktree/repo-safety hooks`**
   - Why: `block_git_stash.py`, `block_recursive_delete.py`, and `block_agent_dispatch_in_worktree.py` are generic safety hooks; only the worktree-root recognition in the third one is CLAIR-path-coupled (`CLAIR-wt/`).
   - Done when:
     - [ ] All three hooks exist under `scripts/hooks/` and are wired into `.claude/settings.json`'s `PreToolUse` matcher
     - [ ] `block_agent_dispatch_in_worktree.py` recognizes this repo's worktree directory convention instead of `CLAIR-wt/`
     - [ ] A new `block_unlabelled_vm_create.py` (does not exist in CLAIR) is written per Appendix C §4's third-hook description
   - Observable: running a blocked command (e.g. `git stash`) under Claude Code produces a deny with the hook's message.
   - area: scripts

6. **`scripts: port compile_sprint_log.py and issue_precheck.py with the repo name fixed`**
   - Why: Both scripts are otherwise repo-agnostic tooling, but `issue_precheck.py` line 127 hard-codes `REPO = "<private-org>/CLAIR"` and both assume `backend/`/`frontend/` path prefixes that map to `app/`/`worker/` here.
   - Done when:
     - [ ] `scripts/issue_precheck.py`'s `REPO` constant reads `"Raaif-Yousuf/DNA-Entropy-Graph"`
     - [ ] Both scripts' path-prefix assumptions (`backend/`, `frontend/`, `src-tauri/`) are updated to `app/`, `worker/`
     - [ ] `python scripts/issue_precheck.py <n>` runs against a real open issue in the new repo without error
   - Observable: `python scripts/issue_precheck.py 1` returns a real precheck result, not a 404 or wrong-repo error.
   - area: scripts

7. **`scripts: port sync_memory.py and add the required secret scan`**
   - Why: `sync_memory.py` moves `.claude/memory/` content between machines via SessionStart/Stop hooks; Appendix C §4 requires a secret-pattern scan before any push because this repo is public, and CLAIR's version (a private repo) has none.
   - Done when:
     - [ ] `scripts/sync_memory.py --push` refuses to write a file containing an email, a project number, a token-shaped string, or a billing account id, and prints which pattern matched
     - [ ] `.claude/README.md` documents the scan and what it blocks
     - [ ] The SessionStart/Stop hooks in `.claude/settings.json` call `sync_memory.py --pull`/`--push` per Appendix C §4
   - Observable: hand-planting a fake email in a memory file and running `--push` produces a refusal, not a silent push.
   - area: scripts

8. **`scripts: port triage_diagnostics.py to this repo's manifest/status schema`**
   - Why: The diagnostics-zip-as-field-report workflow in `fixing-a-bug` depends on `scripts/triage_diagnostics.py` existing and reading the right fields; CLAIR's version reads `backend/state.py`-shaped fields and a `clair_data` directory that don't exist here.
   - Done when:
     - [ ] `scripts/triage_diagnostics.py <zip>` prints app version, worker version, image digest, last `JobPhase`s, `CloudError` classes, and the last 50 progress lines
     - [ ] `--data-dir` points at `%LOCALAPPDATA%\DNAEntropyGraph\` instead of `clair_data`
   - Observable: running the script against a sample diagnostics zip produces the fields named above instead of an exception on a missing key.
   - area: scripts

9. **`docs: derive .claude/settings.json from the CLAIR template`**
   - Why: The permissions allowlist/denylist and the three PreToolUse hooks in Appendix C §4 are drafted but not yet live; CLAIR's version references `backend/.venv` and `run_tests.py`, which don't exist in this repo.
   - Done when:
     - [ ] `.claude/settings.json` allows the `dotnet`/`worker/.venv`/`gh issue`/`gh pr` commands listed in Appendix C §4 and denies `gcloud`, `gh release`, `git push --force`, `git stash`
     - [ ] All three PreToolUse hooks (`block_git_stash.py`, `block_recursive_delete.py`, `block_unlabelled_vm_create.py`) and the SessionStart/Stop `sync_memory.py` hooks are wired
   - Observable: `claude doctor` or a manual permission-prompt test shows the allow/deny list matches Appendix C §4.
   - area: docs

10. **`docs: draft the day-one docs/ set from CLAIR's TEMPLATE files`**
    - Why: 20 of the copied docs are TEMPLATE-verdict format examples; the live `docs/README.md`, `docs/hard_rules.md`, `docs/entry_points.md`, `docs/onboarding.md`, `docs/dev_commands.md`, `docs/tests.md`, and `docs/ToTest.md` need to exist with this repo's content before any other work references them.
    - Done when:
      - [ ] Each of the 7 docs above exists at its live path with content specific to DNA-Entropy-Graph, not CLAIR
      - [ ] `docs/README.md` includes the "house style: who owns which fact" section from Appendix C §3 (ported near-verbatim, it already generalizes)
      - [ ] `docs/ToTest.md` and `docs/changelog.d/README.md` use the row/fragment format from Appendix C §3 with empty content
    - Observable: every link in `CLAUDE.md`'s "Where to look" table resolves to an existing file with no `TODO`/CLAIR-specific placeholder text.
    - area: docs

---

## See also
- `legacy/README.md` — the rule that nothing under `legacy/` is live, and when it gets deleted
- `docs/superpowers/specs/2026-09-18-appendix-c-repo-conventions.md` — the source spec this inventory was drafted from
