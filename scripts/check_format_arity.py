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
   A LITERAL template (`string.Format("x {0}", a)`, regular, verbatim or raw, not interpolated) is
   checked the same way, so excluding CodeQL's cs/invalid-string-formatting loses nothing here.
2. Wrapper helpers: a method `Name(string key, params object[] args)` whose body does
   `string.Format(..., GetString(key), args)` is detected; that one call is not a site, and EVERY
   call `Name("Key", a, b)` / `this.Name(...)` is checked like (1), in the helper's file and in the
   other files of the same `partial` class. A `Class.Const` key is resolved and a conditional key
   `c ? "A" : "B"` checks every branch.
3. Every resw value with a placeholder must be referenced by a string literal, or by a const that is
   used somewhere in the scanned code (UNREFERENCED-TEMPLATE), the "wired to nothing" shape.
4. A file that ends inside a string, char or comment (LEX-DESYNC) fails the run, naming the file, so a
   scanner desync can never pass silently.

What stays DYNAMIC (cannot be checked, listed in KNOWN_DYNAMIC): a key that is a variable, a `switch`
expression or an ambiguous const (same class and const name in two namespaces), an unparseable call,
a helper call whose first argument is not a literal key, and a spread of any array-typed sole argument
(declared `T[]`, `.ToArray()`, or a non-object/string array initializer), which is not one argument.
A helper called from a class that is not a partial sibling of its file is not followed.

HOW IT READS C# (and its limits)
--------------------------------
A text scanner, not a compiler: it blanks `//`, `///` and `/* */` comments, steps over regular,
verbatim, raw (triple-quote) and interpolated string literals and char literals, splits arguments
at depth 0, expands `new object[] { ... }` / `new string[] { ... }` / `new[] { ... }`, and resolves
`Class.Const` / `Ns.Class.Const` keys (`const string`, `static readonly string`) by namespace and enclosing-type span. Known gap: an interpolated
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
from collections import Counter
from dataclasses import dataclass, field
from pathlib import Path

CULTURE = re.compile(r"^(?:System\.Globalization\.)?CultureInfo\.(?:CurrentCulture|InvariantCulture|CurrentUICulture)$")
KEY_ARG = re.compile(
    r'^(?:(?:[A-Za-z_]\w*\.)*|\((?:[^()]|\([^()]*\))*\)\.)GetString\(\s*(?:"([^"\\]*)"|([A-Za-z_]\w*(?:\.[A-Za-z_]\w*)+))\s*\)$'
)
KEY_COND_ARG = re.compile(r"^(?:(?:[A-Za-z_]\w*\.)*|\((?:[^()]|\([^()]*\))*\)\.)GetString\((.+)\)$", re.DOTALL)
KEY_VAR_ARG = re.compile(r"^(?:[A-Za-z_]\w*\.)*GetString\(\s*([A-Za-z_]\w*)\s*\)$")
LITERAL_KEY = re.compile(r'^"([^"\\]*)"$')
DOTTED_NAME = re.compile(r"^[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)+$")
HELPER_DECL = re.compile(r"\b(\w+)\s*\(\s*string\s+(\w+)\s*,\s*params\s+object\s*\[\s*\]\s+(\w+)\s*\)")
STRING_FORMAT = re.compile(r"\b[Ss]tring\.Format\(")
ARRAY_DECL = re.compile(r"\b[\w.<>?]+\s*\[\s*\]\s+(\w+)")
ARRAY_VAR_INIT = re.compile(r"\bvar\s+(\w+)\s*=\s*new\s*(?:[\w.<>?]+\s*)?\[")
ARRAY_VAR_TOARRAY = re.compile(r"\bvar\s+(\w+)\s*=[^;\n]*\.ToArray\(\)")
NEW_ARRAY_ARG = re.compile(r"^new\s*(?:[\w.<>?]+\s*)?\[")
NAMESPACE = re.compile(r"\bnamespace\s+([\w.]+)")
IDENT = re.compile(r"\b[A-Za-z_]\w*\b")
NUMERIC_ARG = re.compile(r"^(?:[-+]?\d[\w.]*|true|false|null|default|'[^']*')$")
KEYWORDS = frozenset(
    [
        "return",
        "await",
        "yield",
        "throw",
        "else",
        "case",
        "in",
        "is",
        "as",
        "not",
        "and",
        "or",
        "new",
        "do",
        "when",
        "if",
        "while",
        "foreach",
        "for",
        "using",
        "lock",
        "out",
        "ref",
        "switch",
    ]
)
PLACEHOLDER = re.compile(r"\{(\d+)(?:[,:][^{}]*)?\}")
CONST = re.compile(r'\b(?:const|static\s+readonly)\s+string\s+(\w+)\s*=\s*"([^"\\]*)"')
TYPE_DECL = re.compile(r"\b(?:class|record|struct|interface)\s+(\w+)")
OBJECT_ARRAY = re.compile(r"^new\s*(?:(?:object|string)\s*)?\[\s*\]\s*\{(.*)\}$", re.DOTALL)
MIN_CHECKED = 20

