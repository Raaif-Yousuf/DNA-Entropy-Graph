"""``dna-entropy-worker``: the container's own entrypoint CLI.

A SEPARATE console script from ``dna-entropy`` (the science CLI,
``dna_entropy/cli.py``'s ``worker-run`` subcommand) on purpose: this one's ``run``
subcommand has a STRICT process exit-code contract the VM startup script's cleanup
dispatch depends on (``worker/vm/startup.sh``, mirroring
docs/superpowers/specs/2026-09-18-appendix-b-cloud-design.md section 4.5's
``case $rc in 10) cleanup stop;; 11) cleanup delete;; *) cleanup "$LIFECYCLE";; esac``),
which would be the wrong contract to impose on the human-facing ``dna-entropy`` CLI (whose
``worker-run`` keeps ordinary 0/1/2 exit codes for a developer running it by hand).

**How the startup script invokes this** (appendix B section 4.5, verbatim shape):

.. code-block:: bash

   docker run --rm --gpus all ... \\
     -e DEG_JOB_URI=$JOBURI -e DEG_BUCKET=$BUCKET -e DEG_VM_NAME=$NAME \\
     -e DEG_ZONE=$ZONE -e DEG_PROJECT=$PROJECT "$IMAGE" \\
     dna-entropy-worker run --manifest /work/manifest.json --store gcs

The bucket/job identity travels via ``DEG_JOB_URI``/``DEG_BUCKET`` environment variables,
NOT CLI flags — the script downloads ``manifest.json`` to a local path itself and passes
that path mostly for documentation/debugging value, since the REST of the job (status
heartbeat, cancel checks, output uploads) needs the full :class:`~.blobstore.Blobstore`,
which this module builds from ``DEG_JOB_URI`` (a ``gs://<bucket>/<prefix>/`` URI). ``run``
also accepts explicit ``--bucket``/``--prefix``/``--root`` for manual/debugging
invocation outside the startup script.
"""

from __future__ import annotations

import argparse
import os
import sys

from .. import __version__
from .blobstore import BlobstoreError, GcsBlobstore, LocalBlobstore
from .manifest import ManifestError
from .runner import run_job

# Exit codes the startup script's cleanup dispatch reads (appendix B section 4.5).
# Only DONE/FAILED/CANCELLED are ever actually returned today — REQUEST_STOP/
# REQUEST_DELETE are reserved for a future worker-initiated lifecycle override (e.g. "I
# hit an unrecoverable OOM, delete me regardless of the configured after-task action")
# that is not implemented yet; the script's default case (`*) cleanup "$LIFECYCLE"`)
# already handles every code this worker build can produce correctly.
EXIT_DONE = 0
EXIT_FAILED = 2
EXIT_CANCELLED = 3
EXIT_REQUEST_STOP = 10
EXIT_REQUEST_DELETE = 11


def _parse_gs_uri(uri: str) -> tuple[str, str]:
    """Split ``gs://<bucket>/<prefix>/`` into ``(bucket, prefix)``."""
    if not uri.startswith("gs://"):
        raise ValueError(f"not a gs:// URI: {uri!r}")
    rest = uri[len("gs://"):]
    bucket, _, prefix = rest.partition("/")
    if not bucket:
        raise ValueError(f"gs:// URI has no bucket: {uri!r}")
    return bucket, prefix


def _build_store(args: argparse.Namespace):
    if args.store == "localdir":
        root = args.root or os.environ.get("DEG_LOCAL_ROOT")
        if not root:
            raise SystemExit_with_message("--root (or $DEG_LOCAL_ROOT) is required with --store localdir")
        return LocalBlobstore(root)

    if args.store == "gcs":
        if args.bucket and args.prefix is not None:
            return GcsBlobstore(args.bucket, args.prefix)
        job_uri = os.environ.get("DEG_JOB_URI")
        if job_uri:
            bucket, prefix = _parse_gs_uri(job_uri)
            return GcsBlobstore(bucket, prefix)
        raise SystemExit_with_message(
            "--bucket/--prefix or $DEG_JOB_URI (gs://bucket/jobs/<jobId>/) is required with --store gcs"
        )

    raise SystemExit_with_message(f"unknown --store {args.store!r}")


class SystemExit_with_message(Exception):  # noqa: N801 - deliberately exception-shaped control flow
    """Internal: carries a user-facing message for ``_build_store`` failures, caught by
    the caller so it can print to stderr and return :data:`EXIT_FAILED` uniformly."""


def _cmd_run(args: argparse.Namespace) -> int:
    try:
        store = _build_store(args)
    except SystemExit_with_message as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        return EXIT_FAILED

    try:
        result = run_job(store)
    except (ManifestError, BlobstoreError) as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        return EXIT_FAILED

    print(f"job {result.jobId}: {result.status}")
    if result.status == "done":
        return EXIT_DONE
    if result.status == "cancelled":
        return EXIT_CANCELLED
    return EXIT_FAILED  # "failed" — matches result.status exactly, no other value exists


def _cmd_selftest(_args: argparse.Namespace) -> int:
    """A MEANINGFUL smoke test for the container image (issue #36's observable: a fresh
    container prints OK). Runs the real mock-predictor pipeline end to end — no GPU, no
    network — so a broken image build (missing dependency, wrong Python version, an
    import cycle) is caught before it ever reaches a real job."""
    try:
        import tempfile

        from .. import pipeline
        from ..config import Direction, RunConfig

        with tempfile.TemporaryDirectory(prefix="deg-selftest-") as tmp:
            cfg = RunConfig(
                name="selftest", out_dir=tmp, context_length=128, max_len=256,
                direction=Direction.BOTH_COMBINED,
            )
            result = pipeline.run(cfg, raw="ACGT" * 40)  # 160 nt, well-formed, deterministic
            if result.values.shape[0] != 160:
                raise AssertionError(f"expected 160 entropy values, got {result.values.shape[0]}")
            if not result.outputs:
                raise AssertionError("pipeline.run() produced no output files")
    except Exception as exc:
        print(f"FAIL: {exc}", file=sys.stderr)
        return 1
    print("OK")
    return 0


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(prog="dna-entropy-worker")
    parser.add_argument("--version", action="version", version=f"dna-entropy-worker {__version__}")
    sub = parser.add_subparsers(dest="command", required=True)

    run_p = sub.add_parser("run", help="Run one job from manifest.json (docs/job_contract.md).")
    run_p.add_argument(
        "--manifest", required=False,
        help="Local path the startup script already staged manifest.json at (informational — "
             "the manifest is always (re-)read from the store root, same relative layout either way).",
    )
    run_p.add_argument("--store", choices=["gcs", "localdir"], required=True)
    run_p.add_argument("--bucket", help="GCS bucket (--store gcs). Falls back to $DEG_JOB_URI if omitted.")
    run_p.add_argument("--prefix", help="GCS job prefix, e.g. jobs/<jobId>/ (--store gcs).")
    run_p.add_argument("--root", help="Local job directory (--store localdir). Falls back to $DEG_LOCAL_ROOT.")
    run_p.set_defaults(func=_cmd_run)

    selftest_p = sub.add_parser("selftest", help="GPU-free smoke test; prints OK and exits 0 on success.")
    selftest_p.set_defaults(func=_cmd_selftest)

    return parser


def main(argv: list[str] | None = None) -> int:
    parser = build_parser()
    args = parser.parse_args(argv)
    return args.func(args)


if __name__ == "__main__":
    raise SystemExit(main())
