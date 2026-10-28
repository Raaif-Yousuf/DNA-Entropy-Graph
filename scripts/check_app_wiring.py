"""Fail if a piece of the WinUI app is wired to nothing.

WHY THIS IS A GUARD AND NOT A CONVENTION
----------------------------------------
"Wired to nothing" is this project's named, recurring bug class (CLAUDE.md,
and the `wired-to-nothing` skill). `scripts/check_unused_fields.py` makes one
of its shapes mechanical on the Python side: a dataclass field that is parsed,
validated, schema-checked and never read. Its first run found nine real ones.

The C# app has the same bug class with different shapes, and until this script
there was nothing mechanical looking for any of them. They were all named in
CLAUDE.md's pitfalls section before a single one could be detected:

    a XAML binding to a property that does not exist compiles and shows blank;
    an ICommand nobody binds; a service never registered in DI; ... a setting
    saved and never read.

Each one compiles, runs, passes its tests, and does nothing. `dotnet build` has
nothing to say about any of them, and `Guards.Tests/DiResolutionTests` covers
exactly one: that every ViewModel resolves.

WHAT IT LOOKS FOR
-----------------
Nine findings, each with its own code so the allowlist can be specific:

`UNBOUND-COMMAND`
    A `[RelayCommand]` method (or a declared `ICommand`/`IRelayCommand`
    property) whose command name appears in no `.xaml` file at all, and in no
    `.cs` file outside the one that declares it. The generated `ICommand`
    exists so that XAML can bind to it. Nothing binds to it.

`UNBOUND-OBSERVABLE`
    A `[ObservableProperty]` field whose generated property name appears in no
    `.xaml` file, and in no `.cs` file outside the one that declares it. This
    is the sharpest of the seven: the *only* thing `[ObservableProperty]` adds
    over a plain field is a `PropertyChanged` notification, and the only
    consumer of that notification is a binding. No XAML naming it means the
    notification is raised into nothing.

`TEST-ONLY-COMMAND` / `TEST-ONLY-OBSERVABLE`
    Same, except the only references outside the declaring file are under
    `app/tests/`. A command only a test invokes is not in the product. This is
    the C# twin of check_unused_fields.py's `SERIALIZED-ONLY`: it exists so
    that a test asserting the member round-trips cannot make a dead member look
    consumed.

`CODE-ONLY-COMMAND` / `CODE-ONLY-OBSERVABLE`
    No XAML names it, but production C# outside the declaring file does. This
    is a weaker finding and often a real design smell rather than a bug: the
    member is used, but `[RelayCommand]`/`[ObservableProperty]` is the wrong
    tool for something no view binds to.

`UNBOUND-DISPLAY-PROPERTY` (#568)
    A public `string` property (or positional-record string parameter) of a type a view binds against (an
    `x:DataType` of a DataTemplate, or a page's ViewModel) that no `{x:Bind}`/`{Binding}` in any .xaml names. C# and
    test references do NOT count as use: the user never sees them (MEASURED 2026-10-03, #459: ReasonText). State
    flags, enums, models and identifier-like names (`Id`, `Path`, `Url`, `Value`...) are out of scope on purpose.

`MISSING-UID-RESOURCE`
    An `x:Uid="Foo"` in XAML with no matching `.resw` entry (`Foo` itself, or
    any `Foo.<property>`). MEASURED and written into `docs/ToTest.md`: this
    gives a window whose every affected label is **blank**, which reads as an
    unfinished layout rather than as a broken build. Nothing else in the
    toolchain says a word about it.

`ORPHAN-RESOURCE`
    A `.resw` entry whose uid root appears in no `x:Uid` and whose full name
    appears in no C# source. Copy that was written, reviewed, and that no user
    can ever see. Hard Rule 13 puts every user-visible string in the `.resw`,
    which makes the `.resw` the one place this is findable.

`UNREGISTERED-DEPENDENCY`
    A constructor parameter of a DI-constructed class (a ViewModel, or any
    type registered in `ServiceRegistration.cs`) whose type is never registered
    there. A service never registered resolves to a runtime exception on first
    page open, not at build (CLAUDE.md's DI row).

`UNREGISTERED-RESOLVE`
    A `GetRequiredService<T>()` in any production file (typically a factory lambda in `ServiceRegistration.cs`, or
    `AppStartup.cs`) whose `T` is not registered in any production file. `T` may be generic (`IOptions<X>`); a bare
    type parameter (`T`) is skipped. Factory-registered types skip the constructor check above (any argument counts
    as a factory: a lambda, a method group, a variable; `AddSingleton(_ => (IFoo)new Foo())` registers `IFoo`), so
    this is the check that covers what their lambda asks the container for (#601). Out of scope: a method group with
    no type argument (`AddSingleton(CreateFoo)`) names no type, and a registration made through a helper
    extension method this scanner cannot see.

`DEAD-REGISTRATION`
    A type registered in `ServiceRegistration.cs` that is never a constructor
    parameter anywhere, never appears in a `GetRequiredService`/`GetService`,
    and is not itself a ViewModel. The container builds it and nothing asks for
    it.

`DANGLING-BINDING`
    A `{Binding Foo...}` or `{x:Bind Foo...}` whose path root resolves to no
    member of the page's ViewModel or its code-behind. The ViewModel is found
    by this repo's own naming convention: `Views/NewRunPage.xaml` ->
    `NewRunViewModel`. A `{Binding}` to a name that does not exist is the
    original, canonical form of this bug: it compiles and shows blank.

WHAT THIS GUARD CANNOT SEE
--------------------------
It is a text scanner, not Roslyn, and the honest statement of its limits is the
part of this docstring worth reading twice:

- **Identifiers are matched by name, not resolved by type.** A command named
  `RefreshCommand` on one ViewModel is counted as bound if *any* XAML anywhere
  mentions `RefreshCommand`, even a different page binding a different
  ViewModel's identically named command. That is a false negative and it is
  invisible. The same masking bit `check_unused_fields.py` on the Python side
  (`WindowPlan.context`), which is why that script's docstring says a clean run
  means "no field with a name nothing reads", not "no unread field". The same
  caveat applies here, word for word.

  Three cheap refinements narrow that for C# member READS (#461, MEASURED 2026-10-02): comments are blanked
  before matching; a read through the literal name of another declared class (`RunOptions.ModelId`) is not a read
  of this member; and neither is a read through a variable whose declaration in the same file names another
  declared class (`RunOptions options` ... `options.ModelId`). A `var`, a field declared elsewhere or a property
  chain (`vm.Options.ModelId`) stays a name match, so those still mask. Constructor parameters with a default
  value parse to their type and, being optional, are never an UNREGISTERED-DEPENDENCY; `AddSingleton(new Foo())`
  registers `Foo`. A constructor parameter list and an `Add*<...>` argument list are cut by balanced scanning (#478),
  so a default such as `= default(T)` / `= new()` does not hide the parameters after it and
  `AddSingleton<IRepo<Run, Guid>, Repo>()` registers `IRepo<Run,Guid>` once. Still NOT a C# parser: a `(` or `<`
  inside a verbatim/raw string or an interpolated string's braces is not understood, and an attribute argument
  list on a parameter is cut only by the leading-attribute strip.

- **`DANGLING-BINDING` resolves the ViewModel by naming convention only.** A
  page whose DataContext is set to something the convention does not name is
  skipped entirely rather than guessed at, and the skip is reported in the
  `--verbose` output. Guessing would produce exactly the confident false
  positive that makes a guard get switched off.

- **A member consumed only through reflection, only by a generated binding
  helper, or only from a `.xaml` this scanner cannot parse, reads as dead.**
  That is a false positive, and it is what the allowlist is for.

- **It reports what it READ, not only what it objected to.** Every run prints
  the file counts it scanned. A guard that silently scans zero files is the
  failure mode this repo has already shipped once: `ci-app.yml`'s test step
  carried VSTest options against Microsoft.Testing.Platform projects and
  reported `Zero tests ran` with an exit code that could have been 0.
  `scripts/check_guard_drift.py` exists for that shape; the counts here are so
  a human can see it without running another script.

THE ALLOWLIST
-------------
`scripts/app_wiring_allowlist.json` maps `"CODE:Symbol"` to a reason string,
e.g. `"UNBOUND-OBSERVABLE:NewRunViewModel.ModelId": "bound by the New Run page
in #63, not yet written"`. It is checked in **both** directions: an entry whose
finding no longer fires fails too, so the file cannot rot into a list of things
that used to be true. That bidirectional check is not decoration - the
equivalent allowlist on the Python side went stale within an hour of being
written, and its guard said so.

USAGE
-----
    python scripts/check_app_wiring.py                # check app/
    python scripts/check_app_wiring.py --verbose      # also print what was read and skipped
    python scripts/check_app_wiring.py --json         # machine-readable findings
    python scripts/check_app_wiring.py --self-test    # synthetic fixtures, never the real tree

Exit code 0 when clean, 1 on any finding. Run from the repo root.

`--self-test` builds throwaway trees in a temp directory and asserts that each
of the nine findings **fires** on a broken tree and that a correctly wired tree
is **clean**. That second half is the one that matters: this repo has shipped a
guard that could not fail (`a-self-test-that-always-passes`), and a self-test
that only ever checks the failing direction would not have caught it.
"""

