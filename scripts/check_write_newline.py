"""scripts/check_write_newline.py -- Hard Rule 5: every text-file writer pins LF.

    python scripts/check_write_newline.py
    python scripts/check_write_newline.py --self-test
    python scripts/check_write_newline.py --root <dir>

Hard Rule 5: "files are UTF-8 with LF. Every file writer passes
`encoding="utf-8", newline="\\n"`." MEASURED 2026-10-02 (#444): compile_sprint_log.py
called `sprint_log.write_text(..., encoding="utf-8")` and wrote 995 CRLFs into
docs/sprint_log.md on Windows; git's autocrlf hid it at commit time, so nothing
noticed. On Linux CI the bug is invisible, so only a static check can catch it.

WHAT IT FLAGS (AST, not grep, so strings and comments never trigger it)
-----------------------------------------------------------------------
- `<expr>.write_text(text, ...)` with at most ONE positional argument and no
  `newline=` keyword. (A two-positional `store.write_text(path, text)` is the
  worker's own blob-store method, not `pathlib`, and is not flagged.)
- `open(...)`, `io.open(...)` or `<path>.open(...)` whose mode is a string
  literal that writes text (contains w, a or x, no b) and has no `newline=`.
  A mode that is not a literal cannot be judged and is not flagged.

SCOPE
-----
`scripts/**/*.py` and `worker/src/**/*.py`. Not scanned: `scripts/tests/` (test
fixtures in tmp dirs), and any call lexically inside a function named
`self_test*`, `_self_test*` or `test_*` (the same: throwaway fixtures that are
never shipped and are read back with universal newlines). A deliberate
exception elsewhere carries a `# newline-ok: <reason>` comment on its line.

Exit codes: 0 clean, 1 at least one violation, 2 bad usage.
"""

from __future__ import annotations

import argparse
import ast
import re
import sys
from collections.abc import Iterator
from dataclasses import dataclass
from pathlib import Path

SCAN_DIRS = ("scripts", "worker/src")
SKIP_DIR_PARTS = {"tests", "__pycache__", ".venv", "legacy"}
EXEMPT_MARKER = "newline-ok:"
_MODE_RE = re.compile(r"^[rwaxbt+U]+$")


@dataclass(frozen=True)
class Finding:
    path: str
    line: int
    what: str

    def __str__(self) -> str:
        return f"{self.path}:{self.line}: {self.what}"


def _has_kw(call: ast.Call, name: str) -> bool:
    return any(kw.arg == name for kw in call.keywords)


def _literal_mode(call: ast.Call, positional_index: int) -> str | None:
    for kw in call.keywords:
        if kw.arg == "mode" and isinstance(kw.value, ast.Constant) and isinstance(kw.value.value, str):
            return kw.value.value
    if positional_index < len(call.args):
        arg = call.args[positional_index]
        if isinstance(arg, ast.Constant) and isinstance(arg.value, str) and _MODE_RE.match(arg.value):
            return arg.value
    return None


def _violation(call: ast.Call) -> str | None:
    func = call.func
    if isinstance(func, ast.Attribute) and func.attr == "write_text":
        if len(call.args) <= 1 and not _has_kw(call, "newline"):
            return 'write_text() without newline="\\n"'
        return None
    is_name_open = isinstance(func, ast.Name) and func.id == "open"
    is_io_open = (
        isinstance(func, ast.Attribute)
        and func.attr == "open"
        and isinstance(func.value, ast.Name)
        and func.value.id == "io"
    )
    is_path_open = isinstance(func, ast.Attribute) and func.attr == "open" and not is_io_open
    if is_name_open or is_io_open or is_path_open:
        # open(path, mode) / io.open(path, mode) take the mode second; Path.open(mode) first.
        mode = _literal_mode(call, 0 if is_path_open else 1)
        if mode and any(c in mode for c in "wax") and "b" not in mode and not _has_kw(call, "newline"):
            return f'open(..., "{mode}") without newline="\\n"'
    return None


def _is_fixture_function(name: str) -> bool:
    return "self_test" in name or name.startswith("test_")


