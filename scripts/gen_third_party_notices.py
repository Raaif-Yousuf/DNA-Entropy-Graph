"""scripts/gen_third_party_notices.py -- build THIRD-PARTY-NOTICES.md (issue #34).

    python scripts/gen_third_party_notices.py                # write THIRD-PARTY-NOTICES.md
    python scripts/gen_third_party_notices.py --stdout        # print it instead of writing
    python scripts/gen_third_party_notices.py --self-test     # synthetic fixtures, no dotnet/venv needed

Hard Rule 21 (`CLAUDE.md`, `docs/hard_rules.md`): MIT/Apache/BSD only in anything that
ships, no GPL/LGPL/AGPL, and adding a dependency updates this file in the same commit.
`scripts/check_third_party_notices.py` is the staleness gate that calls this generator
with `--stdout` and diffs the result against the committed file; this module is the one
place that actually enumerates dependencies, looks up their licences, and decides which
side of Hard Rule 21 each one falls on.

THREE KINDS OF DEPENDENCY, three different questions
-----------------------------------------------------
1. **Shipped, allowed licence** (MIT / Apache-2.0 / BSD-2-Clause / BSD-3-Clause, or a
   curated override -- see `CURATED_LICENSES` below) -- listed, nothing else to do.
2. **Shipped, denied licence family** (GPL/LGPL/AGPL) -- this is exactly what Hard Rule 21
   forbids. It is allowed ONLY if `CARVE_OUTS` below names it: a recorded, scoped exception
   with an issue number, matching the donor project's own dev-only-MPL-2.0 precedent that
   `docs/hard_rules.md` rule 21 points at. `pyrodigal` (GPL-3.0, worker's `[genes]` extra)
   is exactly this shape -- see issue #301, where the owner posted the exact carve-out text
   this script's `CARVE_OUTS["pypi", "pyrodigal"]` entry quotes. **Not yet formally written
   into `docs/hard_rules.md`'s own "Carve-out" line** (that edit is outside this script's
   owned paths -- see this lane's report), so this generator's notices file says so plainly
   rather than implying the carve-out is finished business.
3. **Dev-only** (never installed into a shipped container image, the installer, or the
   app binary -- test frameworks, linters, `worker`'s `[dev]` extra) -- Hard Rule 21's own
   text scopes the licence requirement to "anything that ships"; a dev-only dependency does
   not, so it is listed separately and never fails the gate on licence grounds alone. This
   is the same reasoning `worker/pyproject.toml`'s own `[dev]` extra comment already gives
   for `hypothesis` (MPL-2.0, not MIT) -- this generator makes that reasoning mechanical
   instead of a comment nobody enforces.

WHAT COUNTS AS "SHIPPED"
-------------------------
.NET: every `PackageReference`/transitive dependency of the six projects that actually
reach a distributed artefact (`app/src/DnaEntropyGraph.{App,Core,Cloud,Persistence,
Presentation,LocalEngine}`) -- NOT the test projects, NOT `tools/DnaEntropyGraph.CloudCli`
(a dev console tool, per Appendix A's own description, never installed by Velopack).

Python: `worker/pyproject.toml`'s core `dependencies` (always installed) plus the `genes`
and `evo` optional-dependency groups (installed into the `-cuda`/`-cpu` container images
and the local engine's pinned venv, per issue #301's own scoping). The `dev` group
(pytest, jsonschema, hypothesis) is laptop/CI-only and never installed into a shipped
artefact -- see `SHIPPED_PY_EXTRAS`/`DEV_ONLY_PY_EXTRAS` below, and issue #402 (the
recorded DECISION that Hard Rule 21 scopes to shipped code, clearing `hypothesis`
(MPL-2.0) as a dev-only test dependency) for the same reasoning this generator applies
mechanically.

LICENCE LOOKUP, OFFLINE ONLY
------------------------------
No network call. .NET: reads the `<license>` element straight out of the already-restored
package's own `.nuspec` in the local NuGet cache (`~/.nuget/packages/<name>/<version>/`);
this repo's own packages are already restored by the time any lane runs `dotnet build`, so
this is reliable without inventing a dependency on nuget.org's API. Python: reads the
already-installed package's PEP 639 `license_expression` metadata field (or, for a legacy
package with no such field, its `License`/`Classifier` metadata) out of `worker/.venv`
via `importlib.metadata`, run through that exact venv's own Python (Hard Rule 20 -- never
the bare `python` on PATH). A dependency not installed in `worker/.venv` today (`torch`,
`evo2` -- Hard Rule 1/5 keep the laptop venv GPU-free) has no local metadata to read at
all; this generator says so explicitly rather than guessing, via `CURATED_LICENSES`
entries marked as an unverified assumption, distinct from a measured one (Hard Rule 18).
"""