from __future__ import annotations

import argparse
import json
import re
import sys
import tempfile
from collections.abc import Callable
from dataclasses import dataclass
from pathlib import Path

# Script-relative, never cwd-relative (MEASURED 2026-10-02: a worktree copy run from the
# shared checkout silently scanned the shared tree).
REPO_ROOT = Path(__file__).resolve().parents[1]
ALLOWLIST_PATH = REPO_ROOT / "scripts" / "app_wiring_allowlist.json"
DEFAULT_ROOT = REPO_ROOT / "app"

# The five WinUI/MVVM idioms this scanner understands, as regexes over C# text.
# Deliberately anchored on the attribute, not on the member shape: an
# [ObservableProperty] field and a hand-written property are different bugs.
_OBSERVABLE_FIELD = re.compile(
    r"\[ObservableProperty(?:\([^)]*\))?\]\s*"
    r"(?:\[[^\]]*\]\s*)*"
    r"(?:private|protected|internal|public)?\s*"
    r"(?:readonly\s+)?"
    r"[\w\.\?<>\[\], ]+?\s+"
    r"(_\w+)\s*(?:=[^;]*)?;",
    re.MULTILINE,
)

_RELAY_COMMAND_METHOD = re.compile(
    r"\[RelayCommand(?:\([^)]*\))?\]\s*"
    r"(?:\[[^\]]*\]\s*)*"
    r"(?:private|protected|internal|public)?\s*"
    r"(?:static\s+)?(?:async\s+)?"
    r"[\w\.\?<>\[\], ]+?\s+"
    r"(\w+)\s*\(",
    re.MULTILINE,
)

_DECLARED_COMMAND_PROPERTY = re.compile(
    r"public\s+(?:I?RelayCommand|ICommand)(?:<[^>]*>)?\s+(\w*Command)\s*(?:\{|=>)",
    re.MULTILINE,
)

_CLASS_DECL = re.compile(
    r"(?:^|\n)\s*(?:public|internal|private|protected)?\s*"
    r"(?:sealed\s+|abstract\s+|static\s+|partial\s+)*"
    r"(?:class|record)\s+(\w+)",
)

# The generic form `Add*<A, B<C, D>>(...)`: only the opening is matched here; `_add_service_calls` finds the
# matching `>` by balanced scanning (#478), because a regex stops at the first `>` of a nested generic.
_ADD_SERVICE_OPEN = re.compile(r"\.Add(?:Singleton|Transient|Scoped)\s*<")
# The instance form (#461): `services.AddSingleton(new Foo())` / `AddSingleton(new Foo { ... })`.
_ADD_SERVICE_INSTANCE = re.compile(r"\.Add(?:Singleton|Transient|Scoped)\s*\(\s*new\s+([\w.]+)")
# A factory registration (#530): `AddSingleton<T>(sp => ...)`, `AddSingleton<I, T>((IServiceProvider _) => ...)`. The
# container runs the lambda and never constructor-injects T, so T's constructor parameters are the lambda's business.
#
# #601: ANY argument is a factory or an instance, not only a lambda: a method group (`AddSingleton<T>(CreateT)`) or a
# variable is never constructor-injected either. Only the empty `AddSingleton<T>()` is constructor-checked.
_FACTORY_ARGUMENT = re.compile(r"\s*\(\s*(?!\))")
# The inferred-type factory form: `AddSingleton(_ => new SqliteDatabase(path))` registers the type the lambda news up,
# or the cast type when the body is cast (`_ => (IFoo)new Foo()` registers IFoo: that is what the lambda returns).
# A method group with no type argument (`AddSingleton(CreateFoo)`) names no type and stays out of scope.
_ADD_SERVICE_INFERRED = re.compile(
    r"\.Add(?:Singleton|Transient|Scoped)\s*\(\s*(?:static\s+)?(?:async\s+)?(?:\w+|\((?:[^()]|\([^()]*\))*\))\s*=>\s*"
    r"(?:\(\s*(?P<cast>[\w.<>?, ]+?)\s*\)\s*)?new\s+(?P<new>[\w.]+)"
)
_GET_SERVICE = re.compile(r"\.Get(?:Required)?Service\s*<\s*([\w\.]+)\s*>")
# Only the required resolve throws on a missing registration; GetService<T> returns null by design. The type argument
# is cut by balanced scanning (#601: `GetRequiredService<IOptions<X>>()` is one generic type, not a skipped match).
_GET_REQUIRED_SERVICE_OPEN = re.compile(r"\.GetRequiredService\s*<")
# A generic type parameter (`GetRequiredService<T>()` in a helper) names no concrete registration.
# A generic argument list longer than this is not a type this guard reads (and bounds every scan, #601).
_GENERIC_ARGUMENT_LIMIT = 400
_TYPE_PARAMETER = re.compile(r"^T(?:[A-Z]?|Service|Impl\w*)$")


def _inferred_registration(match: re.Match[str]) -> str:
    """The type an inferred-type factory registers: the cast type when there is one, else the type it news up."""
    return _normalise_type(match.group("cast") or match.group("new"))


_X_UID = re.compile(r'x:Uid\s*=\s*"([^"]+)"')
_RESW_DATA = re.compile(r'<data\s+name\s*=\s*"([^"]+)"')

# A markup extension body: {Binding ...} or {x:Bind ...}. The path is either the
# first positional token or an explicit Path=.
_MARKUP = re.compile(r"\{\s*(x:Bind|Binding)\s*([^}]*)\}")

# A DataTemplate with an x:DataType: its {x:Bind} paths are members of that item type, not of the page's
# ViewModel, and the XAML compiler already checks them against the type (a wrong name is a build error).
# The tags of a DataTemplate, and its x:DataType (double- or single-quoted). Templates nest (a list of groups whose
# template holds a list of items), so the spans are matched with a stack, never with a non-greedy regex that would end an
# outer template at the first inner closing tag.
_TEMPLATE_TAG = re.compile(r"<DataTemplate\b([^>]*)>|</DataTemplate\s*>")
_DATA_TYPE_ATTR = re.compile(r"\bx:DataType\s*=\s*([\"'])(?:[\w]+:)?([\w\.]+)\1")

# Types that are constructor parameters but are never DI registrations: a
# CancellationToken, a primitive, a string. Listing them beats a heuristic that
# guesses at "looks like an interface".
_NON_SERVICE_PARAM_TYPES = frozenset(
    {
        "string",
        "int",
        "long",
        "bool",
        "double",
        "float",
        "decimal",
        "object",
        "Guid",
        "CancellationToken",
        "TimeSpan",
        "DateTime",
        "DateTimeOffset",
        "IServiceProvider",
    }
)


@dataclass(frozen=True)
class Finding:
    code: str
    symbol: str
    detail: str
    path: str

    @property
    def key(self) -> str:
        return f"{self.code}:{self.symbol}"

    def render(self) -> str:
        return f"  {self.code:<24} {self.symbol}\n      {self.detail}\n      {self.path}"


@dataclass
class Scan:
    """Everything read off disk once, so nothing is parsed twice."""

    cs_files: dict[Path, str]
    xaml_files: dict[Path, str]
    resw_files: dict[Path, str]
    skipped: list[str]

    @property
    def xaml_text(self) -> str:
        return "\n".join(self.xaml_files.values())

    def production_cs(self, exclude: Path) -> dict[Path, str]:
        return {p: t for p, t in self.cs_files.items() if p != exclude and not _is_test_path(p)}

    def test_cs(self, exclude: Path) -> dict[Path, str]:
        return {p: t for p, t in self.cs_files.items() if p != exclude and _is_test_path(p)}


def _is_test_path(path: Path) -> bool:
    parts = {p.lower() for p in path.parts}
    return "tests" in parts


def _is_generated(path: Path) -> bool:
    parts = {p.lower() for p in path.parts}
    return bool(parts & {"bin", "obj", "generated"}) or path.name.endswith(".g.cs")


def read_tree(root: Path) -> Scan:
    cs_files: dict[Path, str] = {}
    xaml_files: dict[Path, str] = {}
    resw_files: dict[Path, str] = {}
    skipped: list[str] = []

    for path in sorted(root.rglob("*")):
        if not path.is_file() or _is_generated(path):
            continue
        suffix = path.suffix.lower()
        if suffix not in {".cs", ".xaml", ".resw"}:
            continue
        try:
            text = path.read_text(encoding="utf-8")
        except (OSError, UnicodeDecodeError) as exc:  # pragma: no cover - IO edge
            skipped.append(f"{path}: {exc}")
            continue
        if suffix == ".cs":
            cs_files[path] = text
        elif suffix == ".xaml":
            xaml_files[path] = text
        else:
            resw_files[path] = text

    return Scan(cs_files, xaml_files, resw_files, skipped)


