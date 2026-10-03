"""Tests for the per-gene summary table (issue #124): analysis/gene_summary.py, the
writers/gene_summary.py TSV/CSV writer, and their wiring through the pipeline and manifest.

The decisive observable: the mean entropy in ``<name>.genes.tsv`` is the mean of the SAME
entropy track the run wrote, over exactly that gene's bases (not a neighbour's, not the whole
molecule).
"""

from __future__ import annotations

import csv
import io
import json
from pathlib import Path

import numpy as np
import pytest

from dna_entropy import pipeline
from dna_entropy.analysis.gene_summary import (
    DEFAULT_LOW_ENTROPY_BITS,
    GeneRow,
    summarize_genes,
)
from dna_entropy.annotators.base import GeneFeature
from dna_entropy.config import Direction, RunConfig, Topology
from dna_entropy.readers.genbank import read_genbank
from dna_entropy.worker.manifest import JobManifest
from dna_entropy.writers.gene_summary import GENE_SUMMARY_COLUMNS, GeneSummaryWriter

DATA = Path(__file__).parent / "data"


def _ramp(n: int) -> np.ndarray:
    """A convex ramp 0..2 bits: every base distinguishable, and (unlike a straight line) the head
    and tail of the molecule do not average to the molecule's own mean."""
    return ((np.arange(n, dtype=np.float64) / n) ** 2 * 2.0).astype(np.float32)


# --- analysis: the numbers -----------------------------------------------------------------


def test_default_low_entropy_threshold_is_half_a_bit() -> None:
    assert DEFAULT_LOW_ENTROPY_BITS == 0.5


def test_a_plain_gene_reports_mean_min_max_length_and_low_fraction() -> None:
    values = np.array([2.0, 0.1, 0.2, 0.9, 0.4, 2.0], dtype=np.float32)
    rows = summarize_genes(
        "c1", [GeneFeature(begin=2, end=5, strand="+", gene_id="g")], values, circular=False, start=1
    )
    assert len(rows) == 1
    r = rows[0]
    assert (r.contig, r.gene_id, r.begin, r.end, r.strand, r.length) == ("c1", "g", 2, 5, "+", 4)
    assert r.mean_entropy == pytest.approx((0.1 + 0.2 + 0.9 + 0.4) / 4, abs=1e-6)
    assert r.min_entropy == pytest.approx(0.1)
    assert r.max_entropy == pytest.approx(0.9)
    assert r.fraction_low_entropy == pytest.approx(3 / 4)  # 0.1, 0.2, 0.4 are below 0.5
    assert r.mean_surprisal is None
    assert r.wraps_origin is False


def test_low_fraction_is_strictly_below_the_threshold() -> None:
    values = np.array([0.5, 0.5, 0.49, 0.51], dtype=np.float32)
    (r,) = summarize_genes(
        "c", [GeneFeature(begin=1, end=4, strand="+", gene_id="g")], values, circular=False, start=1
    )
    assert r.fraction_low_entropy == pytest.approx(1 / 4)


def test_mean_surprisal_is_reported_when_given() -> None:
    values = np.full(10, 1.0, dtype=np.float32)
    surprisal = np.arange(10, dtype=np.float32)
    (r,) = summarize_genes(
        "c",
        [GeneFeature(begin=3, end=6, strand="-", gene_id="g")],
        values,
        surprisal=surprisal,
        circular=False,
        start=1,
    )
    assert r.mean_surprisal == pytest.approx(np.mean([2, 3, 4, 5]))
    assert r.strand == "-"


def test_coordinates_follow_the_start_offset_like_every_other_output() -> None:
    values = _ramp(50)
    (r,) = summarize_genes(
        "c", [GeneFeature(begin=10, end=20, strand="+", gene_id="g")], values, circular=False, start=101
    )
    assert (r.begin, r.end) == (110, 120)
    # the offset moves the label, never the bases the statistics are taken over
    assert r.mean_entropy == pytest.approx(values[9:20].mean(), abs=1e-6)