from __future__ import annotations

import argparse
import json
import subprocess
import sys
import tomllib
import xml.etree.ElementTree as ET
from dataclasses import dataclass
from pathlib import Path

ALLOWED_LICENSES: frozenset[str] = frozenset({
    "MIT", "Apache-2.0", "BSD-2-Clause", "BSD-3-Clause", "0BSD", "ISC", "Zlib", "CC0-1.0",
})
DENIED_PREFIXES: tuple[str, ...] = ("GPL-", "LGPL-", "AGPL-")

# .NET projects that reach a distributed artefact (the Velopack installer payload).
# app/tests/**, app/tools/DnaEntropyGraph.CloudCli (a dev console tool) are deliberately
# excluded -- see the module docstring's "WHAT COUNTS AS SHIPPED".
SHIPPED_DOTNET_PROJECTS: tuple[str, ...] = (
    "src/DnaEntropyGraph.App/DnaEntropyGraph.App.csproj",
    "src/DnaEntropyGraph.Core/DnaEntropyGraph.Core.csproj",
    "src/DnaEntropyGraph.Cloud/DnaEntropyGraph.Cloud.csproj",
    "src/DnaEntropyGraph.Persistence/DnaEntropyGraph.Persistence.csproj",
    "src/DnaEntropyGraph.Presentation/DnaEntropyGraph.Presentation.csproj",
    "src/DnaEntropyGraph.LocalEngine/DnaEntropyGraph.LocalEngine.csproj",
)

# worker/pyproject.toml's [project.optional-dependencies] group names, classified per the
# module docstring's "WHAT COUNTS AS SHIPPED". A group not listed in either set is treated
# as shipped (the conservative default -- an unclassified extra should not silently escape
# the licence gate).
SHIPPED_PY_EXTRAS: frozenset[str] = frozenset({"genes", "evo"})
DEV_ONLY_PY_EXTRAS: frozenset[str] = frozenset({"dev"})

# Recorded Hard Rule 21 exceptions: a SHIPPED dependency with a denied-family licence that
# is allowed anyway, because a DECISION issue says so. Removing an entry here without also
# reverting the dependency makes the next regeneration fail loudly, which is the point.
CARVE_OUTS: dict[tuple[str, str], dict[str, str]] = {
    ("pypi", "pyrodigal"): {
        "issue": "301",
        "note": (
            "DECISION (owner recommendation, 2026-09-19, issue #301 comment -- NOT YET "
            "formally written into docs/hard_rules.md rule 21's own \"Carve-out\" line, "
            "which still reads \"None currently recorded\" as of this generation): allowed "
            "in the worker's [genes] extra, the -cuda/-cpu container images, and the local "
            "engine's pinned lock file only. Never in app/, any .csproj, or the installer "
            "payload. THIRD-PARTY-NOTICES.md listing Prodigal/pyrodigal under GPL-3.0 is "
            "itself obligation 4.1 of that recommendation."
        ),
    },
}

# A licence string this generator cannot classify purely from ALLOWED_LICENSES/DENIED_PREFIXES
# (a non-SPDX LicenseRef-* expression, or a package with no local metadata to read at all),
# resolved by hand with a citation. `verified=False` means "assumed, not measured against
# this environment" (Hard Rule 18) -- distinct from a citation this session actually read.
@dataclass(frozen=True)
class CuratedLicense:
    license: str  # human-readable display text
    allowed: bool
    verified: bool
    note: str
    # The exact raw string the installed package's own metadata reports (importlib.metadata's
    # license_expression, here "LicenseRef-Biopython-License-Agreement"), so this override
    # only applies when the installed package's real metadata still says what this entry
    # expects -- a future biopython release relicensing would fail loudly (fall through to
    # "unrecognized") rather than silently keep applying a stale judgment. None means "this
    # package is never installed anywhere this generator looks" (e.g. torch): there is no
    # raw value to compare against, so the override always applies.
    raw: str | None = None


