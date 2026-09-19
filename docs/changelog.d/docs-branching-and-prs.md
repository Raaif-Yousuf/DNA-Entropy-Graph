- Added `docs/branching_and_prs.md` and indexed it: nothing lands on `main` by a direct push any
  more, every unit of work is a branch, a pull request and a merge, one issue per PR. It records
  why `--merge` and never `--squash` (local `main` already holds the branch commit, so a squash
  makes the next `git merge --ff-only` fail), that CI here fires on demand only so a PR that looks
  green has been checked by nothing, and the local guard list to run before every merge.
