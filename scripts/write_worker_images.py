"""scripts/write_worker_images.py -- pin the just-built worker images into the app's allowlist (#178, #458).

    python scripts/write_worker_images.py --version v1.2.3 --cuda-digest sha256:<64 hex> --cpu-digest sha256:<64 hex>
    python scripts/write_worker_images.py --file <path> ...

release.yml runs this after the images are pushed and before the app is built: the app embeds
`app/src/DnaEntropyGraph.App/worker-images.json` and refuses any worker image not listed for its own version,
so a build without the entry ends every run in `no_worker_image`. The entry is keyed by the version WITHOUT the
leading `v` (the app compares against its assembly version) and each image is `ghcr.io/.../dna-entropy-worker@sha256:...`,
which is the shape `PinnedWorkerImageList` keeps (anything else is dropped).

Exit codes: 0 written, 1 a bad digest or unreadable file (the file is left untouched), 2 bad usage.
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path

IMAGE_REPO = "ghcr.io/raaif-yousuf/dna-entropy-worker"
DEFAULT_FILE = Path(__file__).resolve().parent.parent / "app" / "src" / "DnaEntropyGraph.App" / "worker-images.json"
_DIGEST_RE = re.compile(r"^sha256:[0-9a-f]{64}$")


def add_entry(doc: dict, version: str, cuda_digest: str, cpu_digest: str) -> dict:
    """A copy of `doc` with the entry for `version` added (replacing one for the same version)."""
    images = doc.get("images") if isinstance(doc, dict) else None
    if not isinstance(images, list):
        raise ValueError("worker-images.json has no images array")
    for label, digest in (("cuda", cuda_digest), ("cpu", cpu_digest)):
        if not _DIGEST_RE.match(digest):
            raise ValueError(f"{label} digest {digest!r} is not sha256:<64 lowercase hex>")
    bare = version[1:] if version.startswith("v") else version
    entry = {"version": bare, "cuda": f"{IMAGE_REPO}@{cuda_digest}", "cpu": f"{IMAGE_REPO}@{cpu_digest}"}
    kept = [i for i in images if not (isinstance(i, dict) and i.get("version") == bare)]
    return {**doc, "images": [*kept, entry]}


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--file", type=Path, default=DEFAULT_FILE)
    ap.add_argument("--version", required=True)
    ap.add_argument("--cuda-digest", required=True)
    ap.add_argument("--cpu-digest", required=True)
    args = ap.parse_args(argv)
    try:
        doc = json.loads(args.file.read_text(encoding="utf-8"))
        updated = add_entry(doc, args.version, args.cuda_digest, args.cpu_digest)
    except (OSError, ValueError) as exc:
        print(f"ERROR: {exc}")
        return 1
    with args.file.open("w", encoding="utf-8", newline="\n") as fh:
        fh.write(json.dumps(updated, indent=2) + "\n")
    print(f"OK: pinned worker images for {updated['images'][-1]['version']} in {args.file}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
