"""Tests for scripts/check_manifest_spec_reads.py (#432)."""

from __future__ import annotations

import json
import subprocess
import sys
from pathlib import Path

SCRIPTS_DIR = Path(__file__).resolve().parent.parent
REPO_ROOT = SCRIPTS_DIR.parent
sys.path.insert(0, str(SCRIPTS_DIR))

import check_manifest_spec_reads as cms  # noqa: E402
import check_unused_fields as cuf  # noqa: E402

SPECS = (
    "from __future__ import annotations\n"
    "from dataclasses import dataclass\n"
    "\n"
    "@dataclass\n"
    "class AnalysisSpec:\n"
    "    window: int = 0\n"
    "    stride: int = 0\n"
    "\n"
    "@dataclass\n"
    "class InputSpec:\n"
    "    path: str = ''\n"
    "    genes: bool = False\n"
    "\n"
    "@dataclass\n"
    "class JobManifest:\n"
    "    analysis: AnalysisSpec\n"
    "    inputs: list[InputSpec]\n"
    "\n"
    "    @staticmethod\n"
    "    def parse(text: str) -> JobManifest:\n"
    "        raise NotImplementedError\n"
    "\n"
    "    def build(self, spec: InputSpec):\n"
    "        return self.analysis.window, spec.path\n"
)

# Same field name as AnalysisSpec.stride, read constantly: the masking that hid #345.
OTHER = (
    "from dataclasses import dataclass\n"
    "\n"
    "@dataclass\n"
    "class WindowPlan:\n"
    "    stride: int = 0\n"
    "\n"
    "def use(plan: WindowPlan):\n"
    "    return plan.stride\n"
)


def _pkg(tmp_path: Path, extra: dict[str, str] | None = None) -> tuple[Path, Path]:
    root = tmp_path / "pkg"
    root.mkdir()
    spec = root / "manifest.py"
    spec.write_text(SPECS, encoding="utf-8", newline="\n")
    (root / "other.py").write_text(OTHER, encoding="utf-8", newline="\n")
    for name, text in (extra or {}).items():
        (root / name).write_text(text, encoding="utf-8", newline="\n")
    return spec, root


def _keys(findings):
    return {f.key: f.kind for f in findings}


def _allow(tmp_path: Path, allowed: dict) -> Path:
    p = tmp_path / "allow.json"
    p.write_text(json.dumps({"allowed": allowed}), encoding="utf-8", newline="\n")
    return p


def test_self_test_passes():
    proc = subprocess.run(
        [sys.executable, str(SCRIPTS_DIR / "check_manifest_spec_reads.py"), "--self-test"],
        capture_output=True, text=True, timeout=60, check=False,
    )
    assert proc.returncode == 0, proc.stdout + proc.stderr
    assert "PASS" in proc.stdout


def test_a_same_named_field_on_another_class_does_not_mask_an_unread_spec_field(tmp_path):
    """The #345 shape: AnalysisSpec.stride is read by nothing; WindowPlan.stride is read constantly."""
    spec, root = _pkg(tmp_path)
    findings, _ = cms.check_specs(spec, [root], _allow(tmp_path, {}))
    assert _keys(findings).get("AnalysisSpec.stride") == "UNREAD"
    # ...and the name-based guard is blind to it, which is why this check exists.
    old, _ = cuf.check(root, _allow(tmp_path, {}))
    assert "AnalysisSpec.stride" not in {f.key for f in old}


def test_reads_resolved_through_self_chain_and_annotated_param_count(tmp_path):
    spec, root = _pkg(tmp_path)
    keys = _keys(cms.check_specs(spec, [root], _allow(tmp_path, {}))[0])
    assert "AnalysisSpec.window" not in keys  # self.analysis.window
    assert "InputSpec.path" not in keys  # spec.path, spec: InputSpec


def test_reads_resolved_through_parse_result_and_loop_over_a_list_field(tmp_path):
    use = (
        "from .manifest import JobManifest\n"
        "\n"
        "def run(text):\n"
        "    m = JobManifest.parse(text)\n"
        "    for item in m.inputs:\n"
        "        print(item.genes)\n"
        "    return m.analysis.stride\n"
    )
    spec, root = _pkg(tmp_path, {"use.py": use})
    keys = _keys(cms.check_specs(spec, [root], _allow(tmp_path, {}))[0])
    assert "InputSpec.genes" not in keys
    assert "AnalysisSpec.stride" not in keys


def test_a_write_and_a_keyword_argument_are_not_reads(tmp_path):
    use = (
        "from .manifest import AnalysisSpec\n"
        "\n"
        "def run(a: AnalysisSpec):\n"
        "    a.stride = 3\n"
        "    return AnalysisSpec(stride=4)\n"
    )
    spec, root = _pkg(tmp_path, {"use.py": use})
    assert _keys(cms.check_specs(spec, [root], _allow(tmp_path, {}))[0]).get("AnalysisSpec.stride") == "UNREAD"


def test_a_read_only_while_serializing_is_reported_separately(tmp_path):
    use = (
        "from .manifest import AnalysisSpec\n"
        "\n"
        "def f(a: AnalysisSpec):\n"
        "    def to_dict():\n"
        "        return a.stride\n"
        "    return to_dict()\n"
    )
    spec, root = _pkg(tmp_path, {"use.py": use})
    assert _keys(cms.check_specs(spec, [root], _allow(tmp_path, {}))[0]).get("AnalysisSpec.stride") == "SERIALIZED-ONLY"


def test_allowlist_suppresses_and_is_checked_for_staleness(tmp_path):
    spec, root = _pkg(tmp_path)
    findings, stale = cms.check_specs(spec, [root], _allow(tmp_path, {"AnalysisSpec.stride": "validated at parse, #345"}))
    assert "AnalysisSpec.stride" not in _keys(findings)
    assert stale == []
    _, stale = cms.check_specs(spec, [root], _allow(tmp_path, {"AnalysisSpec.window": "x", "Gone.field": "x"}))
    assert any(s.startswith("AnalysisSpec.window") for s in stale)
    assert any(s.startswith("Gone.field") for s in stale)


def test_a_spec_module_with_no_dataclasses_is_an_error_not_a_pass(tmp_path):
    root = tmp_path / "pkg"
    root.mkdir()
    (root / "manifest.py").write_text("x = 1\n", encoding="utf-8", newline="\n")
    try:
        cms.check_specs(root / "manifest.py", [root], _allow(tmp_path, {}))
    except SystemExit as exc:
        assert "no dataclass" in str(exc)
    else:
        raise AssertionError("expected SystemExit")


def test_the_real_manifest_resolves_reads_and_is_clean():
    """Non-vacuous: the resolver must actually attribute reads in the real tree, and every
    remaining finding must be allowlisted with a reason."""
    spec = REPO_ROOT / cms.SPEC_MODULE
    scan = cms.scan_specs(spec, [REPO_ROOT / "worker" / "src" / "dna_entropy"])
    assert len(scan.fields) >= 20
    assert "AnalysisSpec.window" in scan.real_reads
    assert "Limits.heartbeat_seconds" in scan.real_reads
    findings, stale = cms.check_specs(spec, [REPO_ROOT / "worker" / "src" / "dna_entropy"], REPO_ROOT / cms.ALLOWLIST_PATH)
    assert findings == [], [f"{f.key} {f.kind}" for f in findings]
    assert stale == [], stale
