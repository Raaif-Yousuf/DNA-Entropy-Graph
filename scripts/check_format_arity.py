"""Fail if a string.Format call site disagrees with its Resources.resw template (#761).

WHY THIS IS A GUARD
-------------------
`string.Format(culture, _strings.GetString("Key"), a, b)` is checked by nothing at build
time: the template lives in a .resw file. Too few arguments throws FormatException on the
screen that shows the string; too many is silently ignored (CodeQL's cs/invalid-string-
formatting). MEASURED 2026-10-28: CodeQL raised that rule against four correct call sites
(alerts 1383, 1417, 1466, 1467), because a test stub `GetString(key) => key` flowed into
the call. The product provider has the same `return key` fallback, so codeql-config.yml
excludes that rule and THIS script is the real check CodeQL could never do: it reads the actual
template.

WHAT IT CHECKS
--------------
Scope: every `.cs` file under app/src and app/tools (never obj/ or bin/); templates come from every
app/src `en-US/Resources.resw` outside obj/ and bin/.

1. `string.Format([culture,] <recv>.GetString(<"literal" | Class.Const>), args...)`: the template's
   highest placeholder index + 1 must equal the argument count (TOO-MANY-ARGS / TOO-FEW-ARGS) and
   the key must exist (MISSING-KEY). A parenthesised receiver `(a ?? b).GetString("K")` counts.
2. Wrapper helpers: a method `Name(string key, params object[] args)` whose body does
   `string.Format(..., GetString(key), args)` is detected, its body is not itself a site, and EVERY
   call `Name("Key", a, b)` in the same file is checked like (1). A `Class.Const` key is resolved and a
   conditional key `c ? "A" : "B"` checks every branch.
3. Every resw value with a placeholder must be referenced by a string literal or const somewhere in
   the scanned code (UNREFERENCED-TEMPLATE), the "wired to nothing" shape for a template.

What stays DYNAMIC (cannot be checked, listed in KNOWN_DYNAMIC): a key that is a variable or a
`switch` expression, an unparseable call, and a spread of any variable declared as `object[]` (it is
not one argument). A wrapper helper defined in one file and called from another is not followed.

HOW IT READS C# (and its limits)
--------------------------------
A text scanner, not a compiler: it blanks `//`, `///` and `/* */` comments, steps over regular,
verbatim, raw (triple-quote) and interpolated string literals and char literals, splits arguments
at depth 0, expands `new object[] { ... }`, and resolves `Class.Const` / `Outer.Inner.Const` keys
(`const string` and `static readonly string`) by enclosing-type span. Known gap: an interpolated
string whose hole contains a quote is treated as a plain string.

Dynamic sites (`GetString(key)`, a `params` spread, an unparseable call) cannot be checked. They
are listed in KNOWN_DYNAMIC per file with an EXACT count: an unlisted dynamic site is
UNEXPECTED-DYNAMIC and a list entry that no longer matches is STALE-DYNAMIC-ALLOWLIST, so the
list cannot rot. The run also fails below a floor of checked sites (a pattern that stops
matching must not read as green).
"""

from __future__ import annotations

import argparse
import re
import sys
import xml.etree.ElementTree as ET
from dataclasses import dataclass, field
from pathlib import Path

CULTURE = re.compile(r"^(?:System\.Globalization\.)?CultureInfo\.(?:CurrentCulture|InvariantCulture|CurrentUICulture)$")
KEY_ARG = re.compile(
    r'^(?:(?:[A-Za-z_]\w*\.)*|\((?:[^()]|\([^()]*\))*\)\.)GetString\(\s*(?:"([^"\\]*)"|([A-Za-z_]\w*(?:\.[A-Za-z_]\w*)+))\s*\)$'
)
KEY_VAR_ARG = re.compile(r"^(?:[A-Za-z_]\w*\.)*GetString\(\s*([A-Za-z_]\w*)\s*\)$")
LITERAL_KEY = re.compile(r'^"([^"\\]*)"$')
DOTTED_NAME = re.compile(r"^[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)+$")
HELPER_DECL = re.compile(r"\b(\w+)\s*\(\s*string\s+(\w+)\s*,\s*params\s+object\s*\[\s*\]\s+(\w+)\s*\)")
STRING_FORMAT = re.compile(r"\b[Ss]tring\.Format\(")
ARRAY_DECL = re.compile(r"\bobject\s*\[\s*\]\s+(\w+)")
ARRAY_VAR_INIT = re.compile(r"\bvar\s+(\w+)\s*=\s*new\s*(?:object\s*)?\[")
PLACEHOLDER = re.compile(r"\{(\d+)(?:[,:][^{}]*)?\}")
CONST = re.compile(r'\b(?:const|static\s+readonly)\s+string\s+(\w+)\s*=\s*"([^"\\]*)"')
TYPE_DECL = re.compile(r"\b(?:class|record|struct|interface)\s+(\w+)")
OBJECT_ARRAY = re.compile(r"^new\s*(?:object\s*)?\[\s*\]\s*\{(.*)\}$", re.DOTALL)
MIN_CHECKED = 20

