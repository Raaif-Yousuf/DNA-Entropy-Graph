"""scripts/triage_diagnostics.py -- read a job diagnostics bundle in seconds.

    python scripts/triage_diagnostics.py <bundle.zip> [<bundle2.zip> ...]
    python scripts/triage_diagnostics.py --data-dir            # every local run under %LOCALAPPDATA%\\DNAEntropyGraph\\runs
    python scripts/triage_diagnostics.py <bundle.zip> --full    # every progress line, no capping
    python scripts/triage_diagnostics.py <bundle.zip> --tail 200

A diagnostics bundle is the owner (or a field report) reaching for evidence
about one cloud or local job. The `fixing-a-bug` skill's own body names this
script explicitly: "the diagnostics zip is the field report" -- read it
before screenshots. It prints app version, worker version, image digest, the
`JobPhase` history, the `CloudError` classes seen, and the last N
`progress.jsonl` lines, because those are exactly the fields a bug report
needs and a human hand-reading a multi-line JSONL file will miss half of.

Deliberately NOT a gate. It reports and ranks; it never exits non-zero on a
FINDING (a failed job, a stuck stage, an error code), only on a genuine
operational failure (bad usage, an unreadable bundle, a target with no job
data in it at all) -- a false alarm here costs a real investigation, same
reasoning as `route_reachability.py`'s own report-not-gate design in the
donor conventions this repo's tooling is modelled on.

WHERE THE SCHEMA COMES FROM, AND THE COUPLING THIS FILE CARRIES
-----------------------------------------------------------------
The manifest/status/progress/result shape read here is
`docs/superpowers/specs/2026-09-18-appendix-b-cloud-design.md` sections
3.2-3.6, and the `dna_entropy.worker` subpackage that will actually WRITE
these files is issue #278 (P0 worker: add the worker subpackage), which is
**not implemented yet** as of this script's own port (issue #274). This
script is written against the DOCUMENTED schema, not a real implementation,
on purpose: the alternative is writing it after #278 lands and hoping nobody
needs to triage a job before then. Every literal field name/path below lives
in the `SCHEMA_FIELDS` block so that if #278 ships with a field renamed or
restructured, updating this script is finding and editing that one block,
not re-deriving field paths scattered through the report functions.
"""

from __future__ import annotations

import argparse
import json
import os
import re
import sys
import zipfile
from pathlib import Path
from typing import Any

# ---------------------------------------------------------------------------
# SCHEMA COUPLING (see module docstring). Every dotted path below is a
# literal transcription of Appendix B sections 3.2-3.4. `_dig()` reads one
# of these tuples out of a parsed JSON dict; nothing else in this file
# hand-rolls a `d.get("x", {}).get("y")` chain, so a schema change is a
# one-block edit.
# ---------------------------------------------------------------------------

# jobs/<jobId>/... under a cloud bucket (Appendix B section 3.2), or the
# equivalent local `store.root` directory for a `localdir` run. A bundle is
# "one job" when these live directly at the bundle root, and "many jobs"
# when they live under one subdirectory per job (see `_find_job_roots`).
JOB_FILES = {
    "manifest": "manifest.json",
    "status": "status.json",
    "progress": "progress.jsonl",
    "result": "result.json",
    "worker_log": "logs/worker.log",
    "startup_log": "logs/startup.log",
    "provenance": "output/provenance.json",
}