def _generated_property_name(field_name: str) -> str:
    """`_selectedInputPath` -> `SelectedInputPath`, the toolkit's own rule."""
    name = field_name.lstrip("_")
    if not name:
        return field_name
    return name[0].upper() + name[1:]


def _generated_command_name(method_name: str) -> str:
    """`BrowseAsync` -> `BrowseCommand`; `Browse` -> `BrowseCommand`."""
    base = method_name.removesuffix("Async")
    return f"{base}Command"


def _mentions(identifier: str, texts: dict[Path, str]) -> list[Path]:
    pattern = re.compile(rf"\b{re.escape(identifier)}\b")
    return [p for p, t in texts.items() if pattern.search(t)]


# Strings are matched first and kept, so `"http://x"` is not read as the start of a `//` comment.
_COMMENT_OR_STRING = re.compile(
    r'@"(?:[^"]|"")*"|"(?:[^"\\\n]|\\.)*"|//[^\n]*|/\*.*?\*/',
    re.DOTALL,
)


def _strip_comments(text: str) -> str:
    """Blank `//` and `/* */` comments (so a comment spelling `X.Member` is not a read); string literals stay."""
    return _COMMENT_OR_STRING.sub(lambda m: m.group(0) if m.group(0)[0] in '@"' else " ", text)


def _member_mentions(identifier: str, texts: dict[Path, str], owner: str | None = None, class_names: frozenset[str] = frozenset()) -> list[Path]:
    """Files that READ this member, as opposed to merely spelling the name.

    A bare name match counts an unrelated type's identically named property as
    a consumer: `RunOptions.ModelId`'s own declaration would "consume"
    `NewRunViewModel.ModelId`, which is not a read of anything. Requiring a
    member access (`x.ModelId`) or a `nameof(ModelId)` excludes declarations,
    and that is the whole difference between a read and a coincidence. It is
    still name-matched, not type-resolved - see this script's docstring, with
    two cheap refinements (#461, MEASURED 2026-10-02): comments are blanked first, and when the
    receiver is the literal name of ANOTHER class declared in the tree (`RunOptions.ModelId`) it is
    a read of that class, not of `owner`. An instance receiver (`vm.ModelId`) stays a match.
    """
    pattern = re.compile(rf"(?P<recv>\w+)?(?P<access>\s*\.\s*|nameof\(\s*(?:[\w.]+\.)?){re.escape(identifier)}\b")
    hits: list[Path] = []
    for path, text in texts.items():
        for match in pattern.finditer(_strip_comments(text)):
            recv = match.group("recv")
            other_type = owner is not None and recv in class_names and recv != owner
            if owner is not None and recv and not other_type:
                # An instance whose declaration (`RunOptions options`) is visible in this file: its type decides.
                declared = re.search(rf"(?<![\w.])([A-Z]\w*)(?:<[^>;]*>)?\??\s+{re.escape(recv)}\b", _strip_comments(text))
                other_type = bool(declared) and declared.group(1) in class_names and declared.group(1) != owner
            if match.group("access").startswith("nameof"):
                other_type = False
            if other_type:
                continue
            hits.append(path)
            break
    return hits


def _owning_class(text: str, offset: int) -> str:
    """The nearest class/record declared before `offset`, or `<file>`."""
    best = "<file>"
    for match in _CLASS_DECL.finditer(text):
        if match.start() > offset:
            break
        best = match.group(1)
    return best


def _members_of(text: str) -> set[str]:
    """Every identifier a XAML path could plausibly resolve to on this type."""
    members: set[str] = set()
    for match in _OBSERVABLE_FIELD.finditer(text):
        members.add(_generated_property_name(match.group(1)))
    for match in _RELAY_COMMAND_METHOD.finditer(text):
        members.add(_generated_command_name(match.group(1)))
        members.add(match.group(1))
    for match in _DECLARED_COMMAND_PROPERTY.finditer(text):
        members.add(match.group(1))
    # Plain public properties and public methods.
    for match in re.finditer(
        r"public\s+(?:static\s+)?(?:readonly\s+)?[\w\.\?<>\[\], ]+?\s+(\w+)\s*(?:\{|=>|\()",
        text,
    ):
        members.add(match.group(1))
    return members


def _template_spans(text: str) -> list[tuple[int, int, str | None]]:
    """(start, end, item type or None) of every DataTemplate, nested ones included. A template with no x:DataType has no type."""
    spans: list[tuple[int, int, str | None]] = []
    stack: list[tuple[int, str | None]] = []
    for tag in _TEMPLATE_TAG.finditer(text):
        if tag.group(0).startswith("</"):
            if stack:
                start, item_type = stack.pop()
                spans.append((start, tag.end(), item_type))
        else:
            data_type = _DATA_TYPE_ATTR.search(tag.group(1))
            stack.append((tag.start(), data_type.group(2).split(".")[-1] if data_type else None))
    return spans


def _type_declaration(text: str, name: str) -> re.Match[str] | None:
    """The `class|record|struct <name>` declaration (optionally `record class|struct`, with generic parameters)."""
    return re.search(rf"\b(?:record\s+(?:class|struct)|record|class|struct)\s+{re.escape(name)}\b(?:<[^>{{(]*>)?", text)


def _type_body(text: str, name: str) -> tuple[str, str]:
    """(positional-record parameter list, the type's own body) for `name`, either possibly empty.

    The body is the text between the type's braces, so a member of some other type in the same file is not counted
    as a member of this one. A `record R(int A);` has parameters and no body.
    """
    declaration = _type_declaration(text, name)
    if declaration is None:
        return "", ""
    index = declaration.end()
    parameters = ""
    # A positional record (or a primary-constructor type) lists its parameters right after the name.
    after = text[index:].lstrip()
    if after.startswith("("):
        start = index + (len(text[index:]) - len(after))
        depth = 0
        for offset in range(start, len(text)):
            if text[offset] == "(":
                depth += 1
            elif text[offset] == ")":
                depth -= 1
                if depth == 0:
                    parameters = text[start + 1 : offset]
                    index = offset + 1
                    break
    brace = re.search(r"[{;]", text[index:])
    if brace is None or brace.group(0) == ";":
        return parameters, ""
    open_at = index + brace.start()
    depth = 0
    for offset in range(open_at, len(text)):
        if text[offset] == "{":
            depth += 1
        elif text[offset] == "}":
            depth -= 1
            if depth == 0:
                return parameters, text[open_at + 1 : offset]
    return parameters, text[open_at + 1 :]


def _type_members(text: str, name: str) -> set[str]:
    """Members a XAML path could resolve to on the type `name`: its own body, plus the parameters of a positional
    record (the compiler makes them properties). A plain class's primary-constructor parameters are NOT members."""
    parameters, body = _type_body(text, name)
    members = _members_of(body)
    declaration = _type_declaration(text, name)
    if declaration is not None and declaration.group(0).lstrip().startswith("record"):
        for token in _split_top_level(parameters):
            declared, _ = _cut_default(token)
            declared = re.sub(r"^(?:\[[^\]]*\]\s*)+", "", declared.strip())
            pieces = declared.rsplit(None, 1)
            if len(pieces) == 2:
                members.add(pieces[1])
    return members


def _split_top_level(body: str) -> list[str]:
    """Split a parameter list on commas that are not inside <>, (), [] or a string literal."""
    parts: list[str] = []
    depth = 0
    in_string = False
    current = ""
    for char in body + ",":
        if char == '"' and not current.endswith("\\"):
            in_string = not in_string
        if not in_string:
            if char in "<([":
                depth += 1
            elif char in ">)]":
                depth -= 1
            if char == "," and depth == 0:
                parts.append(current.strip())
                current = ""
                continue
        current += char
    return [p for p in parts if p]


def _cut_default(token: str) -> tuple[str, bool]:
    """(declaration without its default value, whether it had one). The default starts at the first `=` outside
    <>, () and [] (a generic's own `=` cannot occur there)."""
    depth = 0
    for index, char in enumerate(token):
        if char in "<([":
            depth += 1
        elif char in ">)]":
            depth -= 1
        elif char == "=" and depth == 0:
            return token[:index], True
    return token, False


def _balanced_end(text: str, start: int, open_char: str, close_char: str, limit: int | None = None) -> int | None:
    """Index of the bracket that closes the one at `text[start]`, skipping string and char literals; None when it
    never closes (#478: a regex `([^)]*)` stops at the first `)` of a default such as `= default(T)`).

    `limit` caps how far past `start` the scan may go (linear per call, so a file full of unclosed `<` stays linear
    overall, #601). A generic argument list never holds `;`, `{` or `}`, so for `<` the scan also ends at those."""
    depth = 0
    quote = ""
    index = start
    end = len(text) if limit is None else min(len(text), start + limit)
    while index < end:
        char = text[index]
        if open_char == "<" and not quote and char in ";{}":
            return None
        if quote:
            if char == "\\":
                index += 1
            elif char == quote:
                quote = ""
        elif char in "\"'":
            quote = char
        elif char == open_char:
            depth += 1
        elif char == close_char:
            depth -= 1
            if depth == 0:
                return index
        index += 1
    return None


