"""Issue #524: the output GenBank keeps the molecule's topology in its LOCUS line.

The worker wraps a circular input (#128), but the writer used to write ``linear`` always, so a
plasmid came back as a linear record in the user's own annotation tool."""

from __future__ import annotations

from pathlib import Path

import numpy as np
import pytest

from dna_entropy import pipeline
from dna_entropy.annotators.base import GeneFeature
from dna_entropy.config import RunConfig, Topology
from dna_entropy.readers.genbank import read_genbank
from dna_entropy.writers.genbank import GenBankWriter

DATA = Path(__file__).parent / "data"
SEQ = "ATGCGTACGTTAGCAACGTA" * 5


def _locus_lines(path: str) -> list[str]:
    return [ln for ln in Path(path).read_text(encoding="utf-8").splitlines() if ln.startswith("LOCUS")]


def _records(n: int):
    feats = [GeneFeature(begin=1, end=30, strand="+", gene_id="g")]
    return [(f"rec{i}", SEQ, feats, np.full(len(SEQ), 1.5, dtype=np.float32), "") for i in range(n)]


def test_writer_marks_each_record_with_its_own_topology(tmp_path: Path) -> None:
    path = GenBankWriter().write_multi(
        name="w", records=_records(2), out_dir=str(tmp_path), circular=[True, False]
    )
    first, second = _locus_lines(path)
    assert " circular " in first
    assert "circular" not in second


def test_writer_default_is_linear_and_the_bytes_are_unchanged(tmp_path: Path) -> None:
    """A linear record writes no topology word at all (Biopython's own convention), exactly as
    before this option existed, so every existing output file stays byte-identical."""
    default = GenBankWriter().write_multi(name="w", records=_records(1), out_dir=str(tmp_path / "a"))
    explicit = GenBankWriter().write_multi(
        name="w", records=_records(1), out_dir=str(tmp_path / "b"), circular=[False]
    )
    assert Path(default).read_bytes() == Path(explicit).read_bytes()
    (line,) = _locus_lines(default)
    assert "circular" not in line


def test_single_record_write_takes_circular(tmp_path: Path) -> None:
    path = GenBankWriter().write(
        name="w", values=np.zeros(len(SEQ), dtype=np.float32), seq=SEQ, start=1, features=[],
        out_dir=str(tmp_path), circular=True,
    )  # fmt: skip
    assert " circular " in _locus_lines(path)[0]


def test_a_circular_list_of_the_wrong_length_is_refused(tmp_path: Path) -> None:
    with pytest.raises(ValueError, match="one entry per record"):
        GenBankWriter().write_multi(name="w", records=_records(2), out_dir=str(tmp_path), circular=[True])


def _circular_gb(tmp_path: Path, word: str) -> str:
    text = (DATA / "sample.gb").read_text(encoding="utf-8")
    assert "DNA              UNK" in text
    # same column layout as the original: "DNA" + 14 spaces == "DNA" + 5 spaces + 8-letter word + 1 space
    text = text.replace("DNA              UNK", f"DNA     {word} UNK", 1)
    p = tmp_path / f"{word}.gb"
    p.write_text(text, encoding="utf-8", newline="\n")
    return str(p)


def test_circular_genbank_in_circular_genbank_out_round_trips(tmp_path: Path) -> None:
    src = _circular_gb(tmp_path, "circular")
    assert read_genbank(src)[0][0].circular is True  # the premise: the input really is circular
    cfg = RunConfig(name="rt", input_path=src, out_dir=str(tmp_path / "o"), context_length=128)
    outputs = pipeline.run(cfg).outputs
    out_gb = next(o for o in outputs if o.endswith("rt.gb"))
    assert " circular " in _locus_lines(out_gb)[0]
    assert read_genbank(out_gb)[0][0].circular is True


def test_linear_input_stays_linear(tmp_path: Path) -> None:
    cfg = RunConfig(name="lin", input_path=str(DATA / "sample.gb"), out_dir=str(tmp_path), context_length=128)
    out_gb = next(o for o in pipeline.run(cfg).outputs if o.endswith("lin.gb"))
    assert read_genbank(out_gb)[0][0].circular is False


def test_the_topology_option_decides_not_only_the_input_word(tmp_path: Path) -> None:
    """The record was ANALYZED as circular or linear per ``--topology``; the file says the same."""
    src = _circular_gb(tmp_path, "circular")
    forced_linear = RunConfig(
        name="fl", input_path=src, out_dir=str(tmp_path / "a"), context_length=128, topology=Topology.LINEAR
    )
    out = next(o for o in pipeline.run(forced_linear).outputs if o.endswith("fl.gb"))
    assert read_genbank(out)[0][0].circular is False
    forced_circular = RunConfig(
        name="fc", input_path=str(DATA / "sample.gb"), out_dir=str(tmp_path / "b"), context_length=128,
        topology=Topology.CIRCULAR,
    )  # fmt: skip
    out = next(o for o in pipeline.run(forced_circular).outputs if o.endswith("fc.gb"))
    assert read_genbank(out)[0][0].circular is True


def test_a_circular_fasta_run_writes_a_circular_bonus_genbank(tmp_path: Path) -> None:
    fasta = tmp_path / "p.fasta"
    fasta.write_text(">p\n" + SEQ * 3 + "\n", encoding="utf-8", newline="\n")
    cfg = RunConfig(
        name="pf", input_path=str(fasta), out_dir=str(tmp_path / "o"), context_length=128,
        topology=Topology.CIRCULAR,
    )  # fmt: skip
    out = next(o for o in pipeline.run(cfg).outputs if o.endswith("pf.gb"))
    assert " circular " in _locus_lines(out)[0]
