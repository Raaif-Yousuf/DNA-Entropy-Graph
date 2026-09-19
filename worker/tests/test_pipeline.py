"""End-to-end pipeline tests on the mock predictor (no GPU)."""

from __future__ import annotations

from pathlib import Path

import numpy as np
import pytest

from dna_entropy import pipeline
from dna_entropy.analysis import MAX_ENTROPY_BITS
from dna_entropy.analysis.entropy import shannon_entropy
from dna_entropy.analysis.windowing import WindowingError
from dna_entropy.config import Direction, PredictorKind, RunConfig, TrackFormat
from dna_entropy.predictors.base import PredictorError
from dna_entropy.predictors.mock import MockPredictor

RAW = ">demo header\nATGC ATGC ATGC\nACGTACGTACGT"
CLEAN_LEN = 24
DATA = Path(__file__).parent / "data"
SAMPLE_FA = str(DATA / "sample.fasta")


def test_run_writes_all_outputs(tmp_path: Path) -> None:
    cfg = RunConfig(name="demo", out_dir=str(tmp_path))
    result = pipeline.run(cfg, raw=RAW)

    assert len(result.seq) == CLEAN_LEN
    assert result.values.shape == (CLEAN_LEN,)
    assert result.values.min() >= 0.0
    assert result.values.max() <= MAX_ENTROPY_BITS + 1e-6

    # Paste/FASTA input yields the IGV set plus a bonus self-contained GenBank.
    expected = {
        "demo.fasta",
        "demo.entropy.bedgraph",
        "demo.entropy.geneious.gff3",
        "demo.summary.txt",
        "demo.gb",
        "demo.entropy.tsv",
    }
    written = {Path(p).name for p in result.outputs}
    assert expected == written
    for p in result.outputs:
        assert Path(p).exists()


def test_run_propagates_validation_notices(tmp_path: Path) -> None:
    cfg = RunConfig(name="demo", out_dir=str(tmp_path))
    result = pipeline.run(cfg, raw=RAW)
    assert any("header" in n.lower() for n in result.notices)


def test_run_wig_format(tmp_path: Path) -> None:
    cfg = RunConfig(name="demo", out_dir=str(tmp_path), track_format=TrackFormat.WIG)
    result = pipeline.run(cfg, raw="ATGCATGCATGC")
    assert any(Path(p).name == "demo.entropy.wig" for p in result.outputs)


def test_run_is_deterministic(tmp_path: Path) -> None:
    cfg = RunConfig(name="demo", out_dir=str(tmp_path), seed=7)
    a = pipeline.run(cfg, raw="ATGCATGCATGC")
    b = pipeline.run(cfg, raw="ATGCATGCATGC")
    assert np.array_equal(a.values, b.values)


def test_evo_predictor_unavailable_is_clean_error(tmp_path: Path) -> None:
    cfg = RunConfig(name="demo", out_dir=str(tmp_path), predictor=PredictorKind.EVO)
    with pytest.raises(PredictorError):
        pipeline.run(cfg, raw="ATGCATGCATGC")


