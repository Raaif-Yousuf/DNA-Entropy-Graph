"""scripts/release_assets.py: exactly this release's files are staged, attested and checked (#178, #179).

MEASURED 2026-10-15 from velopack 1.2.161 source (tag 1.2.161):
- `vpk download github` fetches ONLY the latest full nupkg (no feed file), into --outputDir;
- `vpk pack` writes assets.<channel>.json (default channel `win`) listing the files it produced;
- `vpk upload github` uploads exactly the files that assets.<channel>.json lists (BuildAssets.GetFilePaths), then
  `releases.win.json` (GetVeloReleaseIndexName) and, for the default Windows channel, a legacy `RELEASES`.
So a previous release's nupkg sitting in the output folder is never uploaded, but a `*.nupkg` glob WOULD attest it.
"""

from __future__ import annotations

import json
import subprocess
import sys
from pathlib import Path

import pytest

SCRIPTS_DIR = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(SCRIPTS_DIR))

import release_assets as ra  # noqa: E402

VER = "0.2.0"
NEW_FULL = f"DnaEntropyGraph-{VER}-win-full.nupkg"
NEW_DELTA = f"DnaEntropyGraph-{VER}-win-delta.nupkg"
OLD_FULL = "DnaEntropyGraph-0.1.0-win-full.nupkg"
SETUP = "DnaEntropyGraph-win-Setup.exe"
PORTABLE = "DnaEntropyGraph-win-Portable.zip"


def _folder(tmp_path: Path, listed: list[str], extra_on_disk: list[str] = ()) -> Path:
    d = tmp_path / "releases"
    d.mkdir()
    for name in [*listed, *extra_on_disk]:
        (d / name).write_bytes(b"x")
    (d / "assets.win.json").write_text(json.dumps([{"RelativeFileName": n, "Type": 0} for n in listed]), encoding="utf-8", newline="\n")
    return d


def test_files_come_only_from_the_assets_file_not_the_folder(tmp_path):
    d = _folder(tmp_path, [NEW_FULL, NEW_DELTA, SETUP, PORTABLE], extra_on_disk=[OLD_FULL])
    got = ra.release_files(d, VER)
    assert sorted(p.name for p in got) == sorted([NEW_FULL, NEW_DELTA, SETUP, PORTABLE])
    assert OLD_FULL not in [p.name for p in got]


def test_a_listed_nupkg_of_another_version_is_refused(tmp_path):
    d = _folder(tmp_path, [NEW_FULL, OLD_FULL, SETUP])
    with pytest.raises(ValueError, match="0.1.0"):
        ra.release_files(d, VER)


def test_a_full_nupkg_is_required_a_delta_alone_is_not_enough(tmp_path):
    d = _folder(tmp_path, [NEW_DELTA, SETUP])
    with pytest.raises(ValueError, match="full"):
        ra.release_files(d, VER)


def test_setup_exe_is_required(tmp_path):
    d = _folder(tmp_path, [NEW_FULL])
    with pytest.raises(ValueError, match="Setup"):
        ra.release_files(d, VER)


def test_a_listed_file_missing_on_disk_is_refused(tmp_path):
    d = _folder(tmp_path, [NEW_FULL, SETUP])
    (d / SETUP).unlink()
    with pytest.raises(ValueError, match="missing"):
        ra.release_files(d, VER)


def test_no_assets_file_is_refused(tmp_path):
    d = tmp_path / "releases"
    d.mkdir()
    with pytest.raises(ValueError, match="assets.win.json"):
        ra.release_files(d, VER)


def test_a_path_escaping_the_folder_is_refused(tmp_path):
    d = _folder(tmp_path, [NEW_FULL, SETUP])
    (d / "assets.win.json").write_text(json.dumps([{"RelativeFileName": "../evil.exe"}]), encoding="utf-8")
    with pytest.raises(ValueError):
        ra.release_files(d, VER)