def _normalise_type(type_text: str) -> str:
    """A type as the guard compares it: no whitespace, no trailing `?`, the namespace dropped from the head only
    (`Demo.IRepo<Run, Guid>?` -> `IRepo<Run,Guid>`). Registrations and constructor parameters both go through here."""
    compact = re.sub(r"\s+", "", type_text).rstrip("?")
    head, bracket, rest = compact.partition("<")
    return head.split(".")[-1] + bracket + rest


def _add_service_calls(text: str) -> list[tuple[str, int]]:
    """Every `Add{Singleton,Transient,Scoped}<...>` call as (the text between the outer `<` `>`, the index just
    after that `>`). The closing `>` is found by balanced scanning, so `<IRepo<Run, Guid>, Repo>` is one argument
    list. A `;` inside means the angle bracket was not a generic argument list at all (a comparison), so it is skipped."""
    calls: list[tuple[str, int]] = []
    for match in _ADD_SERVICE_OPEN.finditer(text):
        open_at = match.end() - 1
        close_at = _balanced_end(text, open_at, "<", ">", _GENERIC_ARGUMENT_LIMIT)
        if close_at is None:
            continue
        arguments = text[open_at + 1 : close_at]
        if ";" not in arguments:
            calls.append((arguments, close_at + 1))
    return calls


def _required_resolves(text: str) -> list[tuple[str, int]]:
    """Every `.GetRequiredService<T>()` as (T normalised, offset). T is cut by balanced scanning, so a generic such as
    `IOptions<X>` is one type; `IServiceProvider` itself is not a registration."""
    resolves: list[tuple[str, int]] = []
    for match in _GET_REQUIRED_SERVICE_OPEN.finditer(text):
        close_at = _balanced_end(text, match.end() - 1, "<", ">", _GENERIC_ARGUMENT_LIMIT)
        if close_at is None:
            continue
        resolved = _normalise_type(text[match.end() : close_at])
        if resolved and ";" not in resolved and resolved != "IServiceProvider":
            resolves.append((resolved, match.start()))
    return resolves


def _constructor_parameters(text: str, class_name: str, skip_defaults: bool = False) -> list[str]:
    """Parameter type names of `public <class_name>(...)`, in order.

    A default value (`Action<string, JobPhase>? onPhase = null`) is cut off first (#461, MEASURED 2026-10-02: the
    old whitespace split read the type as `=`). Generic arguments are kept, spaces removed. With `skip_defaults`
    the optional parameters are left out: the container supplies the default when nothing is registered, so an
    unregistered optional parameter is not an UNREGISTERED-DEPENDENCY.
    """
    match = re.search(rf"public\s+{re.escape(class_name)}\s*\(", text)
    if not match:
        return []
    close_at = _balanced_end(text, match.end() - 1, "(", ")")
    if close_at is None:
        return []
    types: list[str] = []
    for token in _split_top_level(text[match.end() : close_at].strip()):
        declaration, has_default = _cut_default(token)
        if has_default and skip_defaults:
            continue
        declaration = re.sub(r"^(?:\[[^\]]*\]\s*)+", "", declaration.strip())
        pieces = declaration.rsplit(None, 1)
        if len(pieces) < 2:
            continue
        types.append(_normalise_type(pieces[0]))
    return types


def _factory_only_types(text: str) -> set[str]:
    """Types every one of whose registrations is a factory (`Add*<T>(sp => ...)`): the container never
    constructor-injects them, so their constructors are the lambda's business. A type that is ALSO registered
    plainly (`AddTransient<T>()`) is not in this set: that registration is still constructor-injected."""
    factory: set[str] = set()
    plain: set[str] = set()
    for arguments, end in _add_service_calls(text):
        names = {_normalise_type(t) for t in _split_top_level(arguments)}
        (factory if _FACTORY_ARGUMENT.match(text, end) else plain).update(names)
    for match in _ADD_SERVICE_INFERRED.finditer(text):
        factory.add(_inferred_registration(match))
    return factory - plain


def _registered_types(text: str) -> tuple[set[str], set[str]]:
    """Types named in Add*<...> calls, split into (every type, service types).

    `AddSingleton<IFilePicker, FilePickerService>()` registers two type names,
    but only the FIRST is what a consumer asks for. The implementation type is
    never injected by name, so counting it as a dead registration would be a
    confident false positive on the single most common registration shape.
    """
    every: set[str] = set()
    services: set[str] = set()
    for arguments, _end in _add_service_calls(text):
        tokens = [_normalise_type(t) for t in _split_top_level(arguments)]
        tokens = [t for t in tokens if t]
        if not tokens:
            continue
        every.update(tokens)
        services.add(tokens[0])
    inferred = [match.group(1).split(".")[-1] for match in _ADD_SERVICE_INSTANCE.finditer(text)]
    inferred += [_inferred_registration(match) for match in _ADD_SERVICE_INFERRED.finditer(text)]
    for name in inferred:
        every.add(name)
        services.add(name)
    return every, services


# --------------------------------------------------------------------------
# The nine checks.
# --------------------------------------------------------------------------


def _check_members(scan: Scan) -> list[Finding]:
    findings: list[Finding] = []
    xaml_text = scan.xaml_text
    class_names = frozenset(m.group(1) for text in scan.cs_files.values() for m in _CLASS_DECL.finditer(text))

    for path, text in scan.cs_files.items():
        if _is_test_path(path):
            continue

        candidates: list[tuple[str, str, str]] = []  # (identifier, kind, origin)
        for match in _OBSERVABLE_FIELD.finditer(text):
            name = _generated_property_name(match.group(1))
            owner = _owning_class(text, match.start())
            candidates.append((name, "OBSERVABLE", f"{owner}.{name}"))
        for match in _RELAY_COMMAND_METHOD.finditer(text):
            name = _generated_command_name(match.group(1))
            owner = _owning_class(text, match.start())
            candidates.append((name, "COMMAND", f"{owner}.{name}"))
        for match in _DECLARED_COMMAND_PROPERTY.finditer(text):
            name = match.group(1)
            owner = _owning_class(text, match.start())
            candidates.append((name, "COMMAND", f"{owner}.{name}"))

        for identifier, kind, symbol in candidates:
            if re.search(rf"\b{re.escape(identifier)}\b", xaml_text):
                continue
            member_owner = symbol.split(".")[0]
            prod = _member_mentions(identifier, scan.production_cs(exclude=path), member_owner, class_names)
            tests = _member_mentions(identifier, scan.test_cs(exclude=path), member_owner, class_names)
            noun = "command" if kind == "COMMAND" else "observable property"
            a_noun = "A command" if kind == "COMMAND" else "An observable property"
            generator = "[RelayCommand]" if kind == "COMMAND" else "[ObservableProperty]"
            if prod:
                findings.append(
                    Finding(
                        f"CODE-ONLY-{kind}",
                        symbol,
                        f"No .xaml names this {noun}; only C# does "
                        f"({', '.join(str(p) for p in prod[:3])}). "
                        f"{generator} exists to feed a binding.",
                        str(path),
                    )
                )
            elif tests:
                findings.append(
                    Finding(
                        f"TEST-ONLY-{kind}",
                        symbol,
                        f"The only references outside the declaring file are tests "
                        f"({', '.join(str(p) for p in tests[:3])}). "
                        f"{a_noun} only a test drives is not in the product.",
                        str(path),
                    )
                )
            else:
                findings.append(
                    Finding(
                        f"UNBOUND-{kind}",
                        symbol,
                        f"No .xaml and no other .cs names this {noun}. {generator} generates it so a view can bind to it; nothing does.",
                        str(path),
                    )
                )
    return findings


# A plain public instance property: `public string Foo { get; ... }` or `public string Foo => ...`.
_PLAIN_PROPERTY = re.compile(
    r"(?<![\w.])public\s+(?P<mods>(?:(?:static|virtual|override|new|required|abstract|sealed|readonly|const)\s+)*)"
    r"(?P<type>[\w\.\?<>\[\], ]+?)\s+(?P<name>\w+)\s*(?:\{\s*(?:\[[^\]]*\]\s*)*(?:get|set|init)\b|=>)"
)


# Text a user reads is a `string`. State flags, enums and models (`Status`, `Run`, `IsOk`) are read by logic and are
# legitimately unbound, so only string-typed properties are held to a binding; a name that is an identifier or a
# location rather than copy (`JobId`, `Path`, `ActionUrl`, `WireName`) is not display text either.
_STRING_TYPE = re.compile(r"^(?:System\.)?[sS]tring\??$")
_IDENTIFIER_NAME = re.compile(r"(?:Id|Path|Url|Uri|Folder|WireName|Key)$|^Value$")


