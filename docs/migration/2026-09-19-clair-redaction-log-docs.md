# CLAIR redaction log — docs quarantine

Scope: `legacy/clair/*.md` (root files), `legacy/clair/docs/**` (recursive), and
`docs/migration/2026-09-19-clair-conventions-inventory.md`. `legacy/clair/.claude`,
`legacy/clair/scripts`, and `legacy/clair/.github` were explicitly out of scope
(owned by another agent) and were not touched.

## Replacements made

| File | Category | Count replaced | Placeholder used |
|---|---|---|---|
| `legacy/clair/CLAUDE.md` | Private org/repo (GitHub Issues link + issue-tracker mention) | 2 | `<private-org>/CLAIR` |
| `legacy/clair/CLAUDE.md` | Local path with username | 1 | `<repo-root>\CLAIR` |
| `legacy/clair/OWNER_TODO.md` | Owner email | 1 | `<owner-email>` |
| `legacy/clair/OWNER_TODO.md` | Private network gateway IP | 1 | `<ip-address>` |
| `legacy/clair/OWNER_TODO.md` | Telemetry endpoint (scheme + bare) | 2 | `<clair-telemetry-endpoint>` |
| `legacy/clair/OWNER_TODO.md` | Download endpoint (scheme) | 1 | `<clair-download-endpoint>` |
| `legacy/clair/OWNER_TODO.md` | Update endpoint (bare) | 1 | `<clair-download-endpoint>` |
| `legacy/clair/OWNER_TODO.md` | API endpoint (bare) | 2 | `<clair-api-endpoint>` |
| `legacy/clair/OWNER_TODO.md` | Website/app domain (bare) | 2 | `<clair-website>` |
| `legacy/clair/OWNER_TODO.md` | Local path with username | 1 | `<repo-root>\clair-server` |
| `legacy/clair/docs/agent_wave_brief.md` | Local path with username | 2 | `<repo-root>\...` |
| `legacy/clair/docs/branching_and_ci.md` | Private org/repo | 2 | `<private-org>/CLAIR` |
| `legacy/clair/docs/dev_commands.md` | GCP project id | 3 | `<gcp-project-id>` |
| `legacy/clair/docs/dev_commands.md` | Telemetry endpoint (bare) | 2 | `<clair-telemetry-endpoint>` |
| `legacy/clair/docs/dev_commands.md` | Local path with username (Windows) | 12 | `<repo-root>\...` |
| `legacy/clair/docs/dev_commands.md` | Local path with username (macOS) | 1 | `<repo-root>/...` |
| `legacy/clair/docs/entry_points.md` | Private org/repo | 1 | `<private-org>/CLAIR` |
| `legacy/clair/docs/gce_wave_brief.md` | GCP project id (incl. bucket names) | 4 | `<gcp-project-id>` |
| `legacy/clair/docs/gce_wave_brief.md` | GCP billing account id | 1 | `<billing-account-id>` |
| `legacy/clair/docs/hard_rules.md` | Private org/repo | 1 | `<private-org>/CLAIR` |
| `legacy/clair/docs/macos_next_build_runbook.md` | Download endpoint (scheme) | 2 | `<clair-download-endpoint>` |
| `legacy/clair/docs/macos_next_build_runbook.md` | Local path with username (macOS) | 6 | `<repo-root>/CLAIR`, `<repo-root>/clair-wt` |
| `legacy/clair/docs/macos_next_build_runbook.md` | Bare OS username in a `lsof` output line | 1 | `<owner>` |
| `legacy/clair/docs/onboarding.md` | Local path with username | 13 | `<repo-root>\CLAIR` |
| `legacy/clair/docs/packaging_design.md` | Download/update endpoints (bare) | 2 | `<clair-download-endpoint>` |
| `legacy/clair/docs/packaging_design.md` | Local path with username | 1 | `<repo-root>\CLAIR\...` |
| `legacy/clair/docs/project_structure.md` | Update endpoint (bare) | 1 | `<clair-download-endpoint>` |
| `legacy/clair/docs/project_structure.md` | Local path with username | 1 | `<repo-root>\CLAIR` |
| `legacy/clair/docs/README.md` | Private org/repo | 3 | `<private-org>/CLAIR` |
| `legacy/clair/docs/superpowers_plan_dataset_links_2026-09-03.md` | Local path with username | 2 | `<repo-root>\clair_corpus\` |
| `legacy/clair/docs/tests.md` | Private org/repo | 1 | `<private-org>/CLAIR` |
| `legacy/clair/docs/tests.md` | GCP project id | 1 | `<gcp-project-id>` |
| `legacy/clair/docs/tests.md` | Local path with username | 1 | `<repo-root>\CLAIR-wt\` |
| `legacy/clair/docs/threat_model.md` | Private org/repo (PR link) | 1 | `<private-org>/CLAIR` |
| `legacy/clair/docs/threat_model.md` | Telemetry endpoint (scheme) | 1 | `<clair-telemetry-endpoint>` |
| `legacy/clair/docs/threat_model.md` | API endpoint (scheme + bare) | 2 | `<clair-api-endpoint>` |
| `legacy/clair/docs/threat_model.md` | Download endpoint (bare) | 2 | `<clair-download-endpoint>` |
| `legacy/clair/docs/threat_model.md` | Update endpoint (bare) | 4 | `<clair-download-endpoint>` |
| `legacy/clair/docs/ToTest.md` | GCP project id | 1 | `<gcp-project-id>` |
| `legacy/clair/docs/ToTest.md` | Download endpoint (scheme) | 1 | `<clair-download-endpoint>` |
| `legacy/clair/docs/ToTest.md` | Website domain (scheme + UA string) | 2 | `<clair-website>` |
| `legacy/clair/docs/ToTest.md` | API endpoint (bare) | 1 | `<clair-api-endpoint>` |
| `legacy/clair/docs/ToTest.md` | Root website domain (Cloudflare zone name, bare) | 2 | `<clair-website>` |
| `legacy/clair/docs/ToTest.md` | Local path with username | 1 | `<repo-root>\AppData\Local\CLAIR\clair.exe` |
| `docs/migration/2026-09-19-clair-conventions-inventory.md` | Private org/repo | 9 | `<private-org>/CLAIR` |
| `docs/migration/2026-09-19-clair-conventions-inventory.md` | Private org bare (in a grep-pattern list and a prose sentence) | 2 | `<private-org>` |
| `docs/migration/2026-09-19-clair-conventions-inventory.md` | Local path with username (Windows) | 9 | `<repo-root>\...` |
| `docs/migration/2026-09-19-clair-conventions-inventory.md` | Local path with username (macOS) | 1 | `<repo-root>/CLAIR` |
| `docs/migration/2026-09-19-clair-conventions-inventory.md` | Path-slug derived from username (`<project-slug>` form) | 2 | `C--Users-<repo-root>-CLAIR` |
| `docs/migration/2026-09-19-clair-conventions-inventory.md` | Path-slug derived from username (`-Users-raaif-CLAIR` form) | 2 | `-Users-<repo-root>-CLAIR` |

Files checked with **zero** matches (no changes made): `legacy/clair/NEXT_SESSION.md`,
`legacy/clair/THIRD-PARTY-NOTICES.md`, `legacy/clair/docs/changelog.d/README.md`,
`legacy/clair/docs/encryption_at_rest.md`, `legacy/clair/docs/research/chat_declines_2026-09/README.md`,
`legacy/clair/docs/sprint_log.head.md`, `legacy/clair/docs/superpowers/specs/2026-08-08-file-model-and-ia-design.md`,
`legacy/clair/docs/ui_conventions.md`.

## Judgement calls

- **UCI dataset author citations kept** (`THIRD-PARTY-NOTICES.md`): names such as
  Kam Hamidieh, Paulo Cortez, Valentim Realinho, Mónica Vieira Martins, Jorge
  Machado, Luís Baptista, Beata Strack, Jonathan P. DeShazo, Chris Gennings,
  Juan L. Olmo, Sebastian Ventura, Krzysztof J. Cios, John N. Clore, and Oliver
  Roesler are published academic authors credited under the CC BY 4.0 licence
  terms of the UCI ML Repository datasets CLAIR redistributes. This is a
  required, public bibliographic attribution, not private personal data about
  someone the team interacted with — removing it would break the file's actual
  purpose (licence compliance) and these names are already public in the cited
  papers/dataset pages.
- **Statistical/technical literature citations kept**: "Kendall & Babington
  Smith 1939" (Kendall's W citation) and "Anthony Taylor, 'HPLC Solutions
  #126', sepscience.com" (a published methodology article used to validate a
  formula) in `docs/project_structure.md` are citations of public technical
  literature, not private individuals.
- **Synthetic test-fixture names kept**: "Jude Innes", "Ethan Marsh", "Finn
  Vance" in `docs/ToTest.md` are fabricated row values inside a synthetic test
  CSV (`clair_chat_probe.csv`) used to verify ground-truth math, not real
  people.
- **SHA-256 file checksum kept**: `0a3747be998de7dc51d3508e85a8b90090089f0a5e1a5198481a86c24b919638`
  in `OWNER_TODO.md` is the installer's published integrity checksum, not an
  account/identity reference, so it was left as-is per the "long hex is only a
  problem when it's an account id" instruction.
- **40-character git commit SHAs kept** throughout (`dev_commands.md`,
  the migration inventory's `Source commit`) per the explicit instruction that
  40-hex strings are git SHAs, not account ids.
- **Coincidental IPv4-shaped example value kept**: `"<ip-address>"` in
  `docs/project_structure.md` (the `query_totals_guard_plausibility.py` entry)
  is quoted as an example of a data value that happened to look like an IP
  address inside a test dataset column, not a real server address — left
  unredacted since it identifies no infrastructure.
- **Loopback/any-interface addresses kept**: `127.0.0.1` and `0.0.0.0`
  appear dozens of times as generic local dev-server bind addresses (FastAPI,
  Vite, Ollama). These are not private infrastructure and are near-universal
  in software docs; redacting them would remove technical meaning with no
  privacy benefit, so they were left as-is.
- **Vendor/product names kept**: "Coolify", "Hetzner" (incl. "Hetzner CX22"),
  "Render", "Cloudflare", "Resend", "Better Stack", "R2" are technology/vendor
  names, not hostnames or account identifiers, and the instructions say to
  keep technology names. Only literal endpoints, IPs, and account ids tied to
  them were redacted.
- **Internal service/repo codenames kept**: "clair-telemetry-server",
  "clair-license" (Coolify service name), "clair-server" (repo name, distinct
  from the redacted absolute path to it) are internal names for CLAIR's own
  companion services, not hostnames or account identifiers by themselves —
  kept as reasonable technical detail.
- **Env var / secret NAMES kept, no values found**: `AZURE_CLIENT_ID`,
  `RESEND_API_KEY`, `TELEMETRY_INGEST_URL`, `CLAIR_LICENSE_URL`,
  `TAURI_SIGNING_PRIVATE_KEY`, etc. appear only as variable names being
  discussed (e.g. "unset", "confirm on the service"), never with an actual
  secret value attached. Nothing to redact.
- **Internal dataset/file UUID kept**: `32a14e45-8429-4789-aea8-550f48d459dc`
  in `docs/ToTest.md` is a DuckDB row id for a specific test dataset used to
  describe a bug repro, not a cloud/account identifier.
- **Cloudflare `CF-Ray` trace id kept** (`OWNER_TODO.md`): `CF-Ray:
  a2245cd9…-IAD` is an already-partially-truncated, ephemeral per-request debug
  ID from a manual connectivity test, not an account identifier.
- **`<clair-download-endpoint>` mapped to `<clair-download-endpoint>`**
  (rather than a separate placeholder) since the task's placeholder list did
  not include a dedicated "update endpoint" option and the updater serves
  `latest.json`/build artifacts from the same first-party distribution
  infrastructure as the installer/lib-pack downloads.
- **Path-slug forms of the redacted local path** (`<project-slug>`,
  `-Users-raaif-CLAIR`, appearing in `docs/migration/...conventions-inventory.md`
  when describing `.claude/README.md` and `scripts/sync_memory.py`, both of
  which are out of scope themselves) were redacted to
  `C--Users-<repo-root>-CLAIR` / `-Users-<repo-root>-CLAIR` for consistency
  with the `<repo-root>` placeholder used elsewhere for this same absolute
  path, even though they use dashes instead of path separators.
- **Vanderbilt/VUMC/VUIT/eduroam references kept** throughout, per the
  instruction that Vanderbilt/VUMC institution names may stay.
- **Public URLs kept**: UCI dataset pages, DOIs, `github.com/ollama/...`,
  `github.com/tauri-apps/...`, `ollama.com`, `vega.github.io`, Google Fonts,
  World Bank/CDC data portals, `metadata.google.internal` and
  `compute.googleapis.com` (universal, non-project-specific GCP metadata/API
  hosts) were all left untouched as generic public references.

## Final re-scan (after redaction)

Ran across all 26 owned files (`legacy/clair/*.md`, `legacy/clair/docs/**`,
and this repo's migration inventory doc):

- Email regex: **0 matches**
- `<private-org>`: **0 matches**
- `<clair-domain>` (case-insensitive): **0 matches**
- GCP project id `clair-corpus-52g9xx` / billing account `011978-B3FAAF-92D41E`: **0 matches**
- Private gateway IP `<ip-address>`: **0 matches**
- Bare `raaif` username anywhere in scope: **0 matches**
- `onrender|render\.com|betterstack|resend\.[a-z]|cloudflare\.com/account|r2\.dev`: **0 matches**
- Remaining `https?://` URLs: only public/generic domains (UCI, DOI, GitHub
  public repos, Ollama, Vega, Google Fonts, World Bank, CDC, GCP's universal
  metadata/API hosts, redacted `<private-org>` placeholders, and CLAIR's own
  loopback addresses)
- Remaining 32+ hex strings: only 40-character git commit SHAs and one
  SHA-256 file checksum, both explicitly out of scope for redaction per the
  task instructions
