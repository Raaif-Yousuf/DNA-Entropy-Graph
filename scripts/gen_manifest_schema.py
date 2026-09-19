#!/usr/bin/env python3
"""Generate/check ``docs/contract/{manifest,status,result,error-codes}.json`` from the
worker's own dataclasses (issues #39, #254) — never hand-written beside them.

Usage (from the repo root):

    python scripts/gen_manifest_schema.py            # regenerate and write the files
    python scripts/gen_manifest_schema.py --check     # exit 1 if the checked-in files
                                                        # would differ from what the
                                                        # worker's dataclasses produce now

``ci-worker.yml``'s ``contract`` job runs exactly:

    uv run --with jsonschema python scripts/gen_manifest_schema.py --check

...WITHOUT first installing the worker package at all (no ``uv pip install -e worker``
step precedes it) — so this script adds ``worker/src`` to ``sys.path`` itself before
importing ``dna_entropy.worker.*``, the same "run straight from a checkout, no pip
install" trick the prototype's own ``keep_gpu.py`` used, for the same reason. The only
third-party dependency anywhere in this import chain is ``jsonschema``, and only for the
optional sample-manifest validation step below — ``dna_entropy.worker.manifest``/
``status``/``result`` themselves are pure stdlib (deliberately NOT ``runner``, which also
imports ``dna_entropy.pipeline`` -> ``numpy``; see ``worker/result.py``'s own module
docstring for why ``JobResult`` lives there instead) (see ``worker/schema_gen.py``'s own
docstring for why that matters here).
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
WORKER_SRC = REPO_ROOT / "worker" / "src"
if str(WORKER_SRC) not in sys.path:
    sys.path.insert(0, str(WORKER_SRC))

from dna_entropy.worker.errors import WORKER_ERROR_CODES  # noqa: E402
from dna_entropy.worker.manifest import JobManifest  # noqa: E402
from dna_entropy.worker.result import JobResult  # noqa: E402
from dna_entropy.worker.schema_gen import top_level_schema  # noqa: E402
from dna_entropy.worker.status import StatusDocument  # noqa: E402

OUT_DIR = REPO_ROOT / "docs" / "contract"
FIXTURES_DIR = REPO_ROOT / "tests" / "contract-fixtures" / "manifest_samples"
_ID_BASE = "https://raw.githubusercontent.com/Raaif-Yousuf/DNA-Entropy-Graph/main/docs/contract"

# filename -> (dataclass, title)
SCHEMAS: dict[str, tuple[type, str]] = {
    "manifest.schema.json": (JobManifest, "JobManifest (manifest.json)"),
    "status.schema.json": (StatusDocument, "StatusDocument (status.json)"),
    "result.schema.json": (JobResult, "JobResult (result.json)"),
}


def render(cls: type, filename: str, title: str) -> str:
    schema = top_level_schema(cls, schema_id=f"{_ID_BASE}/{filename}", title=title)
    return json.dumps(schema, indent=2) + "\n"


def render_error_codes() -> str:
    """``docs/contract/error-codes.json`` (issue #254) — a plain data document, not a
    JSON Schema, generated from ``dna_entropy.worker.errors.WORKER_ERROR_CODES`` the same
    generate-and-``--check`` way the schemas above are, so the worker and a future C#
    ``ErrorCatalog`` cannot silently drift."""
    payload = {
        "$id": f"{_ID_BASE}/error-codes.json",
        "generatedFrom": "dna_entropy.worker.errors.WORKER_ERROR_CODES",
        "codes": [
            {
                "code": spec.code,
                "raisedBy": spec.raised_by,
                "retriable": spec.retriable,
                "note": spec.note,
            }
            for spec in WORKER_ERROR_CODES
        ],
    }
    return json.dumps(payload, indent=2) + "\n"


def _validate_sample_manifests(manifest_schema: dict) -> tuple[bool, list[str]]:
    """Validate every ``tests/contract-fixtures/manifest_samples/*.json`` fixture against
    the freshly generated manifest schema. Returns ``(ran, failures)``: an empty
    ``failures`` means every sample validated, and ``ran`` is False when the check could
    not run at all, because ``jsonschema`` is absent or no fixtures exist yet. This is a
    bonus safety net, not the primary drift check, so its absence must never be confused
    with success OR failure of the schema-drift check itself.

    MEASURED 2026-09-19 (issue #323): this used to return a bare ``[]`` when
    ``jsonschema`` was missing, and the caller then printed "OK: 2 sample manifest(s)
    validate" one line below its own "jsonschema not installed; skipping" note. A check
    that reports success for work it did not do. It now returns ``(ran, failures)``, and
    ``ran`` being False is a different thing from running and finding nothing wrong.
    """
    try:
        import jsonschema
    except ImportError:
        print("note: jsonschema not installed; skipping sample-manifest validation.")
        return False, []

    if not FIXTURES_DIR.is_dir():
        return False, []

    failures: list[str] = []
    validator = jsonschema.Draft202012Validator(manifest_schema)
    for sample_path in sorted(FIXTURES_DIR.glob("*.json")):
        try:
            sample = json.loads(sample_path.read_text(encoding="utf-8"))
            validator.validate(sample)
        except Exception as exc:  # jsonschema.ValidationError or a JSON parse error
            failures.append(f"{sample_path.relative_to(REPO_ROOT)}: {exc}")
    return True, failures


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--check", action="store_true",
        help="Fail (exit 1) if the checked-in schema files would differ, instead of writing them.",
    )
    args = parser.parse_args(argv)

    rendered: dict[str, str] = {
        filename: render(cls, filename, title) for filename, (cls, title) in SCHEMAS.items()
    }
    rendered["error-codes.json"] = render_error_codes()

    if args.check:
        drifted = []
        for filename, text in rendered.items():
            out_path = OUT_DIR / filename
            current = out_path.read_text(encoding="utf-8") if out_path.exists() else None
            if current != text:
                drifted.append(filename)

        samples_ran, sample_failures = _validate_sample_manifests(
            json.loads(rendered["manifest.schema.json"])
        )

        if drifted or sample_failures:
            if drifted:
                print(
                    "Schema drift detected — these files do not match the worker's "
                    f"current dataclasses: {', '.join(drifted)}",
                    file=sys.stderr,
                )
                print("Run `python scripts/gen_manifest_schema.py` to regenerate.", file=sys.stderr)
            for failure in sample_failures:
                print(f"Sample manifest failed validation: {failure}", file=sys.stderr)
            return 1
        print("OK: docs/contract/*.json match the worker's dataclasses/error-code registry.")
        if samples_ran:
            n = len(list(FIXTURES_DIR.glob("*.json")))
            print(f"OK: {n} sample manifest(s) under {FIXTURES_DIR.relative_to(REPO_ROOT)} validate.")
        else:
            print(
                "NOT CHECKED: the sample manifests were not validated, for the reason "
                "noted above. The schema-drift check itself did run and did pass."
            )
        return 0

    OUT_DIR.mkdir(parents=True, exist_ok=True)
    for filename, text in rendered.items():
        out_path = OUT_DIR / filename
        with open(out_path, "w", encoding="utf-8", newline="\n") as fh:
            fh.write(text)
        print(f"wrote {out_path.relative_to(REPO_ROOT)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