def test_stage_copies_the_files_and_the_assets_file_and_lists_subjects(tmp_path):
    d = _folder(tmp_path, [NEW_FULL, NEW_DELTA, SETUP], extra_on_disk=[OLD_FULL])
    stage = tmp_path / "stage"
    subjects = ra.stage(d, stage, VER)
    assert sorted(p.name for p in stage.iterdir()) == sorted([NEW_FULL, NEW_DELTA, SETUP, "assets.win.json"])
    assert OLD_FULL not in [p.name for p in stage.iterdir()]
    assert OLD_FULL not in "".join(subjects)
    assert all(s.endswith((".nupkg", ".exe")) for s in subjects) and len(subjects) == 3


GOOD = [
    SETUP,
    NEW_FULL,
    NEW_DELTA,
    "releases.win.json",
    "RELEASES",
    f"dna_entropy-{VER}-py3-none-any.whl",
    "SHA256SUMS",
    "image-digests.txt",
    "sbom-app.cdx.json",
    "sbom-image-cuda.cdx.json",
    "sbom-image-cpu.cdx.json",
]


def test_published_set_complete_passes():
    assert ra.missing_assets(GOOD, VER) == []


@pytest.mark.parametrize(
    "drop",
    [
        SETUP,
        NEW_FULL,
        "releases.win.json",
        "RELEASES",
        "SHA256SUMS",
        "image-digests.txt",
        "sbom-app.cdx.json",
        "sbom-image-cuda.cdx.json",
        "sbom-image-cpu.cdx.json",
        f"dna_entropy-{VER}-py3-none-any.whl",
    ],
)
def test_each_required_asset_is_required(drop):
    assert ra.missing_assets([n for n in GOOD if n != drop], VER)


def test_a_delta_alone_does_not_satisfy_the_full_nupkg_requirement():
    assert ra.missing_assets([n for n in GOOD if n != NEW_FULL], VER)


def test_the_full_nupkg_of_another_version_does_not_count():
    names = [n for n in GOOD if n != NEW_FULL] + [OLD_FULL]
    assert ra.missing_assets(names, VER)


@pytest.mark.parametrize(
    "decoy",
    ["xSetup.exe", "DnaEntropyGraph-win-Setup.exe.sig", "myreleases.win.json", "RELEASES-old", "old-RELEASES", "sbom-app.cdx.json.bak"],
)
def test_substring_decoys_do_not_satisfy_an_anchored_check(decoy):
    only_decoy = [n for n in GOOD if n not in (SETUP, "releases.win.json", "RELEASES", "sbom-app.cdx.json")] + [decoy]
    assert ra.missing_assets(only_decoy, VER)


def test_cli_stage_and_check(tmp_path):
    d = _folder(tmp_path, [NEW_FULL, SETUP], extra_on_disk=[OLD_FULL])
    out = tmp_path / "gh_output"
    r = subprocess.run(
        [
            sys.executable,
            str(SCRIPTS_DIR / "release_assets.py"),
            "stage",
            "--dir",
            str(d),
            "--stage",
            str(tmp_path / "s"),
            "--version",
            VER,
            "--github-output",
            str(out),
        ],
        capture_output=True,
        text=True,
    )
    assert r.returncode == 0, r.stdout + r.stderr
    body = out.read_text(encoding="utf-8")
    assert body.startswith("subjects<<") and NEW_FULL in body and OLD_FULL not in body
    r = subprocess.run(
        [sys.executable, str(SCRIPTS_DIR / "release_assets.py"), "check", "--version", VER],
        input="\n".join(GOOD[:3]),
        capture_output=True,
        text=True,
    )
    assert r.returncode == 1 and "ERROR" in r.stdout and "releases.win.json" in r.stdout
    r = subprocess.run(
        [sys.executable, str(SCRIPTS_DIR / "release_assets.py"), "check", "--version", VER],
        input="\n".join(GOOD),
        capture_output=True,
        text=True,
    )
    assert r.returncode == 0