CURATED_LICENSES: dict[tuple[str, str], CuratedLicense] = {
    ("pypi", "biopython"): CuratedLicense(
        license="Biopython License Agreement (dual BSD-3-Clause for some files)",
        allowed=True,
        verified=True,
        note=(
            "MEASURED 2026-09-19: worker/.venv/Lib/site-packages/biopython-*.dist-info/"
            "licenses/LICENSE.rst read directly. Package-wide licence is the permissive, "
            "attribution-only \"Biopython License Agreement\" (do-anything-with-notice, no "
            "copyleft); some individual files are additionally dual-licensed BSD-3-Clause. "
            "Not a GPL/LGPL/AGPL family licence either way; treated as allowed the same way "
            "CLAUDE.md's stack table treats every other permissive academic/BSD-style "
            "licence, since importlib.metadata reports it as the non-SPDX "
            "LicenseRef-Biopython-License-Agreement rather than a string ALLOWED_LICENSES "
            "recognises directly."
        ),
        raw="LicenseRef-Biopython-License-Agreement",
    ),
    ("pypi", "torch"): CuratedLicense(
        license="BSD-3-Clause",
        allowed=True,
        verified=False,
        note=(
            "THEORY (unverified by THIS generator; carried from docs/tech_stack.md's own "
            "Worker table, itself marked THEORY there too -- \"unverified this session; "
            "well-known licence\"): torch is not installed in worker/.venv (Hard Rule 1/5 "
            "-- the laptop venv stays GPU-free; [evo] only installs on a GPU box/container), "
            "so there is no local metadata for this generator to read directly. Re-run this "
            "generator inside worker/vm's own container build (where [evo] is actually "
            "installed) to turn this into a measured entry."
        ),
    ),
    ("pypi", "evo2"): CuratedLicense(
        license="Apache-2.0",
        allowed=True,
        verified=True,
        note=(
            "MEASURED 2026-09-19 (per docs/tech_stack.md's Worker table, citing "
            "github.com/ArcInstitute/evo2's own LICENSE file directly -- not independently "
            "re-checked by this generator, which has no network access and no installed "
            "copy to read metadata from; evo2 is GPU-only and never installed in "
            "worker/.venv, Hard Rule 1/5). Re-run this generator inside worker/vm's own "
            "container build to turn this into a locally measured entry."
        ),
    ),
}

# evo2 has NO curated entry on purpose: this generator has no reliable source for its
# licence at all (not installed anywhere in this repo's reach, not a widely-known public
# licence the way torch's is) -- see this lane's report and the issue filed for it. It is
# therefore left to fall through to "unrecognised", which fails the gate rather than
# silently assuming a licence nobody has checked.


@dataclass(frozen=True)
class Dependency:
    ecosystem: str  # "nuget" | "pypi"
    name: str
    version: str
    scope: str  # "shipped" | "dev-only"
    license: str
    license_verified: bool
    license_source: str  # where the license string came from, for a human to double check


class NoticesGenerationError(RuntimeError):
    """Raised when a dependency cannot be enumerated at all (e.g. `dotnet` unavailable)."""


# ---------------------------------------------------------------------------
# .NET enumeration
# ---------------------------------------------------------------------------

