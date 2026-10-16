"""scripts/check_xaml_shapes.py -- catch the XAML shapes that crash XamlCompiler.exe silently (#423).

    python scripts/check_xaml_shapes.py
    python scripts/check_xaml_shapes.py --root <repo>
    python scripts/check_xaml_shapes.py --self-test

WHY
---
`XamlCompiler.exe` (Windows App SDK) exits 1 with NO diagnostic text for a few shapes: no
`error WMC....` line, nothing under `-v:diag`, no entry in its own output.json. Each one cost
someone a long bisection. This guard finds them by text BEFORE the compiler runs and names the
file, the line and the shape. It never replaces the build; it replaces the hour.

SHAPES
------
`TITLEBAR-ATTRIBUTE`   `ExtendsContentIntoTitleBar=...` as an attribute on a `<Window>`.
    MEASURED 2026-09-19 (#62), Windows App SDK 1.8.250906003. Set it in the code-behind
    constructor after `InitializeComponent()` instead (docs/ui_conventions.md section 8).
`LOCAL-CONVERTER`      FAIL. A `Window.Resources` entry instantiating a locally defined `IValueConverter`
    (a class under `app/src` that implements it). MEASURED 2026-09-19 (#62). Use an `x:Bind` method call
    on a static helper instead.
`LOCAL-CONVERTER-UNMEASURED`   WARN only, never fails the gate. A local converter in any other Resources scope
    (Grid.Resources, ResourceDictionary, ...), or a `*Converter` element in Window.Resources that is not a
    local class. MEASURED 2026-10-16: a local converter in `Page.Resources` builds clean, so that scope is
    silent. The other scopes are THEORY (unverified).
`NO-ISENABLED`         `IsEnabled=...` on a StackPanel, which has no such property.
    MEASURED 2026-10-16: XamlCompiler.exe exits 1 silently (reproduced on MainWindow.xaml, below). Grid, Border,
    RelativePanel, Canvas, TextBlock and Image are in the same family (no IsEnabled);
    THEORY (unverified): they crash the same way, they are flagged anyway because they cannot work.
    Put IsEnabled on the Control inside it, or bind each child control.

Exit codes: 0 clean, 1 a shape found, 2 bad usage.
"""

from __future__ import annotations

import argparse
import re
import sys
import tempfile
from dataclasses import dataclass
from pathlib import Path

NO_ISENABLED = {"StackPanel", "Grid", "Border", "RelativePanel", "Canvas", "TextBlock", "Image"}

_COMMENT = re.compile(r"<!--.*?-->", re.DOTALL)
_TAG = re.compile(r"<(?P<close>/?)(?P<name>[A-Za-z_][\w:.\-]*)(?P<attrs>(?:\"[^\"]*\"|'[^']*'|[^>\"'])*?)(?P<self>/?)>", re.DOTALL)
_ATTR = re.compile(r"(?P<name>[A-Za-z_][\w:.\-]*)\s*=\s*(?:\"[^\"]*\"|'[^']*')")
_CONVERTER_CLASS = re.compile(r"\bclass\s+(\w+)[^{;]*?:[^{;]*\bIValueConverter\b")


@dataclass(frozen=True)
class Finding:
    path: str
    line: int
    code: str
    message: str
    level: str = "FAIL"  # FAIL = measured to crash the compiler; WARN = THEORY (unverified), never fails the gate

    def __str__(self) -> str:
        return f"{self.path}:{self.line}: {self.level}: {self.code}: {self.message}"


def local_converters(cs_sources: list[str]) -> set[str]:
    found: set[str] = set()
    for text in cs_sources:
        found.update(_CONVERTER_CLASS.findall(text))
    return found


def _resources_owner(stack: list[str]) -> str | None:
    """The element whose `.Resources` we are inside (`Window` for `Window.Resources`), nearest first; None outside one."""
    for entry in reversed(stack):
        local = entry.rsplit(":", 1)[-1]
        if local.endswith(".Resources"):
            return local.split(".")[0]
        if local == "ResourceDictionary":
            return "ResourceDictionary"
    return None


