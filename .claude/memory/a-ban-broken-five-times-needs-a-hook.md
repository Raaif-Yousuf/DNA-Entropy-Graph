# A ban broken by reflex needs a mechanical hook, not more prose

This project has not yet accumulated its own incident history for this (it
is new); the principle is carried forward from prior lab-tooling experience
and stated here so it does not have to be relearned the expensive way.

A rule violated by good-faith reflex under time pressure — `git stash`
during a multi-agent session, a recursive delete inside the repo, a cloud
create with no labels — is not fixed by writing the rule more emphatically
in `CLAUDE.md`. A brief that says "no `git stash`" in these exact words has
still lost to the reflex before, in other projects, because the ban has no
mechanical backstop and the instinct fires faster than the instruction is
recalled.

This is why `scripts/hooks/block_git_stash.py`,
`scripts/hooks/block_recursive_delete.py`, and
`scripts/hooks/block_unlabelled_vm_create.py` exist as `PreToolUse` hooks in
`.claude/settings.json` rather than as prose alone — if this project ever
finds its own instance of a ban broken repeatedly, the fix is the same
shape: write a hook, not another sentence.