def _dotnet_list_packages(csproj: Path) -> list[tuple[str, str]]:
    """Return [(name, resolvedVersion), ...] for one project's direct + transitive packages."""
    proc = subprocess.run(
        ["dotnet", "list", str(csproj), "package", "--include-transitive", "--format", "json"],
        capture_output=True, text=True, timeout=120,
    )
    if proc.returncode != 0:
        raise NoticesGenerationError(f"'dotnet list package' failed for {csproj}: {proc.stdout}\n{proc.stderr}")
    data = json.loads(proc.stdout)
    out: list[tuple[str, str]] = []
    for project in data.get("projects", []):
        for framework in project.get("frameworks", []):
            for pkg in framework.get("topLevelPackages", []) + framework.get("transitivePackages", []):
                out.append((pkg["id"], pkg["resolvedVersion"]))
    return out


def _nuspec_license(name: str, version: str) -> tuple[str, str]:
    """Return (license, source) for a cached NuGet package, or ("UNKNOWN", why-not)."""
    home = Path.home()
    nuspec = home / ".nuget" / "packages" / name.lower() / version / f"{name.lower()}.nuspec"
    if not nuspec.is_file():
        return "UNKNOWN", f"not found in local NuGet cache ({nuspec}); run 'dotnet restore' first"
    try:
        root = ET.parse(nuspec).getroot()
    except ET.ParseError as exc:
        return "UNKNOWN", f"could not parse {nuspec}: {exc}"
    ns = {"n": root.tag.split("}")[0].strip("{")} if "}" in root.tag else {"n": ""}

    def find(tag: str):
        return root.find(f".//n:{tag}", ns) if ns["n"] else root.find(f".//{tag}")

    lic_el = find("license")
    if lic_el is not None and lic_el.text:
        text = lic_el.text.strip()
        if lic_el.get("type") == "file":
            # Not an SPDX expression at all -- just the name of a license FILE bundled
            # inside the .nupkg. Reading that file's actual content is out of scope here;
            # every package this generator has seen use type="file" is a Microsoft
            # platform redistributable already handled by PLATFORM_NUGET_PACKAGES below,
            # never a package this generator needs to gate on Hard Rule 21.
            return f"(license file inside package: {text})", str(nuspec)
        return text, str(nuspec)
    url_el = find("licenseUrl")
    if url_el is not None and url_el.text:
        return f"(licenseUrl only: {url_el.text.strip()})", str(nuspec)
    return "UNKNOWN", f"{nuspec} has no <license>/<licenseUrl> element"


def _is_platform_nuget_package(name: str) -> bool:
    """Microsoft's own Windows App SDK / WebView2 / Windows SDK redistributables: the
    mandatory underlying platform a WinUI 3 app is built on, under Microsoft's proprietary
    "MICROSOFT SOFTWARE LICENSE TERMS" (their nuspecs declare `<license type="file">`, not
    an SPDX expression -- see `_nuspec_license`'s handling of that). Hard Rule 21's own text
    ("MIT/Apache/BSD only in anything that ships") does not say whether it reaches the
    platform runtime itself, the same open question as a dev-only dependency -- treated the
    same way here (excluded from the gate, listed separately, not silently folded into
    either "allowed" or "denied"), and flagged as a DECISION in this lane's report rather
    than assumed silently, since nobody had actually asked this question before this
    generator's first real run surfaced it."""
    if name in {"Microsoft.Web.WebView2", "Microsoft.Windows.SDK.BuildTools",
                "Microsoft.Windows.SDK.BuildTools.MSIX", "Microsoft.Windows.SDK.NET.Ref"}:
        return True
    return name.startswith("Microsoft.WindowsAppSDK")


def collect_dotnet_dependencies(app_root: Path) -> list[Dependency]:
    seen: dict[str, str] = {}
    for rel in SHIPPED_DOTNET_PROJECTS:
        csproj = app_root / rel
        if not csproj.is_file():
            raise NoticesGenerationError(f"expected shipped project not found: {csproj}")
        for name, version in _dotnet_list_packages(csproj):
            seen[name] = version  # last-writer-wins; a real cross-project version conflict
            # would already fail NuGet restore before this script ever runs.
    deps: list[Dependency] = []
    for name, version in sorted(seen.items(), key=lambda kv: kv[0].lower()):
        license_str, source = _nuspec_license(name, version)
        scope = "platform" if _is_platform_nuget_package(name) else "shipped"
        deps.append(Dependency("nuget", name, version, scope, license_str, True, source))
    return deps


