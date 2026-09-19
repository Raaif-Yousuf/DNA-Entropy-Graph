"""Standalone entry point: ``python -m dna_entropy.worker --manifest <path> --store <kind> ...``.

Normally reached through the ``dna-entropy worker-run`` console-script command
(``dna_entropy.cli.worker_run``); this module exists so the container's own
``docker run`` ENTRYPOINT (design §5.4's startup script) can invoke the worker directly
without going through the Typer CLI wrapper at all.
"""

from __future__ import annotations

import argparse
import sys

from .blobstore import GcsBlobstore, LocalBlobstore
from .manifest import ManifestError
from .runner import run_job


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="python -m dna_entropy.worker")
    parser.add_argument(
        "--manifest-store",
        choices=["local", "gcs"],
        default="local",
        help="Where manifest.json (and the rest of the job) lives.",
    )
    parser.add_argument("--root", help="Local root directory (--manifest-store local).")
    parser.add_argument("--bucket", help="GCS bucket (--manifest-store gcs).")
    parser.add_argument("--prefix", help="GCS job prefix, e.g. jobs/<jobId>/ (--manifest-store gcs).")
    args = parser.parse_args(argv)

    if args.manifest_store == "local":
        if not args.root:
            parser.error("--root is required with --manifest-store local")
        store = LocalBlobstore(args.root)
    else:
        if not args.bucket or not args.prefix:
            parser.error("--bucket and --prefix are required with --manifest-store gcs")
        store = GcsBlobstore(args.bucket, args.prefix)

    try:
        result = run_job(store)
    except ManifestError as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        return 2

    return 0 if result.status == "done" else 1


if __name__ == "__main__":
    raise SystemExit(main())