# Files with call sites the scanner cannot resolve (wrapper helpers taking the key as a parameter,
# `params` spreads), mapped to the exact number of such sites. Their templates are covered only by
# UNREFERENCED-TEMPLATE. Exact counts on purpose: see the module docstring.
KNOWN_DYNAMIC: dict[str, int] = {
    "app/src/DnaEntropyGraph.App/Startup/StartupDataRoot.cs": 1,
    "app/src/DnaEntropyGraph.Presentation/Services/LeftRunningGuard.cs": 1,
    "app/src/DnaEntropyGraph.Presentation/Services/RunStallCoordinator.cs": 1,
    "app/src/DnaEntropyGraph.Presentation/ViewModels/CloudResourcesViewModel.cs": 1,
    "app/src/DnaEntropyGraph.Presentation/ViewModels/HistoryViewModel.cs": 2,
    "app/src/DnaEntropyGraph.Presentation/ViewModels/InputPillItem.cs": 1,
    "app/src/DnaEntropyGraph.Presentation/ViewModels/PresetPickerViewModel.cs": 1,
    "app/src/DnaEntropyGraph.Presentation/ViewModels/ResultsViewModel.cs": 1,
    "app/src/DnaEntropyGraph.Presentation/ViewModels/SetupStepRow.cs": 1,
    "app/src/DnaEntropyGraph.Presentation/ViewModels/ViewerViewModel.cs": 1,
}


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


def _skip_string(text: str, i: int) -> int:
    """Index just past the string literal whose opening quote is text[i]."""
    if text.startswith('"""', i):
        n = 3
        while text.startswith('"', i + n):
            n += 1
        end = text.find('"' * n, i + n)
        return len(text) if end < 0 else end + n
    verbatim = text[i - 1 : i] == "@" or text[i - 2 : i] == "@$"  # @"..." and $@"..."; @$"..." is the same prefix reversed
    j = i + 1
    while j < len(text):
        if verbatim:
            if text[j] == '"':
                if text[j + 1 : j + 2] == '"':
                    j += 2
                    continue
                return j + 1
        else:
            if text[j] == "\\":
                j += 2
                continue
            if text[j] == '"':
                return j + 1
        j += 1
    return j


def _skip_char(text: str, i: int) -> int:
    j = i + 1
    while j < len(text) and text[j] != "'":
        j += 2 if text[j] == "\\" else 1
    return j + 1


def mask(text: str) -> tuple[str, str]:
    """Return (no_comments, code_only), both the same length and line layout as `text`.

    no_comments keeps strings and blanks every comment form; code_only also blanks the inside of
    string and char literals, so brace/paren scans see code only.
    """
    nc: list[str] = []
    co: list[str] = []
    i, n = 0, len(text)

    def blank(chunk: str) -> str:
        return "".join(c if c in "\r\n" else " " for c in chunk)

    while i < n:
        two = text[i : i + 2]
        if two == "//":
            j = text.find("\n", i)
            j = n if j < 0 else j
            nc.append(blank(text[i:j]))
            co.append(blank(text[i:j]))
            i = j
        elif two == "/*":
            j = text.find("*/", i + 2)
            j = n if j < 0 else j + 2
            nc.append(blank(text[i:j]))
            co.append(blank(text[i:j]))
            i = j
        elif text[i] in "\"'":
            j = _skip_string(text, i) if text[i] == '"' else _skip_char(text, i)
            nc.append(text[i:j])
            co.append(text[i] + blank(text[i + 1 : j - 1]) + text[j - 1 : j] if j - i >= 2 else text[i:j])
            i = j
        else:
            nc.append(text[i])
            co.append(text[i])
            i += 1
    return "".join(nc), "".join(co)


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
                tail = text[cur:i].strip()
                if tail or args:
                    args.append(tail)
                return args, i
            depth -= 1
        elif c == "," and depth == 0:
            args.append(text[cur:i].strip())
            cur = i + 1
        i += 1
    return None