# manifest.json (schema 1) -- Appendix B section 3.3.
SCHEMA_FIELDS: dict[str, tuple[str, tuple[str, ...]]] = {
    "job_id": ("manifest", ("jobId",)),
    "created_at": ("manifest", ("createdAt",)),
    "app_version": ("manifest", ("createdBy", "appVersion")),
    "installation_id": ("manifest", ("createdBy", "installationId")),
    "manifest_worker_version": ("manifest", ("worker", "version")),
    "manifest_worker_image": ("manifest", ("worker", "image")),
    "predictor_model": ("manifest", ("predictor", "model")),
    "predictor_precision": ("manifest", ("predictor", "precision")),
    "max_run_seconds": ("manifest", ("limits", "maxRunSeconds")),
    "lifecycle_after_task": ("manifest", ("lifecycle", "afterTask")),
    "store_kind": ("manifest", ("store", "kind")),
    # status.json -- Appendix B section 3.4.
    "status_job_id": ("status", ("jobId",)),
    "stage": ("status", ("stage",)),
    "percent": ("status", ("percent",)),
    "detail": ("status", ("detail",)),
    "started_at": ("status", ("startedAt",)),
    "updated_at": ("status", ("updatedAt",)),
    "heartbeat_seq": ("status", ("heartbeatSeq",)),
    "status_worker_version": ("status", ("worker", "version")),
    "status_worker_image": ("status", ("worker", "image")),
    "vm_name": ("status", ("vm", "name")),
    "vm_zone": ("status", ("vm", "zone")),
    "vm_gpu": ("status", ("vm", "gpu")),
    "vm_driver": ("status", ("vm", "driver")),
    "error_code": ("status", ("error", "code")),
    "error_message": ("status", ("error", "message")),
    "error_detail": ("status", ("error", "detail")),
    "error_retriable": ("status", ("error", "retriable")),
    "error_remediation": ("status", ("error", "remediation")),
    # result.json -- Appendix B section 3.2's bucket-layout comment:
    # {status, inputs[], timing, gpu, error?}, written LAST.
    "result_status": ("result", ("status",)),
    "result_timing": ("result", ("timing",)),
    "result_gpu": ("result", ("gpu",)),
    "result_error_code": ("result", ("error", "code")),
}

# progress.jsonl -- one JSON object per line: {seq, ts, stage, level,
# percent, message, data}. Appendix B section 3.4.
PROGRESS_LINE_KEYS = ("seq", "ts", "stage", "level", "percent", "message", "data")

# The `JobPhase` state machine, in order -- Appendix B section 3.4. Used only
# to flag an OUT-OF-ORDER or UNRECOGNISED stage in a report; the report never
# refuses to show a stage this tuple does not know about (a documented list
# going stale must never make the tool blind to a real, newer stage).
JOB_STAGES: tuple[str, ...] = (
    "queued", "provisioning", "booting", "installing", "restoring-cache",
    "model-loading", "running", "uploading", "done", "failed", "cancelled",
    "idle", "finalizing",
)

# The `CloudError` taxonomy -- Appendix B section 7. Used only to flag a
# CODE this table does not recognise (worth a second look: either a new
# error class that needs a section 7 row, or a typo in the worker's own
# error-raising code); never used to filter or hide a code that IS
# recognised but looks unfamiliar to a human skimming the table.
KNOWN_CLOUD_ERROR_CODES: frozenset[str] = frozenset((
    "SIGNIN_EXPIRED", "CONSENT_INCOMPLETE", "NOT_PROJECT_OWNER", "PROJECT_QUOTA",
    "ORG_POLICY_BLOCK", "NO_BILLING", "BILLING_NO_PERMISSION", "FREE_TRIAL_NO_GPU",
    "API_DISABLED", "PERMISSION", "PERMISSION_ACTAS", "NO_GPU_QUOTA",
    "QUOTA_NOT_ELIGIBLE", "QUOTA_DENIED", "QUOTA_OTHER", "GPU_STOCKOUT",
    "NO_EXTERNAL_IP", "VM_BOOT_TIMEOUT", "GPU_NOT_VISIBLE", "IMAGE_PULL_FAILED",
    "MODEL_DOWNLOAD_FAILED", "MODEL_NEEDS_HOPPER", "MODEL_OOM", "INPUT_INVALID",
    "HEARTBEAT_LOST", "VM_DIED", "SPOT_PREEMPTED", "WORKER_CRASH",
    "RUN_TIME_LIMIT", "CANCELLED", "RETENTION_EXPIRED", "BUCKET_MISSING",
    "DOWNLOAD_FAILED", "NETWORK", "SPEND_CAP",
))

