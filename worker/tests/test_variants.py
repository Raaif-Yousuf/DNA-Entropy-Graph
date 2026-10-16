"""Issue #130: variant effect scan. One forward pass per variant, bounded by --max-variants.

For each substitution the scan runs the model on the window ``pos +- K/2`` of the reference and of
the variant and reports the change in total surprisal over that window and in its mean entropy,
plus how surprising the reference and the alternative base are at the site under the reference's
own context. The mock predictor stands in for the model in these tests, so they prove the plumbing
and the arithmetic, not the biology (the TnpB acceptance row is in docs/ToTest.md).
"""

from __future__ import annotations

from pathlib import Path

import numpy as np
import pytest
from typer.testing import CliRunner

from dna_entropy.analysis.entropy import shannon_entropy
from dna_entropy.analysis.surprisal import surprisal
from dna_entropy.analysis.variants import (
    DEFAULT_MAX_VARIANTS,
    Variant,
    VariantError,
    check_count,
    cost_estimate,
    parse_variants_tsv,
    positions_to_variants,
    scan,
)
from dna_entropy.cli import app
from dna_entropy.predictors.mock import MockPredictor
from dna_entropy.writers.variants import write_variants

SEQ = "ACGTTGCAAGGCTTAACCGGTATCGATCGGATCCATGCAAT" * 3  # 123 nt


def _tsv(tmp_path: Path, text: str) -> str:
    p = tmp_path / "v.tsv"
    p.write_text(text, encoding="utf-8", newline="\n")
    return str(p)


def test_variants_tsv_with_and_without_a_header_and_comments(tmp_path: Path) -> None:
    got = parse_variants_tsv(_tsv(tmp_path, "pos\tref\talt\n# a note\n1\tA\tG\n\n6\tg\tc\n"), SEQ)
    assert got == [Variant(1, "A", "G"), Variant(6, "G", "C")]
    assert parse_variants_tsv(_tsv(tmp_path, "1\tA\tT\n"), SEQ) == [Variant(1, "A", "T")]


@pytest.mark.parametrize(
    "line, fragment",
    [
        ("1\tC\tG", "line 1"),  # ref does not match the sequence (position 1 is A)
        ("0\tA\tG", "position"),
        ("999\tA\tG", "position"),
        ("1\tA\tA", "same"),
        ("1\tA\tN", "A, C, G or T"),
        ("1\tA", "three"),
        ("x\tA\tG", "position"),
    ],
)
def test_bad_variant_lines_name_the_line_and_an_action(tmp_path: Path, line: str, fragment: str) -> None:
    with pytest.raises(VariantError, match=fragment):
        parse_variants_tsv(_tsv(tmp_path, line + "\n"), SEQ)


def test_positions_expand_to_the_three_other_bases_each() -> None:
    got = positions_to_variants([1, 4], SEQ)  # position 1 is A, position 4 is T
    assert [(v.pos, v.ref, v.alt) for v in got] == [
        (1, "A", "C"),
        (1, "A", "G"),
        (1, "A", "T"),
        (4, "T", "A"),
        (4, "T", "C"),
        (4, "T", "G"),
    ]


def test_positions_outside_the_sequence_are_refused() -> None:
    with pytest.raises(VariantError, match="position"):
        positions_to_variants([0], SEQ)


def test_more_variants_than_the_maximum_is_refused_with_the_action() -> None:
    many = positions_to_variants(list(range(1, 20)), SEQ)
    with pytest.raises(VariantError, match="--max-variants"):
        check_count(many, 50)
    check_count(many[:50], DEFAULT_MAX_VARIANTS)  # at the limit is fine


def test_cost_estimate_names_the_number_of_passes_and_the_time() -> None:
    text = cost_estimate(50)
    assert "50 forward passes" in text and "L4" in text and "50 seconds" in text
    assert "1 forward pass " in cost_estimate(1)


