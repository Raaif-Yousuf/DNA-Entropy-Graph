"""scripts/check_manifest_spec_reads.py -- every manifest spec field is READ, resolved by class (#432).

    python scripts/check_manifest_spec_reads.py
    python scripts/check_manifest_spec_reads.py --json
    python scripts/check_manifest_spec_reads.py --self-test

WHY A SECOND GUARD NEXT TO check_unused_fields.py
-------------------------------------------------
check_unused_fields.py matches attribute reads by NAME, so a field whose name matches a
read attribute on any other class is silently counted as read. MEASURED 2026-09-19:
`AnalysisSpec.stride` was parsed and read by nothing, while `DirectionResult.stride` and
`WindowPlan.stride` are read constantly, so the name-based guard reported clean and a
manifest declaring a disagreeing stride was silently discarded (#345). The manifest spec
classes are the one place where closing that hole is cheap: a small closed set, all parsed
from one document, and every field is a promise the app makes to the worker.

HOW THIS ONE DIFFERS
--------------------
It resolves the RECEIVER of every attribute read to a class before counting it, with a small
flow-insensitive resolver, and attributes the read to `DeclaringClass.field` only when the
receiver is known to be that class. The spec classes are every `@dataclass` in
`worker/src/dna_entropy/worker/manifest.py`; a new spec class is picked up automatically.

Receiver types come from, in this order: `self` inside a spec class; a parameter annotation
(`spec: InputSpec`); a field annotation followed through a chain (`self.analysis.window`,
because `JobManifest.analysis: AnalysisSpec`); `list[X]` fields iterated by a for loop or
comprehension (`for i in m.inputs`); a local assigned from one of those, from `Spec(...)`, from
`Spec.parse(...)`/`.from_dict(...)` (via the method's return annotation) or from a package
function whose return annotation is a spec class. Reads, not writes: an assignment or a
constructor keyword is not a read. Reads inside serialization methods are reported separately
(`SERIALIZED-ONLY`), as in check_unused_fields.py.

WHAT IT CANNOT SEE
------------------
A receiver it cannot type (an unannotated parameter, a value from a call it cannot follow)
contributes no read, so the field shows as UNREAD and the fix is an annotation, not an
allowlist entry. `getattr(x, "field")` with an untyped `x` is counted by name (conservative:
it can only hide a finding, never invent one). A field consumed only through
`dataclasses.fields()`/`asdict()` or only by the C# app reads as unused; that is what
`scripts/manifest_spec_reads_allowlist.json` is for. It is checked in both directions: a stale
entry fails the run.

NOTE ON #345: the stride refusal in `AnalysisSpec.from_dict` compares a LOCAL variable, so the
attribute `AnalysisSpec.stride` is still read by nothing and is allowlisted with that reason.
Reverting the refusal does not change this guard's verdict; what this guard prevents is the
state where nobody knew the field was unread at all.

Exit codes: 0 clean, 1 a finding or a stale allowlist entry, 2 bad usage / broken guard.
"""

from __future__ import annotations

import argparse
import ast
import json
import sys
import tempfile
from dataclasses import dataclass, field
from pathlib import Path

import check_unused_fields as cuf

SPEC_MODULE = Path("worker/src/dna_entropy/worker/manifest.py")
READ_ROOTS = (Path("worker/src/dna_entropy"),)
ALLOWLIST_PATH = Path("scripts/manifest_spec_reads_allowlist.json")

_LIST_NAMES = {"list", "List", "Sequence", "tuple", "Tuple", "set", "frozenset", "Iterable"}


@dataclass(frozen=True)
class TypeRef:
    cls: str
    is_list: bool = False


@dataclass
class SpecScan:
    fields: dict[str, cuf.FieldDef] = field(default_factory=dict)
    field_types: dict[str, dict[str, TypeRef | None]] = field(default_factory=dict)
    method_returns: dict[tuple[str, str], TypeRef] = field(default_factory=dict)
    real_reads: set[str] = field(default_factory=set)
    serialization_reads: set[str] = field(default_factory=set)


# ---------------------------------------------------------------------------
# Annotation and expression typing
# ---------------------------------------------------------------------------