def test_a_partial_feature_running_past_the_sequence_is_clamped_not_dropped() -> None:
    values = _ramp(20)
    (r,) = summarize_genes(
        "c",
        [GeneFeature(begin=15, end=30, strand="+", partial=True, gene_id="g")],
        values,
        circular=False,
        start=1,
    )
    assert r.length == 6
    assert r.partial is True
    assert r.mean_entropy == pytest.approx(values[14:].mean(), abs=1e-6)


def test_no_features_gives_no_rows() -> None:
    assert summarize_genes("c", [], _ramp(10), circular=False, start=1) == []


def test_a_spliced_gene_is_summarised_over_its_segments_only_not_the_intron() -> None:
    values = _ramp(200)
    feat = GeneFeature(begin=1, end=130, strand="+", gene_id="s", segments=((1, 30), (101, 130)))
    (r,) = summarize_genes("c", [feat], values, circular=False, start=1)
    assert r.length == 60
    expected = np.concatenate([values[0:30], values[100:130]])
    assert r.mean_entropy == pytest.approx(expected.mean(), abs=1e-6)
    assert r.mean_entropy != pytest.approx(values[0:130].mean(), abs=1e-3)  # not the outer span
    assert (r.begin, r.end) == (1, 130)
    assert r.segments == "1..30,101..130"
    assert r.wraps_origin is False


# --- circular topology (#128): a gene across the origin --------------------------------------


def _wrap_feature() -> GeneFeature:
    # join(91..100,1..10) on a 100 nt circle: reader keeps the outer box 1..100 + real segments
    return GeneFeature(begin=1, end=100, strand="+", gene_id="w", segments=((1, 10), (91, 100)))


def test_circular_gene_across_the_origin_is_summarised_over_its_real_bases() -> None:
    values = _ramp(100)
    (r,) = summarize_genes("c", [_wrap_feature()], values, circular=True, start=1)
    assert r.wraps_origin is True
    assert r.length == 20
    expected = np.concatenate([values[90:100], values[0:10]])
    assert r.mean_entropy == pytest.approx(expected.mean(), abs=1e-6)
    # not the whole-molecule mean the outer box would give
    assert r.mean_entropy != pytest.approx(values.mean(), abs=1e-3)
    # begin > end is the wrap signal: the gene starts at 91, runs through the origin, ends at 10
    assert (r.begin, r.end) == (91, 10)
    assert r.segments == "91..100,1..10"


def test_the_same_join_on_a_linear_molecule_is_not_called_a_wrap() -> None:
    values = _ramp(100)
    (r,) = summarize_genes("c", [_wrap_feature()], values, circular=False, start=1)
    assert r.wraps_origin is False
    assert (r.begin, r.end) == (1, 100)
    assert r.length == 20  # still exon-only
    assert r.segments == "1..10,91..100"


def test_a_circular_gene_that_does_not_touch_the_origin_is_not_a_wrap() -> None:
    values = _ramp(100)
    feat = GeneFeature(begin=10, end=60, strand="+", gene_id="g")
    (r,) = summarize_genes("c", [feat], values, circular=True, start=1)
    assert r.wraps_origin is False
    assert (r.begin, r.end) == (10, 60)


def test_wrap_coordinates_follow_the_start_offset() -> None:
    (r,) = summarize_genes("c", [_wrap_feature()], _ramp(100), circular=True, start=1001)
    assert (r.begin, r.end) == (1091, 1010)


# --- the reader carries the segments the summary needs ---------------------------------------


def test_the_reader_keeps_real_segments_for_a_compound_feature_and_none_for_a_plain_one() -> None:
    records, _ = read_genbank(str(DATA / "spliced.gb"))
    by_id = {f.gene_id: f for f in records[0].features}
    assert by_id["splicedA"].segments == ((1, 30), (101, 130))
    assert by_id["splicedB"].segments == ((151, 170), (181, 200))  # genomic order, minus strand
    assert by_id["plainC"].segments == ()


# --- the writer ---------------------------------------------------------------------------