# ---------------------------------------------------------------------------
# Python enumeration
# ---------------------------------------------------------------------------

def _parse_pyproject_dependencies(pyproject: Path) -> tuple[list[str], dict[str, list[str]]]:
    data = tomllib.loads(pyproject.read_text(encoding="utf-8"))
    project = data.get("project", {})
    core = [_pep508_name(d) for d in project.get("dependencies", [])]
    extras = {
        group: [_pep508_name(d) for d in deps]
        for group, deps in project.get("optional-dependencies", {}).items()
    }
    return core, extras


def _pep508_name(requirement: str) -> str:
    """'pyrodigal>=3' -> 'pyrodigal'; also handles bare names like 'torch'."""
    for sep in ("[", ">=", "<=", "==", "~=", ">", "<", "!=", " "):
        idx = requirement.find(sep)
        if idx != -1:
            requirement = requirement[:idx]
    return requirement.strip()


def _venv_license_lookup(venv_python: Path, names: list[str]) -> dict[str, dict]:
    """Ask `venv_python` (never the bare `python` on PATH -- Hard Rule 20) for each
    installed package's PEP 639 license_expression, falling back to legacy License/
    Classifier fields. Returns {name: {"installed": bool, "version": str, "license": str}}."""
    script = (
        "import importlib.metadata as m, json, sys\n"
        "names = json.loads(sys.argv[1])\n"
        "out = {}\n"
        "for name in names:\n"
        "    try:\n"
        "        d = m.distribution(name)\n"
        "    except m.PackageNotFoundError:\n"
        "        out[name] = {'installed': False}\n"
        "        continue\n"
        "    raw = d.metadata.json\n"
        "    lic = raw.get('license_expression')\n"
        "    if not lic:\n"
        "        lic = raw.get('license')\n"
        "    if not lic:\n"
        "        for c in raw.get('classifier', []) if isinstance(raw.get('classifier'), list) else []:\n"
        "            if 'License ::' in c:\n"
        "                lic = c.split('::')[-1].strip()\n"
        "                break\n"
        "    out[name] = {'installed': True, 'version': d.version, 'license': lic or 'UNKNOWN'}\n"
        "print(json.dumps(out))\n"
    )
    proc = subprocess.run(
        [str(venv_python), "-c", script, json.dumps(names)],
        capture_output=True, text=True, timeout=60,
    )
    if proc.returncode != 0:
        raise NoticesGenerationError(f"worker venv license lookup failed: {proc.stdout}\n{proc.stderr}")
    return json.loads(proc.stdout)


def _worker_venv_python(worker_root: Path) -> Path:
    """Hard Rule 20: the venv is `worker/.venv` and nothing else, never the bare `python`
    on PATH -- but that rule is about which venv, not which OS. `uv venv` lays out
    `Scripts/python.exe` on Windows (this repo's own dev box) and `bin/python` on Linux/Mac
    (every current CI runner's OS, per `ci-worker.yml`'s own `uv venv` step) -- checking
    both, in that order, is what actually makes this generator portable to a future CI job
    that runs it against a freshly `uv venv`'d worker checkout, rather than assuming the
    dev box's own layout everywhere this script runs."""
    windows = worker_root / ".venv" / "Scripts" / "python.exe"
    if windows.is_file():
        return windows
    posix = worker_root / ".venv" / "bin" / "python"
    if posix.is_file():
        return posix
    return windows  # neither exists; report the Windows path in the error (this repo's own convention)


def collect_python_dependencies(worker_root: Path) -> list[Dependency]:
    pyproject = worker_root / "pyproject.toml"
    venv_python = _worker_venv_python(worker_root)
    if not pyproject.is_file():
        raise NoticesGenerationError(f"expected {pyproject} to exist")
    if not venv_python.is_file():
        raise NoticesGenerationError(
            f"expected {venv_python} to exist (Hard Rule 20: worker\\.venv, never the bare python on PATH)"
        )

    core, extras = _parse_pyproject_dependencies(pyproject)
    shipped_names = list(core)
    dev_names: list[str] = []
    for group, names in extras.items():
        if group in DEV_ONLY_PY_EXTRAS:
            dev_names += names
        else:  # SHIPPED_PY_EXTRAS and anything unclassified (conservative default)
            shipped_names += names

    all_names = sorted(set(shipped_names) | set(dev_names))
    looked_up = _venv_license_lookup(venv_python, all_names)

    deps: list[Dependency] = []
    for name in shipped_names:
        deps.append(_python_dependency(name, "shipped", looked_up.get(name, {"installed": False})))
    for name in dev_names:
        deps.append(_python_dependency(name, "dev-only", looked_up.get(name, {"installed": False})))
    deps.sort(key=lambda d: (d.scope, d.name.lower()))
    return deps


