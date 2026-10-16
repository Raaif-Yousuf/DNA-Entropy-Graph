# Handoff: after the 2026-10-15 wave

> Overwritten every session. Re-check `git log -1 origin/main` and `gh pr list` before trusting anything here.

## What this session was

One orchestrator, at most 3 Sonnet lanes at a time, each in its own worktree; every lane diff got a
framing-free cold review (two rounds on the cloud stack) before it was queued. About 30 PRs merged
(#660 to #738); about 53 reviewed branches are still queued (below).

## Start here next session

1. **Finish the landing queue, in this order.** Use `scripts/land_queue.py` once it lands (it is in the
   queue below); until then `scripts/land_wt.py --branch <b> --sha <s> --title ... --body-file ...
   --merge-when-green` one at a time. Each branch was verified to merge cleanly into main at queue time,
   but main moves: a conflict means re-merge origin/main on a `-r2` branch and re-run the gates.
1. `docs/411-surprisal-default` @ `f21bbb6`
2. `fix/524-genbank-circular-locus` @ `bc35e9a`
3. `feat/99-wizard-page-r2` @ `8b31f47`
4. `feat/107-desktop-notifications` @ `9f7c356`
5. `feat/108-file-associations` @ `08d7393`
6. `feat/134-embl-snapgene` @ `924c2e4`
7. `fix/543-genbank-dropped-feature-notice-r2` @ `362011e`
8. `feat/135-fasta-gff3-pair-r2` @ `4d77e8a`
9. `feat/133-entrez-accession-r2` @ `b503bd0`
10. `fix/694-origin-wrap-writers` @ `3b868f2`
11. `feat/129-130-192-analysis-commands` @ `d4979be`
12. `feat/178-release-workflow` @ `1366419`
13. `feat/180-release-runbook` @ `edfea4d`
14. `feat/179-update-check` @ `3e15fc4`
15. `feat/263-dev-build-banner` @ `a4dadc6`
16. `feat/264-installer-prereqs` @ `c5a8c7f`
17. `feat/110-a11y-shortcuts` @ `2f70e01`
18. `feat/118-entropy-overview` @ `efdcec2`
19. `feat/176-error-copy-guard` @ `ef92347`
20. `feat/136-option-presets` @ `d99e8c6`
21. `feat/685-embl-snapgene-validation` @ `9e77834`
22. `feat/686-gff3-pairing` @ `caf24c0`
23. `docs/169-community-files` @ `cf5b2f3`
24. `docs/168-citation` @ `ad36f1b`
25. `docs/171-examples` @ `d9e799d`
26. `docs/182-user-guide-audit` @ `a45f7a4`
27. `docs/312-viewer-doc` @ `2eb1122`
28. `docs/183-reference-docs` @ `c5567eb`
29. `docs/224-cost-examples` @ `a73801a`
30. `feat/land-queue-tool` @ `5543e0a`
31. `fix/673-settingsstore-concurrency-tests` @ `aeac6b5`
32. `fix/700-bedgraph-reader-perf` @ `014b41b`
33. `chore/scripts-ruff-format-r2` @ `60f255e`
34. `docs/claude-md-462` @ `0888790`
35. `feat/477-wave-findings-r2` @ `57df824`
36. `test/161-golden-files-r2` @ `1f4f220`
37. `feat/local-engine-119-120-173-174` @ `bd5c617`
38. `feat/89-vm-termination-verified-rs` @ `5542ee5`
39. `feat/90-heartbeat-stage-deadlines-rs` @ `acbdb16`
40. `feat/94-reuse-stopped-vm-rs` @ `f21cda5`
41. `feat/670-zone-ladder-rs` @ `54564a2`
42. `fix/678-reuse-labels-rs` @ `d611a5a`
43. `fix/679-vmname-and-limit-rs` @ `e84f981`
44. `fix/691-clock-skew-rs` @ `e8724ac`
45. `feat/105-cloud-page-gateway-rs` @ `fe3cb6f`
46. `feat/99-gpu-quota-request-r2-rs` @ `b218c39`
47. `feat/675-slow-run-control-r2-rs` @ `e98f27f`
48. `worker5/494-496-522-299-rs` @ `f90c43a`
49. `cloud9/723-progress-stall-rs` @ `d321237`
50. `feat/704-manifest-fields-rs` @ `d1a6f0b`
51. `feat/app10-base-rs` @ `575e595`
52. `feat/app10-688-gc-codons-rs` @ `6d20248`
53. `feat/app10-684-accession-rs` @ `c62f5d1`
2. **#740 (P1):** the app12 chain (wizard GPU ask, Cloud page, stall dialogs, partial download,
   Settings follow-ups) is restacked as `feat/app12-*-rs` but must not land until #740's cold-review
   findings are fixed. It goes after `feat/app10-684-accession-rs`.
3. Open follow-ups filed this session, among others: #697 #701 #717 #720 #728 #730 #733 #735.

## Measured this session (worth more than the fixes)

- **Nothing gates a merge on CI.** `scripts tests` was red on three merged PRs (#662). Merge only via
  `--merge-when-green`; #672 asks the owner to turn on branch protection.
- **Wall-clock tests flake on loaded CI runners**: #664, #673, #725 each blocked every merge. Virtual
  time or a gate, never a sleep or a thread-id comparison.
- **A gitignored fixture passes locally and fails in CI**: `worker/.gitignore` hid every golden
  `*.bedgraph`/`*.gff3` (#161). Check `git status --ignored` on new fixture folders.
- **Velopack's uninstaller wipes `%LOCALAPPDATA%\<packId>`**, which equalled the data folder (#712,
  MEASURED from Velopack source). packId is now `DnaEntropyGraph.Desktop`, with a test.
- **Read-only gcloud works** (#661 hook). The CLI points at a CLAIR project, and no project has
  billing on: see `OWNER_TODO.md` 1a/1b.

## Owner asks

#703 (release environment), #672 (branch protection), #721 (Zenodo, private vulnerability reporting),
#37 (releases are blocked until `Dockerfile.cuda` pins its base by digest), the GCP checklist in
`OWNER_TODO.md`, and reversible agent DECISIONs to review: #669 #676 #728 #730 #119 #411.