def load_templates(resw_files: list[Path]) -> dict[str, str]:
    templates: dict[str, str] = {}
    for resw in resw_files:
        for d in ET.parse(resw).getroot().iter("data"):
            templates[d.get("name", "")] = d.findtext("value") or ""
    return templates


def string_literals(text: str) -> list[str]:
    """The contents of every string literal in `text` (comments already blanked): regular, verbatim
    (`""` is a quote) and raw (triple-quote) literals, each ended by the same rules the C# lexer uses."""
    out: list[str] = []
    i, n = 0, len(text)
    while i < n:
        c = text[i]
        if c == "'":
            i = _skip_char(text, i)
        elif c == '"':
            j = _skip_string(text, i)
            if text.startswith('"""', i):
                q = 3
                while text.startswith('"', i + q):
                    q += 1
                out.append(text[i + q : j - q])
            elif text[i - 1 : i] == "@" or text[i - 2 : i] == "@$":
                out.append(text[i + 1 : j - 1].replace('""', '"'))
            else:
                out.append(text[i + 1 : j - 1])
            i = j
        else:
            i += 1
    return out


def _is_build_dir(rel_parts: tuple[str, ...]) -> bool:
    return bool({"obj", "bin"} & set(rel_parts))


def _cs_files(root: Path) -> list[Path]:
    """Product C#: app/src and app/tools, never build output."""
    found: list[Path] = []
    for sub in ("src", "tools"):
        base = root / "app" / sub
        if base.is_dir():
            found.extend(p for p in base.rglob("*.cs") if not _is_build_dir(p.relative_to(base).parts))
    return sorted(found)


def _resw_files(root: Path) -> list[Path]:
    src = root / "app" / "src"
    return sorted(p for p in src.rglob("en-US/Resources.resw") if not _is_build_dir(p.relative_to(src).parts))


@dataclass(frozen=True)
class Helper:
    """A wrapper `Name(string key, params object[] args)` whose body is string.Format(..., GetString(key), args)."""

    name: str
    decl_start: int
    body_range: tuple[int, int]


def _find_helpers(text: str, code: str) -> list[Helper]:
    helpers: list[Helper] = []
    for m in HELPER_DECL.finditer(text):
        key_param, arr_param = m.group(2), m.group(3)
        i = m.end()
        while i < len(text) and text[i].isspace():
            i += 1
        if text.startswith("=>", i):
            end = i
            depth = 0
            while end < len(code):
                ch = code[end]
                if ch in "([{":
                    depth += 1
                elif ch in ")]}":
                    depth -= 1
                elif ch == ";" and depth <= 0:
                    break
                end += 1
        elif text[i : i + 1] == "{":
            end, depth = i, 0
            while end < len(code):
                if code[end] == "{":
                    depth += 1
                elif code[end] == "}":
                    depth -= 1
                    if depth == 0:
                        break
                end += 1
        else:
            continue
        for fm in STRING_FORMAT.finditer(text, i, end):
            parsed = split_args(text, fm.end())
            args = parsed[0] if parsed else []
            if args and CULTURE.match(args[0]):
                args = args[1:]
            kv = KEY_VAR_ARG.match(args[0]) if args else None
            if len(args) == 2 and kv and kv.group(1) == key_param and args[1] == arr_param:
                helpers.append(Helper(m.group(1), m.start(1), (i, end)))
                break
    return helpers