def _python_dependency(name: str, scope: str, info: dict) -> Dependency:
    curated = CURATED_LICENSES.get(("pypi", name))
    if info.get("installed"):
        raw_license = info["license"]
        if curated and curated.raw == raw_license:
            # The installed package's real metadata still says what the curated entry
            # expects -- apply the human-readable override, never the raw non-SPDX string.
            return Dependency("pypi", name, info["version"], scope, curated.license, curated.verified, curated.note)
        return Dependency("pypi", name, info["version"], scope, raw_license, True, "worker/.venv (importlib.metadata)")
    if curated and curated.raw is None:
        return Dependency("pypi", name, "(not installed)", scope, curated.license, curated.verified, curated.note)
    return Dependency("pypi", name, "(not installed)", scope, "UNKNOWN", False,
                       "not installed in worker/.venv and no curated override recorded")


# ---------------------------------------------------------------------------
# Classification + rendering
# ---------------------------------------------------------------------------

def classify(dep: Dependency) -> str:
    """Return 'allowed', 'denied-with-carveout', 'denied', 'unrecognized', 'dev-only', or
    'platform'. The last two are never gated at all (see `_is_platform_nuget_package` and
    `DEV_ONLY_PY_EXTRAS`'s doc comments for why each is out of Hard Rule 21's reach)."""
    if dep.scope in ("dev-only", "platform"):
        return dep.scope
    atoms = [a.strip() for a in dep.license.replace(" OR ", " AND ").split(" AND ") if a.strip()]
    if atoms and all(a in ALLOWED_LICENSES for a in atoms):
        return "allowed"
    if any(a.startswith(DENIED_PREFIXES) for a in atoms):
        return "denied-with-carveout" if (dep.ecosystem, dep.name) in CARVE_OUTS else "denied"
    curated = CURATED_LICENSES.get((dep.ecosystem, dep.name))
    if curated and curated.license == dep.license:
        return "allowed" if curated.allowed else "denied"
    return "unrecognized"