def annotation_type(node: ast.expr | None, specs: set[str]) -> TypeRef | None:
    if node is None:
        return None
    if isinstance(node, ast.Constant) and isinstance(node.value, str):
        try:
            return annotation_type(ast.parse(node.value, mode="eval").body, specs)
        except SyntaxError:
            return None
    if isinstance(node, ast.Name):
        return TypeRef(node.id) if node.id in specs else None
    if isinstance(node, ast.Attribute):
        return TypeRef(node.attr) if node.attr in specs else None
    if isinstance(node, ast.BinOp) and isinstance(node.op, ast.BitOr):
        return annotation_type(node.left, specs) or annotation_type(node.right, specs)
    if isinstance(node, ast.Subscript):
        base = node.value
        base_name = base.id if isinstance(base, ast.Name) else base.attr if isinstance(base, ast.Attribute) else ""
        inner = node.slice
        if isinstance(inner, ast.Tuple) and inner.elts:
            inner = inner.elts[0]
        if base_name in _LIST_NAMES:
            element = annotation_type(inner, specs)
            return TypeRef(element.cls, True) if element and not element.is_list else None
        if base_name == "Optional":
            return annotation_type(inner, specs)
    return None


class _Typer:
    def __init__(self, scan: SpecScan, specs: set[str], func_returns: dict[str, TypeRef]) -> None:
        self.scan = scan
        self.specs = specs
        self.func_returns = func_returns

    def type_of(self, node: ast.AST, env: dict[str, TypeRef | None]) -> TypeRef | None:
        if isinstance(node, ast.Name):
            return env.get(node.id)
        if isinstance(node, ast.Attribute):
            base = self.type_of(node.value, env)
            if base and not base.is_list:
                return self.scan.field_types.get(base.cls, {}).get(node.attr)
            return None
        if isinstance(node, ast.Subscript):
            base = self.type_of(node.value, env)
            return TypeRef(base.cls) if base and base.is_list else None
        if isinstance(node, ast.Call):
            func = node.func
            if isinstance(func, ast.Name):
                if func.id in self.specs:
                    return TypeRef(func.id)
                return self.func_returns.get(func.id)
            if isinstance(func, ast.Attribute) and isinstance(func.value, ast.Name) and func.value.id in self.specs:
                return self.scan.method_returns.get((func.value.id, func.attr))
        return None

    def bind(self, env: dict[str, TypeRef | None], name: str, value: TypeRef | None) -> None:
        if value is None:
            return
        if name in env and env[name] != value:
            env[name] = None  # bound to two different types: ambiguous, so unresolved
        else:
            env[name] = value

    def collect_bindings(self, body_owner: ast.AST, env: dict[str, TypeRef | None]) -> None:
        for _ in range(2):  # a second pass resolves `a = b.x` written before `b = ...` textually
            for node in ast.walk(body_owner):
                if isinstance(node, ast.Assign):
                    value = self.type_of(node.value, env)
                    for target in node.targets:
                        if isinstance(target, ast.Name):
                            self.bind(env, target.id, value)
                elif isinstance(node, ast.AnnAssign) and isinstance(node.target, ast.Name):
                    declared = annotation_type(node.annotation, self.specs)
                    self.bind(env, node.target.id, declared or (self.type_of(node.value, env) if node.value else None))
                elif isinstance(node, (ast.For, ast.AsyncFor, ast.comprehension)) and isinstance(node.target, ast.Name):
                    iterated = self.type_of(node.iter, env)
                    if iterated and iterated.is_list:
                        self.bind(env, node.target.id, TypeRef(iterated.cls))


class _ReadCollector(ast.NodeVisitor):
    def __init__(self, typer: _Typer, scan: SpecScan) -> None:
        self.typer = typer
        self.scan = scan
        self.env_stack: list[dict[str, TypeRef | None]] = []
        self.class_stack: list[str] = []
        self.method_stack: list[str] = []

    @property
    def env(self) -> dict[str, TypeRef | None]:
        return self.env_stack[-1]

    def run(self, tree: ast.Module) -> None:
        module_env: dict[str, TypeRef | None] = {}
        self.typer.collect_bindings(tree, module_env)
        self.env_stack.append(module_env)
        self.visit(tree)

    def visit_ClassDef(self, node: ast.ClassDef) -> None:
        self.class_stack.append(node.name)
        self.generic_visit(node)
        self.class_stack.pop()

    def visit_FunctionDef(self, node: ast.FunctionDef) -> None:
        env = dict(self.env)
        owner = self.class_stack[-1] if self.class_stack else None
        args = [*node.args.posonlyargs, *node.args.args, *node.args.kwonlyargs]
        for index, arg in enumerate(args):
            declared = annotation_type(arg.annotation, self.typer.specs)
            if declared:
                env[arg.arg] = declared
            elif index == 0 and owner in self.typer.specs and arg.arg == "self":
                env[arg.arg] = TypeRef(owner)
            elif arg.arg in env:
                del env[arg.arg]  # a parameter shadows the enclosing binding
        self.typer.collect_bindings(node, env)
        self.env_stack.append(env)
        self.method_stack.append(node.name)
        self.generic_visit(node)
        self.method_stack.pop()
        self.env_stack.pop()

    visit_AsyncFunctionDef = visit_FunctionDef  # type: ignore[assignment]

    def _record(self, keys: list[str]) -> None:
        target = (
            self.scan.serialization_reads
            if any(m in cuf.SERIALIZATION_METHODS for m in self.method_stack)
            else self.scan.real_reads
        )
        target.update(keys)

    def visit_Attribute(self, node: ast.Attribute) -> None:
        if isinstance(node.ctx, ast.Load):
            base = self.typer.type_of(node.value, self.env)
            if base and not base.is_list and node.attr in self.scan.field_types.get(base.cls, {}):
                self._record([f"{base.cls}.{node.attr}"])
        self.generic_visit(node)

    def visit_Call(self, node: ast.Call) -> None:
        is_reflective = isinstance(node.func, ast.Name) and node.func.id in {"getattr", "hasattr"}
        if is_reflective and len(node.args) >= 2 and isinstance(node.args[1], ast.Constant) and isinstance(node.args[1].value, str):
            name = node.args[1].value
            base = self.typer.type_of(node.args[0], self.env)
            if base and not base.is_list:
                if name in self.scan.field_types.get(base.cls, {}):
                    self._record([f"{base.cls}.{name}"])
            else:  # untyped receiver: count by name (can only hide a finding, never invent one)
                self._record([f"{c}.{name}" for c, fields in self.scan.field_types.items() if name in fields])
        self.generic_visit(node)


