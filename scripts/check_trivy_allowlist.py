"""scripts/check_trivy_allowlist.py -- the accepted-CVE list expires itself (#255).

    python scripts/check_trivy_allowlist.py [path]        # default: .trivyignore.yaml

The release image scan (release.yml, `images` job) reads `.trivyignore.yaml`. Every accepted CVE must carry:

    vulnerabilities:
      - id: CVE-2026-12345
        statement: why it is accepted and what removes it (an issue number)
        expired_at: 2026-12-01

An entry whose `expired_at` is today or earlier FAILS this check (Trivy also stops honouring it, so the scan fails
too: the entry has to be re-justified or the dependency fixed). An expiry further than MAX_DAYS away fails, so an
acceptance can never be written as "forever". Only a `vulnerabilities:` section is allowed.

Hand-parsed (no PyYAML in the worker venv); the format above is the only one accepted.

Exit codes: 0 valid, 1 a problem or an unreadable file.
"""

from __future__ import annotations

import datetime as dt
import re
import sys
from pathlib import Path

MAX_DAYS = 90
DEFAULT_PATH = Path(__file__).resolve().parent.parent / ".trivyignore.yaml"
_ID_RE = re.compile(r"^(CVE-\d{4}-\d{4,}|GHSA(-[23456789cfghjmpqrvwx]{4}){3})$")
_DATE_RE = re.compile(r"^\d{4}-\d{2}-\d{2}$")
_FIELDS = ("id", "statement", "expired_at")


def _parse(text: str) -> tuple[list[dict[str, str]], list[str]]:
    entries: list[dict[str, str]] = []
    problems: list[str] = []
    saw_key = False
    for n, raw in enumerate(text.splitlines(), 1):
        line = raw.rstrip()
        if not line.strip() or line.lstrip().startswith("#"):
            continue
        if not line.startswith(" "):
            key = line.split("#", 1)[0].strip()
            if key == "vulnerabilities:" or key == "vulnerabilities: []":
                saw_key = True
            else:
                problems.append(f"line {n}: only a `vulnerabilities:` section is allowed, got {line.strip()!r}")
            continue
        body = line.strip()
        if body.startswith("- "):
            entries.append({})
            body = body[2:].strip()
        if not entries:
            problems.append(f"line {n}: a field before any `- id:` entry")
            continue
        name, sep, value = body.partition(":")
        if not sep or name not in _FIELDS:
            problems.append(f"line {n}: unknown or malformed field {body!r} (allowed: {', '.join(_FIELDS)})")
            continue
        entries[-1][name] = value.strip().strip("'\"")
    if not saw_key:
        problems.append("no `vulnerabilities:` key (write `vulnerabilities: []` for an empty list)")
    return entries, problems


def problems(text: str, today: dt.date) -> list[str]:
    entries, out = _parse(text)
    seen: set[str] = set()
    for i, entry in enumerate(entries, 1):
        label = entry.get("id") or f"entry {i}"
        for field in _FIELDS:
            if not entry.get(field):
                out.append(f"{label}: missing `{field}`")
        cve = entry.get("id", "")
        if cve and not _ID_RE.match(cve):
            out.append(f"{label}: id is not a CVE or GHSA id")
        if cve in seen:
            out.append(f"{label}: duplicate id")
        seen.add(cve)
        raw = entry.get("expired_at", "")
        if not raw:
            continue
        if not _DATE_RE.match(raw):
            out.append(f"{label}: expired_at {raw!r} is not YYYY-MM-DD")
            continue
        try:
            when = dt.date.fromisoformat(raw)
        except ValueError:
            out.append(f"{label}: expired_at {raw!r} is not a real date")
            continue
        if when <= today:
            out.append(f"{label}: expired on {raw}; fix the dependency or re-justify with a new date")
        elif (when - today).days > MAX_DAYS:
            out.append(f"{label}: expires in more than {MAX_DAYS} days ({raw}); an acceptance is never open-ended")
    return out


def main(argv: list[str] | None = None) -> int:
    args = sys.argv[1:] if argv is None else argv
    path = Path(args[0]) if args else DEFAULT_PATH
    try:
        text = path.read_text(encoding="utf-8")
    except OSError as exc:
        print(f"ERROR: cannot read {path} ({type(exc).__name__}). Check the path.")
        return 1
    found = problems(text, dt.date.today())
    for p in found:
        print(f"ERROR: {path.name}: {p} (issue 255). Edit .trivyignore.yaml.")
    if found:
        return 1
    print(f"OK: {path.name} has no expired or open-ended entry")
    return 0


if __name__ == "__main__":
    sys.exit(main())