def test_scan_arithmetic_matches_a_hand_computed_delta() -> None:
    pred = MockPredictor(seed=0)
    v = Variant(10, SEQ[9], "T" if SEQ[9] != "T" else "A")
    (res,) = scan(SEQ, [v], pred, context_length=20)
    lo, hi = 0, 20  # pos0 = 9, half = 10 -> window [max(0, -1), 20)
    ref = SEQ[lo:hi]
    alt = ref[: 9 - lo] + v.alt + ref[10 - lo :]
    p_ref, p_alt = pred.predict(ref), pred.predict(alt)
    assert res.window_start == lo + 1 and res.window_end == hi
    assert res.delta_surprisal == pytest.approx(
        float(surprisal(p_alt, alt).sum() - surprisal(p_ref, ref).sum()), abs=1e-3
    )
    assert res.delta_mean_entropy == pytest.approx(
        float(shannon_entropy(p_alt).mean() - shannon_entropy(p_ref).mean()), abs=1e-4
    )
    col = "ACGT".index
    assert res.site_surprisal_ref == pytest.approx(float(-np.log2(p_ref[9, col(v.ref)])), abs=1e-3)
    assert res.site_surprisal_alt == pytest.approx(float(-np.log2(p_ref[9, col(v.alt)])), abs=1e-3)


def test_one_pass_per_variant_plus_one_per_distinct_reference_window() -> None:
    class Counting(MockPredictor):
        calls = 0

        def predict(self, seq: str) -> np.ndarray:
            Counting.calls += 1
            return super().predict(seq)

    vs = positions_to_variants([60], SEQ)  # three variants at one site: one reference window
    scan(SEQ, vs, Counting(), context_length=20)
    assert Counting.calls == 3 + 1


def test_a_variant_at_the_start_and_end_clamps_its_window() -> None:
    first, last = scan(
        SEQ,
        [
            Variant(1, SEQ[0], "C" if SEQ[0] != "C" else "A"),
            Variant(len(SEQ), SEQ[-1], "A" if SEQ[-1] != "A" else "C"),
        ],
        MockPredictor(),
        context_length=20,
    )
    assert first.window_start == 1 and last.window_end == len(SEQ)


def test_written_table_has_the_documented_columns_and_one_row_per_variant(tmp_path: Path) -> None:
    results = scan(SEQ, positions_to_variants([5], SEQ), MockPredictor(), context_length=20)
    path = write_variants(name="r", results=results, out_dir=str(tmp_path))
    rows = Path(path).read_text(encoding="utf-8").splitlines()
    assert rows[0].split("\t") == [
        "position",
        "ref",
        "alt",
        "window_start",
        "window_end",
        "delta_surprisal_bits",
        "delta_mean_entropy_bits",
        "site_surprisal_ref_bits",
        "site_surprisal_alt_bits",
    ]
    assert len(rows) == 4 and rows[1].split("\t")[:3] == ["5", SEQ[4], "ACGT".replace(SEQ[4], "")[0]]


def test_cli_variants_prints_the_estimate_before_running_and_writes_the_table(tmp_path: Path) -> None:
    fasta = tmp_path / "s.fasta"
    fasta.write_text(f">s\n{SEQ}\n", encoding="utf-8", newline="\n")
    res = CliRunner().invoke(
        app,
        [
            "variants",
            "-i",
            str(fasta),
            "--positions",
            "5,6",
            "--context-length",
            "20",
            "--name",
            "r",
            "--out-dir",
            str(tmp_path / "o"),
        ],
    )
    assert res.exit_code == 0, res.output
    assert res.output.index("6 forward passes") < res.output.index("OK: wrote")
    assert (tmp_path / "o" / "r.variants.tsv").exists()


def test_cli_dry_run_estimates_and_writes_nothing(tmp_path: Path) -> None:
    fasta = tmp_path / "s.fasta"
    fasta.write_text(f">s\n{SEQ}\n", encoding="utf-8", newline="\n")
    res = CliRunner().invoke(
        app, ["variants", "-i", str(fasta), "--positions", "5", "--dry-run", "--out-dir", str(tmp_path / "o")]
    )
    assert res.exit_code == 0 and "3 forward passes" in res.output and not (tmp_path / "o").exists()


def test_cli_too_many_variants_is_an_error_naming_the_option(tmp_path: Path) -> None:
    fasta = tmp_path / "s.fasta"
    fasta.write_text(f">s\n{SEQ}\n", encoding="utf-8", newline="\n")
    res = CliRunner().invoke(
        app, ["variants", "-i", str(fasta), "--positions", "1,2,3", "--max-variants", "5"]
    )
    assert res.exit_code == 1 and "ERROR:" in res.output and "--max-variants" in res.output