# ---------------------------------------------------------------------------
# Scanning
# ---------------------------------------------------------------------------


def _load_specs(spec_module: Path) -> tuple[SpecScan, set[str], ast.Module]:
    rel = spec_module.as_posix()
    tree = ast.parse(spec_module.read_text(encoding="utf-8"), filename=rel)
    classes = [
        node for node in tree.body if isinstance(node, ast.ClassDef) and cuf.DATACLASS_DECORATORS & cuf._decorator_names(node)
    ]
    specs = {node.name for node in classes}
    scan = SpecScan()
    for node in classes:
        scan.field_types[node.name] = {}
        for stmt in node.body:
            if isinstance(stmt, ast.AnnAssign) and isinstance(stmt.target, ast.Name):
                annotation = ast.unparse(stmt.annotation)
                if annotation.startswith(("ClassVar", "typing.ClassVar", "t.ClassVar")):
                    continue
                scan.fields[f"{node.name}.{stmt.target.id}"] = cuf.FieldDef(node.name, stmt.target.id, rel, stmt.lineno)
                scan.field_types[node.name][stmt.target.id] = annotation_type(stmt.annotation, specs)
    for node in classes:  # method return annotations, for `Spec.parse(...)` style calls
        for stmt in node.body:
            if isinstance(stmt, (ast.FunctionDef, ast.AsyncFunctionDef)):
                returned = annotation_type(stmt.returns, specs)
                if returned:
                    scan.method_returns[(node.name, stmt.name)] = returned
    return scan, specs, tree


def scan_specs(spec_module: Path, read_roots: list[Path]) -> SpecScan:
    scan, specs, spec_tree = _load_specs(spec_module)
    if not scan.fields:
        raise SystemExit(f"ERROR: no dataclass fields found in {spec_module} -- the guard is broken")

    parsed: list[tuple[str, ast.Module]] = [(spec_module.as_posix(), spec_tree)]
    seen = {spec_module.resolve()}
    for root in read_roots:
        for path in sorted(root.rglob("*.py")):
            if path.resolve() in seen:
                continue
            seen.add(path.resolve())
            try:
                parsed.append((path.as_posix(), ast.parse(path.read_text(encoding="utf-8"), filename=path.as_posix())))
            except (SyntaxError, UnicodeDecodeError):
                continue

    func_returns: dict[str, TypeRef] = {}
    for _, tree in parsed:
        for node in tree.body:
            if isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef)):
                returned = annotation_type(node.returns, specs)
                if returned:
                    func_returns[node.name] = returned

    typer = _Typer(scan, specs, func_returns)
    for _, tree in parsed:
        _ReadCollector(typer, scan).run(tree)
    return scan


def find_findings(scan: SpecScan) -> list[cuf.Finding]:
    findings: list[cuf.Finding] = []
    for key, fd in scan.fields.items():
        if key in scan.real_reads:
            continue
        if key in scan.serialization_reads:
            findings.append(
                cuf.Finding(key, "SERIALIZED-ONLY", fd.path, fd.line, "read only while serializing, never acted on")
            )
        else:
            findings.append(
                cuf.Finding(key, "UNREAD", fd.path, fd.line, "no attribute read of this class's field resolves anywhere")
            )
    return findings