def _is_display_property(type_text: str, name: str) -> bool:
    return bool(_STRING_TYPE.match(type_text.strip())) and not _IDENTIFIER_NAME.search(name)


def _display_properties(text: str, type_name: str) -> list[str]:
    """Public instance string properties a view could bind on `type_name`: its plain properties and, for a positional
    record, its parameters. Commands are the UNBOUND-COMMAND check's business, not this one's."""
    parameters, body = _type_body(text, type_name)
    names: list[str] = []
    for match in _PLAIN_PROPERTY.finditer(body):
        mods = match.group("mods").split()
        if {"static", "override", "const"} & set(mods) or not _is_display_property(match.group("type"), match.group("name")):
            continue
        names.append(match.group("name"))
    declaration = _type_declaration(text, type_name)
    if declaration is not None and declaration.group(0).lstrip().startswith("record"):
        for token in _split_top_level(parameters):
            declared, _ = _cut_default(token)
            declared = re.sub(r"^(?:\[[^\]]*\]\s*)+", "", declared.strip())
            pieces = declared.rsplit(None, 1)
            if len(pieces) == 2 and _is_display_property(pieces[0], pieces[1]):
                names.append(pieces[1])
    return list(dict.fromkeys(names))


def _display_property_candidates(scan: Scan) -> list[tuple[str, str, Path]]:
    """(type, property, declaring file) for every type a view binds against: each `x:DataType` of a DataTemplate and
    each page's ViewModel (by the naming convention). A type the tree does not declare is skipped (reported by DANGLING-BINDING)."""
    types: set[str] = set()
    for text in scan.xaml_files.values():
        types.update(cls for _s, _e, cls in _template_spans(text) if cls)
    for xaml_path in scan.xaml_files:
        resolved = _viewmodel_for(xaml_path, scan)
        if resolved is not None:
            types.add(resolved[0])
    candidates: list[tuple[str, str, Path]] = []
    for type_name in sorted(types):
        for path, text in scan.cs_files.items():
            if _is_test_path(path) or not re.search(rf"\b(?:class|record)\s+{re.escape(type_name)}\b", text):
                continue
            candidates.extend((type_name, prop, path) for prop in _display_properties(text, type_name))
            break
    return candidates


def _check_display_properties(scan: Scan) -> list[Finding]:
    """UNBOUND-DISPLAY-PROPERTY (#568): a public property of a bound type that no `{x:Bind}`/`{Binding}` in any .xaml
    names. C# and test references do NOT count as use: a property the ViewModel computes and no view shows is the
    wired-to-nothing bug with every gate green (MEASURED 2026-10-03, #459: ReasonText)."""
    bound = {word for text in scan.xaml_files.values() for match in _MARKUP.finditer(text) for word in re.findall(r"\w+", match.group(2))}
    findings: list[Finding] = []
    for type_name, prop, path in _display_property_candidates(scan):
        if prop in bound:
            continue
        findings.append(
            Finding(
                "UNBOUND-DISPLAY-PROPERTY",
                f"{type_name}.{prop}",
                f"No {{x:Bind}} or {{Binding}} in any .xaml names this public property of {type_name}, a type a view binds "
                f"against. C# and test references do not count: the user never sees it.",
                str(path),
            )
        )
    return findings


def _check_resources(scan: Scan) -> list[Finding]:
    findings: list[Finding] = []

    uids: dict[str, Path] = {}
    for path, text in scan.xaml_files.items():
        for match in _X_UID.finditer(text):
            uids.setdefault(match.group(1), path)

    resources: dict[str, Path] = {}
    for path, text in scan.resw_files.items():
        for match in _RESW_DATA.finditer(text):
            resources.setdefault(match.group(1), path)

    resource_roots = {name.split(".")[0] for name in resources}

    for uid, path in sorted(uids.items()):
        if uid in resources or uid in resource_roots:
            continue
        findings.append(
            Finding(
                "MISSING-UID-RESOURCE",
                uid,
                "x:Uid with no .resw entry. Every control it names renders "
                "BLANK at runtime, which reads as an unfinished layout rather "
                "than as a broken build (docs/ToTest.md).",
                str(path),
            )
        )

    production = {p: t for p, t in scan.cs_files.items() if not _is_test_path(p)}
    for name, path in sorted(resources.items()):
        root = name.split(".")[0]
        if root in uids:
            continue
        if _mentions(name, production) or _mentions(root, production):
            continue
        findings.append(
            Finding(
                "ORPHAN-RESOURCE",
                name,
                "No x:Uid names it and no production C# looks it up. User-facing copy no user can reach (Hard Rule 13).",
                str(path),
            )
        )
    return findings


def _check_di(scan: Scan) -> list[Finding]:
    registration = next((p for p in scan.cs_files if p.name == "ServiceRegistration.cs"), None)
    if registration is None:
        return []

    reg_text = _strip_comments(scan.cs_files[registration])
    registered, service_types = _registered_types(reg_text)
    factory_built = _factory_only_types(reg_text)

    findings: list[Finding] = []

    # Every registered concrete type's constructor must itself be satisfiable.
    class_texts: dict[str, tuple[Path, str]] = {}
    for path, text in scan.cs_files.items():
        if _is_test_path(path):
            continue
        for match in _CLASS_DECL.finditer(text):
            class_texts.setdefault(match.group(1), (path, text))

    for type_name in sorted(registered):
        entry = class_texts.get(type_name)
        if entry is None or type_name in factory_built:
            continue
        path, text = entry
        for param in _constructor_parameters(text, type_name, skip_defaults=True):
            if param in _NON_SERVICE_PARAM_TYPES or param in registered:
                continue
            findings.append(
                Finding(
                    "UNREGISTERED-DEPENDENCY",
                    f"{type_name}({param})",
                    f"'{param}' is a constructor parameter of a DI-constructed "
                    f"type and is never registered in ServiceRegistration.cs. "
                    f"This throws on first resolve at runtime, not at build.",
                    str(path),
                )
            )

    # Every `GetRequiredService<T>()` in production code names a registered type. Factory-built types are exempt
    # from the constructor check above because their lambda supplies the arguments, so this is where a missing one is
    # caught (#530). It covers every production file, not only ServiceRegistration.cs (#601: AppStartup.cs and
    # App.xaml.cs resolve too), and a type registered in any production file satisfies it.
    production = {p: _strip_comments(x) for p, x in scan.cs_files.items() if not _is_test_path(p)}
    resolvable = set(service_types)
    for text in production.values():
        resolvable |= _registered_types(text)[1]
    for path, text in production.items():
        for resolved, _at in _required_resolves(text):
            if resolved in resolvable or resolved in _NON_SERVICE_PARAM_TYPES or _TYPE_PARAMETER.match(resolved):
                continue
            finding = Finding(
                "UNREGISTERED-RESOLVE",
                resolved,
                f"'{resolved}' is resolved from the container in {path.name} but never registered "
                f"in any production file. It throws on first resolve at runtime, not at build.",
                str(path),
            )
            if finding not in findings:
                findings.append(finding)

    # A registration nothing asks for.
    consumers: dict[str, set[str]] = {}
    for path, text in scan.cs_files.items():
        # A factory delegate inside ServiceRegistration.cs IS a consumer:
        # `AddSingleton<IGcpAccount>(sp => sp.GetRequiredService<FakeGcp>())`
        # is the canonical "one object behind several interfaces" shape, and
        # skipping this file entirely made every such object look dead.
        for match in _GET_SERVICE.finditer(text):
            consumers.setdefault(match.group(1).split(".")[-1], set()).add(str(path))
        if path == registration:
            continue
        for class_name, (cpath, ctext) in class_texts.items():
            if cpath != path:
                continue
            for param in _constructor_parameters(ctext, class_name):
                consumers.setdefault(param, set()).add(str(path))

    for type_name in sorted(service_types):
        if type_name in consumers:
            continue
        if type_name.endswith("ViewModel"):
            # A ViewModel is resolved by the navigation layer by type, which
            # this scanner cannot see. DiResolutionTests covers these.
            continue
        findings.append(
            Finding(
                "DEAD-REGISTRATION",
                type_name,
                "Registered in ServiceRegistration.cs, but no constructor takes "
                "it and no GetRequiredService asks for it. The container builds "
                "it for nobody.",
                str(registration),
            )
        )
    return findings


def _viewmodel_for(xaml_path: Path, scan: Scan) -> tuple[str, str] | None:
    """This repo's convention: Views/NewRunPage.xaml -> NewRunViewModel."""
    stem = xaml_path.stem
    for suffix in ("Page", "View", "Window", "Dialog"):
        if stem.endswith(suffix):
            stem = stem[: -len(suffix)]
            break
    candidate = f"{stem}ViewModel"
    for path, text in scan.cs_files.items():
        if _is_test_path(path):
            continue
        if re.search(rf"\b(?:class|record)\s+{re.escape(candidate)}\b", text):
            return candidate, text
    return None


