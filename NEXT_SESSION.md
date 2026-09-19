# Handoff: after the 2026-09-18/19 planning session

> Overwritten every session. Re-check `git log -1 main` and `gh issue list` before trusting anything here.

## What this session was
Planning only. No application code exists yet. The owner approved the design; the repo, labels,
milestones and the full issue set were created from it.

## State
- `main` has one commit: the approved spec (`docs/superpowers/specs/2026-09-18-dna-entropy-graph-design.md`)
  with three appendices, `FEATURES.md` (prototype inventory), `LICENSE`, `README.md`, `.gitignore`.
- 8 epics, 12 DECISION issues, 4 owner tasks, and the milestone-scoped children are open on GitHub.
  Children are sub-issues of their epic. Every non-epic issue carries Done-when and Observable blocks.
- Milestones: v0.1 walking skeleton, v0.2 real Evo on GPU, v0.3 daily-use polish, v1.0 lab release, post-v1.

## Start here next session
1. Read the spec body (not the appendices) once. Section 2 lists every decision and why.
2. Work the `area:docs` v0.1 issues first: CLAUDE.md and `.claude/` (skills, hooks) come before any code,
   because they are what every later session is held to. Appendix C has the paste-ready drafts.
3. Then `worker: port dna_entropy ...` (v0.1). The source is `C:\Users\raaif\DNA-Entropy-Genbank\src\dna_entropy`
   minus `cloud/`; keep every test green in `worker\.venv` (uv, Python 3.12; never the MSYS2 python on PATH).
4. The two week-1 spikes (`packaging: spike: WinUI 3 unpackaged + Velopack ...` and
   `packaging: spike: NGC PyTorch base + pip install evo2 ...`) can run in parallel with the docs work and
   answer DECISION issues D2, D3 and the container stack.

## Owner actions pending (issues labelled `owner`)
OAuth client + consent screen; privacy policy + verification; Azure Trusted Signing; GPU quota on a dev
project. None block the docs or worker-port issues.

## Traps this session paid for
- Evo 2 1B, 20B and 40B require FP8 on Hopper (H100). Only the 7B variants run on an L4 or A100. The
  "small model = cheap" intuition is wrong here.
- "Reverse" prediction must be reverse complement, not reversed text.
- A true per-base rolling window is one forward pass per base. Overlapping windows of 2K with stride K
  are the cheap equivalent.
- `instanceTerminationAction` does not fire on guest `shutdown`; the VM must stop or delete itself via the
  Compute API, and the app must verify it.
- Two lab members may share one Google account. Nothing cloud-side may be a fixed-name singleton.
