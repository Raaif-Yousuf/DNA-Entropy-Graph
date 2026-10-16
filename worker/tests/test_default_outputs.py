"""Issue #411: the DEFAULT output set is a decision, not an inherited flag order.

DECISION (agent-made, reversible): surprisal outputs stay ON by default. Surprisal is the other
half of the entropy number (entropy says how unsure the model is, surprisal says how wrong it was
about the base that is actually there) and costs no GPU time, so a default run includes it; the
app offers a switch to leave it out. These tests pin the decided default so changing it is a
deliberate edit here, in docs/user_guide/04-view.md and in docs/science_and_formats.md together.
"""

from __future__ import annotations

from pathlib import Path

from dna_entropy import pipeline
from dna_entropy.config import RunConfig

DATA = Path(__file__).parent / "data"

# What a default run of a GenBank input writes (name "d"), one entry per file.
GENBANK_DEFAULT_OUTPUTS = {
    "d.fasta",
    "d.gb",
    "d.entropy.bedgraph",
    "d.entropy.geneious.gff3",
    "d.entropy.smooth51.bedgraph",
    "d.entropy.tsv",
    "d.genes.csv",
    "d.genes.gff3",
    "d.genes.tsv",
    "d.regions.bed",
    "d.regions.gff3",
    "d.surprisal.bedgraph",
    "d.surprisal.geneious.gff3",
    "provenance.json",
    "stats.txt",
}
SURPRISAL_FILES = {"d.surprisal.bedgraph", "d.surprisal.geneious.gff3"}


def _names(cfg: RunConfig) -> set[str]:
    return {Path(o).name for o in pipeline.run(cfg).outputs}


def test_a_default_run_writes_exactly_the_decided_output_set(tmp_path: Path) -> None:
    cfg = RunConfig(name="d", input_path=str(DATA / "sample.gb"), out_dir=str(tmp_path))
    assert _names(cfg) == GENBANK_DEFAULT_OUTPUTS


def test_a_default_fasta_run_includes_the_surprisal_files(tmp_path: Path) -> None:
    cfg = RunConfig(name="d", input_path=str(DATA / "prokaryotic_demo.fasta"), out_dir=str(tmp_path))
    names = _names(cfg) - {"d.gb"}  # the bonus GenBank depends on the optional [genes] extra
    assert names >= SURPRISAL_FILES
    assert {"d.fasta", "d.entropy.bedgraph", "d.summary.txt", "provenance.json"} <= names


def test_turning_surprisal_off_removes_exactly_the_surprisal_files(tmp_path: Path) -> None:
    cfg = RunConfig(
        name="d", input_path=str(DATA / "sample.gb"), out_dir=str(tmp_path), include_surprisal=False
    )
    assert _names(cfg) == GENBANK_DEFAULT_OUTPUTS - SURPRISAL_FILES


def test_the_default_is_the_decided_one() -> None:
    assert RunConfig().include_surprisal is True