def _ternary_split(arg: str) -> tuple[str, str] | None:
    """Split `cond ? a : b` at its depth-0 `?` and the matching `:`; None when `arg` is not a conditional."""
    depth, q, pending = 0, -1, 0
    i = 0
    while i < len(arg):
        c = arg[i]
        if c == '"':
            i = _skip_string(arg, i)
            continue
        if c == "'":
            i = _skip_char(arg, i)
            continue
        if c in "([{":
            depth += 1
        elif c in ")]}":
            depth -= 1
        elif depth == 0 and c == "?" and arg[i + 1 : i + 2] not in ("?", ".", "["):
            if q < 0:
                q = i
            else:
                pending += 1
        elif depth == 0 and c == ":" and q >= 0:
            if pending == 0:
                return arg[q + 1 : i].strip(), arg[i + 1 :].strip()
            pending -= 1
        i += 1
    return None


def _literal_keys(arg: str, consts: dict[str, str]) -> list[str] | None:
    """Every resw key `arg` can be: a bare literal or const, or a conditional whose branches all are."""
    one = _literal_key(arg, consts)
    if one is not None:
        return [one]
    split = _ternary_split(arg)
    if split is None:
        return None
    left, right = _literal_keys(split[0], consts), _literal_keys(split[1], consts)
    return left + right if left is not None and right is not None else None


def _literal_key(arg: str, consts: dict[str, str]) -> str | None:
    """The resw key of a bare `"Key"` or `Class.Const` argument, else None."""
    lit = LITERAL_KEY.match(arg)
    if lit:
        return lit.group(1)
    if DOTTED_NAME.match(arg):
        parts = arg.split(".")
        return consts.get(arg) or consts.get(".".join(parts[-2:]))
    return None


def _type_spans(code: str) -> list[tuple[int, int, str]]:
    """(open brace, close brace, name) for every type declaration with a body."""
    spans = []
    for m in TYPE_DECL.finditer(code):
        j = m.end()
        while j < len(code) and code[j] not in "{;":
            j += 1
        if j >= len(code) or code[j] == ";":
            continue
        depth, k = 0, j
        while k < len(code):
            if code[k] == "{":
                depth += 1
            elif code[k] == "}":
                depth -= 1
                if depth == 0:
                    break
            k += 1
        spans.append((j, k, m.group(1)))
    return spans


def _collect_consts(no_comments: str, code: str, consts: dict[str, str]) -> None:
    spans = _type_spans(code)
    for m in CONST.finditer(no_comments):
        chain = [name for (a, b, name) in sorted(spans) if a < m.start() < b]
        for start in range(len(chain)):
            consts[".".join([*chain[start:], m.group(1)])] = m.group(2)


def _object_array_args(arg: str) -> list[str] | None:
    m = OBJECT_ARRAY.match(arg)
    if not m:
        return None
    parsed = split_args(m.group(1) + ")", 0)
    return parsed[0] if parsed else None


