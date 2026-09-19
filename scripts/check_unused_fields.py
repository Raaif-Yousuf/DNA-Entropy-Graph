"""Fail if a dataclass field in the worker is set but never read.

WHY THIS IS A GUARD AND NOT A CONVENTION
----------------------------------------
"Wired to nothing" is this project's named, recurring bug class, and one of its
shapes is the cheapest to write and the hardest to see: a setting that is
parsed, validated, stored, serialized, round-tripped through a schema, covered
by a test that asserts it round-trips, and never once *read* by the code that
would act on it. Everything about it looks finished. The only thing missing is
the behaviour.

MEASURED 2026-09-19: two of them were found by hand, on the same afternoon, in
the same file. `JobManifest.outputs` was parsed and did not suppress any writer
(#304). `JobManifest.fasta_records` was parsed and did not stop the reader
processing every record (#306). Both had passing tests. Both had schema
entries. Neither did anything.

Found by hand means the next one will be found by hand too, or not at all. This
guard makes the check mechanical: every dataclass field in the worker package
must be read somewhere, or be listed in the allowlist with a reason.

WHAT COUNTS AS A READ
---------------------
An attribute load (`cfg.max_len`, `self.outputs`) anywhere in the package, or a
`getattr(x, "outputs")` / `hasattr(x, "outputs")`, is a read. An assignment
(`cfg.outputs = ...`) and a keyword argument at construction
(`JobManifest(outputs=...)`) are NOT: those are writes, and a field with only
writes is exactly the bug.

Reads from inside a **serialization** method (`to_dict`, `to_json`, `__repr__`,
...) are counted separately and reported as a weaker finding
(`SERIALIZED-ONLY`), because a field that only ever travels back out to JSON
still does nothing. That is the false negative this guard would otherwise have:
a hand-written `to_dict` mentioning `self.fasta_records` would have made #306
look consumed.

WHAT THIS GUARD CANNOT SEE
--------------------------
It matches attribute reads **by name**, not by type, because Python attribute
access is not statically resolvable here. Two consequences, and the second one
is the one to remember:

- A field consumed only by the C# app, only by a template, or only through
  `dataclasses.asdict(obj)` reads as unused. That is a false positive, and it
  is what the allowlist is for.
- A field whose name happens to match a read attribute on **any other class**
  is silently counted as read. That is a false *negative*, and it is invisible.
  MEASURED 2026-09-19: `WindowPlan.context` was set and never read, and this
  guard missed it, because `.context` also names a field on `SinglePassResult`
  that is read. `StoreSpec.bucket`, `.prefix` and `.root` are masked the same
  way by `GcsBlobstore`/`LocalBlobstore`'s same-named attributes.

So a clean run means "no field with a name nothing reads", not "no unread
field". The guard is a lower bound on the problem and was still worth writing:
its first run found nine real ones.

Reads are collected from the package **and** from `scripts/`, because a
generator such as `scripts/gen_manifest_schema.py` legitimately consumes a
field (`ErrorCodeSpec.raised_by`) that nothing in the package itself touches.

THE ALLOWLIST
-------------
`scripts/unused_fields_allowlist.json` maps `Class.field` to a reason string.
`Class.*` covers every field of one class, which is what an output document
serialized wholesale with `dataclasses.asdict()` needs: its reader is the C#
app, not this package, and listing eleven fields with the same sentence teaches
nobody anything.

It is checked in both directions: an entry whose field is now read, or whose
class or field no longer exists, fails too, so the file cannot rot into a list
of things that used to be true.

USAGE
-----
    python scripts/check_unused_fields.py             # check the worker package
    python scripts/check_unused_fields.py --json      # machine-readable findings
    python scripts/check_unused_fields.py --self-test

Exit code 0 when clean, 1 on any finding. Run from the repo root.
"""

from __future__ import annotations

import argparse
import ast
import json
import sys
import tempfile
from dataclasses import dataclass, field
from pathlib import Path

PACKAGE_ROOT = Path("worker/src/dna_entropy")
ALLOWLIST_PATH = Path("scripts/unused_fields_allowlist.json")

# Directories scanned for READS only, never for field definitions. A generator
# outside the package can be a field's only legitimate consumer.
EXTRA_READ_ROOTS = (Path("scripts"),)

