# A process exit code of 0 is not a pass

A script, a background pipeline, or an SSH session returning cleanly proves
only that nothing crashed hard enough to non-zero the exit code — it does
not prove the job succeeded. Read the real verdict the worker actually
wrote (`result.json`, `status.json`'s terminal state), not the shell's own
exit code, before reporting a run as successful.

This is the same shape as `wired-to-nothing-is-the-house-bug.md` one level
down: a thing that "ran without error" and a thing that "did the job
correctly" are different claims, and only the second one is worth anything.
