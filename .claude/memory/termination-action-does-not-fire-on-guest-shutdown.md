# instanceTerminationAction does not fire on guest shutdown

MEASURED against Compute Engine's own semantics: `instanceTerminationAction`
fires when *Compute Engine* stops the instance — i.e. when
`maxRunDuration` expires — and does **not** fire when the guest OS runs its
own `shutdown` command.

`worker/vm/startup.sh` therefore ends by calling the Compute API on itself
(`stop` or `delete`, using the instance's metadata token) rather than
relying on a guest-side `shutdown -h now`. `maxRunDuration` stays as the
backstop for a script that dies before reaching its own cleanup call — it
is not a substitute for the self-delete call, and a VM relying on
`maxRunDuration` alone will sit TERMINATED-but-not-deleted (still billing
for its boot disk) for however long is left on the backstop timer. The app
also verifies the terminal state itself, after reading `result.json`.

See `.claude/skills/working-on-gcp/SKILL.md`'s preflight checklist, which
makes this a mechanical item, not just prose.