def _binding_root(body: str) -> str | None:
    """The first path segment a markup-extension body names, when it looks like a member (upper-case first letter)."""
    body = body.strip()
    if not body:
        return None
    explicit = re.search(r"\bPath\s*=\s*([\w\.\[\]]+)", body)
    if explicit:
        expression = explicit.group(1)
    else:
        first = body.split(",")[0].strip()
        if "=" in first:
            return None
        expression = first
    root = expression.split(".")[0].split("[")[0].strip()
    return root if root and root[0].isupper() else None


def _class_text(name: str, scan: Scan) -> str | None:
    for path, text in scan.cs_files.items():
        if _is_test_path(path):
            continue
        if re.search(rf"\b(?:class|record)\s+{re.escape(name)}\b", text):
            return text
    return None


def _check_bindings(scan: Scan) -> tuple[list[Finding], list[str]]:
    findings: list[Finding] = []
    skipped: list[str] = []

    for path, text in scan.xaml_files.items():
        paths_used: set[str] = set()
        # An x:Bind inside a DataTemplate with an x:DataType belongs to that item type (the XAML compiler resolves it
        # there), so it is checked against that class, never skipped and never held to the page ViewModel.
        template_spans = _template_spans(text)
        typed_used: dict[str, set[str]] = {}
        for match in _MARKUP.finditer(text):
            root = _binding_root(match.group(2))
            if root is None:
                continue
            # The innermost template around the binding owns it (the smallest span that contains it).
            enclosing = [(end - start, cls) for start, end, cls in template_spans if start <= match.start() < end]
            owner = min(enclosing, key=lambda pair: pair[0])[1] if enclosing and match.group(1) == "x:Bind" else None
            if owner is not None:
                typed_used.setdefault(owner, set()).add(root)
            else:
                paths_used.add(root)

        for cls, roots in sorted(typed_used.items()):
            class_text = _class_text(cls, scan)
            if class_text is None:
                skipped.append(f"{path}: x:DataType '{cls}' is not a class in this tree; {len(roots)} binding path(s) unchecked")
                continue
            for root in sorted(roots - _type_members(class_text, cls)):
                findings.append(
                    Finding(
                        "DANGLING-BINDING",
                        f"{path.name}:{cls}.{root}",
                        f"Binding path root '{root}' inside a DataTemplate typed {cls} is not a member of {cls}. "
                        f"It compiles in the template's own scope only when the member exists; otherwise it renders blank.",
                        str(path),
                    )
                )

        if not paths_used:
            continue

        resolved = _viewmodel_for(path, scan)
        code_behind = scan.cs_files.get(path.with_suffix(".xaml.cs"), "")
        if resolved is None:
            skipped.append(f"{path}: no ViewModel matches the naming convention; {len(paths_used)} binding path(s) unchecked")
            continue

        vm_name, vm_text = resolved
        known = _members_of(vm_text) | _members_of(code_behind)
        # A binding may name the ViewModel property itself, e.g. {x:Bind ViewModel.X}.
        known.add("ViewModel")
        known.add(vm_name)

        for root in sorted(paths_used - known):
            findings.append(
                Finding(
                    "DANGLING-BINDING",
                    f"{path.name}:{root}",
                    f"Binding path root '{root}' is not a member of {vm_name} "
                    f"or of the code-behind. A {{Binding}} to a name that does "
                    f"not exist compiles and renders blank.",
                    str(path),
                )
            )
    return findings, skipped


def collect_findings(root: Path) -> tuple[list[Finding], Scan, list[str]]:
    scan = read_tree(root)
    binding_findings, binding_skips = _check_bindings(scan)
    findings = _check_members(scan) + _check_resources(scan) + _check_di(scan) + binding_findings + _check_display_properties(scan)
    findings.sort(key=lambda f: (f.code, f.symbol))
    return findings, scan, binding_skips


def load_allowlist(path: Path) -> dict[str, str]:
    if not path.exists():
        return {}
    data = json.loads(path.read_text(encoding="utf-8"))
    return {k: v for k, v in data.items() if not k.startswith("_")}


def run(root: Path, allowlist_path: Path, verbose: bool, as_json: bool) -> int:
    findings, scan, binding_skips = collect_findings(root)
    allowlist = load_allowlist(allowlist_path)

    live = [f for f in findings if f.key not in allowlist]
    suppressed = [f for f in findings if f.key in allowlist]
    stale = sorted(set(allowlist) - {f.key for f in findings})

    if as_json:
        print(
            json.dumps(
                {
                    "read": {
                        "cs": len(scan.cs_files),
                        "xaml": len(scan.xaml_files),
                        "resw": len(scan.resw_files),
                    },
                    "findings": [
                        {
                            "code": f.code,
                            "symbol": f.symbol,
                            "detail": f.detail,
                            "path": f.path,
                        }
                        for f in live
                    ],
                    "suppressed": [f.key for f in suppressed],
                    "stale_allowlist_entries": stale,
                },
                indent=2,
            )
        )
        return 1 if (live or stale) else 0

    # Always say what was READ, not only what was objected to: a guard that
    # scans nothing must not be able to look clean.
    print(f"- read {len(scan.cs_files)} .cs, {len(scan.xaml_files)} .xaml, {len(scan.resw_files)} .resw under {root}")
    if verbose:
        for skip in binding_skips + scan.skipped:
            print(f"  skipped: {skip}")
        for f in suppressed:
            print(f"  allowed: {f.key}  ({allowlist[f.key]})")

    if not live and not stale:
        print(f"OK: nothing in {root} is wired to nothing that this guard can see.")
        print("    (Name-matched, not type-resolved: see this script's docstring for what it cannot see.)")
        return 0

    if live:
        print(f"\nERROR: {len(live)} finding(s):\n")
        for finding in live:
            print(finding.render())
            print()

    if stale:
        print(f"ERROR: {len(stale)} allowlist entry(ies) no longer fire:\n")
        for key in stale:
            print(f"  {key}\n      reason on file: {allowlist[key]}")
        print(
            "\n  An allowlist that keeps entries for findings that stopped "
            "firing rots into\n  a list of things that used to be true. Delete "
            "them."
        )
    return 1


# --------------------------------------------------------------------------
# Self-test: synthetic trees only, never the real repository.
# --------------------------------------------------------------------------

_WIRED_VM = """
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Demo.ViewModels;

public sealed partial class NewRunViewModel : ObservableObject
{
    [ObservableProperty]
    private string? _selectedInputPath;

    public NewRunViewModel(IFilePicker filePicker)
    {
        _filePicker = filePicker;
    }

    private readonly IFilePicker _filePicker;

    [RelayCommand]
    private void Browse()
    {
    }
}
"""

_WIRED_XAML = """
<Page x:Class="Demo.Views.NewRunPage">
    <StackPanel>
        <TextBlock x:Uid="NewRunTitle" Text="{Binding SelectedInputPath}" />
        <Button Command="{x:Bind BrowseCommand}" />
    </StackPanel>
</Page>
"""

_WIRED_RESW = """<?xml version="1.0" encoding="utf-8"?>
<root>
  <data name="NewRunTitle.Text" xml:space="preserve">
    <value>Start a run</value>
  </data>
</root>
"""

_WIRED_REGISTRATION = """
namespace Demo.Startup;

public static class ServiceRegistration
{
    public static IServiceCollection AddDemo(this IServiceCollection services)
    {
        services.AddSingleton<IFilePicker, FilePickerService>();
        services.AddTransient<NewRunViewModel>();
        return services;
    }
}
"""

_WIRED_SERVICE = """
namespace Demo.Services;

public sealed class FilePickerService : IFilePicker
{
    public FilePickerService()
    {
    }
}
"""


def _write_wired(root: Path) -> None:
    (root / "src" / "Demo.Presentation" / "ViewModels").mkdir(parents=True)
    (root / "src" / "Demo.App" / "Views").mkdir(parents=True)
    (root / "src" / "Demo.App" / "Startup").mkdir(parents=True)
    (root / "src" / "Demo.App" / "Services").mkdir(parents=True)
    (root / "src" / "Demo.App" / "Strings" / "en-US").mkdir(parents=True)

    (root / "src" / "Demo.Presentation" / "ViewModels" / "NewRunViewModel.cs").write_text(_WIRED_VM, encoding="utf-8", newline="\n")
    (root / "src" / "Demo.App" / "Views" / "NewRunPage.xaml").write_text(_WIRED_XAML, encoding="utf-8", newline="\n")
    (root / "src" / "Demo.App" / "Startup" / "ServiceRegistration.cs").write_text(_WIRED_REGISTRATION, encoding="utf-8", newline="\n")
    (root / "src" / "Demo.App" / "Services" / "FilePickerService.cs").write_text(_WIRED_SERVICE, encoding="utf-8", newline="\n")
    (root / "src" / "Demo.App" / "Strings" / "en-US" / "Resources.resw").write_text(_WIRED_RESW, encoding="utf-8", newline="\n")


