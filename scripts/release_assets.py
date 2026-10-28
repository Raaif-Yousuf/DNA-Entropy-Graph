"""scripts/release_assets.py -- exactly this release's files, staged, attested and checked (#178, #179).

    python scripts/release_assets.py stage --dir app/releases --stage stage --version 0.2.0 --github-output $GITHUB_OUTPUT
    gh release view <tag> --json assets --jq '.assets[].name' | python scripts/release_assets.py check --version 0.2.0

MEASURED 2026-10-15 from velopack 1.2.161 source (tag 1.2.161):
  - `vpk download github` fetches only the latest full nupkg (no feed file) into --outputDir;
  - `vpk pack` writes `assets.<channel>.json` (default channel `win`) naming the files it produced;
  - `vpk upload github` uploads exactly the files `assets.<channel>.json` names (BuildAssets.GetFilePaths), then
    `releases.win.json` (CoreUtil.GetVeloReleaseIndexName) and, for the default Windows channel, a legacy `RELEASES`.
So the previous release's nupkg that `vpk download` leaves in the folder is not uploaded, but a `*.nupkg` glob would
attest it. `stage` reads the assets file instead of the folder, refuses a nupkg of another version, requires the FULL
nupkg and Setup.exe, copies those files (plus the assets file vpk upload needs) to --stage, and writes the attestation
subjects to $GITHUB_OUTPUT. `check` compares a published release's asset names, anchored, against the required set.

Exit codes: 0 ok, 1 a problem (printed as `ERROR:`).
"""

from __future__ import annotations

import argparse
import json
import re
import shutil
import sys
from pathlib import Path

PACK_ID = "DnaEntropyGraph"
CHANNEL = "win"
SETUP_NAME = f"{PACK_ID}-{CHANNEL}-Setup.exe"
_NUPKG_RE = re.compile(rf"^{PACK_ID}-(?P<version>.+)-{CHANNEL}-(?P<kind>full|delta)\.nupkg$")


def _assets_file(folder: Path) -> Path:
    return folder / f"assets.{CHANNEL}.json"


def release_files(folder: Path, version: str) -> list[Path]:
    """The files `vpk pack` produced for `version`, from its assets file (never from a folder listing)."""
    assets = _assets_file(folder)
    if not assets.is_file():
        raise ValueError(f"{assets.name} is not in {folder} (did vpk pack run with the default channel?)")
    try:
        listed = json.loads(assets.read_text(encoding="utf-8-sig"))
        names = [str(item["RelativeFileName"]) for item in listed]
    except (ValueError, KeyError, TypeError):
        raise ValueError(f"{assets.name} is not the list vpk pack writes") from None
    root = folder.resolve()
    files: list[Path] = []
    for name in names:
        path = (folder / name).resolve()
        if path.parent != root:
            raise ValueError(f"{name!r} in {assets.name} is outside the release folder")
        if not path.is_file():
            raise ValueError(f"{name} is listed in {assets.name} but is missing on disk")
        m = _NUPKG_RE.match(name)
        if m and m.group("version") != version:
            raise ValueError(f"{name} is a package of version {m.group('version')}, not {version}")
        files.append(path)
    kinds = {m.group("kind") for m in (_NUPKG_RE.match(p.name) for p in files) if m}
    if "full" not in kinds:
        raise ValueError(f"no full package ({PACK_ID}-{version}-{CHANNEL}-full.nupkg) is listed: a delta alone cannot install")
    if SETUP_NAME not in {p.name for p in files}:
        raise ValueError(f"{SETUP_NAME} is not listed")
    return files


def stage(folder: Path, dest: Path, version: str) -> list[str]:
    """Copy this release's files (and the assets file) to `dest`. Returns the attestation subjects (nupkg and exe)."""
    files = release_files(folder, version)
    dest.mkdir(parents=True, exist_ok=True)
    for path in files:
        shutil.copy2(path, dest / path.name)
    shutil.copy2(_assets_file(folder), dest / _assets_file(folder).name)
    return sorted(str(dest / p.name).replace("\\", "/") for p in files if p.suffix in (".nupkg", ".exe"))


def required_patterns(version: str) -> dict[str, re.Pattern[str]]:
    v = re.escape(version)
    return {
        SETUP_NAME: re.compile(rf"^{re.escape(SETUP_NAME)}$"),
        f"{PACK_ID}-{version}-{CHANNEL}-full.nupkg": re.compile(rf"^{PACK_ID}-{v}-{CHANNEL}-full\.nupkg$"),
        f"releases.{CHANNEL}.json": re.compile(rf"^releases\.{CHANNEL}\.json$"),
        "RELEASES": re.compile(r"^RELEASES$"),
        "the worker wheel (*.whl)": re.compile(r"^[A-Za-z0-9_.+-]+\.whl$"),
        "SHA256SUMS": re.compile(r"^SHA256SUMS$"),
        "image-digests.txt": re.compile(r"^image-digests\.txt$"),
        "sbom-app.cdx.json": re.compile(r"^sbom-app\.cdx\.json$"),
        "sbom-image-cuda.cdx.json": re.compile(r"^sbom-image-cuda\.cdx\.json$"),
        "sbom-image-cpu.cdx.json": re.compile(r"^sbom-image-cpu\.cdx\.json$"),
    }


def missing_assets(names: list[str], version: str) -> list[str]:
    return [label for label, rx in required_patterns(version).items() if not any(rx.match(n) for n in names)]


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=(__doc__ or "").split("\n")[0])
    sub = ap.add_subparsers(dest="cmd", required=True)
    st = sub.add_parser("stage")
    st.add_argument("--dir", type=Path, required=True)
    st.add_argument("--stage", type=Path, required=True)
    st.add_argument("--version", required=True)
    st.add_argument("--github-output", type=Path, default=None)
    ck = sub.add_parser("check")
    ck.add_argument("--version", required=True)
    args = ap.parse_args(argv)
    version = args.version.lstrip("v")
    if args.cmd == "stage":
        try:
            subjects = stage(args.dir, args.stage, version)
        except (ValueError, OSError) as exc:
            print(f"ERROR: {exc}. Fix the pack step and re-run the job.")
            return 1
        if args.github_output:
            with args.github_output.open("a", encoding="utf-8", newline="\n") as fh:
                fh.write("subjects<<EOF_SUBJECTS\n" + "\n".join(subjects) + "\nEOF_SUBJECTS\n")
        print(f"OK: staged {len(subjects)} attestable file(s) for {version}")
        return 0
    names = [ln.strip() for ln in sys.stdin.read().splitlines() if ln.strip()]
    missing = missing_assets(names, version)
    if missing:
        print(f"ERROR: the draft release is missing: {', '.join(missing)}. It stays a draft; fix the cause and re-run the failed job.")
        return 1
    print(f"OK: all {len(required_patterns(version))} required assets are attached")
    return 0


if __name__ == "__main__":
    sys.exit(main())
