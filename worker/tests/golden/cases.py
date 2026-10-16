"""The golden-file cases (issue #161): fixed inputs, the MockPredictor, fixed settings.

Shared by ``tests/test_golden.py`` (compares) and ``regenerate.py`` (rewrites). Everything a
run writes is compared byte for byte EXCEPT the few provenance.json keys that are a pure
function of the clock or the release, which :func:`normalize` removes (the same two keys the
provenance contract already allows to differ, plus ``worker_version``, so a version bump is
not a golden failure).
"""

from __future__ import annotations

import json
from pathlib import Path

HERE = Path(__file__).resolve().parent
DATA = HERE.parent / "data"

# name -> (input file, RunConfig overrides). The FASTA case turns the bonus GenBank off: that
# file's genes come from Prodigal when the optional [genes] extra happens to be installed, so
# its content would depend on the machine. The GenBank cases cover the .gb writer.
CASES: dict[str, tuple[str, dict]] = {
    "sample_gb": ("sample.gb", {}),
    "multi_gb": ("multi.gb", {}),
    "prokaryotic_demo_fasta": ("prokaryotic_demo.fasta", {"include_genbank": False}),
}

_VOLATILE_PROVENANCE_KEYS = ("generated_at", "wall_time_seconds", "worker_version")


def normalize(name: str, data: bytes) -> bytes:
    """Return ``data`` with the clock/release-dependent provenance keys removed."""
    if name != "provenance.json":
        return data
    obj = json.loads(data.decode("utf-8"))
    for key in _VOLATILE_PROVENANCE_KEYS:
        obj.pop(key, None)
    return (json.dumps(obj, indent=2, sort_keys=True) + "\n").encode("utf-8")


def build(case: str, out_dir: Path) -> dict[str, bytes]:
    """Run ``case`` into ``out_dir`` and return ``{file name: normalized bytes}``."""
    from dna_entropy import pipeline
    from dna_entropy.config import RunConfig

    filename, overrides = CASES[case]
    cfg = RunConfig(name="golden", input_path=str(DATA / filename), out_dir=str(out_dir), seed=0, **overrides)
    pipeline.run(cfg)
    return {p.name: normalize(p.name, p.read_bytes()) for p in sorted(out_dir.iterdir())}