# Methods whose body is about turning the object into bytes, not about acting on
# it. A read in here keeps a field alive on the wire without giving it any
# behaviour, so it is reported rather than accepted.
SERIALIZATION_METHODS = {
    "to_dict",
    "to_json",
    "as_dict",
    "asdict",
    "to_manifest",
    "to_payload",
    "serialize",
    "dump",
    "__repr__",
    "__str__",
}

DATACLASS_DECORATORS = {"dataclass", "dataclasses.dataclass"}


@dataclass
class FieldDef:
    cls: str
    name: str
    path: str
    line: int

    @property
    def key(self) -> str:
        return f"{self.cls}.{self.name}"


@dataclass
class Finding:
    key: str
    kind: str  # "UNREAD" or "SERIALIZED-ONLY"
    path: str
    line: int
    detail: str


@dataclass
class Scan:
    fields: list[FieldDef] = field(default_factory=list)
    real_reads: set[str] = field(default_factory=set)
    serialization_reads: set[str] = field(default_factory=set)


def _decorator_names(node: ast.ClassDef) -> set[str]:
    names: set[str] = set()
    for dec in node.decorator_list:
        target = dec.func if isinstance(dec, ast.Call) else dec
        if isinstance(target, ast.Name):
            names.add(target.id)
        elif isinstance(target, ast.Attribute):
            names.add(ast.unparse(target))
    return names


class _Collector(ast.NodeVisitor):
    """One pass over one module: dataclass fields, and every attribute read."""

    def __init__(self, scan: Scan, rel_path: str, collect_fields: bool = True) -> None:
        self.scan = scan
        self.rel_path = rel_path
        self.collect_fields = collect_fields
        self._method_stack: list[str] = []

    def visit_ClassDef(self, node: ast.ClassDef) -> None:
        if self.collect_fields and DATACLASS_DECORATORS & _decorator_names(node):
            for stmt in node.body:
                # A dataclass field is an annotated assignment at class level. A
                # bare `x = 5` is a plain class attribute, and a ClassVar is not
                # a field at all.
                if isinstance(stmt, ast.AnnAssign) and isinstance(stmt.target, ast.Name):
                    annotation = ast.unparse(stmt.annotation)
                    if annotation.startswith(("ClassVar", "typing.ClassVar", "t.ClassVar")):
                        continue
                    self.scan.fields.append(
                        FieldDef(node.name, stmt.target.id, self.rel_path, stmt.lineno)
                    )
        self.generic_visit(node)

    def visit_FunctionDef(self, node: ast.FunctionDef) -> None:
        self._method_stack.append(node.name)
        self.generic_visit(node)
        self._method_stack.pop()

    visit_AsyncFunctionDef = visit_FunctionDef  # type: ignore[assignment]

    def _record(self, name: str) -> None:
        if any(m in SERIALIZATION_METHODS for m in self._method_stack):
            self.scan.serialization_reads.add(name)
        else:
            self.scan.real_reads.add(name)

    def visit_Attribute(self, node: ast.Attribute) -> None:
        if isinstance(node.ctx, ast.Load):
            self._record(node.attr)
        self.generic_visit(node)

    def visit_Call(self, node: ast.Call) -> None:
        # Reflective access: getattr(x, "field"), hasattr(x, "field").
        if isinstance(node.func, ast.Name) and node.func.id in {"getattr", "hasattr"}:
            if len(node.args) >= 2 and isinstance(node.args[1], ast.Constant):
                value = node.args[1].value
                if isinstance(value, str):
                    self._record(value)
        self.generic_visit(node)


def _scan_into(scan: Scan, root: Path, collect_fields: bool) -> None:
    for path in sorted(root.rglob("*.py")):
        rel = path.as_posix()
        try:
            tree = ast.parse(path.read_text(encoding="utf-8"), filename=rel)
        except (SyntaxError, UnicodeDecodeError):
            # A read-only root can hold anything; a file we cannot parse simply
            # contributes no reads. Never let it fail the whole guard.
            continue
        _Collector(scan, rel, collect_fields=collect_fields).visit(tree)


def scan_package(root: Path, extra_read_roots: tuple[Path, ...] = ()) -> Scan:
    """Fields come from ``root`` only; reads come from ``root`` and every extra root."""
    scan = Scan()
    _scan_into(scan, root, collect_fields=True)
    for extra in extra_read_roots:
        if extra.exists() and extra.resolve() != root.resolve():
            _scan_into(scan, extra, collect_fields=False)
    return scan