def scan_source(source: str, display_path: str) -> list[Finding]:
    tree = ast.parse(source, filename=display_path)
    lines = source.splitlines()
    findings: list[Finding] = []

    def visit(node: ast.AST, in_fixture: bool) -> None:
        if isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef)) and _is_fixture_function(node.name):
            in_fixture = True
        if isinstance(node, ast.Call) and not in_fixture:
            what = _violation(node)
            if what:
                end = getattr(node, "end_lineno", None) or node.lineno
                span = lines[node.lineno - 1 : end]
                if not any(EXEMPT_MARKER in text for text in span):
                    findings.append(Finding(display_path, node.lineno, what))
        for child in ast.iter_child_nodes(node):
            visit(child, in_fixture)

    visit(tree, False)
    return findings


def iter_files(root: Path) -> Iterator[Path]:
    for rel in SCAN_DIRS:
        base = root / rel
        if not base.is_dir():
            continue
        for path in sorted(base.rglob("*.py")):
            if SKIP_DIR_PARTS.intersection(path.relative_to(root).parts):
                continue
            yield path


def check(root: Path) -> list[Finding]:
    findings: list[Finding] = []
    for path in iter_files(root):
        rel = path.relative_to(root).as_posix()
        try:
            findings.extend(scan_source(path.read_text(encoding="utf-8"), rel))
        except (OSError, SyntaxError, UnicodeDecodeError) as exc:
            findings.append(Finding(rel, 1, f"could not scan: {exc}"))
    return findings


def self_test() -> bool:
    import tempfile

    ok = True

    def expect(label: str, source: str, flagged: bool) -> None:
        nonlocal ok
        got = bool(scan_source(source, "fixture.py"))
        if got != flagged:
            print(f"FAIL: {label}: expected flagged={flagged}, got {got}")
            ok = False
        else:
            print(f"ok    {label}")

    expect("write_text without newline is flagged", 'p.write_text("x", encoding="utf-8")\n', True)
    expect("write_text with newline passes", 'p.write_text("x", encoding="utf-8", newline="\\n")\n', False)
    expect("two-positional store.write_text is not pathlib", "store.write_text(path, text)\n", False)
    expect('open(path, "w") is flagged', 'open(p, "w", encoding="utf-8")\n', True)
    expect("open with mode= kw is flagged", 'open(p, mode="a")\n', True)
    expect('open "w" with newline passes', 'open(p, "w", newline="\\n")\n', False)
    expect("binary open passes", 'open(p, "wb")\n', False)
    expect("read open passes", 'open(p, "r")\n', False)
    expect("Path.open('w') is flagged", 'p.open("w")\n', True)
    expect("a non-literal mode is not judged", "open(p, mode)\n", False)
    expect("a string mentioning write_text is ignored", 's = "p.write_text(x)"\n', False)
    expect("an exempt marker on the line passes", 'p.write_text("x")  # newline-ok: CRLF wanted\n', False)
    expect("a call inside self_test is fixture code", 'def self_test():\n    p.write_text("x")\n', False)
    expect("a call inside an ordinary function is flagged", 'def run():\n    p.write_text("x")\n', True)

    with tempfile.TemporaryDirectory() as tmp:
        root = Path(tmp)
        (root / "scripts").mkdir()
        (root / "scripts" / "bad.py").write_text(
            'def f(p):\n    p.write_text("x")\n', encoding="utf-8", newline="\n"
        )
        found = check(root)
        if len(found) != 1 or found[0].line != 2 or found[0].path != "scripts/bad.py":
            print(f"FAIL: planted violation should be named with file and line, got {found}")
            ok = False
        else:
            print("ok    a planted violation is named with file and line")
    print(f"\n{'PASS' if ok else 'FAIL'}: check_write_newline self-test")
    return ok


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description='Flag text-file writers without newline="\\n" (Hard Rule 5).')
    ap.add_argument("--root", type=Path, default=Path.cwd(), help="repo root (default: cwd)")
    ap.add_argument("--self-test", action="store_true")
    args = ap.parse_args(argv)
    if args.self_test:
        return 0 if self_test() else 1
    root = args.root.resolve()
    if not (root / "scripts").is_dir():
        print(f"ERROR: {root} has no scripts/ directory; run from the repo root or pass --root.", file=sys.stderr)
        return 2
    findings = check(root)
    for finding in findings:
        print(f"ERROR: {finding}", file=sys.stderr)
    if findings:
        print(f'\n{len(findings)} writer(s) without newline="\\n" (Hard Rule 5).', file=sys.stderr)
        return 1
    print("check_write_newline: clean.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