# `--data-dir`'s target: the local run-history directory (Appendix B section
# 3.3's `store: {"kind": "localdir", "root": "...\\runs\\<jobId>"}`), not
# CLAIR's `clair_data`. `%LOCALAPPDATA%` is read from the environment so
# this works on a real Windows machine; the hard-coded fallback only matters
# for a non-Windows dev/test box, which is where this script's own test
# suite runs it from.
def default_local_data_dir() -> Path:
    local_appdata = os.environ.get("LOCALAPPDATA")
    base = Path(local_appdata) if local_appdata else Path.home() / "AppData" / "Local"
    return base / "DNAEntropyGraph"


DEFAULT_TAIL = 50


def _dig(obj: dict[str, Any] | None, path: tuple[str, ...]) -> Any:
    cur: Any = obj or {}
    for key in path:
        if not isinstance(cur, dict):
            return None
        cur = cur.get(key)
    return cur


def field(bundle: dict[str, Any], name: str) -> Any:
    """Look up one SCHEMA_FIELDS entry against an already-loaded bundle dict
    (bundle["manifest"], bundle["status"], bundle["result"] are the parsed
    JSON documents, or {} if that file was absent)."""
    doc_name, path = SCHEMA_FIELDS[name]
    return _dig(bundle.get(doc_name), path)


def first(bundle: dict[str, Any], *names: str) -> Any:
    """The first non-None value among several SCHEMA_FIELDS lookups, in
    order -- e.g. prefer status.json's live worker version, fall back to
    manifest.json's if status.json is missing or not yet written."""
    for name in names:
        v = field(bundle, name)
        if v is not None:
            return v
    return None


# ---------------------------------------------------------------------------
# Bundle loading: a zip or a directory, containing either one job's files
# directly, or one subdirectory per job (matching the cloud bucket layout
# `jobs/<jobId>/...` or the local layout `runs/<jobId>/...`).
# ---------------------------------------------------------------------------

def _read_json(read_bytes) -> dict[str, Any]:
    try:
        return json.loads(read_bytes.decode("utf-8", "replace"))
    except (json.JSONDecodeError, UnicodeDecodeError):
        return {}


def _load_job_from_dir(root: Path) -> dict[str, Any]:
    bundle: dict[str, Any] = {"_files_present": []}
    for doc_name, rel in JOB_FILES.items():
        p = root / rel
        if not p.exists():
            continue
        bundle["_files_present"].append(rel)
        if rel.endswith(".jsonl"):
            continue  # progress is streamed separately, see _progress_lines
        try:
            bundle[doc_name] = json.loads(p.read_text(encoding="utf-8", errors="replace"))
        except json.JSONDecodeError:
            bundle[doc_name] = {}
    return bundle


def _progress_lines_from_dir(root: Path) -> list[str]:
    p = root / JOB_FILES["progress"]
    if not p.exists():
        return []
    return p.read_text(encoding="utf-8", errors="replace").splitlines()


def _find_job_roots_dir(root: Path) -> list[tuple[str, Path]]:
    """[(label, job_root_dir), ...]. A directory IS a job root if it
    contains manifest.json directly; a bundle may be one job root itself,
    or a tree with many (`jobs/<id>/manifest.json`, `runs/<id>/manifest.json`)."""
    if (root / "manifest.json").exists():
        return [(root.name, root)]
    found = sorted(root.rglob("manifest.json"))
    return [(p.parent.name, p.parent) for p in found]