def test_build_predictor_passes_max_len_as_evo_max_context(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    """Regression guard: the GPU ceiling windowing computes W against (cfg.max_len) MUST
    reach EvoPredictor's own max_context, or a bigger-ceiling config (e.g. A100) would be
    silently capped back down to EvoPredictor's 8192 default, rejecting valid windows.
    """
    import sys
    import types

    calls: list[dict] = []

    class _StubEvoPredictor:
        def __init__(self, **kwargs):
            calls.append(kwargs)

        def predict(self, seq):  # pragma: no cover - not exercised here
            raise AssertionError("predict() should not be called by this test")

    fake_module = types.ModuleType("dna_entropy.predictors.evo")
    fake_module.EvoPredictor = _StubEvoPredictor
    monkeypatch.setitem(sys.modules, "dna_entropy.predictors.evo", fake_module)

    cfg = RunConfig(name="demo", out_dir=str(tmp_path), predictor=PredictorKind.EVO, max_len=16384)
    pipeline.build_predictor(cfg)

    assert calls == [{"model": cfg.model, "device": cfg.device, "max_context": 16384}]


# --- windowing + direction: forward-only reproduces the legacy single-pass output ------


def test_forward_only_reproduces_legacy_single_pass_output_bit_for_bit(tmp_path: Path) -> None:
    """Issue #279's own observable: on tests/data/sample.fasta, Forward-only must match
    what the pre-windowing pipeline computed: predictor.predict(seq) -> shannon_entropy,
    in one pass, no windowing seam, no reverse-complement — a straight bit-for-bit replay.
    """
    from dna_entropy.readers.fasta import read_fasta
    from dna_entropy.validation.validators import validate_sequence

    records, _ = read_fasta(SAMPLE_FA)
    validated = validate_sequence(records[0].seq, allow_ambiguity=True)
    seq = validated.seq
    assert len(seq) < 8192  # must land in the single-window path, matching the old code

    # The "legacy" reference: exactly what pipeline.run() did before windowing existed.
    legacy_probs = MockPredictor(seed=0).predict(seq)
    legacy_values = shannon_entropy(legacy_probs)

    cfg = RunConfig(
        name="legacycheck",
        out_dir=str(tmp_path),
        input_path=SAMPLE_FA,
        direction=Direction.FORWARD_ONLY,
        seed=0,
    )
    result = pipeline.run(cfg)

    assert np.array_equal(result.values, legacy_values)
    # The classic "first base has zero context -> not applicable to mock, but the shape
    # and window/stride bookkeeping must still be present and correct.
    assert result.direction is Direction.FORWARD_ONLY
    assert result.window == 8192
    assert result.context_length == 4096


def test_run_records_window_stride_and_seam_in_result(tmp_path: Path) -> None:
    cfg = RunConfig(
        name="prov",
        out_dir=str(tmp_path),
        context_length=128,
        direction=Direction.BOTH_COMBINED,
        seed=1,
    )
    result = pipeline.run(cfg, raw="ACGT" * 70)  # L=280 >= 2*128
    assert result.window == 256  # min(2*128, 8192)
    assert result.stride == 128
    assert result.context_length == 128
    assert result.seam == 128
    assert result.reduced_context_count == 0


def test_run_records_reduced_context_when_sequence_shorter_than_2k(tmp_path: Path) -> None:
    cfg = RunConfig(
        name="reduced",
        out_dir=str(tmp_path),
        context_length=200,
        direction=Direction.BOTH_COMBINED,
        seed=1,
    )
    result = pipeline.run(cfg, raw="ACGT" * 30)  # L=120 < 2*200
    assert result.reduced_context_count > 0
    assert result.seam is None
    assert any("reduced context" in n.lower() for n in result.notices)


def test_run_rejects_context_length_below_minimum(tmp_path: Path) -> None:
    cfg = RunConfig(name="badk", out_dir=str(tmp_path), context_length=10)
    with pytest.raises(WindowingError):
        pipeline.run(cfg, raw="ACGT" * 30)


def test_run_warns_when_input_shorter_than_context_length(tmp_path: Path) -> None:
    cfg = RunConfig(name="shortin", out_dir=str(tmp_path), context_length=4096)
    result = pipeline.run(cfg, raw="ACGT" * 10)  # 40 nt, far under K=4096
    assert any("shorter than the context length" in n for n in result.notices)


def test_both_separate_writes_fwd_and_rev_track_files(tmp_path: Path) -> None:
    cfg = RunConfig(
        name="sep",
        out_dir=str(tmp_path),
        context_length=128,
        direction=Direction.BOTH_SEPARATE,
        seed=2,
    )
    result = pipeline.run(cfg, raw="ACGT" * 70)  # L=280 >= 2*128
    names = {Path(p).name for p in result.outputs}
    assert "sep.entropy.fwd.bedgraph" in names
    assert "sep.entropy.rev.bedgraph" in names
    assert "sep.entropy.bedgraph" in names  # the combined track is still written too

    tsv_path = next(p for p in result.outputs if p.endswith("sep.entropy.tsv"))
    header = Path(tsv_path).read_text(encoding="utf-8").splitlines()[0]
    assert header == "position\tbase\tentropy_fwd\tentropy_rev\tentropy_combined"


def test_non_separate_direction_writes_the_plain_three_column_tsv(tmp_path: Path) -> None:
    cfg = RunConfig(name="notsep", out_dir=str(tmp_path), direction=Direction.FORWARD_ONLY)
    result = pipeline.run(cfg, raw="ATGCATGCATGC")
    tsv_path = next(p for p in result.outputs if p.endswith("notsep.entropy.tsv"))
    header = Path(tsv_path).read_text(encoding="utf-8").splitlines()[0]
    assert header == "position\tbase\tentropy_bits"


def test_no_tsv_flag_omits_the_tsv_output(tmp_path: Path) -> None:
    cfg = RunConfig(name="notsvcfg", out_dir=str(tmp_path), include_tsv=False)
    result = pipeline.run(cfg, raw="ATGCATGCATGC")
    names = {Path(p).name for p in result.outputs}
    assert "notsvcfg.entropy.tsv" not in names


def test_windowing_tiles_a_sequence_longer_than_the_ceiling(tmp_path: Path) -> None:
    # 20,000 nt is well past DEFAULT_MAX_LEN=8192 -- must NOT be rejected outright now
    # that windowing tiles long sequences into multiple passes.
    long_seq = "ACGT" * 5000  # 20,000 nt
    cfg = RunConfig(name="longseq", out_dir=str(tmp_path), direction=Direction.FORWARD_ONLY)
    result = pipeline.run(cfg, raw=long_seq)
    assert len(result.values) == 20000
    assert result.values.min() >= 0.0
    assert result.values.max() <= MAX_ENTROPY_BITS + 1e-6


# --- on_window / on_contig cooperative-cancellation hooks (worker issue #278) ---------


def test_on_window_is_called_once_per_completed_window(tmp_path: Path) -> None:
    # K=128, ceiling=256 -> W=256, S=128; a 600 nt sequence needs multiple windows.
    calls = []
    cfg = RunConfig(
        name="hook",
        out_dir=str(tmp_path),
        context_length=128,
        max_len=256,
        direction=Direction.FORWARD_ONLY,
    )
    pipeline.run(cfg, raw="ACGT" * 150, on_window=lambda: calls.append(1))  # 600 nt
    from dna_entropy.analysis.windowing import plan_windows

    plan = plan_windows(600, 128, 256)
    assert len(calls) == plan.num_windows


def test_on_contig_is_called_once_per_completed_contig(tmp_path: Path) -> None:
    from dna_entropy.readers.genbank import read_genbank

    DATA = Path(__file__).parent / "data"
    contigs_seen = []
    cfg = RunConfig(name="hookmulti", input_path=str(DATA / "multi.gb"), out_dir=str(tmp_path))
    pipeline.run(cfg, on_contig=lambda c: contigs_seen.append(c.name))
    records, _ = read_genbank(str(DATA / "multi.gb"))
    assert len(contigs_seen) == len(records)


def test_on_window_exception_stops_the_run_and_propagates(tmp_path: Path) -> None:
    class _Stop(Exception):
        pass

    cfg = RunConfig(name="cancelled", out_dir=str(tmp_path))
    with pytest.raises(_Stop):
        pipeline.run(cfg, raw="ATGCATGCATGC", on_window=lambda: (_ for _ in ()).throw(_Stop()))


def test_on_contig_exception_still_writes_partial_output_for_completed_contigs(
    tmp_path: Path,
) -> None:
    """docs/job_contract.md §6: 'partial results are always kept, never discarded.'"""
    from dna_entropy.readers.genbank import read_genbank

    DATA = Path(__file__).parent / "data"
    records, _ = read_genbank(str(DATA / "multi.gb"))
    assert len(records) >= 2  # precondition: there IS a second contig to never reach

    class _Stop(Exception):
        pass

    seen = []

    def _on_contig(contig):
        seen.append(contig.name)
        if len(seen) == 1:
            raise _Stop()

    cfg = RunConfig(name="partial", input_path=str(DATA / "multi.gb"), out_dir=str(tmp_path))
    with pytest.raises(_Stop):
        pipeline.run(cfg, on_contig=_on_contig)

    assert len(seen) == 1  # stopped after exactly the first contig
    # Partial output WAS written despite the exception, for the one completed contig.
    written = list(Path(tmp_path).glob("partial.fasta"))
    assert written, "expected partial.fasta to exist from the salvaged partial output"
    fasta_text = written[0].read_text(encoding="utf-8")
    assert fasta_text.count(">") == 1  # only the one completed contig, not both


def test_on_window_exception_before_any_contig_completes_writes_nothing(tmp_path: Path) -> None:
    # on_window fires DURING the (only) contig's analysis, before it is ever appended to
    # the completed list — nothing has been produced yet for the salvage path to write.
    class _Stop(Exception):
        pass

    cfg = RunConfig(name="nowrite", out_dir=str(tmp_path))
    with pytest.raises(_Stop):
        pipeline.run(cfg, raw="ATGCATGCATGC", on_window=lambda: (_ for _ in ()).throw(_Stop()))
    assert list(Path(tmp_path).iterdir()) == []