# Files with call sites the scanner cannot resolve (wrapper helpers taking the key as a parameter,
# `params` spreads), mapped to the exact number of such sites. Their templates are covered only by
# UNREFERENCED-TEMPLATE. Exact counts on purpose: see the module docstring.
KNOWN_DYNAMIC: dict[str, int] = {
    "app/src/DnaEntropyGraph.App/Startup/StartupDataRoot.cs": 1,
    "app/src/DnaEntropyGraph.Presentation/Services/LeftRunningGuard.cs": 1,
    "app/src/DnaEntropyGraph.Presentation/Services/RunStallCoordinator.cs": 1,
    # _requestKey picks one of three request texts; each takes {0} (and some {1}).
    "app/src/DnaEntropyGraph.Presentation/ViewModels/BudgetSettingsViewModel.cs": 1,
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


def _prefix_has_at(text: str, i: int) -> bool:
    """True when the quote at text[i] is preceded by an `@` (verbatim) in `@"`, `$@"` or `@$"`."""
    k = i - 1
    while k >= 0 and i - k <= 3 and text[k] in "@$":
        if text[k] == "@":
            return True
        k -= 1
    return False


def _scan_string(text: str, i: int) -> tuple[int, bool]:
    """(index just past the literal whose opening quote is text[i], whether it was terminated).

    A regular literal ends at a line break (unterminated); a raw literal needs its own quote run."""
    at = _prefix_has_at(text, i)
    if text.startswith('"""', i) and not at:
        n = 3
        while text.startswith('"', i + n):
            n += 1
        end = text.find('"' * n, i + n)
        return (len(text), False) if end < 0 else (end + n, True)
    j = i + 1
    while j < len(text):
        c = text[j]
        if at:
            if c == '"':
                if text[j + 1 : j + 2] == '"':
                    j += 2
                    continue
                return j + 1, True
        else:
            if c == "\\":
                j += 2
                continue
            if c == '"':
                return j + 1, True
            if c in "\r\n":
                return j, False
        j += 1
    return len(text), False


def _skip_string(text: str, i: int) -> int:
    return _scan_string(text, i)[0]


def _scan_char(text: str, i: int) -> tuple[int, bool]:
    j = i + 1
    while j < len(text) and text[j] != "'":
        if text[j] in "\r\n":
            return j, False
        j += 2 if text[j] == "\\" else 1
    return (j + 1, True) if j < len(text) else (len(text), False)


def _skip_char(text: str, i: int) -> int:
    return _scan_char(text, i)[0]


def _literal_content(text: str, i: int, j: int) -> str:
    """The value text of the terminated literal text[i:j] (escapes of regular literals stay as written)."""
    if text.startswith('"""', i) and not _prefix_has_at(text, i):
        q = 3
        while text.startswith('"', i + q):
            q += 1
        return text[i + q : j - q]
    if _prefix_has_at(text, i):
        return text[i + 1 : j - 1].replace('""', '"')
    return text[i + 1 : j - 1]


def mask(text: str, problems: list[int] | None = None) -> tuple[str, str]:
    """Return (no_comments, code_only), both the same length and line layout as `text`.

    no_comments keeps strings and blanks every comment form; code_only also blanks the inside of
    string and char literals, so brace/paren scans see code only. When `problems` is given, the index
    of every unterminated string, char or block comment is appended to it.
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
            if j < 0 and problems is not None:
                problems.append(i)
            j = n if j < 0 else j + 2
            nc.append(blank(text[i:j]))
            co.append(blank(text[i:j]))
            i = j
        elif text[i] in "\"'":
            j, ok = _scan_string(text, i) if text[i] == '"' else _scan_char(text, i)
            if not ok and problems is not None:
                problems.append(i)
            nc.append(text[i:j])
            co.append(text[i] + blank(text[i + 1 : j - 1]) + text[j - 1 : j] if ok and j - i >= 2 else text[i] + blank(text[i + 1 : j]))
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
            out.append(_literal_content(text, i, j))
            i = j
        else:
            i += 1
    return out


def _whole_literal(arg: str) -> str | None:
    """The value of `arg` when it is exactly one non-interpolated string literal, else None."""
    arg = arg.strip()
    k = 1 if arg.startswith('@"') else 0
    if not arg.startswith('"', k):
        return None
    end, ok = _scan_string(arg, k)
    return _literal_content(arg, k, end) if ok and end == len(arg) else None


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
    inner_start: int  # the one string.Format call in the body that is the wrapper itself
    type_name: str
    partial: bool


def _find_helpers(text: str, code: str) -> list[Helper]:
    helpers: list[Helper] = []
    spans = _type_spans(code)
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
                enclosing = [sp for sp in spans if sp[0] < m.start() < sp[1]]
                type_name = max(enclosing)[2] if enclosing else ""
                partial = bool(type_name) and re.search(rf"\bpartial\s+(?:class|record|struct)\s+{re.escape(type_name)}\b", code) is not None
                helpers.append(Helper(m.group(1), m.start(1), fm.start(), type_name, partial))
                break
    return helpers


def _is_declaration(code: str, start: int) -> bool:
    """True when the name at code[start] is preceded by a return type or modifier, so it declares a method."""
    k = start - 1
    while k >= 0 and code[k].isspace():
        k -= 1
    if k < 0:
        return False
    if code[k] == ">":
        return code[k - 1 : k] != "="  # `=> Name(` is a call; `List<T> Name(` is a declaration
    if code[k] == "]":
        return True
    if code[k].isalnum() or code[k] == "_":
        e = k
        while k >= 0 and (code[k].isalnum() or code[k] == "_"):
            k -= 1
        return code[k + 1 : e + 1] not in KEYWORDS
    return False


def _is_array_spread(arg: str, array_vars: set[str]) -> bool:
    """A sole `args` argument that is array-typed is a params spread, not one value."""
    return arg in array_vars or arg.endswith(".ToArray()") or NEW_ARRAY_ARG.match(arg) is not None


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


def _literal_keys(arg: str, consts: dict[str, set[str]]) -> list[str] | None:
    """Every resw key `arg` can be: a bare literal or const, or a conditional whose branches all are."""
    one = _literal_key(arg, consts)
    if one is not None:
        return [one]
    split = _ternary_split(arg)
    if split is None:
        return None
    left, right = _literal_keys(split[0], consts), _literal_keys(split[1], consts)
    return left + right if left is not None and right is not None else None


def _get_string_conditional_keys(arg: str, consts: dict[str, set[str]]) -> list[str] | None:
    """Every key of `<recv>.GetString(c ? "A" : "B")` (nested ternaries too), else None.

    None when the argument is not that shape, when the parenthesis closing GetString( is not the last
    character (`GetString(c ? "A" : "B").Trim()`), or when any branch is not a literal/const key.
    """
    m = KEY_COND_ARG.match(arg)
    if not m:
        return None
    inner = m.group(1)
    depth = 0
    i = 0
    while i < len(inner):
        c = inner[i]
        if c == '"':
            i = _skip_string(inner, i)
            continue
        if c == "'":
            i = _skip_char(inner, i)
            continue
        if c in "([{":
            depth += 1
        elif c in ")]}":
            depth -= 1
            if depth < 0:
                return None
        i += 1
    if depth != 0 or _ternary_split(inner.strip()) is None:
        return None
    return _literal_keys(inner.strip(), consts)


def _literal_key(arg: str, consts: dict[str, set[str]]) -> str | None:
    """The resw key of a bare `"Key"` or `Ns.Class.Const` argument, else None (also None when ambiguous)."""
    lit = LITERAL_KEY.match(arg)
    if lit:
        return lit.group(1)
    if DOTTED_NAME.match(arg):
        parts = arg.split(".")
        for k in range(len(parts) - 1):
            values = consts.get(".".join(parts[k:]))
            if values:
                return next(iter(values)) if len(values) == 1 else None
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


def _collect_consts(no_comments: str, code: str, consts: dict[str, set[str]], decls: Counter[str], by_name: dict[str, set[str]]) -> None:
    spans = _type_spans(code)
    ns = NAMESPACE.search(code)
    prefix = ns.group(1).split(".") if ns else []
    for m in CONST.finditer(no_comments):
        chain = [name for (a, b, name) in sorted(spans) if a < m.start() < b]
        full = [*prefix, *chain, m.group(1)]
        for start in range(len(full) - 1):
            consts.setdefault(".".join(full[start:]), set()).add(m.group(2))
        decls[m.group(1)] += 1
        by_name.setdefault(m.group(1), set()).add(m.group(2))


def _object_array_args(arg: str) -> list[str] | None:
    m = OBJECT_ARRAY.match(arg)
    if not m:
        return None
    parsed = split_args(m.group(1) + ")", 0)
    return parsed[0] if parsed else None


@dataclass
class _File:
    rel: str
    text: str
    code: str
    array_vars: set[str]
    helpers: list[Helper]


def scan_repo(root: Path, known_dynamic: dict[str, int] | None = KNOWN_DYNAMIC) -> Result:
    """Scan app/src and app/tools. `known_dynamic=None` turns the dynamic-site allowlist check off."""
    result = Result()
    templates = load_templates(_resw_files(root))
    files: list[_File] = []
    for p in _cs_files(root):
        raw = p.read_text(encoding="utf-8-sig")
        problems: list[int] = []
        text, code = mask(raw, problems)
        rel = p.relative_to(root).as_posix()
        for index in problems:
            result.findings.append(
                Finding(
                    "LEX-DESYNC",
                    "",
                    rel,
                    raw.count("\n", 0, index) + 1,
                    "a string, char or comment is not terminated: the rest of the file was not scanned reliably",
                )
            )
        array_vars = set(ARRAY_DECL.findall(code)) | set(ARRAY_VAR_INIT.findall(code)) | set(ARRAY_VAR_TOARRAY.findall(code))
        files.append(_File(rel, text, code, array_vars, _find_helpers(text, code)))

    consts: dict[str, set[str]] = {}
    decls: Counter[str] = Counter()
    by_name: dict[str, set[str]] = {}
    literals: set[str] = set()
    idents: Counter[str] = Counter()
    for f in files:
        # a const's own declaring literal is not a reference; the const counts only when its name is used (below)
        literals.update(string_literals(CONST.sub(lambda m: " " * len(m.group(0)), f.text)))
        _collect_consts(f.text, f.code, consts, decls, by_name)
        idents.update(IDENT.findall(f.code))
    for name, values in by_name.items():
        if idents[name] > decls[name]:  # the const's name is used somewhere besides its declarations
            literals.update(values)

    dynamic: dict[str, list[int]] = {}

    def check_template(label: str, template: str, rest: list[str], rel: str, line: int) -> None:
        result.checked += 1
        need = placeholder_count(template)
        if len(rest) > need:
            result.findings.append(Finding("TOO-MANY-ARGS", label, rel, line, f"template takes {need}, call passes {len(rest)}"))
        elif len(rest) < need:
            result.findings.append(Finding("TOO-FEW-ARGS", label, rel, line, f"template takes {need}, call passes {len(rest)}"))

    def check(key: str, rest: list[str], rel: str, line: int) -> None:
        if key not in templates:
            result.findings.append(Finding("MISSING-KEY", key, rel, line, "no such entry in any Resources.resw"))
            return
        check_template(key, templates[key], rest, rel, line)

    def args_after(args: list[str]) -> list[str]:
        rest = args[1:]
        if len(rest) == 1 and (expanded := _object_array_args(rest[0])) is not None:
            rest = expanded
        return rest

    for f in files:
        skip = {h.inner_start for h in f.helpers}
        for m in STRING_FORMAT.finditer(f.text):
            if m.start() in skip:
                continue  # a wrapper's own call: its call sites are checked below
            line = f.text.count("\n", 0, m.start()) + 1
            parsed = split_args(f.text, m.end())
            if parsed is None:
                dynamic.setdefault(f.rel, []).append(line)
                continue
            args = parsed[0]
            if args and CULTURE.match(args[0]):
                args = args[1:]
            if not args:
                continue
            rest = args_after(args)
            spread = len(rest) == 1 and _is_array_spread(rest[0], f.array_vars)
            literal = _whole_literal(args[0])
            if literal is not None and not spread:
                check_template("literal " + literal[:30], literal, rest, f.rel, line)
                continue
            key_match = KEY_ARG.match(args[0])
            if not key_match and not spread and (cond_keys := _get_string_conditional_keys(args[0], consts)) is not None:
                for key in cond_keys:
                    check(key, rest, f.rel, line)
                continue
            if not key_match or spread:
                dynamic.setdefault(f.rel, []).append(line)
                continue
            key = key_match.group(1)
            if key is None:
                key = _literal_key(key_match.group(2), consts)
                if key is None:
                    dynamic.setdefault(f.rel, []).append(line)
                    continue
            check(key, rest, f.rel, line)

    for owner in files:
        for h in owner.helpers:
            targets = [owner]
            if h.partial:
                sibling = re.compile(rf"\bpartial\s+(?:class|record|struct)\s+{re.escape(h.type_name)}\b")
                targets += [g for g in files if g is not owner and sibling.search(g.code)]
            call = re.compile(rf"(?<![\w.])(?:this\.)?{re.escape(h.name)}\s*\(")
            for f in targets:
                for m in call.finditer(f.code):
                    if (f is owner and m.start() <= h.decl_start < m.end()) or _is_declaration(f.code, m.start()):
                        continue
                    parsed = split_args(f.text, m.end())
                    if parsed is None or not parsed[0] or NUMERIC_ARG.match(parsed[0][0]):
                        continue  # another overload (or no arguments), not this helper
                    line = f.text.count("\n", 0, m.start()) + 1
                    keys = _literal_keys(parsed[0][0], consts)
                    rest = args_after(parsed[0])
                    if keys is None or (len(rest) == 1 and _is_array_spread(rest[0], f.array_vars)):
                        dynamic.setdefault(f.rel, []).append(line)
                        continue
                    for key in keys:
                        check(key, rest, f.rel, line)

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
                Finding(
                    "UNREFERENCED-TEMPLATE", key, "Resources.resw", 0, "has placeholders but no string literal or used const in app/src names it"
                )
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