def render(dotnet_deps: list[Dependency], python_deps: list[Dependency]) -> str:
    lines: list[str] = []
    lines.append("# Third-Party Notices")
    lines.append("")
    lines.append(
        "Generated by `scripts/gen_third_party_notices.py` (issue #34). Do not hand-edit -- "
        "regenerate after any dependency change and commit the result in the same commit "
        "(Hard Rule 16/21). `scripts/check_third_party_notices.py` fails CI if this file "
        "and a fresh regeneration disagree."
    )
    lines.append("")

    shipped_dotnet = [d for d in dotnet_deps if d.scope == "shipped"]
    platform_dotnet = [d for d in dotnet_deps if d.scope == "platform"]
    shipped_python = [d for d in python_deps if d.scope == "shipped"]
    dev_python = [d for d in python_deps if d.scope == "dev-only"]

    lines.append("## Shipped .NET dependencies (app installer payload)")
    lines.append("")
    lines.append("| Package | Version | Licence |")
    lines.append("|---|---|---|")
    for d in shipped_dotnet:
        lines.append(f"| {d.name} | {d.version} | {d.license} |")
    lines.append("")

    lines.append("## Shipped Python dependencies (worker core + [genes]/[evo] container extras)")
    lines.append("")
    lines.append("| Package | Version | Licence | Verified |")
    lines.append("|---|---|---|---|")
    for d in shipped_python:
        verified = "measured" if d.license_verified else "THEORY (unverified)"
        lines.append(f"| {d.name} | {d.version} | {d.license} | {verified} |")
    lines.append("")

    lines.append("## Development-only dependencies (never shipped; Hard Rule 21 does not gate these)")
    lines.append("")
    lines.append(
        "Test frameworks, linters, and `worker`'s own `[dev]` extra. Never installed into a "
        "container image, the local engine's pinned venv, or the Velopack installer payload."
    )
    lines.append("")
    lines.append("| Package | Ecosystem | Version | Licence |")
    lines.append("|---|---|---|---|")
    for d in dev_python:
        lines.append(f"| {d.name} | pypi | {d.version} | {d.license} |")
    lines.append("")

    lines.append(
        "## Platform components (Microsoft proprietary redistributables, not a third-party OSS choice)"
    )
    lines.append("")
    lines.append(
        "The Windows App SDK, WebView2, and Windows SDK build tools a WinUI 3 app is built "
        "on, under Microsoft's own \"MICROSOFT SOFTWARE LICENSE TERMS\" (their nuspecs "
        "declare `<license type=\"file\">`, not an SPDX expression). Whether Hard Rule 21's "
        "MIT/Apache/BSD gate reaches the platform runtime itself, as opposed to a dependency "
        "this project chose to add, is an open scoping question - flagged as a DECISION "
        "rather than assumed silently either way."
    )
    lines.append("")
    lines.append("| Package | Version | Licence |")
    lines.append("|---|---|---|")
    for d in platform_dotnet:
        lines.append(f"| {d.name} | {d.version} | {d.license} |")
    lines.append("")

    exceptions = [d for d in shipped_dotnet + shipped_python if classify(d) == "denied-with-carveout"]
    lines.append("## Recorded licence exceptions (Hard Rule 21 carve-outs)")
    lines.append("")
    if not exceptions:
        lines.append("(none)")
    for d in exceptions:
        carve = CARVE_OUTS[(d.ecosystem, d.name)]
        lines.append(f"### {d.name} {d.version} ({d.license}) -- issue #{carve['issue']}")
        lines.append("")
        lines.append(carve["note"])
        lines.append("")

    problems = [d for d in shipped_dotnet + shipped_python if classify(d) in ("denied", "unrecognized")]
    if problems:
        lines.append("## UNRESOLVED licence problems (this generator refuses to call the tree clean)")
        lines.append("")
        for d in problems:
            lines.append(f"- **{d.name} {d.version}** ({d.ecosystem}): licence `{d.license}` is not "
                          f"MIT/Apache-2.0/BSD, and no `CARVE_OUTS`/`CURATED_LICENSES` entry resolves it. "
                          f"Source: {d.license_source}.")
        lines.append("")

    return "\n".join(lines) + "\n"


def generate(app_root: Path, worker_root: Path) -> tuple[str, bool]:
    """Return (rendered_markdown, clean). clean=False means an unresolved licence problem exists."""
    dotnet_deps = collect_dotnet_dependencies(app_root)
    python_deps = collect_python_dependencies(worker_root)
    text = render(dotnet_deps, python_deps)
    clean = not any(classify(d) in ("denied", "unrecognized") for d in dotnet_deps + python_deps)
    return text, clean


# ---------------------------------------------------------------------------
# Self-test: pure classify()/render() logic, no dotnet/venv required.
# ---------------------------------------------------------------------------

