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
from dataclasses import dataclass
from pathlib import Path

ALLOWLIST_PATH = Path("scripts/app_wiring_allowlist.json")

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

_ADD_SERVICE = re.compile(
    r"\.Add(?:Singleton|Transient|Scoped)\s*<([^>;]+)>",
)
_GET_SERVICE = re.compile(r"\.Get(?:Required)?Service\s*<\s*([\w\.]+)\s*>")

_X_UID = re.compile(r'x:Uid\s*=\s*"([^"]+)"')
_RESW_DATA = re.compile(r'<data\s+name\s*=\s*"([^"]+)"')

# A markup extension body: {Binding ...} or {x:Bind ...}. The path is either the
# first positional token or an explicit Path=.
_MARKUP = re.compile(r"\{\s*(x:Bind|Binding)\s*([^}]*)\}")

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
        return {
            p: t
            for p, t in self.cs_files.items()
            if p != exclude and not _is_test_path(p)
        }

    def test_cs(self, exclude: Path) -> dict[Path, str]:
        return {
            p: t for p, t in self.cs_files.items() if p != exclude and _is_test_path(p)
        }


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


def _member_mentions(identifier: str, texts: dict[Path, str]) -> list[Path]:
    """Files that READ this member, as opposed to merely spelling the name.

    A bare name match counts an unrelated type's identically named property as
    a consumer: `RunOptions.ModelId`'s own declaration would "consume"
    `NewRunViewModel.ModelId`, which is not a read of anything. Requiring a
    member access (`x.ModelId`) or a `nameof(ModelId)` excludes declarations,
    and that is the whole difference between a read and a coincidence. It is
    still name-matched, not type-resolved - see this script's docstring.
    """
    pattern = re.compile(
        rf"(?:\.\s*|nameof\(\s*(?:[\w.]+\.)?){re.escape(identifier)}\b"
    )
    return [p for p, t in texts.items() if pattern.search(t)]


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


def _constructor_parameters(text: str, class_name: str) -> list[str]:
    """Parameter type names of `public <class_name>(...)`, in order."""
    match = re.search(
        rf"public\s+{re.escape(class_name)}\s*\(([^)]*)\)",
        text,
        re.DOTALL,
    )
    if not match:
        return []
    body = match.group(1).strip()
    if not body:
        return []
    types: list[str] = []
    depth = 0
    current = ""
    for char in body + ",":
        if char in "<([":
            depth += 1
        elif char in ">)]":
            depth -= 1
        if char == "," and depth == 0:
            token = current.strip()
            if token:
                parts = token.split()
                if len(parts) >= 2:
                    types.append(parts[-2].split(".")[-1].rstrip("?"))
            current = ""
        else:
            current += char
    return types


def _registered_types(text: str) -> tuple[set[str], set[str]]:
    """Types named in Add*<...> calls, split into (every type, service types).

    `AddSingleton<IFilePicker, FilePickerService>()` registers two type names,
    but only the FIRST is what a consumer asks for. The implementation type is
    never injected by name, so counting it as a dead registration would be a
    confident false positive on the single most common registration shape.
    """
    every: set[str] = set()
    services: set[str] = set()
    for match in _ADD_SERVICE.finditer(text):
        tokens = [t.strip().split(".")[-1] for t in match.group(1).split(",")]
        tokens = [t for t in tokens if t]
        if not tokens:
            continue
        every.update(tokens)
        services.add(tokens[0])
    return every, services


# --------------------------------------------------------------------------
# The nine checks.
# --------------------------------------------------------------------------


def _check_members(scan: Scan) -> list[Finding]:
    findings: list[Finding] = []
    xaml_text = scan.xaml_text

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
            prod = _member_mentions(identifier, scan.production_cs(exclude=path))
            tests = _member_mentions(identifier, scan.test_cs(exclude=path))
            noun = "command" if kind == "COMMAND" else "observable property"
            a_noun = "A command" if kind == "COMMAND" else "An observable property"
            generator = (
                "[RelayCommand]" if kind == "COMMAND" else "[ObservableProperty]"
            )
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
                        f"No .xaml and no other .cs names this {noun}. "
                        f"{generator} generates it so a view can bind to it; "
                        f"nothing does.",
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
                "No x:Uid names it and no production C# looks it up. "
                "User-facing copy no user can reach (Hard Rule 13).",
                str(path),
            )
        )
    return findings