def scan_xaml(text: str, path: str, converters: set[str]) -> list[Finding]:
    # Blank comments but keep their newlines, so reported line numbers stay true.
    text = _COMMENT.sub(lambda m: re.sub(r"[^\n]", " ", m.group(0)), text)
    findings: list[Finding] = []
    stack: list[str] = []
    for match in _TAG.finditer(text):
        name = match.group("name")
        local = name.rsplit(":", 1)[-1]
        if match.group("close"):
            if stack:
                stack.pop()
            continue
        line = text.count("\n", 0, match.start()) + 1
        attrs = {m.group("name") for m in _ATTR.finditer(match.group("attrs"))}
        owner = _resources_owner(stack)
        if local == "Window" and "ExtendsContentIntoTitleBar" in attrs:
            findings.append(
                Finding(
                    path,
                    line,
                    "TITLEBAR-ATTRIBUTE",
                    "ExtendsContentIntoTitleBar as an attribute on <Window> crashes XamlCompiler.exe with exit 1 and no message. "
                    "Set `ExtendsContentIntoTitleBar = true;` in the code-behind constructor after InitializeComponent() "
                    "(docs/ui_conventions.md section 8)",
                )
            )
        if owner is not None and (local in converters or (local.endswith("Converter") and name != local)):
            is_local = local in converters
            if owner == "Window" and is_local:
                findings.append(
                    Finding(
                        path,
                        line,
                        "LOCAL-CONVERTER",
                        f"<{name}> is a locally defined converter instantiated in Window.Resources; XamlCompiler.exe exits 1 "
                        "with no message (MEASURED 2026-09-19). Use an x:Bind method call on a static helper instead "
                        "(docs/ui_conventions.md section 8)",
                    )
                )
            elif owner != "Page" or not is_local:
                what = "a locally defined converter" if is_local else "a *Converter element"
                findings.append(
                    Finding(
                        path,
                        line,
                        "LOCAL-CONVERTER-UNMEASURED",
                        f"<{name}> is {what} in {owner}.Resources. THEORY (unverified): it crashes XamlCompiler.exe like the "
                        "Window.Resources case. If it builds, ignore this warning; if the build exits 1 with no message, "
                        "this is why (docs/ui_conventions.md section 8)",
                        "WARN",
                    )
                )
        if local in NO_ISENABLED and "IsEnabled" in attrs:
            findings.append(
                Finding(
                    path,
                    line,
                    "NO-ISENABLED",
                    f"<{name}> has no IsEnabled property; XamlCompiler.exe exits 1 with no message. "
                    "Put IsEnabled on the Control inside it, or bind each child",
                )
            )
        if not match.group("self"):
            stack.append(name)
    return findings


def scan_repo(root: Path) -> tuple[list[Finding], int]:
    app = root / "app" / "src"
    if not app.is_dir():
        return [], 0
    skip = ("obj", "bin")
    files = [p for p in app.rglob("*") if not any(part in skip for part in p.relative_to(app).parts)]
    cs = [p.read_text(encoding="utf-8", errors="replace") for p in files if p.suffix == ".cs"]
    converters = local_converters(cs)
    findings: list[Finding] = []
    xaml = [p for p in files if p.suffix == ".xaml"]
    for p in sorted(xaml):
        rel = p.relative_to(root).as_posix()
        findings.extend(scan_xaml(p.read_text(encoding="utf-8", errors="replace"), rel, converters))
    return findings, len(xaml)


def self_test() -> bool:
    ok = True

    def expect(label: str, cond: bool) -> None:
        nonlocal ok
        print(("ok    " if cond else "FAIL: ") + label)
        ok = ok and cond

    bad = (
        '<Window\n  x:Class="A.W"\n  ExtendsContentIntoTitleBar="True">\n<Window.Resources>\n'
        '  <local:BoolConv x:Key="k"/>\n</Window.Resources>\n'
        '<StackPanel IsEnabled="False"/>\n</Window>'
    )
    got = scan_xaml(bad, "w.xaml", {"BoolConv"})
    expect("all three shapes found", {f.code for f in got} == {"TITLEBAR-ATTRIBUTE", "LOCAL-CONVERTER", "NO-ISENABLED"})
    expect("line of the title bar attribute is the Window start", next(f for f in got if f.code == "TITLEBAR-ATTRIBUTE").line == 1)
    expect("clean xaml is clean", scan_xaml('<Window><Button IsEnabled="False"/></Window>', "w.xaml", set()) == [])
    with tempfile.TemporaryDirectory() as tmp:
        findings, count = scan_repo(Path(tmp))
        expect("a repo without app/src checks nothing and says so via count 0", findings == [] and count == 0)
    print(f"\n{'PASS' if ok else 'FAIL'}: check_xaml_shapes self-test")
    return ok


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description="Fail on XAML shapes that crash XamlCompiler.exe silently.")
    ap.add_argument("--root", type=Path, default=Path(__file__).resolve().parent.parent)
    ap.add_argument("--self-test", action="store_true")
    args = ap.parse_args(argv)
    if args.self_test:
        return 0 if self_test() else 1
    if not (args.root / "app" / "src").is_dir():
        print(f"ERROR: {args.root} does not look like this repo (no app/src); checked nothing", file=sys.stderr)
        return 2
    findings, count = scan_repo(args.root)
    failures = [f for f in findings if f.level == "FAIL"]
    for finding in findings:
        print(finding, file=sys.stderr if finding.level == "FAIL" else sys.stdout)
    if failures:
        print(f"\nFAIL: {len(failures)} XAML shape(s) that crash XamlCompiler.exe silently", file=sys.stderr)
        return 1
    print(f"OK: {count} XAML file(s) checked, no silent-crash shapes ({len(findings)} warning(s))")
    return 0


if __name__ == "__main__":
    sys.exit(main())
