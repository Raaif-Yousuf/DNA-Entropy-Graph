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


# ---------------------------------------------------------------------------
# Vendored web assets (scripts/vendored_assets.json)
# ---------------------------------------------------------------------------

import hashlib  # noqa: E402
import json  # noqa: E402

import pytest  # noqa: E402

_ASSET_BYTES = b"console.log('vendored');\n"
_LICENSE_TEXT = "The MIT License (MIT)\n"


def _make_root(tmp_path: Path, *, licence: str = "MIT", sha: str | None = None,
               write_asset: bool = True, write_licence: bool = True) -> Path:
    (tmp_path / "scripts").mkdir()
    viewer = tmp_path / "web"
    viewer.mkdir()
    if write_asset:
        (viewer / "lib.min.js").write_bytes(_ASSET_BYTES)
    if write_licence:
        (viewer / "lib.LICENSE.txt").write_text(_LICENSE_TEXT, encoding="utf-8")
    manifest = {
        "directory": "web",
        "assets": [{
            "path": "web/lib.min.js",
            "name": "lib.min.js",
            "description": "a test library",
            "version": "1.2.3",
            "license": licence,
            "license_file": "web/lib.LICENSE.txt",
            "source": "https://example.invalid/lib-1.2.3.tgz",
            "upstream_shasum": "abc123",
            "sha256": sha if sha is not None else hashlib.sha256(_ASSET_BYTES).hexdigest().upper(),
        }],
    }
    (tmp_path / "scripts" / "vendored_assets.json").write_text(json.dumps(manifest), encoding="utf-8")
    return tmp_path


def test_vendored_section_is_emitted_from_a_manifest_in_a_tmp_root(tmp_path):
    root = _make_root(tmp_path)
    entries = gtpn.load_vendored_assets(root)
    text = gtpn.render([], [], vendored=entries)
    assert "## Vendored web assets (app installer payload, `web/`)" in text
    assert "| Asset | Version | Licence | Source | sha256 |" in text
    assert "| lib.min.js (a test library) | 1.2.3 | MIT |" in text
    assert hashlib.sha256(_ASSET_BYTES).hexdigest().upper() in text
    assert "(shasum abc123)" in text


def test_sha_mismatch_names_the_file(tmp_path):
    root = _make_root(tmp_path, sha="0" * 64)
    with pytest.raises(gtpn.VendoredAssetError) as exc:
        gtpn.load_vendored_assets(root)
    assert "web/lib.min.js" in str(exc.value)
    assert "sha256" in str(exc.value)


def test_missing_asset_names_the_file(tmp_path):
    root = _make_root(tmp_path, write_asset=False)
    with pytest.raises(gtpn.VendoredAssetError) as exc:
        gtpn.load_vendored_assets(root)
    assert "web/lib.min.js" in str(exc.value)


def test_missing_licence_file_names_the_file(tmp_path):
    root = _make_root(tmp_path, write_licence=False)
    with pytest.raises(gtpn.VendoredAssetError) as exc:
        gtpn.load_vendored_assets(root)
    assert "web/lib.LICENSE.txt" in str(exc.value)


@pytest.mark.parametrize("licence", ["GPL-3.0-only", "LGPL-2.1-or-later", "AGPL-3.0-only",
                                      "LicenseRef-Whatever", "MIT AND GPL-2.0-only"])
def test_non_allowed_licence_fails_naming_the_file(tmp_path, licence):
    root = _make_root(tmp_path, licence=licence)
    with pytest.raises(gtpn.VendoredAssetError) as exc:
        gtpn.load_vendored_assets(root)
    assert "web/lib.min.js" in str(exc.value)
    assert licence in str(exc.value)


def test_cli_exits_2_with_error_line_on_sha_mismatch(tmp_path):
    root = _make_root(tmp_path, sha="0" * 64)
    proc = subprocess.run(
        [sys.executable, str(SCRIPTS_DIR / "gen_third_party_notices.py"), "--stdout",
         "--repo-root", str(root), "--app-root", str(root / "app"), "--worker-root", str(root / "worker")],
        capture_output=True, text=True, timeout=60,
    )
    assert proc.returncode == 2
    assert any(line.startswith("ERROR:") and "web/lib.min.js" in line for line in proc.stderr.splitlines())


def test_real_manifest_matches_the_committed_vendored_section_and_files():
    repo_root = SCRIPTS_DIR.parent
    entries = gtpn.load_vendored_assets(repo_root)  # also verifies sha256 + licence on disk
    assert len(entries) >= 1
    assert any(e.path.endswith("igv.min.js") and e.license == "MIT" for e in entries)
    section = gtpn.render_vendored_section(entries)
    committed = (repo_root / "THIRD-PARTY-NOTICES.md").read_text(encoding="utf-8").replace("\r\n", "\n")
    assert section in committed