def _check_di(scan: Scan) -> list[Finding]:
    registration = next(
        (p for p in scan.cs_files if p.name == "ServiceRegistration.cs"), None
    )
    if registration is None:
        return []

    reg_text = scan.cs_files[registration]
    registered, service_types = _registered_types(reg_text)

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
        if entry is None:
            continue
        path, text = entry
        for param in _constructor_parameters(text, type_name):
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


def _check_bindings(scan: Scan) -> tuple[list[Finding], list[str]]:
    findings: list[Finding] = []
    skipped: list[str] = []

    for path, text in scan.xaml_files.items():
        paths_used: set[str] = set()
        for match in _MARKUP.finditer(text):
            body = match.group(2).strip()
            if not body:
                continue
            explicit = re.search(r"\bPath\s*=\s*([\w\.\[\]]+)", body)
            if explicit:
                expression = explicit.group(1)
            else:
                first = body.split(",")[0].strip()
                if "=" in first:
                    continue
                expression = first
            root = expression.split(".")[0].split("[")[0].strip()
            if root and root[0].isupper():
                paths_used.add(root)

        if not paths_used:
            continue

        resolved = _viewmodel_for(path, scan)
        code_behind = scan.cs_files.get(path.with_suffix(".xaml.cs"), "")
        if resolved is None:
            skipped.append(
                f"{path}: no ViewModel matches the naming convention; "
                f"{len(paths_used)} binding path(s) unchecked"
            )
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
    findings = (
        _check_members(scan)
        + _check_resources(scan)
        + _check_di(scan)
        + binding_findings
    )
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
    print(
        f"- read {len(scan.cs_files)} .cs, {len(scan.xaml_files)} .xaml, "
        f"{len(scan.resw_files)} .resw under {root}"
    )
    if verbose:
        for skip in binding_skips + scan.skipped:
            print(f"  skipped: {skip}")
        for f in suppressed:
            print(f"  allowed: {f.key}  ({allowlist[f.key]})")

    if not live and not stale:
        print(f"OK: nothing in {root} is wired to nothing that this guard can see.")
        print(
            "    (Name-matched, not type-resolved: see this script's docstring "
            "for what it cannot see.)"
        )
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

    (root / "src" / "Demo.Presentation" / "ViewModels" / "NewRunViewModel.cs").write_text(
        _WIRED_VM, encoding="utf-8"
    )
    (root / "src" / "Demo.App" / "Views" / "NewRunPage.xaml").write_text(
        _WIRED_XAML, encoding="utf-8"
    )
    (root / "src" / "Demo.App" / "Startup" / "ServiceRegistration.cs").write_text(
        _WIRED_REGISTRATION, encoding="utf-8"
    )
    (root / "src" / "Demo.App" / "Services" / "FilePickerService.cs").write_text(
        _WIRED_SERVICE, encoding="utf-8"
    )
    (root / "src" / "Demo.App" / "Strings" / "en-US" / "Resources.resw").write_text(
        _WIRED_RESW, encoding="utf-8"
    )


def _codes(root: Path) -> set[str]:
    findings, _, _ = collect_findings(root)
    return {f.code for f in findings}


def self_test() -> int:
    failures: list[str] = []

    def check(label: str, condition: bool, detail: str = "") -> None:
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
            len(scan.cs_files) >= 3
            and len(scan.xaml_files) >= 1
            and len(scan.resw_files) >= 1,
            f"cs={len(scan.cs_files)} xaml={len(scan.xaml_files)} resw={len(scan.resw_files)}",
        )

        # 3. Each finding fires on the tree broken in exactly that one way.
        cases: list[tuple[str, str, object]] = [
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

    print()
    if failures:
        print(f"SELF-TEST FAILED: {len(failures)} check(s): {', '.join(failures)}")
        return 1
    print(f"SELF-TEST PASSED: {len(cases) + 4} checks.")
    return 0


def _patch(root: Path, relative: str, old: str, new: str) -> None:
    path = root / relative
    text = path.read_text(encoding="utf-8")
    if old not in text:
        raise AssertionError(f"self-test fixture drift: {old!r} not in {relative}")
    path.write_text(text.replace(old, new), encoding="utf-8")


def _add(root: Path, relative: str, content: str) -> None:
    path = root / relative
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(content, encoding="utf-8")


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--root", default="app", help="tree to scan (default: app)")
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