def _load_bundle_dir(root: Path) -> list[tuple[str, dict[str, Any], list[str]]]:
    jobs = _find_job_roots_dir(root)
    if not jobs:
        # No manifest.json anywhere: still report whatever is directly at
        # the root, rather than refusing outright -- a partial bundle (say,
        # just a worker.log with no manifest yet) is still worth reading.
        return [(root.name, _load_job_from_dir(root), _progress_lines_from_dir(root))]
    return [(label, _load_job_from_dir(jroot), _progress_lines_from_dir(jroot)) for label, jroot in jobs]


def _load_bundle_zip(path: Path) -> list[tuple[str, dict[str, Any], list[str]]]:
    with zipfile.ZipFile(path) as z:
        names = [n for n in z.namelist() if not n.endswith("/")]

        # Group entries by "job directory": the path with the JOB_FILES
        # relative suffix stripped off. A flat zip (files at the archive
        # root) yields one group keyed "".
        by_job: dict[str, dict[str, str]] = {}
        for n in names:
            norm = n.replace("\\", "/")
            for doc_name, rel in JOB_FILES.items():
                if norm == rel or norm.endswith("/" + rel):
                    job_dir = norm[: -len(rel)].rstrip("/")
                    by_job.setdefault(job_dir, {})[doc_name] = n
                    break

        if not by_job:
            return [(path.name, {"_files_present": []}, [])]

        jobs: list[tuple[str, dict[str, Any], list[str]]] = []
        for job_dir, entries in sorted(by_job.items()):
            label = job_dir.rsplit("/", 1)[-1] if job_dir else path.name
            bundle: dict[str, Any] = {"_files_present": sorted(entries.values())}
            progress_lines: list[str] = []
            for doc_name, zname in entries.items():
                if doc_name == "progress":
                    progress_lines = z.read(zname).decode("utf-8", "replace").splitlines()
                    continue
                bundle[doc_name] = _read_json(z.read(zname))
            jobs.append((label, bundle, progress_lines))
        return jobs


def load_bundle(path: Path) -> list[tuple[str, dict[str, Any], list[str]]]:
    """[(job_label, bundle_dict, progress_lines), ...] for one target (a
    zip file or a directory), one entry per job found inside it."""
    if path.is_dir():
        return _load_bundle_dir(path)
    return _load_bundle_zip(path)


# ---------------------------------------------------------------------------
# Derived findings
# ---------------------------------------------------------------------------

def job_phase_history(bundle: dict[str, Any], progress_lines: list[str]) -> list[str]:
    """The distinct, in-order sequence of `stage` values this job passed
    through -- consecutive repeats collapsed (a `running` stage logs many
    progress lines; it is one phase, not N). Falls back to status.json's
    single current stage when there is no progress.jsonl to derive a
    history from."""
    stages: list[str] = []
    for line in progress_lines:
        try:
            obj = json.loads(line)
        except json.JSONDecodeError:
            continue
        stage = obj.get("stage")
        if stage and (not stages or stages[-1] != stage):
            stages.append(stage)
    if not stages:
        current = field(bundle, "stage")
        if current:
            stages = [current]
    return stages


def unrecognised_phases(phases: list[str]) -> list[str]:
    return [p for p in phases if p not in JOB_STAGES]


def cloud_error_classes(bundle: dict[str, Any], progress_lines: list[str]) -> list[str]:
    """Every distinct `CloudError` code seen: status.json's terminal error
    (the authoritative one), plus any `data.code` spotted on an
    `level: "error"` progress line (a transient error the worker logged and
    retried past, which status.json alone would never show since it only
    ever holds the CURRENT/terminal state)."""
    codes: list[str] = []

    terminal = field(bundle, "error_code")
    if terminal:
        codes.append(terminal)

    for line in progress_lines:
        try:
            obj = json.loads(line)
        except json.JSONDecodeError:
            continue
        if obj.get("level") != "error":
            continue
        data = obj.get("data")
        code = data.get("code") if isinstance(data, dict) else None
        if code and code not in codes:
            codes.append(code)

    result_code = field(bundle, "result_error_code")
    if result_code and result_code not in codes:
        codes.append(result_code)

    return codes


