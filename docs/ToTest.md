# ToTest queue

Hard Rule 17's one carve-out from "GitHub Issues is the only tracker": a
closed issue whose behaviour could not be proven on the thing it actually
ships on. Not a backlog — a queue that must drain. See
[`docs/README.md`](README.md)'s house-style table for how this surface
relates to Issues and `sprint_log.md`.

## Rules

1. Add a row when you close an issue whose behaviour you could not prove on
   the thing it ships on: an installer build, a real GPU VM, a second
   Google account, a local GPU. **"Tests pass" is never that proof** — see
   the `wired-to-nothing` skill for why a green suite does not prove a
   feature is reachable.
2. A row names: the issue, the closing commit sha (validated against the
   real commit graph, not typed from memory), what a human needs (`Needs`),
   the exact action to take, what passing looks like, and what a **false**
   pass looks like — the thing that would fool a quick check into reporting
   success when the behaviour is actually still broken.
3. Delete the row when someone verifies it. If verification fails, reopen
   the issue with the measured evidence, then delete the row — a failed
   verification does not stay in this table, it goes back to being an open
   issue.
4. Group rows by `Needs`, and drain by group, because the cost here is
   setup (booting an installer build, spinning up a GPU VM), not the
   individual check. Doing five `installer`-needs rows in one sitting costs
   one installer build, not five.
5. [`docs/release_runbook.md`](release_runbook.md) drains every `installer`
   row as part of every release. A release with `installer` rows still open
   is not a release.
6. `scripts/check_totest_format.py --max-age-days 45` (once that script
   exists) fails CI on any row older than 45 days, so this table cannot
   quietly turn into a second backlog.

`Needs` is one of: `app-dev`, `installer`, `gpu-vm`, `cpu-vm`,
`two-accounts`, `local-gpu`.

## Row format

```
| # | Closed by (commit sha) | Needs | Do this | Passing looks like | False pass looks like |
```

## Queue

Empty. No issue has been closed against unproven behaviour yet — this table
starts with no rows, and no row below is invented ahead of a real closure.

| # | Closed by (commit sha) | Needs | Do this | Passing looks like | False pass looks like |
| --- | --- | --- | --- | --- | --- |
| *(none yet)* | | | | | |
