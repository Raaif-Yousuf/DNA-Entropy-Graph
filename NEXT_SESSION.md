# Handoff: after the 2026-10-02 three-lane wave

> Overwritten every session. Re-check `git log -1 main` and `gh issue list` before trusting
> anything here.

## What this session was

One orchestrator and three Sonnet lanes in one shared checkout: C# app/cloud, Python worker
and repo tooling. Each lane owned disjoint paths and the orchestrator ran every `git` command.
There were 9 merged PRs (#443, #446, #447, #457, #465, #467, #470, #471, plus this handoff) and
about 30 issues closed. Every test count in a PR body was re-run by the orchestrator, and two
full `premerge.py` runs covered the combined tree.

## Start here next session

1. **Sweep the v0.1 milestone with `issue_precheck.py`.** The previous handoff said five v0.1
   issues remained. That was wrong: its issue list was truncated at 60, and about 29 are
   open. Several look already built but never closed (#26, #28, #31, #33 by their titles;
   #41 and #44 are only waiting on doc and keep-idle boxes). Run
   `worker\.venv\Scripts\python.exe scripts\issue_precheck.py <every v0.1 number>`, then have
   one read-only lane classify each issue as DONE, PARTIAL or NOT STARTED with file:line
   evidence. Lane B did exactly this for nine worker issues this session, and two of them
   turned out fully built.
2. **The two P1 wired-to-nothing gaps in the cloud path:** **#460** (no input is uploaded and
   no output is downloaded; the request carries no object keys) and **#458** (no source for the
   worker image digest, so a real VM gets no startup script). Until both are fixed, pressing
   Run reaches a VM that has nothing to do. Next come #204 (SetupHealthService) and #205 (IAM
   preflight).
3. **Launch the app.** It has still never been run. `docs/ToTest.md`'s #62 row names what to
   look for.

## Tools that exist now and did not this morning

- **`scripts/premerge.py`** runs every gate (29) with the right interpreter and working
  directory. `--fast` is per-lane. FAIL, ERROR, SKIP and KNOWN are reported separately. A
  pytest run that reaches 100% and then crashes in teardown is ERROR, not FAIL. Run it before
  `gh pr create`. It is red in full mode with `--log-dir` under a deep path until **#469** is
  fixed (MAX_PATH in `test_land_pr`'s temp bare repo); without `--log-dir` it is fine.
- **`scripts/land_pr.py`** lands one lane from the shared tree: explicit paths or byte-exact
  hunks, then PR, wait, merge, and `git checkout -B main origin/main`. **Known bug (#472):** it queries checks seconds after `gh pr create`, sees none, and stops with
  "no checks reported", so the PR is left open on its branch. Until that is fixed, use
  `--no-merge` and merge by hand after `gh pr checks --watch`. Never use `--allow-no-checks` to
  get around it, because it would merge before CI starts.
- **New guards:** `check_em_dash.py`, `check_write_newline.py` (Hard Rule 5, which now has a
  mechanical check), `check_manifest_spec_reads.py`, `check_repo_hygiene.py`; a fragment
  presence check (`check_changelog_fragments --base`); `scripts/ruff.toml`.
- **`ci-notices.yml`** runs `check_third_party_notices.py` on windows-latest on every
  dependency-touching PR. Its first real run was green.
- **`scripts/hooks/require_premerge_before_pr.py`** is written and tested but **not
  registered** in `.claude/settings.json`. That is deliberate (#451): in a shared-checkout wave,
  premerge sees other lanes' in-flight edits, so a stamp keyed by HEAD either blocks every PR
  mid-wave or means less than it claims. Decide whether premerge should run in a temporary
  worktree of the commit being landed before registering it.

## Found this session, worth more than the fixes

- **A framing-free review caught eight defects in a lane's finished, green, mutation-checked
  cloud diff.** The cold-diff reviewer saw only the patch. It found that a transport timeout
  left a run non-terminal (Hard Rule 11), that Network failures were reported as Stockout (the
  exact CLAUDE.md pitfall), that Cancel was blocked by an open circuit breaker, and inline
  English reaching the UI. Every one was real. Run a cold review on any cloud-path diff
  before landing it.
- **The worker deleted its own VM with `POST .../delete`.** Compute's `instances.delete` is an
  HTTP DELETE on the instance URL. The old test asserted the wrong URL (#452).
- **The reverse pass fed the model uncomplemented ambiguity codes** under the default `keep`
  policy (#78). After an OOM halving, the recorded seam was also wrong (#456). Both were
  wrong numbers with no error.
- **`compile_sprint_log.py` wrote CRLF on Windows** (#444). A sweep found seven more writers
  doing the same, and that is now guarded.

## Open decisions for the owner

#450 (CI trigger intent), and #451 (whether to register the premerge hook, see above). The
earlier list in `OWNER_TODO.md` still stands: #301, #402 and #413 all point at the one
unwritten Rule 21 carve-out section (#433).

## Still unproven

Nothing cloud-facing has run against real Google Cloud. The new retry/breaker pipeline, the
cancel paths and `StartupMetadata` are proven only against `FakeGcp`. `docs/ToTest.md` has a
row for each.