def find_findings(scan: Scan) -> list[Finding]:
    findings: list[Finding] = []
    for fd in scan.fields:
        if fd.name in scan.real_reads:
            continue
        if fd.name in scan.serialization_reads:
            findings.append(
                Finding(
                    fd.key,
                    "SERIALIZED-ONLY",
                    fd.path,
                    fd.line,
                    "read only inside a serialization method, so it travels out to "
                    "JSON and back without ever changing what the worker does",
                )
            )
            continue
        findings.append(
            Finding(fd.key, "UNREAD", fd.path, fd.line, "never read anywhere in the package")
        )
    return findings


def load_allowlist(path: Path) -> dict[str, str]:
    if not path.exists():
        return {}
    data = json.loads(path.read_text(encoding="utf-8"))
    entries = data.get("allowed", data)
    return {k: v for k, v in entries.items() if not k.startswith("_")}


def _allowlist_hit(key: str, allowed: dict[str, str]) -> str | None:
    """The allowlist entry covering ``key``: the exact one, else its ``Class.*``."""
    if key in allowed:
        return key
    wildcard = key.rsplit(".", 1)[0] + ".*"
    return wildcard if wildcard in allowed else None


def check(
    root: Path,
    allowlist_path: Path,
    extra_read_roots: tuple[Path, ...] = (),
) -> tuple[list[Finding], list[str]]:
    """Return (findings that are not allowlisted, stale allowlist entries)."""
    if not root.exists():
        raise SystemExit(f"ERROR: package root not found: {root}")
    scan = scan_package(root, extra_read_roots)
    if not scan.fields:
        # A guard that passes because it found nothing to check is not passing.
        raise SystemExit(f"ERROR: no dataclass fields found under {root} -- the guard is broken")
    allowed = load_allowlist(allowlist_path)
    findings = find_findings(scan)
    flagged = {f.key for f in findings}
    known_keys = {fd.key for fd in scan.fields}
    known_classes = {fd.cls for fd in scan.fields}
    flagged_classes = {k.rsplit(".", 1)[0] for k in flagged}

    stale: list[str] = []
    for key in sorted(allowed):
        cls, name = key.rsplit(".", 1)
        if name == "*":
            if cls in flagged_classes:
                continue
            why = "no longer exists" if cls not in known_classes else "has no unread field left"
            stale.append(f"{key} -- {why}")
            continue
        if key in flagged:
            continue
        stale.append(f"{key} -- {'no longer exists' if key not in known_keys else 'is read now'}")

    remaining = [f for f in findings if _allowlist_hit(f.key, allowed) is None]
    return remaining, stale


def _print_report(findings: list[Finding], stale: list[str]) -> None:
    for f in findings:
        print(f"::error title=dataclass field set but never read::{f.detail}")
        print(f"{f.path}:{f.line}: {f.kind} {f.key} -- {f.detail}")
    for s in stale:
        print("::error title=stale allowlist entry::Delete it, or say why it is still needed.")
        print(f"{ALLOWLIST_PATH}: STALE {s}")