def self_test() -> bool:
    ok = True

    def check(label: str, condition: bool):
        nonlocal ok
        print(f"{'ok' if condition else 'FAIL'}    {label}")
        ok = ok and condition

    mit = Dependency("nuget", "Widget", "1.0.0", "shipped", "MIT", True, "test")
    check("an MIT-licensed shipped dependency classifies as allowed", classify(mit) == "allowed")

    compound = Dependency("pypi", "numpy", "2.0", "shipped", "BSD-3-Clause AND 0BSD AND MIT", True, "test")
    check("a compound AND licence of all-allowed atoms classifies as allowed", classify(compound) == "allowed")

    gpl_no_carveout = Dependency("pypi", "some-gpl-tool", "1.0", "shipped", "GPL-3.0-or-later", True, "test")
    check(
        "a GPL-family shipped dependency with NO carve-out classifies as denied (the gate this whole "
        "script exists to enforce)",
        classify(gpl_no_carveout) == "denied",
    )

    gpl_with_carveout = Dependency("pypi", "pyrodigal", "3.7.1", "shipped", "GPL-3.0-or-later", True, "test")
    check(
        "pyrodigal (the recorded issue #301 carve-out) classifies as denied-with-carveout, not denied",
        classify(gpl_with_carveout) == "denied-with-carveout",
    )

    dev_gpl = Dependency("pypi", "some-gpl-dev-tool", "1.0", "dev-only", "GPL-3.0-or-later", True, "test")
    check(
        "a GPL-family DEV-ONLY dependency never fails the gate at all (Hard Rule 21 scopes to shipped code)",
        classify(dev_gpl) == "dev-only",
    )

    weird = Dependency("pypi", "some-custom-licensed-tool", "1.0", "shipped", "LicenseRef-Something-Weird", True, "test")
    check(
        "an unrecognised, non-SPDX licence string with no curated override classifies as unrecognized "
        "(never silently allowed)",
        classify(weird) == "unrecognized",
    )

    biopython = Dependency("pypi", "biopython", "1.88", "shipped", CURATED_LICENSES[("pypi", "biopython")].license, True, "test")
    check(
        "biopython's curated override (measured against its own LICENSE.rst) classifies as allowed",
        classify(biopython) == "allowed",
    )

    rendered = render([mit], [gpl_with_carveout, dev_gpl])
    check("render() names the shipped dependency", "Widget" in rendered)
    check("render() lists the carve-out with its issue number", "issue #301" in rendered)
    check("render() puts the dev-only dependency in its own section, not the shipped one", "some-gpl-dev-tool" in rendered)
    check(
        "render() does NOT report an unresolved-problem line for the carved-out dependency",
        "pyrodigal" not in rendered.split("## UNRESOLVED")[-1] if "## UNRESOLVED" in rendered else True,
    )

    rendered_with_problem = render([mit, weird], [])
    check(
        "render() reports an UNRESOLVED licence problem for an unrecognised shipped dependency",
        "UNRESOLVED licence problems" in rendered_with_problem and "some-custom-licensed-tool" in rendered_with_problem,
    )

    # Vacuity guard: this self-test itself must exercise a plausible number of classify()
    # outcomes, or a future refactor that made classify() a no-op could still show "PASS".
    outcomes = {classify(d) for d in [mit, compound, gpl_no_carveout, gpl_with_carveout, dev_gpl, weird, biopython]}
    check(
        "the self-test actually exercised every classify() outcome "
        f"(saw {sorted(outcomes)}, need all 5)",
        outcomes == {"allowed", "denied", "denied-with-carveout", "dev-only", "unrecognized"},
    )

    print(f"\n{'PASS' if ok else 'FAIL'}: gen_third_party_notices self-test")
    return ok


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--app-root", type=Path, default=Path("app"), help="path to app/ (default: ./app)")
    ap.add_argument("--worker-root", type=Path, default=Path("worker"), help="path to worker/ (default: ./worker)")
    ap.add_argument("--stdout", action="store_true", help="print the notices instead of writing THIRD-PARTY-NOTICES.md")
    ap.add_argument("--out", type=Path, default=Path("THIRD-PARTY-NOTICES.md"), help="output path (default: ./THIRD-PARTY-NOTICES.md)")
    ap.add_argument("--self-test", action="store_true", help="run against synthetic fixtures and exit")
    args = ap.parse_args(argv)

    if args.self_test:
        return 0 if self_test() else 1

    try:
        text, clean = generate(args.app_root, args.worker_root)
    except NoticesGenerationError as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        return 2

    if args.stdout:
        sys.stdout.write(text)
    else:
        args.out.write_text(text, encoding="utf-8", newline="\n")
        print(f"wrote {args.out}")

    if not clean:
        print("ERROR: one or more shipped dependencies have an unresolved licence problem "
              "(see the UNRESOLVED section above) -- Hard Rule 21.", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
