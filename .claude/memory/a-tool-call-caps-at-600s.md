# A tool call caps at roughly 600 seconds

Create + boot + pull the container + run a job is realistically 6 to 20
minutes end to end for this app. An agent or automation driving a cloud run
must never block a single tool call on the whole thing finishing.

The runner is a polled state machine, progress is a stream of events
(`status.json`'s heartbeat), and an agent driving
`scripts/cloud_gpu_test.ps1` launches it detached and polls the log/status
file instead of waiting on one long-running call. See
`.claude/skills/working-on-gcp/SKILL.md`.
