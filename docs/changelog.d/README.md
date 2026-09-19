# `docs/changelog.d/` — the branch-to-sprint-log write path

`docs/sprint_log.md`'s "Recent changes" section is append-only and newest
first. With more than one branch in flight, every branch editing the top of
that file collides with every other branch doing the same thing. This
directory is the fix: a branch writes its own fragment here, which cannot
conflict with any other branch's fragment, and the fragment is folded into
`sprint_log.md` once, at merge time.

## The convention

- One fragment per branch: `docs/changelog.d/<branch-name>.md`, with every
  `/` in the branch name replaced by `-` (`feat/42-windowing` becomes
  `feat-42-windowing.md`).
- The fragment **must start with `- `** (a literal hyphen and a space). A
  heading of any depth (`#`, `##`, ...) is rejected — `scripts/
  compile_sprint_log.py --check` validates this shape.
- Write it in the **same commit** as the behaviour change it describes (Hard
  Rule 16). A commit with a behaviour change and no fragment is missing its
  docs update, not deferring it.
- One bullet is normal; more than one is fine if the branch did more than
  one thing worth recording.

## Folding it in

At merge time, the person or process merging the branch runs:

```powershell
python scripts/compile_sprint_log.py
```

This prepends every fragment's content under `## Recent changes` in
`docs/sprint_log.md`, newest fragment first, and deletes the fragment files
it folded. `--dry-run` previews without writing or deleting.
`--since-tag v0.2.0` renders release notes from the fragments folded since a
given tag without deleting anything (used by `release.yml`, once that
workflow exists). `--check` validates fragment shape only (the `- ` prefix
rule above), without folding anything — this is what `ci-docs.yml` runs on
every PR.

## What NOT to do

- Do not edit `docs/sprint_log.md` directly from a feature branch. That is
  exactly the collision this directory exists to avoid.
- Do not leave a fragment unfolded after merge — an unfolded fragment sitting
  in `docs/changelog.d/` past its own merge is dead weight the next branch
  has to visually filter past.
- Do not write a fragment that is really an issue body. If it needs a "Done
  when" or an "Observable," it belongs in a GitHub issue (Hard Rule 17); the
  fragment is one line for the record, not the record itself.