def _row(**kw) -> GeneRow:
    base = {
        "contig": "c1",
        "gene_id": "g1",
        "begin": 1,
        "end": 10,
        "strand": "+",
        "length": 10,
        "partial": False,
        "wraps_origin": False,
        "segments": "1..10",
        "mean_entropy": 1.23456,
        "min_entropy": 0.5,
        "max_entropy": 1.9,
        "mean_surprisal": None,
        "fraction_low_entropy": 0.25,
    }
    base.update(kw)
    return GeneRow(**base)


def test_writer_header_is_the_documented_column_list(tmp_path: Path) -> None:
    tsv, csv_path = GeneSummaryWriter().write_multi(name="run", rows=[_row()], out_dir=str(tmp_path))
    assert Path(tsv).name == "run.genes.tsv"
    assert Path(csv_path).name == "run.genes.csv"
    assert GENE_SUMMARY_COLUMNS == (
        "contig",
        "gene_id",
        "begin",
        "end",
        "strand",
        "length_nt",
        "partial",
        "wraps_origin",
        "segments",
        "mean_entropy_bits",
        "min_entropy_bits",
        "max_entropy_bits",
        "mean_surprisal_bits",
        "fraction_low_entropy",
    )
    assert Path(tsv).read_text(encoding="utf-8").splitlines()[0] == "\t".join(GENE_SUMMARY_COLUMNS)
    assert Path(csv_path).read_text(encoding="utf-8").splitlines()[0] == ",".join(GENE_SUMMARY_COLUMNS)


def test_writer_row_formatting_and_empty_surprisal(tmp_path: Path) -> None:
    tsv, _ = GeneSummaryWriter().write_multi(name="run", rows=[_row()], out_dir=str(tmp_path))
    line = Path(tsv).read_text(encoding="utf-8").splitlines()[1].split("\t")
    assert line == [
        "c1",
        "g1",
        "1",
        "10",
        "+",
        "10",
        "false",
        "false",
        "1..10",
        "1.2346",
        "0.5000",
        "1.9000",
        "",
        "0.2500",
    ]


def test_writer_files_are_lf_only_and_end_with_a_newline(tmp_path: Path) -> None:
    for path in GeneSummaryWriter().write_multi(
        name="run", rows=[_row(), _row(gene_id="g2")], out_dir=str(tmp_path)
    ):
        raw = Path(path).read_bytes()
        assert b"\r" not in raw
        assert raw.endswith(b"\n")
        raw.decode("utf-8")


def test_csv_quotes_a_gene_id_with_a_comma_and_round_trips(tmp_path: Path) -> None:
    tricky = 'dnaA, "replication" initiator'
    _, csv_path = GeneSummaryWriter().write_multi(
        name="run", rows=[_row(gene_id=tricky)], out_dir=str(tmp_path)
    )
    rows = list(csv.DictReader(io.StringIO(Path(csv_path).read_text(encoding="utf-8"))))
    assert rows[0]["gene_id"] == tricky
    assert rows[0]["segments"] == "1..10"


def test_tsv_cannot_be_broken_by_a_tab_or_newline_in_a_gene_id(tmp_path: Path) -> None:
    tsv, _ = GeneSummaryWriter().write_multi(
        name="run", rows=[_row(gene_id="a\tb\nc")], out_dir=str(tmp_path)
    )
    lines = Path(tsv).read_text(encoding="utf-8").splitlines()
    assert len(lines) == 2
    assert all(len(line.split("\t")) == len(GENE_SUMMARY_COLUMNS) for line in lines)


def test_writer_with_no_rows_still_writes_a_header_only_file(tmp_path: Path) -> None:
    tsv, csv_path = GeneSummaryWriter().write_multi(name="run", rows=[], out_dir=str(tmp_path))
    assert Path(tsv).read_text(encoding="utf-8").splitlines() == ["\t".join(GENE_SUMMARY_COLUMNS)]
    assert Path(csv_path).read_text(encoding="utf-8").splitlines() == [",".join(GENE_SUMMARY_COLUMNS)]


