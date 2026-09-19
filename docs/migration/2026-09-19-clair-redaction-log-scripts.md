# CLAIR quarantine redaction log — `.claude/`, `scripts/`, `.github/` (2026-09-19)

Scope: `legacy/clair/.claude/**`, `legacy/clair/scripts/**`, `legacy/clair/.github/**`,
`legacy/clair/.ignore`. Root `legacy/clair/*.md` and `legacy/clair/docs/` are owned by
another agent and were not touched here (confirmed unchanged except for the other
agent's own concurrent edit to `CLAUDE.md`, noted by the harness mid-session).

## Replacements made

| File | Category | Count replaced | Placeholder used |
| --- | --- | --- | --- |
| `.claude/README.md` | Absolute path w/ username (project slug, 3x) | 3 | `<project-slug>` |
| `.claude/README.md` | Absolute path w/ username (repo root) | 1 | `<repo-root>` |
| `.claude/memory/MEMORY.md` | Infra host names (Hetzner/Coolify/Render, prose) | 2 lines edited | `<clair-hosting-provider>`, `<clair-server-spec>`, `<clair-deploy-tool>` |
| `.claude/skills/field-walkthrough/SKILL.md` | Absolute path w/ username | 1 | `<user-home>` |
| `.claude/skills/orchestrating-agents/SKILL.md` | Absolute path w/ username | 1 | `<user-home>` |
| `.claude/skills/working-an-issue/SKILL.md` | Private org/repo slug (gh call) | 1 | `<private-org>/CLAIR` + `# REDACTED` comment |
| `.claude/skills/working-on-macos/SKILL.md` | Absolute path w/ username | 1 | `<repo-root>` |
| `.claude/tools/usage.md` | Absolute path w/ username | 1 | `<user-home>` |
| `scripts/hooks/README.md` | Absolute path w/ username (2 forms: `\\` and `/`) | 2 | `<user-home>` |
| `scripts/issue_precheck.py` | Private org/repo slug (constant used by `gh`) | 1 | `<private-org>/CLAIR` + `# REDACTED` comment |
| `scripts/post_merge_check.py` | CLAIR telemetry endpoint | 1 | `<clair-telemetry-endpoint>` |
| `scripts/sync_memory.py` | Absolute path w/ username (2 forms: slug + raw path) | 2 | `<project-slug>`, `<user-home>` |
| `.github/workflows/build-msi.yml` | CLAIR download/update endpoints (`downloads.`/`<clair-download-endpoint>`) | 6 | `<clair-download-endpoint>` |
| `.github/workflows/ci.yml` | Private org/repo slug (comment, `gh api` example) | 1 | `<private-org>/CLAIR` + `# REDACTED` comment |
| `.github/workflows/ci.yml` | Infra vendor names (Better Stack x2, Hetzner x1) | 3 | `<clair-monitoring-vendor>`, `<clair-hosting-provider>` |
| `.github/workflows/sign-artifact.yml` | R2 bucket name (`clair-updates`) | 1 | `<clair-download-endpoint>` |

No emails, secrets/tokens, GCP/Azure account values, or UUID-shaped account refs were
found anywhere in the scoped paths — every Azure/Trusted-Signing/BetterStack/R2
reference in the workflows was already only a **secret name** (`${{ secrets.NAME }}`)
or a GitHub Actions variable, which the task instructions say to leave as-is.
`.claude/settings.json` had no machine-specific paths or private repo slugs in its
permission allowlist; it was inspected but not modified (still valid JSON, confirmed
by `json.load`).

## Judgement calls

1. **Loopback addresses (`127.0.0.1`) and `http://host/x`** — left unredacted in
   `.claude/skills/working-on-macos/SKILL.md`, `.github/workflows/build-msi.yml`
   (CI smoke-test health checks), and `scripts/route_reachability.py` (a comment
   about a hardcoded frontend string, plus an illustrative placeholder-shaped
   example URL). These are local/loopback or already-illustrative, not production
   infrastructure, and reveal nothing about CLAIR's real hosting.
2. **Public, non-CLAIR URLs kept as-is**: `json.schemastore.org` (settings.json
   `$schema`), the GTK3 runtime GitHub release download, `timestamp.acs.microsoft.com`
   (Microsoft's public RFC 3161 timestamp authority), and the
   `https://${{ secrets.R2_ACCOUNT_ID }}.r2.cloudflarestorage.com` template in
   `build-msi.yml` (the literal domain suffix is a generic, publicly-documented
   Cloudflare R2 pattern; the only account-specific part is already a secret
   reference).
3. **`.claude/memory/MEMORY.md` link filenames** (`infra-hetzner-not-render.md`,
   `download-url-repoint-via-coolify-api.md`, `session-2026-08-12d-render-to-hetzner.md`)
   — the visible prose naming Hetzner/Coolify/Render was redacted, but the `.md`
   slugs themselves were left unchanged. Those linked memory files are not present
   in this quarantine copy (only the `MEMORY.md` index was copied), so the slugs
   are inert filenames rather than working links or live content; renaming them
   without the target files would not remove any real information and seemed out
   of scope for a same-string redaction pass. Flagged here in case the other agent
   handling `docs/`/root markdown wants a consistent call.
4. **Secret names left untouched everywhere** (`AZURE_CLIENT_ID`,
   `TRUSTED_SIGNING_*`, `BETTERSTACK_CI_HEARTBEAT_URL`, `R2_ACCOUNT_ID`, etc.),
   per the task instructions — only literal values are sensitive, and every
   value in these workflows was already sourced from `${{ secrets.* }}`.
5. **`working-an-issue/SKILL.md` and `issue_precheck.py`**: treated the markdown
   code example the same as the real Python constant, since both are places a
   porter would literally run/copy a `gh --repo` invocation against the private
   repo — both got the placeholder plus the `# REDACTED: set to the target repo
   when porting` comment, even though only `issue_precheck.py` is "a script" in
   the strict sense.
6. Generic role words ("owner", "author", "tester") and the product name "CLAIR"
   were left alone throughout, per the keep-list. No personal names other than
   "Raaif" were found anywhere in the scoped paths (checked via a broad
   author/contact/credit/Dr./Mr./Prof./freemail grep in addition to the regex
   passes).

## Syntax checks

- `python -m py_compile` on all 4 modified Python files
  (`scripts/issue_precheck.py`, `scripts/post_merge_check.py`,
  `scripts/sync_memory.py`, `.claude/tools/usage.py`) — **all passed**, no output.
- `.claude/settings.json` — not modified, but re-verified with `json.load` after
  the pass — **parses cleanly**.
- No `.ps1` files were modified (`agent_wave.ps1`, `is_it_alive.ps1` had no
  redaction hits), so no PowerShell parse check was needed.
- YAML workflow files (`build-msi.yml`, `ci.yml`, `sign-artifact.yml`) — all
  edits were same-line, in-place string substitutions inside existing comments
  or string literals; indentation and structure were not touched. Spot-checked
  by re-reading the edited regions after the pass.
- Incidental `__pycache__/` directories created by the `py_compile` run were
  deleted afterward so they don't leak into the quarantine folder.

## Final re-scan (owned paths only)

Re-ran the full grep/regex sweep (emails, `https?://` URLs, `<private-org>`,
`Users[\\/]raaif` / `<project-slug>`, `onrender|render\.com|hetzner|coolify|
betterstack|resend\.|<clair-domain>|cloudflare|r2\.dev`, UUID-shaped strings,
IPs, `AZURE_|TRUSTED_SIGNING|tenant|client_id|clientId`) after all edits:

- **Emails: 0. Private org/repo slug (`<private-org>`): 0. Absolute
  user-home paths (`<user-home>`, `<project-slug>` slug forms): 0. UUIDs: 0.**
- Remaining matches are exactly the judgement-call items above: `hetzner` /
  `coolify` (2 inert `.md` link slugs + 1 already-redacted prose line's link
  target in `MEMORY.md`), loopback IPs/URLs (`127.0.0.1`, `http://host/x`),
  public URLs (`schemastore.org`, the GTK3 GitHub release, Microsoft's
  timestamp authority, the R2 domain template), and secret **names**
  (`AZURE_*`, `TRUSTED_SIGNING_*`, `BETTERSTACK_*`) which the task instructions
  explicitly say are fine to keep.

## Files touched

- `legacy/clair/.claude/README.md`
- `legacy/clair/.claude/memory/MEMORY.md`
- `legacy/clair/.claude/skills/field-walkthrough/SKILL.md`
- `legacy/clair/.claude/skills/orchestrating-agents/SKILL.md`
- `legacy/clair/.claude/skills/working-an-issue/SKILL.md`
- `legacy/clair/.claude/skills/working-on-macos/SKILL.md`
- `legacy/clair/.claude/tools/usage.md`
- `legacy/clair/scripts/hooks/README.md`
- `legacy/clair/scripts/issue_precheck.py`
- `legacy/clair/scripts/post_merge_check.py`
- `legacy/clair/scripts/sync_memory.py`
- `legacy/clair/.github/workflows/build-msi.yml`
- `legacy/clair/.github/workflows/ci.yml`
- `legacy/clair/.github/workflows/sign-artifact.yml`

Files inspected but left unmodified (no redaction needed):
`.claude/agents/cold-diff-reviewer.md`, `.claude/settings.json`,
`.claude/skills/fixing-a-bug/SKILL.md` + `bug-shapes.md`,
`.claude/skills/tests-first/SKILL.md`, `.claude/skills/wired-to-nothing/SKILL.md`,
`.claude/tools/RTK.md`, `.claude/tools/statusline-command.sh`,
`.claude/tools/subagent-statusline.jq`, `.claude/tools/usage.py` (checked, no
hits), `scripts/agent_wave.ps1`, `scripts/branch_gate_check.py`,
`scripts/compile_sprint_log.py`, `scripts/gen_api_endpoints.py`,
`scripts/hooks/block_agent_dispatch_in_worktree.py`,
`scripts/hooks/block_git_stash.py`, `scripts/hooks/block_recursive_delete.py`,
`scripts/is_it_alive.ps1`, `scripts/run_tests.py`,
`scripts/triage_diagnostics.py`, `scripts/wiring_audit.py`, `.ignore`.

No git commit or push was performed.