def scan_repo(root: Path, known_dynamic: dict[str, int] | None = KNOWN_DYNAMIC) -> Result:
    """Scan app/src and app/tools. `known_dynamic=None` turns the dynamic-site allowlist check off."""
    result = Result()
    templates = load_templates(_resw_files(root))
    files = []
    for p in _cs_files(root):
        no_comments, code = mask(p.read_text(encoding="utf-8-sig"))
        files.append((p, no_comments, code))

    consts: dict[str, str] = {}
    literals: set[str] = set()
    for _path, no_comments, code in files:
        literals.update(string_literals(no_comments))
        _collect_consts(no_comments, code, consts)
    literals.update(consts.values())

    dynamic: dict[str, list[int]] = {}

    def check(key: str, rest: list[str], rel: str, line: int) -> None:
        if key not in templates:
            result.findings.append(Finding("MISSING-KEY", key, rel, line, "no such entry in any Resources.resw"))
            return
        result.checked += 1
        need = placeholder_count(templates[key])
        if len(rest) > need:
            result.findings.append(Finding("TOO-MANY-ARGS", key, rel, line, f"template takes {need}, call passes {len(rest)}"))
        elif len(rest) < need:
            result.findings.append(Finding("TOO-FEW-ARGS", key, rel, line, f"template takes {need}, call passes {len(rest)}"))

    for path, text, code in files:
        rel = path.relative_to(root).as_posix()
        array_vars = set(ARRAY_DECL.findall(code)) | set(ARRAY_VAR_INIT.findall(code))
        helpers = _find_helpers(text, code)

        def args_after(args: list[str]) -> list[str]:
            rest = args[1:]
            if len(rest) == 1 and (expanded := _object_array_args(rest[0])) is not None:
                rest = expanded
            return rest

        def line_of(index: int, text: str = text) -> int:
            return text.count("\n", 0, index) + 1

        for m in STRING_FORMAT.finditer(text):
            if any(h.body_range[0] <= m.start() < h.body_range[1] for h in helpers):
                continue  # the wrapper's own body: its call sites are checked below
            line = line_of(m.start())
            parsed = split_args(text, m.end())
            if parsed is None:
                dynamic.setdefault(rel, []).append(line)
                continue
            args = parsed[0]
            if args and CULTURE.match(args[0]):
                args = args[1:]
            if not args:
                continue
            key_match = KEY_ARG.match(args[0])
            rest = args_after(args)
            if not key_match or (len(rest) == 1 and rest[0] in array_vars):
                dynamic.setdefault(rel, []).append(line)
                continue
            key = key_match.group(1)
            if key is None:
                key = _literal_key(key_match.group(2), consts)
                if key is None:
                    dynamic.setdefault(rel, []).append(line)
                    continue
            check(key, rest, rel, line)

        for h in helpers:
            for m in re.finditer(rf"(?<![\w.]){re.escape(h.name)}\s*\(", text):
                if m.start() == h.decl_start:
                    continue
                line = line_of(m.start())
                parsed = split_args(text, m.end())
                keys = _literal_keys(parsed[0][0], consts) if parsed and parsed[0] else None
                rest = args_after(parsed[0]) if parsed and parsed[0] else []
                if keys is None or (len(rest) == 1 and rest[0] in array_vars):
                    dynamic.setdefault(rel, []).append(line)
                    continue
                for key in keys:
                    check(key, rest, rel, line)

    result.skipped = sum(len(v) for v in dynamic.values())
    if known_dynamic is not None:
        for rel, lines in sorted(dynamic.items()):
            allowed = known_dynamic.get(rel, 0)
            if len(lines) > allowed:
                for line in lines:
                    result.findings.append(
                        Finding(
                            "UNEXPECTED-DYNAMIC",
                            "",
                            rel,
                            line,
                            f"unresolvable string.Format site ({len(lines)} in file, {allowed} listed in KNOWN_DYNAMIC)",
                        )
                    )
        for rel, allowed in sorted(known_dynamic.items()):
            actual = len(dynamic.get(rel, []))
            if actual < allowed:
                result.findings.append(
                    Finding(
                        "STALE-DYNAMIC-ALLOWLIST", "", rel, 0, f"KNOWN_DYNAMIC lists {allowed}, the file has {actual}: lower or remove the entry"
                    )
                )

    for key, value in sorted(templates.items()):
        if placeholder_count(value) > 0 and key not in literals:
            result.findings.append(
                Finding("UNREFERENCED-TEMPLATE", key, "Resources.resw", 0, "has placeholders but no string literal or const in app/src names it")
            )
    return result


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description="Fail when string.Format arity disagrees with the Resources.resw template.")
    ap.add_argument("--root", type=Path, default=Path(__file__).resolve().parent.parent)
    ap.add_argument("--min-checked", type=int, default=MIN_CHECKED, help="fail if fewer call sites than this were checked")
    ap.add_argument("--no-dynamic-check", action="store_true", help="skip the KNOWN_DYNAMIC allowlist checks (for a synthetic tree)")
    args = ap.parse_args(argv)
    if not (args.root / "app" / "src").is_dir():
        print(f"ERROR: {args.root} does not look like this repo (no app/src); checked nothing", file=sys.stderr)
        return 2
    result = scan_repo(args.root, None if args.no_dynamic_check else KNOWN_DYNAMIC)
    for f in result.findings:
        print(f, file=sys.stderr)
    failed = bool(result.findings)
    if result.checked < args.min_checked:
        print(
            f"FAIL: only {result.checked} site(s) checked, below the floor of {args.min_checked}: the scanner stopped matching", file=sys.stderr
        )
        failed = True
    if failed:
        print(f"\nFAIL: {len(result.findings)} finding(s)", file=sys.stderr)
        return 1
    print(f"OK: {result.checked} string.Format site(s) match their resw template ({result.skipped} dynamic, all listed)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
