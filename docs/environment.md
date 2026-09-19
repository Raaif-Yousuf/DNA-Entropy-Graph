# Environment variables and dev overrides

**Status: mostly planned, not yet implemented.** A repo-wide search
(`grep -rn "getenv\|environ\[" worker/src/dna_entropy/`, checked while
writing this file) found **no environment-variable reads anywhere in the
worker package today**, and `app/` does not exist yet (issue #61) so it
reads none either. This file records the *convention* this project has
committed to (`DEG_*` prefix for dev overrides, per Appendix C's repo
layout) and the two variable names already named in a design document, so
the first real implementation has a place to land instead of inventing the
convention from scratch. Do not treat anything below as already wired —
check the "Where read" column; `not yet implemented` means exactly that.

## Convention

- Every dev-only override the app or worker reads is prefixed `DEG_`
  (`DNA-Entropy-Graph`), so a `Get-ChildItem Env:` sweep on a dev machine
  can find all of them at a glance and they cannot collide with an
  unrelated tool's variable.
- A `DEG_*` variable is a **developer convenience**, never a supported
  end-user setting — a lab user configures the app through its UI
  (Settings), never through an environment variable. If a `DEG_*` override
  and a real Settings value ever disagree about the same behaviour, the
  `DEG_*` override should say so loudly (a startup log line), not silently
  win.
- `.env` files are not used by the worker or the app themselves (unlike the
  donor project's own `.env.dist` convention, which does not carry over —
  this app's user-facing config lives in the SQLite-backed settings store
  once `app/` exists, not a `.env` file). `*.env` stays in `.gitignore` as a
  defensive carve-out in case a future dev tool wants one, not because
  anything reads one today.

## Named today, not yet implemented

| Variable | Purpose | Where read | Status |
| --- | --- | --- | --- |
| `DEG_FAKE_CLOUD` | When `1`, run the app against `FakeGcp` (Hard Rule 7) instead of a real Google Cloud project. `scripts/dev_app.ps1` sets this before launching. | `app/src/DnaEntropyGraph.App/App.xaml.cs::ConfigureServices` (planned) | Not yet implemented — `app/` and `scripts/dev_app.ps1` do not exist yet |

## Not an app environment variable (do not confuse with the above)

`GPUS_ALL_REGIONS` and its siblings (`NVIDIA_L4_GPUS`, `CPUS_ALL_REGIONS`,
etc., referenced in `CLAUDE.md`'s Critical Pitfalls and the
`quota-is-per-region-and-fixable-stockout-is-not` memory seed) are **Google
Cloud quota names**, checked via the Cloud Quotas API or the console — not
environment variables this app's own code reads. They are listed here only
to head off the natural mix-up given the similar `ALL_CAPS_WITH_
UNDERSCORES` shape.

## Adding a new `DEG_*` variable

When the first one is actually wired into code, update this table with a
real "Where read" file:line reference (Hard Rule 16: docs in the same
commit) and remove the "Not yet implemented" status for that row. Do not
add a row for a variable that is only planned — the table above already
has exactly one of those, named ahead of time because `winui-dev`'s own
skill body references it; do not pre-populate more rows speculatively.
