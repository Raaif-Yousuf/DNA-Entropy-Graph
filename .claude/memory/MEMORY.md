> **What is not here.** `scripts/sync_memory.py --push` mirrors the live
> session memory into this directory, and on its first real run it wrote four
> of the owner's own memories here: how he wants sessions run, what he said in
> a planning meeting, and the name of a private repository. This directory is
> published, so those were removed and the sync is being narrowed to the memory
> types that belong in a public repo (see the `DECISION` issue on memory sync).
> Everything below is authored in this repo, for this repo.

## Lesson index (this repo's 27 seeded memory files)

Read this section first for anything code-shaped. Each link is one lesson,
one file, kept short on purpose so a session pulls in only the ones
relevant to what it is doing. See `.claude/README.md` for how this
directory is installed on a fresh machine (it is read from a user-level,
project-scoped path, not from this repo directly — copy it into place once
per machine).

Grouped by the area of the project each lesson bites in. A lesson tagged
`(THEORY)` below is explicitly unverified — see the file itself for what
would confirm or replace it (Hard Rule 18).

### The predictor boundary (Evo 2)

- [Evo 2 returns a nested tuple](evo2-returns-a-nested-tuple.md) — `_extract_logits` exists to unwrap it; do not simplify
- [Tokenizer ids are uint8](tokenizer-ids-are-uint8.md) — torch reads an uncast uint8 array as a boolean mask, silently
- [Evo 2's tokenizer adds no BOS](evo2-tokenizer-adds-no-bos.md) — row 0 is uniform by design in Forward-only mode
- [One forward pass per window](one-forward-pass-per-window.md) — never per position; a per-base loop is forbidden
- [Reverse means reverse complement](reverse-means-reverse-complement.md) — not a reversed string; remap `i -> L-1-i`
- [Evo 2's smaller variants still need Hopper too](evo2-1b-needs-hopper-too.md) — 1B/20B/40B need FP8/Hopper, not just 40B; only 7B runs on L4/A100 (see also the synced `evo2-hardware-facts.md` above, same fact from a different source)

### The Windows/worker split

- [cp1252 crashes on glyphs](cp1252-console-crashes-on-glyphs.md) — ASCII-safe console, UTF-8/LF files (Hard Rule 5)
- [`flash-attn` wheel availability is unmeasured](flash-attn-has-no-cu12-wheel-for-torch-2-9.md) `(THEORY)` — check before pinning `[evo]` extras
- [MSYS2 python is on PATH](msys2-python-on-path.md) — always call `worker\.venv\Scripts\python.exe` explicitly
- [The dev laptop has no CUDA](dev-laptop-has-no-cuda.md) — Intel Arc; GPU tests only via `scripts/cloud_gpu_test.ps1`

### Cloud (GCP)

- [Billing off looks like stockout](billing-off-looks-like-stockout.md) — `CloudErrorClassifier`'s whole reason to exist
- [Quota is per-region; stockout is not](quota-is-per-region-and-fixable-stockout-is-not.md) — only stockout is worth retrying
- [RUNNING is not working](running-is-not-working.md) — the heartbeat in `status.json` is the only real signal
- [`instanceTerminationAction` does not fire on guest shutdown](termination-action-does-not-fire-on-guest-shutdown.md) — `startup.sh` must self-delete via the Compute API
- [The VM runs released bytes](the-vm-runs-released-bytes.md) — pinned by digest, not by branch
- [A tool call caps at ~600s](a-tool-call-caps-at-600s.md) — launch cloud runs detached, then poll
- [Exit 0 is not a pass](exit-0-is-not-a-pass.md) — read the real verdict file, not the shell's exit code
- [Never a shared, fixed-name cloud resource](no-shared-singleton-cloud-resources.md) — discovery is always by label
- [The signing step can silently no-op](the-signing-step-silently-no-ops-without-the-secret.md) — `release.yml` must assert every secret exists

### Process (inherited discipline)

- ["Wired to nothing" is the house bug](wired-to-nothing-is-the-house-bug.md) — name the one observable, always
- [A check that cannot fail is not a check](a-check-that-cannot-fail.md) — mutation-test every guard
- [Closing on the route, not the observable](closing-on-the-route-not-the-observable.md) — a link-by-link trace is not proof
- [A hand-maintained denominator](a-hand-maintained-denominator.md) — widen the predicate, never narrow the scope
- [A ban broken by reflex needs a hook](a-ban-broken-five-times-needs-a-hook.md) — prose loses to the reflex; a `PreToolUse` hook does not

### Stack facts to verify, not assume

- [FluentAssertions 8+ is commercial](fluentassertions-8-is-commercial.md) — why this repo uses Shouldly instead
- [Geneious's track-import support](geneious-imports-gff3-not-wig.md) `(THEORY)` — verify per viewer before shipping export claims
- [igv.js may not build a genome from GenBank directly](igv-web-cannot-build-a-genome-from-genbank.md) `(THEORY)` — verify against the pinned igv.js version