def self_test() -> int:
    """Break the thing this guard protects, deliberately, and watch it say no."""
    failures = 0

    def check_case(name: str, actual: object, expected: object) -> None:
        nonlocal failures
        if actual != expected:
            print(f"FAIL: {name} -> got {actual!r}, expected {expected!r}")
            failures += 1
        else:
            print(f"ok: {name}")

    with tempfile.TemporaryDirectory() as tmp:
        root = Path(tmp) / "pkg"
        root.mkdir()
        (root / "models.py").write_text(
            "from dataclasses import dataclass\n"
            "from typing import ClassVar\n"
            "\n"
            "@dataclass\n"
            "class Job:\n"
            "    consumed: int = 0\n"
            "    only_serialized: int = 0\n"
            "    never_read: int = 0\n"
            "    version: ClassVar[int] = 1\n"
            "\n"
            "    def to_dict(self):\n"
            "        return {'a': self.only_serialized}\n",
            encoding="utf-8",
        )
        (root / "use.py").write_text(
            "from .models import Job\n"
            "\n"
            "def go(job: Job) -> int:\n"
            "    job.never_read = 5          # a write is not a read\n"
            "    other = Job(never_read=6)   # nor is a keyword argument\n"
            "    return job.consumed + other.consumed\n",
            encoding="utf-8",
        )

        empty_allowlist = Path(tmp) / "none.json"
        empty_allowlist.write_text("{}", encoding="utf-8")

        findings, stale = check(root, empty_allowlist)
        by_key = {f.key: f.kind for f in findings}

        check_case("a field with only a write and a kwarg is UNREAD", by_key.get("Job.never_read"), "UNREAD")
        check_case("a field read only in to_dict is SERIALIZED-ONLY", by_key.get("Job.only_serialized"), "SERIALIZED-ONLY")
        check_case("a genuinely consumed field is not flagged", "Job.consumed" in by_key, False)
        check_case("a ClassVar is not a dataclass field", "Job.version" in by_key, False)
        check_case("exactly two findings", len(findings), 2)
        check_case("nothing is stale against an empty allowlist", stale, [])

        full_allowlist = Path(tmp) / "all.json"
        full_allowlist.write_text(
            json.dumps(
                {
                    "allowed": {
                        "Job.never_read": "reason",
                        "Job.only_serialized": "reason",
                        "Job.consumed": "reason",
                    }
                }
            ),
            encoding="utf-8",
        )
        findings2, stale2 = check(root, full_allowlist)
        check_case("allowlisted findings are suppressed", findings2, [])
        check_case("an allowlist entry for a field that IS read is stale", len(stale2), 1)
        check_case("the stale entry names the read field", stale2[0].startswith("Job.consumed"), True)

        # A Class.* entry covers every field of that class, and goes stale the
        # moment the class has nothing flagged left.
        wildcard = Path(tmp) / "wild.json"
        wildcard.write_text(json.dumps({"allowed": {"Job.*": "wire format"}}), encoding="utf-8")
        findings3, stale3 = check(root, wildcard)
        check_case("Class.* suppresses every field of that class", findings3, [])
        check_case("a live Class.* entry is not stale", stale3, [])

        absent = Path(tmp) / "absent.json"
        absent.write_text(json.dumps({"allowed": {"Missing.*": "gone"}}), encoding="utf-8")
        _, stale4 = check(root, absent)
        check_case("a Class.* for a class that does not exist is stale", len(stale4), 1)

        # A read in an extra root counts, which is how a generator outside the
        # package keeps a field alive.
        gen_root = Path(tmp) / "gen"
        gen_root.mkdir()
        (gen_root / "render.py").write_text(
            "def render(job):" + chr(10) + "    return job.never_read" + chr(10),
            encoding="utf-8",
        )
        findings5, _ = check(root, empty_allowlist, extra_read_roots=(gen_root,))
        keys5 = {f.key for f in findings5}
        check_case("a read in an extra root clears the finding", "Job.never_read" in keys5, False)
        check_case("an extra root does not add fields of its own", len(findings5), 1)

    if failures:
        print(f"check_unused_fields: self-test FAILED ({failures} case(s)).")
        return 1
    print("check_unused_fields: self-test passed (14 cases).")
    return 0


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter
    )
    parser.add_argument("--self-test", action="store_true", help="prove the guard can fail")
    parser.add_argument("--json", action="store_true", help="print findings as JSON")
    parser.add_argument("--root", default=str(PACKAGE_ROOT), help="package root to scan")
    args = parser.parse_args(argv)

    if args.self_test:
        return self_test()

    findings, stale = check(Path(args.root), ALLOWLIST_PATH, EXTRA_READ_ROOTS)

    if args.json:
        print(
            json.dumps(
                {
                    "findings": [f.__dict__ for f in findings],
                    "stale_allowlist_entries": stale,
                },
                indent=2,
            )
        )
    else:
        _print_report(findings, stale)

    if findings or stale:
        print(
            f"check_unused_fields: {len(findings)} field(s) set but never read, "
            f"{len(stale)} stale allowlist entry/entries."
        )
        print(
            "Either make the field do something, delete it, or add it to "
            f"{ALLOWLIST_PATH} with a reason a human wrote."
        )
        return 1

    print("check_unused_fields: clean -- every dataclass field in the worker is read somewhere.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
