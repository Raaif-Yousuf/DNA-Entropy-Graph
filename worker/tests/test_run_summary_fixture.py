"""Issue #102: the app's Results page reads the worker's summary file with a C# parser
(``RunSummaryReader``) pinned to ``tests/contract-fixtures/run_summary_two_contigs.txt``.
This test regenerates that file from the mock pipeline, so a change to ``writers/summary.py``
fails here and the fixture (and the C# parser) are updated together, never one without the other.
"""

from __future__ import annotations

from pathlib import Path

from dna_entropy import pipeline
from dna_entropy.config import RunConfig

FIXTURE = Path(__file__).resolve().parents[2] / "tests" / "contract-fixtures" / "run_summary_two_contigs.txt"

TWO_RECORD_FASTA = (
    ">record_one first locus\n"
    "ACGTACGTACGTACGTACGTACGTACGTACGT\n"
    ">record_two second locus\n"
    "TTTTGGGGCCCCAAAATTTTGGGGCCCCAAAA\n"
)


def test_summary_fixture_matches_what_the_mock_pipeline_writes(tmp_path: Path) -> None:
    source = tmp_path / "in.fasta"
    source.write_text(TWO_RECORD_FASTA, encoding="utf-8", newline="\n")
    cfg = RunConfig(name="two", input_path=str(source), out_dir=str(tmp_path / "out"))

    pipeline.run(cfg)

    written = (tmp_path / "out" / "two.summary.txt").read_text(encoding="utf-8")
    assert written == FIXTURE.read_text(encoding="utf-8"), (
        "writers/summary.py output changed: regenerate tests/contract-fixtures/run_summary_two_contigs.txt "
        "and check app/src/DnaEntropyGraph.Core/Runs/RunSummaryReader.cs still parses it"
    )