def check_specs(
    spec_module: Path, read_roots: list[Path], allowlist_path: Path
) -> tuple[list[cuf.Finding], list[str]]:
    if not spec_module.exists():
        raise SystemExit(f"ERROR: spec module not found: {spec_module}")
    scan = scan_specs(spec_module, read_roots)
    allowed = cuf.load_allowlist(allowlist_path)
    findings = find_findings(scan)
    flagged = {f.key for f in findings}
    known_classes = set(scan.field_types)
    flagged_classes = {k.rsplit(".", 1)[0] for k in flagged}

    stale: list[str] = []
    for key in sorted(allowed):
        cls, name = key.rsplit(".", 1)
        if name == "*":
            if cls not in flagged_classes:
                why = "no longer exists" if cls not in known_classes else "has no unread field left"
                stale.append(f"{key} -- {why}")
        elif key not in flagged:
            stale.append(f"{key} -- {'no longer exists' if key not in scan.fields else 'is read now'}")
    remaining = [f for f in findings if cuf._allowlist_hit(f.key, allowed) is None]
    return remaining, stale


# ---------------------------------------------------------------------------
# Self-test
# ---------------------------------------------------------------------------


_FIXTURE_SPECS = (
    "from __future__ import annotations\n"
    "from dataclasses import dataclass\n"
    "\n"
    "@dataclass\n"
    "class Spec:\n"
    "    used: int = 0\n"
    "    ignored: int = 0\n"
    "\n"
    "@dataclass\n"
    "class Top:\n"
    "    spec: Spec\n"
    "    def go(self):\n"
    "        return self.spec.used\n"
)
_FIXTURE_OTHER = (
    "from dataclasses import dataclass\n"
    "@dataclass\n"
    "class Plan:\n"
    "    ignored: int = 0\n"
    "def use(p: Plan):\n"
    "    return p.ignored\n"
)


def self_test() -> bool:
    ok = True
    with tempfile.TemporaryDirectory() as tmp:
        root = Path(tmp) / "pkg"
        root.mkdir()
        spec = root / "manifest.py"
        spec.write_text(_FIXTURE_SPECS, encoding="utf-8", newline="\n")
        (root / "other.py").write_text(_FIXTURE_OTHER, encoding="utf-8", newline="\n")
        allow = Path(tmp) / "a.json"
        allow.write_text("{}", encoding="utf-8", newline="\n")

        findings, _ = check_specs(spec, [root], allow)
        keys = {f.key for f in findings}
        if keys != {"Spec.ignored"}:
            print(f"FAIL: expected exactly Spec.ignored (masked by Plan.ignored by name), got {keys}")
            ok = False
        else:
            print("ok    a same-named field on another class does not mask an unread spec field")
        old, _ = cuf.check(root, allow)
        if "Spec.ignored" in {f.key for f in old}:
            print("FAIL: the name-based guard was expected to be blind to this fixture")
            ok = False
        else:
            print("ok    the name-based guard really is blind to it (this guard's reason to exist)")

        allow.write_text(json.dumps({"allowed": {"Spec.ignored": "reason", "Spec.used": "reason"}}), encoding="utf-8", newline="\n")
        findings, stale = check_specs(spec, [root], allow)
        if findings or len(stale) != 1 or not stale[0].startswith("Spec.used"):
            print(f"FAIL: allowlist should suppress one and flag the read one stale, got {findings} {stale}")
            ok = False
        else:
            print("ok    the allowlist suppresses, and an entry for a read field is stale")
    print(f"\n{'PASS' if ok else 'FAIL'}: check_manifest_spec_reads self-test")
    return ok


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--self-test", action="store_true")
    ap.add_argument("--json", action="store_true")
    ap.add_argument("--root", type=Path, default=Path.cwd(), help="repo root (default: cwd)")
    args = ap.parse_args(argv)
    if args.self_test:
        return 0 if self_test() else 1

    root = args.root.resolve()
    findings, stale = check_specs(root / SPEC_MODULE, [root / r for r in READ_ROOTS], root / ALLOWLIST_PATH)
    for finding in findings:  # report repo-relative paths, like every other guard
        finding.path = Path(finding.path).resolve().relative_to(root).as_posix()
    if args.json:
        print(json.dumps({"findings": [f.__dict__ for f in findings], "stale_allowlist_entries": stale}, indent=2))
    else:
        for f in findings:
            print(f"{f.path}:{f.line}: {f.kind} {f.key} -- {f.detail}")
        for s in stale:
            print(f"{ALLOWLIST_PATH}: STALE {s}")
    if findings or stale:
        print(
            f"check_manifest_spec_reads: {len(findings)} manifest field(s) nothing reads, {len(stale)} stale allowlist entry/entries.\n"
            "Wire it, cross-check it, delete it from the contract, or allowlist it with the issue that will clear it."
        )
        return 1
    print("check_manifest_spec_reads: clean -- every manifest spec field is read, resolved by class.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