# --- wired through the pipeline -----------------------------------------------------------


def _tsv_rows(path: str) -> list[dict[str, str]]:
    lines = Path(path).read_text(encoding="utf-8").splitlines()
    header = lines[0].split("\t")
    return [dict(zip(header, line.split("\t"), strict=True)) for line in lines[1:]]


def _out(result: pipeline.RunResult, suffix: str) -> str:
    return next(p for p in result.outputs if p.endswith(suffix))


def test_genbank_run_writes_the_gene_table_matching_its_own_entropy_track(tmp_path: Path) -> None:
    cfg = RunConfig(name="toy", input_path=str(DATA / "sample.gb"), out_dir=str(tmp_path), seed=2)
    result = pipeline.run(cfg)
    rows = _tsv_rows(_out(result, "toy.genes.tsv"))
    assert {r["gene_id"] for r in rows} == {"geneA", "geneB"}
    a = next(r for r in rows if r["gene_id"] == "geneA")
    b = next(r for r in rows if r["gene_id"] == "geneB")
    assert (a["begin"], a["end"], a["strand"], a["length_nt"]) == ("1", "42", "+", "42")
    assert (b["begin"], b["end"], b["strand"], b["length_nt"]) == ("85", "126", "-", "42")
    assert float(a["mean_entropy_bits"]) == pytest.approx(result.values[0:42].mean(), abs=1e-4)
    assert float(b["mean_entropy_bits"]) == pytest.approx(result.values[84:126].mean(), abs=1e-4)
    assert float(a["min_entropy_bits"]) == pytest.approx(result.values[0:42].min(), abs=1e-4)
    assert float(a["max_entropy_bits"]) == pytest.approx(result.values[0:42].max(), abs=1e-4)
    # surprisal is on by default, so the column is filled
    assert a["mean_surprisal_bits"] != ""
    # the CSV is the same table
    csv_rows = list(
        csv.DictReader(io.StringIO(Path(_out(result, "toy.genes.csv")).read_text(encoding="utf-8")))
    )
    assert [r["gene_id"] for r in csv_rows] == [r["gene_id"] for r in rows]
    assert [r["mean_entropy_bits"] for r in csv_rows] == [r["mean_entropy_bits"] for r in rows]


def test_the_gene_table_is_listed_in_the_run_outputs_and_exists_on_disk(tmp_path: Path) -> None:
    cfg = RunConfig(name="toy", input_path=str(DATA / "sample.gb"), out_dir=str(tmp_path))
    result = pipeline.run(cfg)
    names = {Path(p).name for p in result.outputs}
    assert {"toy.genes.tsv", "toy.genes.csv"} <= names
    # everything on disk is listed (the runner uploads the directory; nothing may be orphaned)
    assert {p.name for p in tmp_path.iterdir()} == names


def test_no_surprisal_leaves_the_surprisal_column_empty(tmp_path: Path) -> None:
    cfg = RunConfig(
        name="toy", input_path=str(DATA / "sample.gb"), out_dir=str(tmp_path), include_surprisal=False
    )
    result = pipeline.run(cfg)
    assert all(r["mean_surprisal_bits"] == "" for r in _tsv_rows(_out(result, "toy.genes.tsv")))


def test_include_flag_off_writes_no_gene_table(tmp_path: Path) -> None:
    cfg = RunConfig(
        name="toy", input_path=str(DATA / "sample.gb"), out_dir=str(tmp_path), include_gene_summary=False
    )
    result = pipeline.run(cfg)
    names = {Path(p).name for p in result.outputs}
    assert "toy.genes.tsv" not in names
    assert "toy.genes.csv" not in names


def test_an_input_with_no_genes_writes_no_gene_table(tmp_path: Path) -> None:
    cfg = RunConfig(name="plain", out_dir=str(tmp_path))
    result = pipeline.run(cfg, raw="ATGC" * 10)
    assert not any(p.endswith((".genes.tsv", ".genes.csv")) for p in result.outputs)