def _codes(root: Path) -> set[str]:
    findings, _, _ = collect_findings(root)
    return {f.code for f in findings}


def self_test() -> int:
    failures: list[str] = []
    ran = 0

    def check(label: str, condition: bool, detail: str = "") -> None:
        nonlocal ran
        ran += 1
        if condition:
            print(f"  PASS  {label}")
        else:
            print(f"  FAIL  {label}{(' -- ' + detail) if detail else ''}")
            failures.append(label)

    with tempfile.TemporaryDirectory() as tmp:
        base = Path(tmp)

        # 1. The direction that matters most: a correctly wired tree is clean.
        wired = base / "wired"
        wired.mkdir()
        _write_wired(wired)
        codes = _codes(wired)
        check("a correctly wired tree produces no findings", codes == set(), str(codes))

        # 2. The scanner actually read something. A guard that scans zero files
        #    and reports clean is this repo's known false-pass shape.
        _, scan, _ = collect_findings(wired)
        check(
            "the scanner read the fixture files",
            len(scan.cs_files) >= 3 and len(scan.xaml_files) >= 1 and len(scan.resw_files) >= 1,
            f"cs={len(scan.cs_files)} xaml={len(scan.xaml_files)} resw={len(scan.resw_files)}",
        )

        # 3. Each finding fires on the tree broken in exactly that one way.
        cases: list[tuple[str, str, Callable[[Path], object]]] = [
            (
                "UNBOUND-COMMAND",
                "a [RelayCommand] no XAML binds",
                lambda r: _patch(
                    r,
                    "src/Demo.App/Views/NewRunPage.xaml",
                    'Command="{x:Bind BrowseCommand}"',
                    "",
                ),
            ),
            (
                "UNBOUND-OBSERVABLE",
                "an [ObservableProperty] no XAML binds",
                lambda r: _patch(
                    r,
                    "src/Demo.App/Views/NewRunPage.xaml",
                    'Text="{Binding SelectedInputPath}"',
                    "",
                ),
            ),
            (
                "MISSING-UID-RESOURCE",
                "an x:Uid with no .resw entry",
                lambda r: _patch(
                    r,
                    "src/Demo.App/Strings/en-US/Resources.resw",
                    'name="NewRunTitle.Text"',
                    'name="SomethingElse.Text"',
                ),
            ),
            (
                "ORPHAN-RESOURCE",
                "a .resw entry no x:Uid and no C# names",
                lambda r: _patch(
                    r,
                    "src/Demo.App/Strings/en-US/Resources.resw",
                    "</root>",
                    '<data name="NeverShown.Text"><value>hi</value></data></root>',
                ),
            ),
            (
                "UNREGISTERED-DEPENDENCY",
                "a constructor parameter nothing registers",
                lambda r: _patch(
                    r,
                    "src/Demo.App/Startup/ServiceRegistration.cs",
                    "services.AddSingleton<IFilePicker, FilePickerService>();",
                    "",
                ),
            ),
            (
                "DEAD-REGISTRATION",
                "a registration nothing asks for",
                lambda r: _patch(
                    r,
                    "src/Demo.App/Startup/ServiceRegistration.cs",
                    "return services;",
                    "services.AddSingleton<INobodyWants, NobodyService>();\n        return services;",
                ),
            ),
            (
                "DANGLING-BINDING",
                "a binding path that is not a member",
                lambda r: _patch(
                    r,
                    "src/Demo.App/Views/NewRunPage.xaml",
                    "{Binding SelectedInputPath}",
                    "{Binding TypoedPropertyName}",
                ),
            ),
            (
                "UNBOUND-DISPLAY-PROPERTY",
                "a string property of a page ViewModel that no XAML binds",
                lambda r: _patch(
                    r,
                    "src/Demo.Presentation/ViewModels/NewRunViewModel.cs",
                    "    [RelayCommand]",
                    '    public string Summary => "";\n\n    [RelayCommand]',
                ),
            ),
            (
                "TEST-ONLY-COMMAND",
                "a command only a test drives",
                lambda r: (
                    _patch(
                        r,
                        "src/Demo.App/Views/NewRunPage.xaml",
                        'Command="{x:Bind BrowseCommand}"',
                        "",
                    ),
                    _add(
                        r,
                        "tests/Demo.Tests/NewRunViewModelTests.cs",
                        "public class T { void A() { vm.BrowseCommand.Execute(null); } }",
                    ),
                ),
            ),
            (
                "CODE-ONLY-OBSERVABLE",
                "an observable only production C# reads",
                lambda r: (
                    _patch(
                        r,
                        "src/Demo.App/Views/NewRunPage.xaml",
                        'Text="{Binding SelectedInputPath}"',
                        "",
                    ),
                    _add(
                        r,
                        "src/Demo.App/Services/Reader.cs",
                        "public class Reader { void A(NewRunViewModel vm) { _ = vm.SelectedInputPath; } }",
                    ),
                ),
            ),
        ]

        for code, label, mutate in cases:
            broken = base / code.lower()
            broken.mkdir()
            _write_wired(broken)
            mutate(broken)
            found = _codes(broken)
            check(f"{code} fires on {label}", code in found, f"got {sorted(found)}")

        # 3b. The three false results of #461, one fixture each (revert the fix and these fail).
        check(
            "a constructor parameter with a default value parses to its type",
            _constructor_parameters("public class C { public C(IFoo f, Action<string, JobPhase>? cb = null) { } }", "C")
            == ["IFoo", "Action<string,JobPhase>"],
        )
        check(
            "the instance registration form registers the type",
            _registered_types("s.AddSingleton(new Dep());")[0] == {"Dep"},
        )
        # 3c. The two parser shortcuts of #478 (balanced scanning; revert it and each of these fails).
        check(
            "a parenthesised default does not hide a later required parameter",
            _constructor_parameters("public class Foo { public Foo(IBar bar = default(IBar), IBaz baz) { } }", "Foo", skip_defaults=True)
            == ["IBaz"],
        )
        check(
            "a nested generic registration is one type, not two half-types",
            _registered_types("s.AddSingleton<IRepo<Run, Guid>, Repo>();")[1] == {"IRepo<Run,Guid>"},
        )
        probe = {Path("Reader.cs"): "class R { // see Vm.Caption\n string A() => Options.Caption; }"}
        check(
            "a comment and another declared type's property are not reads of this member",
            _member_mentions("Caption", probe, "Vm", frozenset({"Vm", "Options"})) == [],
        )

        # 4. The allowlist is checked in both directions.
        stale_root = base / "stale"
        stale_root.mkdir()
        _write_wired(stale_root)
        allow = base / "stale_allowlist.json"
        allow.write_text(
            json.dumps({"UNBOUND-COMMAND:NewRunViewModel.BrowseCommand": "reason"}),
            encoding="utf-8",
        )
        import contextlib
        import io

        with contextlib.redirect_stdout(io.StringIO()):
            exit_code = run(stale_root, allow, verbose=False, as_json=True)
        check(
            "an allowlist entry that no longer fires fails the run",
            exit_code == 1,
            f"exit={exit_code}",
        )

        # An x:Bind inside a DataTemplate with an x:DataType belongs to the item type (the XAML compiler checks it),
        # so it is not a dangling binding on the page ViewModel; a plain {Binding} in the same place still is.
        typed_root = Path(tempfile.mkdtemp(prefix="wiring-typed-"))
        _write_wired(typed_root)
        (typed_root / "src/Demo.Presentation").mkdir(parents=True, exist_ok=True)
        (typed_root / "src/Demo.Presentation/Item.cs").write_text(
            'public sealed class Item { public string ItemOnlyMember { get; } = ""; }\n', encoding="utf-8"
        )
        _patch(
            typed_root,
            "src/Demo.App/Views/NewRunPage.xaml",
            "{Binding SelectedInputPath}",
            '{Binding SelectedInputPath}<DataTemplate x:DataType="vm:Item"><TextBlock Text="{x:Bind ItemOnlyMember}" /></DataTemplate>',
        )
        check(
            "an x:Bind inside a typed DataTemplate is checked against the template's class, not the page ViewModel",
            "DANGLING-BINDING" not in _codes(typed_root),
        )
        # And it is still checked: a member the item type does not have is a dangling binding.
        typed_bad_root = Path(tempfile.mkdtemp(prefix="wiring-typed-bad-"))
        _write_wired(typed_bad_root)
        (typed_bad_root / "src/Demo.Presentation").mkdir(parents=True, exist_ok=True)
        (typed_bad_root / "src/Demo.Presentation/Item.cs").write_text(
            'public sealed class Item { public string ItemOnlyMember { get; } = ""; }\n', encoding="utf-8"
        )
        _patch(
            typed_bad_root,
            "src/Demo.App/Views/NewRunPage.xaml",
            "{Binding SelectedInputPath}",
            '{Binding SelectedInputPath}<DataTemplate x:DataType="vm:Item"><TextBlock Text="{x:Bind NoSuchMember}" /></DataTemplate>',
        )
        check(
            "an x:Bind to a nonexistent member inside a typed DataTemplate is a dangling binding",
            "DANGLING-BINDING" in _codes(typed_bad_root),
        )
        # Templates nest: a binding in an inner typed template is held to the INNER type, not the outer one.
        nested_root = Path(tempfile.mkdtemp(prefix="wiring-nested-"))
        _write_wired(nested_root)
        (nested_root / "src/Demo.Presentation").mkdir(parents=True, exist_ok=True)
        (nested_root / "src/Demo.Presentation/Item.cs").write_text(
            'public sealed record Group(string Header);\npublic sealed class Row { public string Title { get; } = ""; }\n',
            encoding="utf-8",
        )
        _patch(
            nested_root,
            "src/Demo.App/Views/NewRunPage.xaml",
            "{Binding SelectedInputPath}",
            '{Binding SelectedInputPath}<DataTemplate x:DataType="vm:Group"><TextBlock Text="{x:Bind Header}" />'
            '<DataTemplate x:DataType="vm:Row"><TextBlock Text="{x:Bind Title}" /></DataTemplate></DataTemplate>',
        )
        check(
            "a binding in a nested typed DataTemplate is checked against the inner type",
            "DANGLING-BINDING" not in _codes(nested_root),
        )
        # A positional record's parameters are its properties; a member the record lacks is still caught.
        record_root = Path(tempfile.mkdtemp(prefix="wiring-record-"))
        _write_wired(record_root)
        (record_root / "src/Demo.Presentation").mkdir(parents=True, exist_ok=True)
        (record_root / "src/Demo.Presentation/Item.cs").write_text(
            "public sealed record Item(string Header, System.Collections.Generic.IReadOnlyList<string> Items);\n", encoding="utf-8"
        )
        _patch(
            record_root,
            "src/Demo.App/Views/NewRunPage.xaml",
            "{Binding SelectedInputPath}",
            '{Binding SelectedInputPath}<DataTemplate x:DataType="vm:Item"><TextBlock Text="{x:Bind Header}" />'
            '<ListView ItemsSource="{x:Bind Items}" /></DataTemplate>',
        )
        check(
            "an x:Bind to a positional record's parameter is not a dangling binding",
            "DANGLING-BINDING" not in _codes(record_root),
        )
        record_bad_root = Path(tempfile.mkdtemp(prefix="wiring-record-bad-"))
        _write_wired(record_bad_root)
        (record_bad_root / "src/Demo.Presentation").mkdir(parents=True, exist_ok=True)
        (record_bad_root / "src/Demo.Presentation/Item.cs").write_text(
            "public sealed record Item(string Header, System.Collections.Generic.IReadOnlyList<string> Items);\n", encoding="utf-8"
        )
        _patch(
            record_bad_root,
            "src/Demo.App/Views/NewRunPage.xaml",
            "{Binding SelectedInputPath}",
            '{Binding SelectedInputPath}<DataTemplate x:DataType="vm:Item"><TextBlock Text="{x:Bind Heder}" /></DataTemplate>',
        )
        check(
            "an x:Bind to a name a positional record lacks is still a dangling binding",
            "DANGLING-BINDING" in _codes(record_bad_root),
        )
        # The member scan is scoped to the class's own body: a property of another class in the file does not count.
        scoped_root = Path(tempfile.mkdtemp(prefix="wiring-scoped-"))
        _write_wired(scoped_root)
        (scoped_root / "src/Demo.Presentation").mkdir(parents=True, exist_ok=True)
        (scoped_root / "src/Demo.Presentation/Item.cs").write_text(
            'public sealed class Item { public string Own { get; } = ""; }\n'
            'public sealed class Other { public string OnlyOnOther { get; } = ""; }\n',
            encoding="utf-8",
        )
        _patch(
            scoped_root,
            "src/Demo.App/Views/NewRunPage.xaml",
            "{Binding SelectedInputPath}",
            '{Binding SelectedInputPath}<DataTemplate x:DataType="vm:Item"><TextBlock Text="{x:Bind OnlyOnOther}" /></DataTemplate>',
        )
        check(
            "a member that exists only on another class in the same file is a dangling binding",
            "DANGLING-BINDING" in _codes(scoped_root),
        )
        # A single-quoted x:DataType is read too, so its bad binding is caught instead of skipped.
        single_root = Path(tempfile.mkdtemp(prefix="wiring-single-"))
        _write_wired(single_root)
        (single_root / "src/Demo.Presentation").mkdir(parents=True, exist_ok=True)
        (single_root / "src/Demo.Presentation/Item.cs").write_text(
            'public sealed class Item { public string ItemOnlyMember { get; } = ""; }\n', encoding="utf-8"
        )
        _patch(
            single_root,
            "src/Demo.App/Views/NewRunPage.xaml",
            "{Binding SelectedInputPath}",
            "{Binding SelectedInputPath}<DataTemplate x:DataType='vm:Item'><TextBlock Text=\"{x:Bind NoSuchMember}\" /></DataTemplate>",
        )
        check(
            "a single-quoted x:DataType is read: a bad binding inside it is a dangling binding",
            "DANGLING-BINDING" in _codes(single_root),
        )
        # The loosening is pinned both ways: a plain {Binding} in the same place is not compile-checked
        # against the x:DataType (it is resolved at run time), so it is still held to the page ViewModel.
        untyped_root = Path(tempfile.mkdtemp(prefix="wiring-untyped-"))
        _write_wired(untyped_root)
        _patch(
            untyped_root,
            "src/Demo.App/Views/NewRunPage.xaml",
            "{Binding SelectedInputPath}",
            '{Binding SelectedInputPath}<DataTemplate x:DataType="vm:Item"><TextBlock Text="{Binding ItemOnlyMember}" /></DataTemplate>',
        )
        check(
            "a plain {Binding} inside a typed DataTemplate is still a dangling binding",
            "DANGLING-BINDING" in _codes(untyped_root),
        )

    print()
    if failures:
        print(f"SELF-TEST FAILED: {len(failures)} check(s): {', '.join(failures)}")
        return 1
    print(f"SELF-TEST PASSED: {ran} checks.")
    return 0


