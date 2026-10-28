"""Fail if a string.Format call site disagrees with its Resources.resw template (#761).

WHY THIS IS A GUARD
-------------------
`string.Format(culture, _strings.GetString("Key"), a, b)` is checked by nothing at build
time: the template lives in a .resw file. Too few arguments throws FormatException on the
screen that shows the string; too many is silently ignored (CodeQL's cs/invalid-string-
formatting). MEASURED 2026-10-28: CodeQL raised that rule against four correct call sites
(alerts 1383, 1417, 1466, 1467), because a test stub `GetString(key) => key` flowed into
the call; that noise is removed by building only app/src in codeql.yml, and THIS script is
the real check CodeQL could never do: it reads the actual template.

WHAT IT CHECKS
--------------
For every `string.Format([culture,] <...>.GetString(<"literal" | Class.Const>), args...)` in
app/src, the template's highest placeholder index + 1 must equal the argument count
(TOO-MANY-ARGS / TOO-FEW-ARGS), and the key must exist (MISSING-KEY). Every resw value with a
placeholder must be referenced by a string literal or const somewhere in app/src
(UNREFERENCED-TEMPLATE), the "wired to nothing" shape for a template.

WHAT IT CANNOT SEE
------------------
Dynamic keys (`GetString(key)`) and `params` spreads (`args`) are skipped and counted; their
templates are only covered by the UNREFERENCED check, via the literal at the wrapper's caller.
"""

from __future__ import annotations

import argparse
import re
import sys
import xml.etree.ElementTree as ET
from dataclasses import dataclass, field
from pathlib import Path

CULTURE = re.compile(r"^(?:System\.Globalization\.)?CultureInfo\.(?:CurrentCulture|InvariantCulture|CurrentUICulture)$")
KEY_ARG = re.compile(r'GetString\(\s*(?:"([^"\\]*)"|([A-Za-z_]\w*(?:\.[A-Za-z_]\w*)+))\s*\)$')
PLACEHOLDER = re.compile(r"\{(\d+)(?:[,:][^{}]*)?\}")
CONST = re.compile(r'\bconst\s+string\s+(\w+)\s*=\s*"([^"\\]*)"')
CLASS = re.compile(r"\b(?:class|record|struct)\s+(\w+)")
STRING_LITERAL = re.compile(r'"([^"\\\r\n]*)"')


@dataclass(frozen=True)
class Finding:
    code: str
    key: str
    path: str
    line: int
    detail: str

    def __str__(self) -> str:
        return f"{self.code}: {self.path}:{self.line} key {self.key!r}: {self.detail}"


@dataclass
class Result:
    findings: list[Finding] = field(default_factory=list)
    checked: int = 0
    skipped: int = 0


def placeholder_count(template: str) -> int:
    """Highest placeholder index + 1 (0 when none); `{{` and `}}` are escaped braces."""
    stripped = template.replace("{{", "").replace("}}", "")
    return max((int(m) for m in PLACEHOLDER.findall(stripped)), default=-1) + 1


def split_args(text: str, start: int) -> tuple[list[str], int] | None:
    """Split the argument list of the call whose `(` is at text[start-1]; returns (args, end index)."""
    args: list[str] = []
    depth, i, cur = 0, start, start
    while i < len(text):
        c = text[i]
        if c == '"':
            i = _skip_string(text, i)
            continue
        if c == "'":
            i = _skip_char(text, i)
            continue
        if c in "([{":
            depth += 1
        elif c in ")]}":
            if depth == 0:
                args.append(text[cur:i].strip())
                return args, i
            depth -= 1
        elif c == "," and depth == 0:
            args.append(text[cur:i].strip())
            cur = i + 1
        i += 1
    return None


def _skip_string(text: str, i: int) -> int:
    verbatim = i > 0 and text[i - 1] in "@" or (i > 1 and text[i - 2 : i] in ("$@", "@$"))
    i += 1
    while i < len(text):
        if verbatim:
            if text[i] == '"':
                if text[i + 1 : i + 2] == '"':
                    i += 2
                    continue
                return i + 1
        else:
            if text[i] == "\\":
                i += 2
                continue
            if text[i] == '"':
                return i + 1
        i += 1
    return i