def test_multi_record_genbank_rows_carry_their_own_contig(tmp_path: Path) -> None:
    cfg = RunConfig(name="multi", input_path=str(DATA / "multi.gb"), out_dir=str(tmp_path))
    result = pipeline.run(cfg)
    rows = _tsv_rows(_out(result, "multi.genes.tsv"))
    assert len({r["contig"] for r in rows}) == result.contigs
    records, _ = read_genbank(str(DATA / "multi.gb"))
    assert len(rows) == sum(len(rec.features) for rec in records) > 0


_PLASMID = (
    "LOCUS       pWRAP                {n:>11} bp    DNA     {topo:<8} SYN 01-JAN-2000\n"
    "DEFINITION  demo.\nACCESSION   pWRAP\nFEATURES             Location/Qualifiers\n"
    '     gene            join({a}..{n},1..{b})\n                     /gene="wrapper"\n'
    '     gene            100..150\n                     /gene="inside"\n'
    "ORIGIN\n{origin}\n//\n"
)


def _plasmid(tmp_path: Path, topo: str) -> str:
    rng = np.random.default_rng(124)
    seq = "".join(rng.choice(list("acgt"), size=400))
    lines = []
    for i in range(0, len(seq), 60):
        chunk = seq[i : i + 60]
        lines.append(f"{i + 1:>9} " + " ".join(chunk[j : j + 10] for j in range(0, len(chunk), 10)))
    text = _PLASMID.format(n=len(seq), a=381, b=20, topo=topo, origin="\n".join(lines))
    path = tmp_path / f"{topo}.gb"
    path.write_text(text, encoding="utf-8", newline="\n")
    return str(path)


@pytest.mark.parametrize(
    ("locus", "topology", "wraps"),
    [
        ("circular", Topology.AUTO, True),
        ("linear", Topology.CIRCULAR, True),
        ("circular", Topology.LINEAR, False),
    ],
)
def test_origin_wrapping_gene_uses_the_resolved_topology_end_to_end(
    tmp_path: Path, locus: str, topology: Topology, wraps: bool
) -> None:
    out = tmp_path / "out"
    cfg = RunConfig(
        name="p",
        input_path=_plasmid(tmp_path, locus),
        out_dir=str(out),
        context_length=128,
        max_len=512,
        direction=Direction.FORWARD_ONLY,
        topology=topology,
    )
    result = pipeline.run(cfg)
    rows = {r["gene_id"]: r for r in _tsv_rows(_out(result, "p.genes.tsv"))}
    w = rows["wrapper"]
    assert w["wraps_origin"] == ("true" if wraps else "false")
    assert w["length_nt"] == "40"  # 381..400 + 1..20, never the 400 nt outer box
    expected = np.concatenate([result.values[380:400], result.values[0:20]]).mean()
    assert float(w["mean_entropy_bits"]) == pytest.approx(expected, abs=1e-4)
    assert (w["begin"], w["end"]) == (("381", "20") if wraps else ("1", "400"))
    assert rows["inside"]["wraps_origin"] == "false"


# --- manifest: the `gene_summary` output name -------------------------------------------


def _manifest(outputs: list[str]) -> JobManifest:
    return JobManifest.parse(
        json.dumps(
            {
                "schema": 1,
                "jobId": "j1",
                "inputs": [{"id": "i1", "path": "input/x.gb", "name": "x"}],
                "outputs": outputs,
                "store": {"kind": "localdir", "root": "/tmp/store"},
            }
        )
    )


def _cfg(outputs: list[str]) -> RunConfig:
    m = _manifest(outputs)
    return m.build_run_config(m.inputs[0], local_input_path="x.gb", local_out_dir="out")


def test_manifest_outputs_name_gene_summary_turns_it_on_and_omitting_it_turns_it_off() -> None:
    assert _cfg(["genbank", "gene_summary"]).include_gene_summary is True
    assert _cfg(["genbank"]).include_gene_summary is False


def test_an_unspecified_outputs_list_means_everything_including_the_gene_table() -> None:
    assert _cfg([]).include_gene_summary is True
