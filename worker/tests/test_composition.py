"""Issue #191: GC content and codon-usage companion tracks (no model needed)."""

from __future__ import annotations

from pathlib import Path

import numpy as np
import pytest
from typer.testing import CliRunner

from dna_entropy import pipeline
from dna_entropy.analysis.composition import DEFAULT_GC_WINDOW, codon_usage, gc_percent
from dna_entropy.annotators.base import GeneFeature
from dna_entropy.cli import app
from dna_entropy.config import RunConfig
from dna_entropy.writers.composition import CODON_COLUMNS

DATA = Path(__file__).parent / "data"


# ---- GC percent -----------------------------------------------------------------------


def test_default_window_is_51() -> None:
    assert DEFAULT_GC_WINDOW == 51


def test_poly_g_is_100_percent_everywhere() -> None:
    out = gc_percent("G" * 200)
    assert out.shape == (200,) and out.dtype == np.float32
    assert (out == 100.0).all()


def test_poly_a_is_zero_and_alternating_is_about_fifty() -> None:
    assert (gc_percent("A" * 80) == 0.0).all()
    mid = gc_percent("GA" * 100)[60:140]
    assert np.allclose(mid, 50.0, atol=1.0)


def _naive(seq: str, window: int, circular: bool) -> list[float]:
    half = (window - 1) // 2
    n = len(seq)
    out = []
    for i in range(n):
        if circular:
            half_c = min(half, (n - 1) // 2)
            idx = [(i + d) % n for d in range(-half_c, half_c + 1)]
        else:
            idx = list(range(max(0, i - half), min(n, i + half + 1)))
        bases = [seq[j] for j in idx if seq[j] in "ACGT"]
        out.append(100.0 * sum(b in "GC" for b in bases) / len(bases) if bases else 0.0)
    return out


@pytest.mark.parametrize("circular", [False, True])
@pytest.mark.parametrize("window", [1, 5, 51])
def test_matches_a_naive_loop_including_edges_and_ambiguity(window: int, circular: bool) -> None:
    rng = np.random.default_rng(7)
    seq = "".join(rng.choice(list("ACGTN"), size=120, p=[0.28, 0.24, 0.24, 0.2, 0.04]))
    got = gc_percent(seq, window, circular=circular)
    assert np.allclose(got, _naive(seq, window, circular), atol=1e-4)


def test_linear_window_shrinks_at_the_ends() -> None:
    # 26 Gs then 26 As: base 0 sees only its right half (26 Gs) -> 100; base 51 sees 26 As -> 0
    out = gc_percent("G" * 26 + "A" * 26, 51)
    assert out[0] == pytest.approx(100.0) and out[51] == pytest.approx(0.0)


def test_circular_window_wraps_the_origin() -> None:
    seq = "G" * 10 + "A" * 40
    lin = gc_percent(seq, 11)[0]
    circ = gc_percent(seq, 11, circular=True)[0]
    assert circ < lin  # base 0 now also sees the As at the end of the molecule


def test_ambiguity_codes_are_excluded_and_an_all_n_window_is_zero() -> None:
    assert gc_percent("GGNN", 1)[0] == 100.0
    assert gc_percent("GGNN", 1)[2] == 0.0  # no unambiguous base in the window


def test_empty_sequence_and_bad_windows() -> None:
    assert gc_percent("").shape == (0,)
    for bad in (0, 2, -3):
        with pytest.raises(ValueError):
            gc_percent("ACGT", bad)


# ---- codon usage ----------------------------------------------------------------------


def _feat(begin: int, end: int, strand: str = "+", **kw) -> GeneFeature:
    return GeneFeature(begin=begin, end=end, strand=strand, gene_id=kw.pop("gene_id", "g"), **kw)


def test_codon_counts_frequency_and_rscu() -> None:
    rows = codon_usage("ATGGCTGCCTAA", _feat(1, 12))
    assert rows is not None
    by = {r.codon: r for r in rows}
    assert set(by) == {"ATG", "GCT", "GCC", "TAA"}
    assert by["ATG"].amino_acid == "M" and by["TAA"].amino_acid == "*"
    assert by["GCT"].count == 1 and by["GCT"].frequency == pytest.approx(0.25)
    assert by["GCT"].rscu == pytest.approx(2.0)  # 1 / (2 Ala codons used / 4 synonymous codons)
    assert by["ATG"].rscu == pytest.approx(1.0)  # one-codon amino acid is always 1
    assert by["GCT"].deviation == pytest.approx(1.0)


def test_minus_strand_is_read_as_the_reverse_complement() -> None:
    fwd = codon_usage("ATGGCTGCCTAA", _feat(1, 12))
    rev = codon_usage("TTAGGCAGCCAT", _feat(1, 12, "-"))  # reverse complement of the above
    assert [(r.codon, r.count) for r in fwd] == [(r.codon, r.count) for r in rev]


def test_compound_feature_joins_its_segments_not_the_intron() -> None:
    rows = codon_usage("ATGGCTCCCCCCGCCTAA", _feat(1, 18, segments=((1, 6), (13, 18))))
    assert {r.codon for r in rows} == {"ATG", "GCT", "GCC", "TAA"}


def test_feature_whose_length_is_not_a_multiple_of_three_is_skipped() -> None:
    assert codon_usage("ATGGCTGC", _feat(1, 8)) is None


def test_ambiguous_codons_are_not_counted() -> None:
    rows = codon_usage("ATGNNNTAA", _feat(1, 9))
    assert {r.codon for r in rows} == {"ATG", "TAA"}
    assert next(r for r in rows if r.codon == "ATG").frequency == pytest.approx(0.5)


# ---- pipeline / CLI wiring ------------------------------------------------------------


def _fasta(tmp_path: Path, seq: str) -> str:
    p = tmp_path / "g.fasta"
    p.write_text(f">g\n{seq}\n", encoding="utf-8", newline="\n")
    return str(p)


def test_default_run_writes_neither_file(tmp_path: Path) -> None:
    cfg = RunConfig(
        name="r", input_path=_fasta(tmp_path, "G" * 300), out_dir=str(tmp_path / "o"), context_length=128
    )
    names = {Path(o).name for o in pipeline.run(cfg).outputs}
    assert "r.gc.bedgraph" not in names and "r.codons.tsv" not in names


def test_gc_track_on_a_poly_g_sequence_is_100_across_the_file(tmp_path: Path) -> None:
    cfg = RunConfig(
        name="r", input_path=_fasta(tmp_path, "G" * 300), out_dir=str(tmp_path / "o"),
        context_length=128, include_gc=True,
    )  # fmt: skip
    result = pipeline.run(cfg)
    path = next(Path(o) for o in result.outputs if o.endswith("r.gc.bedgraph"))
    lines = path.read_text(encoding="utf-8").splitlines()
    assert lines[0].startswith("track type=bedGraph")
    values = [float(row.split("\t")[3]) for row in lines[1:]]
    assert len(values) == 300 and set(values) == {100.0}


def test_gc_window_option_is_used(tmp_path: Path) -> None:
    seq = "G" * 150 + "A" * 150
    for window, expect_mid in ((1, 0.0), (301, 50.0)):
        cfg = RunConfig(
            name=f"w{window}", input_path=_fasta(tmp_path, seq), out_dir=str(tmp_path / f"o{window}"),
            context_length=128, include_gc=True, gc_window=window,
        )  # fmt: skip
        result = pipeline.run(cfg)
        path = next(Path(o) for o in result.outputs if o.endswith(".gc.bedgraph"))
        rows = path.read_text(encoding="utf-8").splitlines()[1:]
        # base 151 (the first A): window 1 sees only itself; window 301 sees 150 G + 150 A (the
        # whole sequence, clipped at the linear ends)
        assert float(rows[150].split("\t")[3]) == pytest.approx(expect_mid)


def test_codons_tsv_from_genbank_genes(tmp_path: Path) -> None:
    cfg = RunConfig(
        name="r", input_path=str(DATA / "sample.gb"), out_dir=str(tmp_path / "o"),
        context_length=128, include_codons=True,
    )  # fmt: skip
    result = pipeline.run(cfg)
    path = next(Path(o) for o in result.outputs if o.endswith("r.codons.tsv"))
    text = path.read_text(encoding="utf-8")
    assert "\r" not in text
    header, *rows = text.splitlines()
    assert header.split("\t") == list(CODON_COLUMNS)
    assert header.split("\t")[:2] == ["contig", "gene_id"]
    per_gene = {}
    for row in rows:
        f = row.split("\t")
        per_gene[f[1]] = per_gene.get(f[1], 0) + int(f[7])
    assert per_gene == {"geneA": 14, "geneB": 14}  # 42 nt each = 14 codons; minus strand read correctly


def test_codons_notice_counts_skipped_non_cds_shaped_features(tmp_path: Path) -> None:
    gb = tmp_path / "odd.gb"
    text = (
        (DATA / "sample.gb")
        .read_text(encoding="utf-8")
        .replace("gene            1..42", "gene            1..41", 1)
    )
    gb.write_text(text, encoding="utf-8", newline="\n")
    cfg = RunConfig(
        name="r", input_path=str(gb), out_dir=str(tmp_path / "o"), context_length=128, include_codons=True
    )
    result = pipeline.run(cfg)
    assert any("1 feature(s)" in n and "multiple of three" in n for n in result.notices)


def test_cli_flags(tmp_path: Path) -> None:
    result = CliRunner().invoke(
        app,
        [
            "run", "--name", "c", "--out", str(tmp_path / "out"), "-i", str(DATA / "sample.gb"),
            "--context-length", "128", "--gc", "--gc-window", "11", "--codons",
        ],
    )  # fmt: skip
    assert result.exit_code == 0, result.output
    assert "c.gc.bedgraph" in result.output and "c.codons.tsv" in result.output


def test_cli_rejects_an_even_gc_window(tmp_path: Path) -> None:
    result = CliRunner().invoke(
        app,
        [
            "run",
            "--name",
            "c",
            "--out",
            str(tmp_path),
            "-i",
            str(DATA / "sample.gb"),
            "--gc",
            "--gc-window",
            "10",
        ],
    )
    assert result.exit_code == 1 and "ERROR" in result.output