def _fmt_progress_line(raw: str) -> str:
    try:
        obj = json.loads(raw)
    except json.JSONDecodeError:
        return f"  (unparseable) {raw[:190]}"
    seq = obj.get("seq", "?")
    stage = obj.get("stage", "?")
    level = obj.get("level", "info")
    percent = obj.get("percent")
    pct = f"{percent:>5.1f}%" if isinstance(percent, (int, float)) else "      "
    message = obj.get("message", "")
    tag = f"[{level.upper()}]" if level and level != "info" else "      "
    return f"  #{seq:<5} {stage:<16}{pct} {tag} {message}"


# ---------------------------------------------------------------------------
# Reporting
# ---------------------------------------------------------------------------

def _report_job(target_label: str, job_label: str, bundle: dict[str, Any],
                 progress_lines: list[str], tail: int, full: bool) -> dict[str, Any]:
    header = f"{target_label}" + (f"  job={job_label}" if job_label and job_label != target_label else "")
    print("=" * 78)
    print(header)
    print("-" * 78)

    app_version = field(bundle, "app_version") or "?"
    worker_version = first(bundle, "status_worker_version", "manifest_worker_version") or "?"
    worker_image = first(bundle, "status_worker_image", "manifest_worker_image") or "?"
    job_id = first(bundle, "status_job_id", "job_id") or "?"
    print(f"  app version    : {app_version}")
    print(f"  worker version : {worker_version}")
    print(f"  worker image   : {worker_image}")
    print(f"  job id         : {job_id}")

    present = bundle.get("_files_present") or []
    if not present:
        print("  (no manifest.json/status.json/progress.jsonl/result.json found here)")

    stage = field(bundle, "stage")
    percent = field(bundle, "percent")
    updated_at = field(bundle, "updated_at")
    if stage:
        pct = f" ({percent:.1f}%)" if isinstance(percent, (int, float)) else ""
        print(f"  current stage  : {stage}{pct}   updated {updated_at or '?'}")

    vm_name = field(bundle, "vm_name")
    if vm_name:
        print(f"  vm             : {vm_name}  zone={field(bundle, 'vm_zone')}  "
              f"gpu={field(bundle, 'vm_gpu')}  driver={field(bundle, 'vm_driver')}")

    phases = job_phase_history(bundle, progress_lines)
    if phases:
        print("\n-- JobPhase history " + "-" * 57)
        print("  " + " -> ".join(phases))
        odd = unrecognised_phases(phases)
        if odd:
            print(f"  ! unrecognised stage name(s), not in the documented JobPhase list: {odd}")

    errors = cloud_error_classes(bundle, progress_lines)
    if errors:
        print("\n-- CloudError classes " + "-" * 55)
        for code in errors:
            flag = "" if code in KNOWN_CLOUD_ERROR_CODES else "  ! not in the documented taxonomy (section 7)"
            print(f"  * {code}{flag}")
        msg = field(bundle, "error_message")
        detail = field(bundle, "error_detail")
        remediation = field(bundle, "error_remediation")
        retriable = field(bundle, "error_retriable")
        if msg:
            print(f"    message     : {msg}")
        if detail:
            print(f"    detail      : {detail}")
        if remediation:
            print(f"    remediation : {remediation}")
        if retriable is not None:
            print(f"    retriable   : {retriable}")

    if progress_lines:
        shown = progress_lines if full else progress_lines[-tail:]
        omitted = 0 if full else max(0, len(progress_lines) - tail)
        print(f"\n-- progress.jsonl tail ({len(shown)} of {len(progress_lines)} line(s)) " + "-" * 20)
        if omitted:
            print(f"  ... {omitted} earlier line(s) omitted (use --full to see all)")
        for raw in shown:
            print(_fmt_progress_line(raw))

    result_status = field(bundle, "result_status")
    if result_status:
        timing = field(bundle, "result_timing")
        gpu = field(bundle, "result_gpu")
        print("\n-- result.json " + "-" * 62)
        print(f"  status: {result_status}" + (f"  timing={timing}" if timing else "")
              + (f"  gpu={gpu}" if gpu else ""))

    return {
        "app_version": app_version, "worker_version": worker_version,
        "worker_image": worker_image, "stage": stage, "updated_at": updated_at,
        "errors": errors, "phases": phases,
    }


