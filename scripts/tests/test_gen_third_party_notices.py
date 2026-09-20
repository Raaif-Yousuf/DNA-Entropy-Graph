"""Tests for scripts/gen_third_party_notices.py."""

from __future__ import annotations

import subprocess
import sys
from pathlib import Path

SCRIPTS_DIR = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(SCRIPTS_DIR))

import gen_third_party_notices as gtpn  # noqa: E402


def test_self_test_passes():
    proc = subprocess.run(
        [sys.executable, str(SCRIPTS_DIR / "gen_third_party_notices.py"), "--self-test"],
        capture_output=True, text=True, timeout=30,
    )
    assert proc.returncode == 0, proc.stdout + proc.stderr
    assert "PASS" in proc.stdout


def test_help_exits_zero():
    proc = subprocess.run(
        [sys.executable, str(SCRIPTS_DIR / "gen_third_party_notices.py"), "--help"],
        capture_output=True, text=True, timeout=15,
    )
    assert proc.returncode == 0


def test_allowed_license_classifies_allowed():
    dep = gtpn.Dependency("nuget", "Widget", "1.0.0", "shipped", "Apache-2.0", True, "test")
    assert gtpn.classify(dep) == "allowed"


def test_denied_license_with_no_carveout_classifies_denied():
    dep = gtpn.Dependency("pypi", "some-gpl-tool", "1.0", "shipped", "GPL-2.0-only", True, "test")
    assert gtpn.classify(dep) == "denied"


def test_pyrodigal_carveout_is_recorded_and_names_issue_301():
    assert ("pypi", "pyrodigal") in gtpn.CARVE_OUTS
    assert gtpn.CARVE_OUTS[("pypi", "pyrodigal")]["issue"] == "301"


def test_dev_only_scope_never_fails_regardless_of_license():
    dep = gtpn.Dependency("pypi", "anything", "1.0", "dev-only", "AGPL-3.0-only", True, "test")
    assert gtpn.classify(dep) == "dev-only"


def test_platform_scope_never_fails_regardless_of_license():
    dep = gtpn.Dependency("nuget", "Microsoft.WindowsAppSDK", "1.8.0", "platform", "(license file inside package: license.txt)", True, "test")
    assert gtpn.classify(dep) == "platform"


def test_is_platform_nuget_package_matches_the_known_families():
    assert gtpn._is_platform_nuget_package("Microsoft.WindowsAppSDK")
    assert gtpn._is_platform_nuget_package("Microsoft.WindowsAppSDK.Runtime")
    assert gtpn._is_platform_nuget_package("Microsoft.Web.WebView2")
    assert gtpn._is_platform_nuget_package("Microsoft.Windows.SDK.BuildTools")
    assert not gtpn._is_platform_nuget_package("CommunityToolkit.Mvvm")
    assert not gtpn._is_platform_nuget_package("Microsoft.Extensions.DependencyInjection")


def test_pep508_name_strips_version_specifiers_and_extras():
    assert gtpn._pep508_name("pyrodigal>=3") == "pyrodigal"
    assert gtpn._pep508_name("torch") == "torch"
    assert gtpn._pep508_name("numpy>=1.24") == "numpy"


def test_render_names_every_shipped_dependency_passed_in():
    dotnet = [gtpn.Dependency("nuget", "PkgOne", "1.0", "shipped", "MIT", True, "t")]
    python = [gtpn.Dependency("pypi", "pkgtwo", "2.0", "shipped", "MIT", True, "t")]
    rendered = gtpn.render(dotnet, python)
    assert "PkgOne" in rendered
    assert "pkgtwo" in rendered


def test_generate_against_the_real_repo_tree_runs_end_to_end(real_dependency_tree):
    """The decisive, non-synthetic test: this actually shells out to `dotnet list package`
    for every shipped project and to worker/.venv for every worker dependency. Slow (real
    subprocesses), but a fixture-only test suite would never catch a broken --app-root/
    --worker-root default, a csproj path that moved, or a `dotnet` CLI contract change."""
    repo_root = SCRIPTS_DIR.parent
    text, clean = gtpn.generate(repo_root / "app", repo_root / "worker")
    assert "CommunityToolkit.Mvvm" in text
    assert "pyrodigal" in text
    assert "issue #301" in text
    # Not asserting `clean` is True: evo2's licence is a genuine, currently unresolved
    # question (see gen_third_party_notices.py's own CURATED_LICENSES comment) -- this test
    # would need updating the day that is resolved, not before.
    assert isinstance(clean, bool)