def _skip_char(text: str, i: int) -> int:
    j = i + 1
    while j < len(text) and text[j] != "'":
        j += 2 if text[j] == "\\" else 1
    return j + 1


def _strip_comments_keep_layout(text: str) -> str:
    """Blank `//` comments (not inside strings) so a commented-out call is not counted; line numbers stay."""
    out: list[str] = []
    for line in text.split("\n"):
        i, in_str = 0, False
        while i < len(line):
            if line[i] == '"' and (i == 0 or line[i - 1] != "\\"):
                in_str = not in_str
            elif not in_str and line[i : i + 2] == "//":
                line = line[:i]
                break
            i += 1
        out.append(line)
    return "\n".join(out)


def load_templates(resw: Path) -> dict[str, str]:
    root = ET.parse(resw).getroot()
    return {d.get("name", ""): (d.findtext("value") or "") for d in root.iter("data")}


def _cs_files(root: Path) -> list[Path]:
    src = root / "app" / "src"
    return sorted(p for p in src.rglob("*.cs") if not {"obj", "bin"} & set(p.relative_to(src).parts))


def scan_repo(root: Path) -> Result:
    result = Result()
    resw_files = sorted((root / "app" / "src").rglob("en-US/Resources.resw"))
    templates = load_templates(resw_files[0]) if resw_files else {}
    files = [(p, _strip_comments_keep_layout(p.read_text(encoding="utf-8-sig"))) for p in _cs_files(root)]

    consts: dict[str, str] = {}
    literals: set[str] = set()
    for _path, text in files:
        literals.update(STRING_LITERAL.findall(text))
        owner = ""
        for line in text.split("\n"):
            if m := CLASS.search(line):
                owner = m.group(1)
            for name, value in CONST.findall(line):
                consts[f"{owner}.{name}"] = value
                literals.add(value)

    for path, text in files:
        rel = path.relative_to(root).as_posix()
        for m in re.finditer(r"\b[Ss]tring\.Format\(", text):
            parsed = split_args(text, m.end())
            if parsed is None:
                continue
            args = parsed[0]
            if args and CULTURE.match(args[0]):
                args = args[1:]
            if not args:
                continue
            line = text.count("\n", 0, m.start()) + 1
            key_match = KEY_ARG.search(args[0])
            rest = args[1:]
            if not key_match or (len(rest) == 1 and rest[0] == "args"):
                result.skipped += 1
                continue
            key = key_match.group(1)
            if key is None:
                dotted = key_match.group(2)
                key = consts.get(".".join(dotted.split(".")[-2:]))
                if key is None:
                    result.skipped += 1
                    continue
            if key not in templates:
                result.findings.append(Finding("MISSING-KEY", key, rel, line, "no such entry in Resources.resw"))
                continue
            result.checked += 1
            need = placeholder_count(templates[key])
            if len(rest) > need:
                result.findings.append(Finding("TOO-MANY-ARGS", key, rel, line, f"template takes {need}, call passes {len(rest)}"))
            elif len(rest) < need:
                result.findings.append(Finding("TOO-FEW-ARGS", key, rel, line, f"template takes {need}, call passes {len(rest)}"))

    for key, value in sorted(templates.items()):
        if placeholder_count(value) > 0 and key not in literals:
            result.findings.append(
                Finding("UNREFERENCED-TEMPLATE", key, "Resources.resw", 0, "has placeholders but no string literal or const in app/src names it")
            )
    return result


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description="Fail when string.Format arity disagrees with the Resources.resw template.")
    ap.add_argument("--root", type=Path, default=Path(__file__).resolve().parent.parent)
    args = ap.parse_args(argv)
    if not (args.root / "app" / "src").is_dir():
        print(f"ERROR: {args.root} does not look like this repo (no app/src); checked nothing", file=sys.stderr)
        return 2
    result = scan_repo(args.root)
    for f in result.findings:
        print(f, file=sys.stderr)
    if result.findings:
        print(f"\nFAIL: {len(result.findings)} format/template mismatch(es)", file=sys.stderr)
        return 1
    print(f"OK: {result.checked} string.Format site(s) match their resw template ({result.skipped} dynamic skipped)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