def _delta(reports: list[tuple[str, dict[str, Any]]]) -> None:
    print("\n" + "=" * 78)
    print("DELTA across bundles (oldest first)")
    print("=" * 78)
    print("Multiple bundles for the same job are usually sent because something\n"
          "moved -- the stage advanced, stalled, or an error appeared.\n")
    for name, rep in reports:
        print(f"  {name}")
        print(f"    stage={rep.get('stage','?')}  updated={rep.get('updated_at','?')}  "
              f"errors={rep.get('errors') or '[]'}")

    first_rep, last_rep = reports[0][1], reports[-1][1]
    if first_rep.get("stage") == last_rep.get("stage") and first_rep.get("stage") not in (None, "?"):
        print(f"\n  STAGE DID NOT ADVANCE: still {last_rep.get('stage')!r} across every bundle. "
              "See section 3.5's per-stage deadlines -- this may be a hung worker.")
    if not first_rep.get("errors") and last_rep.get("errors"):
        print(f"\n  NEW ERROR appeared: {last_rep.get('errors')}")


def main() -> int:
    ap = argparse.ArgumentParser(
        description="Read a DNA-Entropy-Graph job diagnostics bundle (zip or directory) in seconds.",
    )
    ap.add_argument("bundles", nargs="*", type=Path,
                     help="diagnostics zip(s), or an unpacked bundle directory")
    ap.add_argument("--data-dir", action="store_true",
                     help="also triage every local run under %%LOCALAPPDATA%%\\DNAEntropyGraph\\runs\\")
    ap.add_argument("--data-dir-path", type=Path, default=None,
                     help="override the local data directory (default: %%LOCALAPPDATA%%\\DNAEntropyGraph)")
    ap.add_argument("--full", action="store_true",
                     help="print every progress.jsonl line instead of a capped tail")
    ap.add_argument("--tail", type=int, default=DEFAULT_TAIL,
                     help=f"how many trailing progress.jsonl lines to show (default {DEFAULT_TAIL})")
    args = ap.parse_args()

    targets: list[Path] = list(args.bundles)
    if args.data_dir or args.data_dir_path:
        data_dir = args.data_dir_path or default_local_data_dir()
        runs_dir = data_dir / "runs"
        if runs_dir.is_dir():
            found = sorted(p for p in runs_dir.iterdir() if p.is_dir())
            if found:
                targets.extend(found)
            else:
                print(f"no local runs under {runs_dir}", file=sys.stderr)
        else:
            print(f"no local data dir at {runs_dir}", file=sys.stderr)

    if not targets:
        ap.print_help()
        return 2

    all_reports: list[tuple[str, dict[str, Any]]] = []
    any_loaded = False
    for t in targets:
        if not t.exists():
            print(f"skipping missing {t}", file=sys.stderr)
            continue
        try:
            jobs = load_bundle(t)
        except (zipfile.BadZipFile, OSError) as exc:
            print(f"skipping unreadable {t}: {exc}", file=sys.stderr)
            continue
        for job_label, bundle, progress_lines in jobs:
            any_loaded = True
            rep = _report_job(t.name, job_label, bundle, progress_lines, args.tail, args.full)
            all_reports.append((f"{t.name}:{job_label}" if job_label else t.name, rep))

    if not any_loaded:
        print("no readable diagnostics data in any target", file=sys.stderr)
        return 1

    if len(all_reports) > 1:
        _delta(all_reports)

    print("\nThis is a REPORT, not a gate. It never fails a build: a false alarm here "
          "costs a real investigation.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