def _patch(root: Path, relative: str, old: str, new: str) -> None:
    path = root / relative
    text = path.read_text(encoding="utf-8")
    if old not in text:
        raise AssertionError(f"self-test fixture drift: {old!r} not in {relative}")
    path.write_text(text.replace(old, new), encoding="utf-8", newline="\n")


def _add(root: Path, relative: str, content: str) -> None:
    path = root / relative
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(content, encoding="utf-8", newline="\n")


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=(__doc__ or "").split("\n")[0])
    parser.add_argument("--root", default=str(DEFAULT_ROOT), help="tree to scan (default: <this repo>/app)")
    parser.add_argument("--allowlist", default=str(ALLOWLIST_PATH))
    parser.add_argument("--verbose", action="store_true")
    parser.add_argument("--json", action="store_true", dest="as_json")
    parser.add_argument("--self-test", action="store_true", dest="self_test")
    parser.add_argument(
        "--suggest-allowlist",
        action="store_true",
        dest="suggest_allowlist",
        help=(
            "print an allowlist skeleton for the current findings, with every "
            "reason left EMPTY. It deliberately does not write the file and "
            "deliberately does not invent reasons: an allowlist entry whose "
            "reason nobody wrote is a baseline silently raised, which is the "
            "one thing this guard exists to stop."
        ),
    )
    args = parser.parse_args(argv)

    if args.self_test:
        return self_test()

    root = Path(args.root)
    if args.suggest_allowlist:
        if not root.exists():
            print(f"NOTICE: {root} does not exist; nothing to suggest.")
            return 0
        findings, _, _ = collect_findings(root)
        skeleton = {f.key: "" for f in findings}
        print(json.dumps(skeleton, indent=2))
        print(
            "\n// Every reason above is empty on purpose. Fill each one with the "
            "issue\n// whose work makes the finding stop firing, then delete the "
            "entry in that\n// issue's own pull request.",
            file=sys.stderr,
        )
        return 0

    if not root.exists():
        # Hard Rule / measured lesson: a guard that exits 0 because the thing it
        # checks does not exist is a guard that silently stops enforcing. Say so
        # loudly and still pass, because `app/` genuinely may not exist on a
        # branch; check_guard_drift.py watches for this notice going permanent.
        print(f"NOTICE: {root} does not exist; this guard checked nothing.")
        return 0

    return run(root, Path(args.allowlist), args.verbose, args.as_json)


if __name__ == "__main__":
    sys.exit(main())
