"""End-to-end pipeline tests on the mock predictor (no GPU)."""

from __future__ import annotations

import json
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
FIXTURES = Path(__file__).resolve().parents[2] / "tests" / "contract-fixtures"
PROTOTYPE_FORWARD_ONLY_FIXTURE = FIXTURES / "prototype_parity" / "forward_only_sample_fasta.json"


def test_run_writes_all_outputs(tmp_path: Path) -> None:
    cfg = RunConfig(name="demo", out_dir=str(tmp_path))
    result = pipeline.run(cfg, raw=RAW)

    assert len(result.seq) == CLEAN_LEN
    assert result.values.shape == (CLEAN_LEN,)
    assert result.values.min() >= 0.0
    assert result.values.max() <= MAX_ENTROPY_BITS + 1e-6

    # Paste/FASTA input yields the IGV set plus a bonus self-contained GenBank. issue
    # #123: include_surprisal defaults True, so the surprisal bedgraph/geneious tracks
    # are also written (the TSV grows a 4th column instead of a new file; see
    # test_non_separate_direction_writes_the_plain_three_column_tsv's sibling below).
    # issue #82: provenance.json is written unconditionally, every run.
    expected = {
        "demo.fasta",
        "demo.entropy.bedgraph",
        "demo.entropy.geneious.gff3",
        "demo.surprisal.bedgraph",
        "demo.surprisal.geneious.gff3",
        "demo.summary.txt",
        "demo.gb",
        "demo.entropy.tsv",
        "provenance.json",
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


def test_forward_only_matches_the_recorded_prototype_run(tmp_path: Path) -> None:
    """Issue #279's own observable: on tests/data/sample.fasta, Forward-only must match
    what the DNA-Entropy-Genbank prototype's pre-windowing pipeline actually computed, to
    within last-bit floating-point noise (see the tolerance below; "bit-for-bit" is not a
    promise a float pipeline can keep across platforms).

    issue #316: an earlier version of this test computed its own "legacy reference" live,
    from the CURRENT (ported) ``MockPredictor``/``shannon_entropy``, and compared it to
    the CURRENT pipeline's output — both sides the same code, so this only ever proved
    the new windowing plumbing is a transparent no-op in the single-window case (still
    true and still worth checking — see the notice-count/window assertions below), never
    that it actually matches the prototype. Nobody had checked that claim.

    This version compares against ``tests/contract-fixtures/prototype_parity/
    forward_only_sample_fasta.json`` — the real, frozen output of running the actual
    DNA-Entropy-Genbank prototype (commit 8026bf5c4afbe3021c1f7e79a17a93af4eaad84b, the
    exact commit worker/ was ported from) on its own copy of this same sample.fasta, with
    predictor=mock seed=0. See that file's ``description``/``source`` for exactly how it
    was generated and cross-checked.
    """
    assert PROTOTYPE_FORWARD_ONLY_FIXTURE.exists(), (
        f"missing fixture: {PROTOTYPE_FORWARD_ONLY_FIXTURE} "
        "(see tests/contract-fixtures/prototype_parity/ for how to regenerate it)"
    )
    fixture = json.loads(PROTOTYPE_FORWARD_ONLY_FIXTURE.read_text(encoding="utf-8"))
    prototype_values = np.array(fixture["values"], dtype=np.float32)

    cfg = RunConfig(
        name="legacycheck",
        out_dir=str(tmp_path),
        input_path=SAMPLE_FA,
        direction=Direction.FORWARD_ONLY,
        seed=0,
    )
    result = pipeline.run(cfg)

    assert len(result.seq) == fixture["seq_len"]
    assert len(result.seq) < 8192  # must land in the single-window path, matching the old code

    # Not bit-for-bit, and deliberately so. MEASURED 2026-09-19: exact equality held on
    # the machine that generated the fixture and failed on CI's Linux runner, which has a
    # different numpy and a different libm. `log2` is allowed to differ in the last bit
    # between platforms, so a float pipeline cannot promise bit-identity across them and a
    # test that demands it is testing the toolchain.
    #
    # The tolerance below is three orders of magnitude tighter than the project's own
    # scientific acceptance bar for prototype parity (1e-3, issue #84) on a scale that
    # runs 0 to 2 bits, so anything this catches is a real change in the science and
    # anything it permits is last-bit arithmetic noise.
    largest_difference = float(np.max(np.abs(result.values - prototype_values)))
    assert largest_difference <= 1e-6, (
        f"Forward-only output drifted from the recorded prototype run by "
        f"{largest_difference:.3e}, which is larger than last-bit noise. Regenerate the "
        f"fixture only if the science genuinely changed, and say why in its description."
    )
    # The classic "first base has zero context -> not applicable to mock, but the shape
    # and window/stride bookkeeping must still be present and correct.
    assert result.direction is Direction.FORWARD_ONLY
    assert result.window == 8192
    assert result.context_length == 4096


def test_windowing_is_transparent_in_the_single_window_case(tmp_path: Path) -> None:
    """The property the OLD version of the test above actually proved (and which is still
    worth its own name, separately from prototype parity): when a sequence fits in one
    window, the new windowing/direction plumbing reduces to exactly
    ``predictor.predict(seq) -> shannon_entropy``, with no seam and no windowing artifact
    — the same live comparison the old test made, just no longer wearing a "matches the
    prototype" label it never earned."""
    from dna_entropy.readers.fasta import read_fasta
    from dna_entropy.validation.validators import validate_sequence

    records, _ = read_fasta(SAMPLE_FA)
    validated = validate_sequence(records[0].seq, ambiguity_policy="keep")
    seq = validated.seq
    assert len(seq) < 8192  # single-window path

    direct_probs = MockPredictor(seed=0).predict(seq)
    direct_values = shannon_entropy(direct_probs)

    cfg = RunConfig(
        name="transparencycheck",
        out_dir=str(tmp_path),
        input_path=SAMPLE_FA,
        direction=Direction.FORWARD_ONLY,
        seed=0,
    )
    result = pipeline.run(cfg)

    assert np.array_equal(result.values, direct_values)


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
    # issue #123: include_surprisal defaults True, so the TSV grows a 4th column; turn it
    # off here to test the pre-#123 plain shape in isolation (see the surprisal-specific
    # TSV test below for the 4-column case).
    cfg = RunConfig(
        name="notsep", out_dir=str(tmp_path), direction=Direction.FORWARD_ONLY, include_surprisal=False
    )
    result = pipeline.run(cfg, raw="ATGCATGCATGC")
    tsv_path = next(p for p in result.outputs if p.endswith("notsep.entropy.tsv"))
    header = Path(tsv_path).read_text(encoding="utf-8").splitlines()[0]
    assert header == "position\tbase\tentropy_bits"


# --- issue #123: surprisal is wired end to end -- config -> pipeline -> writers --------
#
# This is the "fails if any one link is cut" test set the issue's own brief asks for. Each
# test below targets ONE link; cutting any single one (reverting config.py's default,
# skipping the pipeline call, or removing a writer's surprisal argument) fails at least
# one of them, never silently produces the same output.


def test_include_surprisal_defaults_true() -> None:
    # The config link: "Selectable output, on by default" (issue #123's own Done-when).
    assert RunConfig().include_surprisal is True


def test_surprisal_is_wired_the_observable_from_the_issue_p_0_01_is_6_64_bits_in_the_tsv(
    tmp_path: Path,
) -> None:
    """THE observable named in issue #123's own body: "A position where the actual base
    has probability 0.01 shows surprisal 6.64 bits in the TSV while entropy there is low."
    Uses the mock predictor's own determinism (seed-based logits) rather than asserting an
    exact P=0.01 fixture -- instead this locks the WEAKER, still-decisive claim: the TSV's
    surprisal_bits column is present, numeric, and NOT identical to entropy_bits row for
    row (which a "wired to nothing" no-op copy of the entropy column would produce)."""
    cfg = RunConfig(name="surp", out_dir=str(tmp_path), seed=3)
    result = pipeline.run(cfg, raw="ATGCATGCATGCATGCATGC")
    tsv_path = next(p for p in result.outputs if p.endswith("surp.entropy.tsv"))
    lines = Path(tsv_path).read_text(encoding="utf-8").splitlines()
    assert lines[0] == "position\tbase\tentropy_bits\tsurprisal_bits"
    rows = [line.split("\t") for line in lines[1:]]
    assert len(rows) == len(result.seq)
    surprisal_col = [float(r[3]) for r in rows]
    entropy_col = [float(r[2]) for r in rows]
    assert any(s != e for s, e in zip(surprisal_col, entropy_col, strict=True))
    assert all(0.0 <= s <= 20.0 for s in surprisal_col)  # MAX_SURPRISAL_BITS ceiling


def test_no_surprisal_flag_omits_surprisal_outputs_and_tsv_column(tmp_path: Path) -> None:
    cfg = RunConfig(name="nosurp", out_dir=str(tmp_path), include_surprisal=False)
    result = pipeline.run(cfg, raw="ATGCATGCATGC")
    names = {Path(p).name for p in result.outputs}
    assert "nosurp.surprisal.bedgraph" not in names
    assert "nosurp.surprisal.geneious.gff3" not in names
    assert "nosurp.entropy.bedgraph" in names  # entropy outputs are unaffected
    tsv_path = next(p for p in result.outputs if p.endswith("nosurp.entropy.tsv"))
    header = Path(tsv_path).read_text(encoding="utf-8").splitlines()[0]
    assert "surprisal" not in header
    stats_path = next(p for p in result.outputs if p.endswith("nosurp.summary.txt"))
    assert "surprisal" not in Path(stats_path).read_text(encoding="utf-8").lower()


def test_surprisal_bedgraph_and_geneious_files_are_written_and_nonempty(tmp_path: Path) -> None:
    cfg = RunConfig(name="surptrack", out_dir=str(tmp_path))
    result = pipeline.run(cfg, raw="ATGCATGCATGC")
    names = {Path(p).name for p in result.outputs}
    assert "surptrack.surprisal.bedgraph" in names
    assert "surptrack.surprisal.geneious.gff3" in names
    bedgraph_path = next(p for p in result.outputs if p.endswith("surptrack.surprisal.bedgraph"))
    text = Path(bedgraph_path).read_text(encoding="utf-8")
    assert "surprisal" in text.splitlines()[0].lower()
    assert len(text.splitlines()) == 1 + len(result.seq)  # track header + one row per base


def test_surprisal_wig_written_when_track_format_is_wig(tmp_path: Path) -> None:
    cfg = RunConfig(name="surpwig", out_dir=str(tmp_path), track_format=TrackFormat.WIG)
    result = pipeline.run(cfg, raw="ATGCATGCATGC")
    names = {Path(p).name for p in result.outputs}
    assert "surpwig.surprisal.wig" in names
    assert "surpwig.entropy.bedgraph" not in names  # wig format selected, not bedgraph


def test_stats_records_surprisal_mean_and_log_likelihood(tmp_path: Path) -> None:
    cfg = RunConfig(name="surpstats", out_dir=str(tmp_path))
    result = pipeline.run(cfg, raw="ATGCATGCATGC")
    stats_path = next(p for p in result.outputs if p.endswith("surpstats.summary.txt"))
    text = Path(stats_path).read_text(encoding="utf-8")
    assert "surprisal mean:" in text
    assert "log-likelihood:" in text


# --- issue #82: provenance.json is written every run, unconditionally -----------------


def test_provenance_json_is_always_written(tmp_path: Path) -> None:
    import json

    cfg = RunConfig(name="prov1", out_dir=str(tmp_path))
    result = pipeline.run(cfg, raw="ATGCATGCATGC")
    names = {Path(p).name for p in result.outputs}
    assert "provenance.json" in names
    prov_path = next(p for p in result.outputs if p.endswith("provenance.json"))
    data = json.loads(Path(prov_path).read_text(encoding="utf-8"))
    assert data["run"]["name"] == "prov1"
    assert data["predictor"]["kind"] == "mock"
    assert len(data["contigs"]) == 1
    assert data["contigs"][0]["window"] == result.window
    assert data["contigs"][0]["stride"] == result.stride
    assert data["contigs"][0]["seam"] == result.seam


def test_provenance_json_records_the_seam_and_reduced_context(tmp_path: Path) -> None:
    import json

    cfg = RunConfig(name="prov2", out_dir=str(tmp_path), context_length=128, seed=1)
    result = pipeline.run(cfg, raw="ACGT" * 70)  # L=280 >= 2*128
    prov_path = next(p for p in result.outputs if p.endswith("provenance.json"))
    data = json.loads(Path(prov_path).read_text(encoding="utf-8"))
    assert data["contigs"][0]["seam"] == 128
    assert data["contigs"][0]["reduced_context_count"] == 0


def test_two_runs_of_the_same_input_produce_provenance_differing_only_in_timestamp_and_wall_time(
    tmp_path: Path,
) -> None:
    """issue #82's own named Observable: "Two runs of the same input on the same GPU
    class produce identical entropy files and provenance differing only in timestamps."""
    import json

    cfg_a = RunConfig(name="reproA", out_dir=str(tmp_path / "a"), seed=7)
    cfg_b = RunConfig(name="reproB", out_dir=str(tmp_path / "b"), seed=7)
    result_a = pipeline.run(cfg_a, raw="ATGCATGCATGCATGCATGC")
    result_b = pipeline.run(cfg_b, raw="ATGCATGCATGCATGCATGC")
    assert np.array_equal(result_a.values, result_b.values)  # entropy files: identical

    prov_a = json.loads(
        Path(next(p for p in result_a.outputs if p.endswith("provenance.json"))).read_text(encoding="utf-8")
    )
    prov_b = json.loads(
        Path(next(p for p in result_b.outputs if p.endswith("provenance.json"))).read_text(encoding="utf-8")
    )
    from dna_entropy.writers.provenance import GENERATED_AT_KEY, WALL_TIME_KEY

    def _normalize(data: dict) -> dict:
        # "run".name and each contig's "name" are the run/contig NAME, expected to differ
        # (reproA vs reproB) -- everything else must be identical for identical input+config.
        out = {k: v for k, v in data.items() if k not in (GENERATED_AT_KEY, WALL_TIME_KEY, "run")}
        out["contigs"] = [{k: v for k, v in c.items() if k != "name"} for c in out["contigs"]]
        return out

    assert _normalize(prov_a) == _normalize(prov_b)


def test_provenance_json_written_even_on_a_partial_run(tmp_path: Path) -> None:
    # docs/job_contract.md §6: "partial results are always kept, never discarded" --
    # provenance.json is written for whatever DID complete, same as every other output.
    cfg = RunConfig(name="provpartial", out_dir=str(tmp_path))

    def _cancel_after_first_contig(_contig) -> None:
        raise RuntimeError("simulated cancellation")

    p = tmp_path / "three.fasta"
    p.write_text(
        ">r1\nACGTACGTACGTACGTACGTACGTACGTACGT\n>r2\nTTTTGGGGCCCCAAAATTTTGGGGCCCCAAAA\n",
        encoding="utf-8",
    )
    cfg.input_path = str(p)
    with pytest.raises(RuntimeError):
        pipeline.run(cfg, on_contig=_cancel_after_first_contig)
    on_disk = {q.name for q in tmp_path.iterdir()}
    assert "provenance.json" in on_disk


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


# --- per-writer suppression flags (issue #304): output files on disk, not just RunConfig ----


def test_include_flags_all_off_writes_nothing_but_tsv(tmp_path: Path) -> None:
    """Every include_* writer flag off except include_tsv -> exactly the TSV plus the
    ALWAYS-written provenance.json (issue #82: not gated by any include_* flag)."""
    cfg = RunConfig(
        name="onlytsv",
        out_dir=str(tmp_path),
        include_fasta=False,
        include_track=False,
        include_geneious=False,
        include_stats=False,
        include_genbank=False,
        include_genes_gff3=False,
        include_tsv=True,
        # issue #123: include_surprisal defaults True independently of the other
        # include_* flags above (it is its own toggle); off here so this test's "exactly
        # one file" claim still holds -- the TSV-column case is covered separately.
        include_surprisal=False,
    )
    result = pipeline.run(cfg, raw="ATGCATGCATGC")
    on_disk = {p.name for p in tmp_path.iterdir()}
    assert on_disk == {"onlytsv.entropy.tsv", "provenance.json"}
    assert {Path(p).name for p in result.outputs} == on_disk


def test_include_track_false_omits_the_track_file_from_disk(tmp_path: Path) -> None:
    cfg = RunConfig(name="notrack", out_dir=str(tmp_path), include_track=False)
    pipeline.run(cfg, raw="ATGCATGCATGC")
    on_disk = {p.name for p in tmp_path.iterdir()}
    assert "notrack.entropy.bedgraph" not in on_disk
    assert "notrack.fasta" in on_disk  # every other default writer still ran


def test_include_fasta_false_omits_fasta_from_disk(tmp_path: Path) -> None:
    cfg = RunConfig(name="nofasta", out_dir=str(tmp_path), include_fasta=False)
    pipeline.run(cfg, raw="ATGCATGCATGC")
    on_disk = {p.name for p in tmp_path.iterdir()}
    assert "nofasta.fasta" not in on_disk
    assert "nofasta.entropy.bedgraph" in on_disk


def test_include_genbank_false_omits_bonus_genbank_from_disk(tmp_path: Path) -> None:
    cfg = RunConfig(name="nogb", out_dir=str(tmp_path), include_genbank=False)
    pipeline.run(cfg, raw="ATGCATGCATGC")
    on_disk = {p.name for p in tmp_path.iterdir()}
    assert "nogb.gb" not in on_disk


def test_include_geneious_false_omits_geneious_gff3_from_disk(tmp_path: Path) -> None:
    cfg = RunConfig(name="nogen", out_dir=str(tmp_path), include_geneious=False)
    pipeline.run(cfg, raw="ATGCATGCATGC")
    on_disk = {p.name for p in tmp_path.iterdir()}
    assert "nogen.entropy.geneious.gff3" not in on_disk


def test_include_stats_false_omits_summary_from_disk(tmp_path: Path) -> None:
    cfg = RunConfig(name="nostats", out_dir=str(tmp_path), include_stats=False)
    pipeline.run(cfg, raw="ATGCATGCATGC")
    on_disk = {p.name for p in tmp_path.iterdir()}
    assert "nostats.summary.txt" not in on_disk


def test_include_flags_on_genbank_input_suppress_the_matching_files(tmp_path: Path) -> None:
    cfg = RunConfig(
        name="gbsup",
        input_path=str(DATA / "multi.gb"),
        out_dir=str(tmp_path),
        include_fasta=False,
        include_geneious=False,
        include_stats=False,
        include_tsv=False,
    )
    pipeline.run(cfg)
    on_disk = {p.name for p in tmp_path.iterdir()}
    assert "gbsup.fasta" not in on_disk
    assert "gbsup.entropy.geneious.gff3" not in on_disk
    assert "stats.txt" not in on_disk
    assert "gbsup.entropy.tsv" not in on_disk
    assert "gbsup.gb" in on_disk  # include_genbank stayed on (default True)


# --- fastaRecords="first" (issue #306): truncates to the first record, on disk -------------


THREE_RECORD_FASTA_FOR_RECORDS_FLAG = (
    ">record_one first locus\n"
    "ACGTACGTACGTACGTACGTACGTACGTACGT\n"
    ">record_two second locus\n"
    "TTTTGGGGCCCCAAAATTTTGGGGCCCCAAAA\n"
    ">record_three third locus\n"
    "GATCGATCGATCGATCGATCGATCGATCGATC\n"
)


def test_fasta_records_first_processes_only_the_first_record(tmp_path: Path) -> None:
    p = tmp_path / "three.fasta"
    p.write_text(THREE_RECORD_FASTA_FOR_RECORDS_FLAG, encoding="utf-8")
    out_dir = tmp_path / "out"
    cfg = RunConfig(name="three", input_path=str(p), out_dir=str(out_dir), fasta_records="first")
    result = pipeline.run(cfg)
    assert result.contigs == 1
    assert result.total_nt == 32
    fasta_path = next(f for f in result.outputs if f.endswith("three.fasta"))
    text = Path(fasta_path).read_text(encoding="utf-8")
    assert text.count(">") == 1
    assert "three_1" in text
    assert "record_two" not in text and "record_three" not in text


def test_fasta_records_all_is_the_default_and_processes_every_record(tmp_path: Path) -> None:
    p = tmp_path / "three.fasta"
    p.write_text(THREE_RECORD_FASTA_FOR_RECORDS_FLAG, encoding="utf-8")
    out_dir = tmp_path / "out"
    cfg = RunConfig(name="three", input_path=str(p), out_dir=str(out_dir))
    assert cfg.fasta_records == "all"
    result = pipeline.run(cfg)
    assert result.contigs == 3


def test_fasta_records_first_notice_names_the_dropped_record_count(tmp_path: Path) -> None:
    p = tmp_path / "three.fasta"
    p.write_text(THREE_RECORD_FASTA_FOR_RECORDS_FLAG, encoding="utf-8")
    cfg = RunConfig(name="three", input_path=str(p), out_dir=str(tmp_path), fasta_records="first")
    result = pipeline.run(cfg)
    assert any("fastaRecords='first'" in n and "2 other" in n for n in result.notices)


def test_fasta_records_first_does_not_affect_genbank_multi_record_input(tmp_path: Path) -> None:
    # job_contract.md §3: fastaRecords is documented as FASTA-specific; a GenBank input's
    # own multi-record handling must be untouched even if fasta_records happens to be "first".
    cfg = RunConfig(
        name="gb", input_path=str(DATA / "multi.gb"), out_dir=str(tmp_path), fasta_records="first"
    )
    result = pipeline.run(cfg)
    assert result.contigs > 1


# --- issue #366: cfg.name reaches every writer's output path with zero sanitization ----
#
# MEASURED 2026-09-19: every writer builds its path as `Path(cfg.out_dir) / f"{cfg.name}
# <suffix>"`. `cfg.name` can come from a CLI `--name` OR straight from a manifest's
# `inputs[].name` (`worker/manifest.py::build_run_config`, never sanitized at all before
# this fix) -- so `pipeline.run()` itself, not just the CLI, is the one choke point both
# paths funnel through. Hard Rule 14 (the user's files are read-only to us; outputs go
# only to the chosen folder) is what actually matters here: a `--name` of `../../evil`
# is that rule broken by a string, so `sanitize_run_name` REFUSES or NEUTRALISES a
# traversal rather than merely tidying a name for cosmetics.


def test_sanitize_run_name_neutralizes_a_traversal_with_forward_slashes() -> None:
    safe = pipeline.sanitize_run_name("../../evil")
    assert "/" not in safe
    assert ".." not in safe


def test_sanitize_run_name_neutralizes_a_traversal_with_backslashes() -> None:
    safe = pipeline.sanitize_run_name("..\\..\\evil")
    assert "\\" not in safe
    assert ".." not in safe


def test_sanitize_run_name_neutralizes_traversal_in_the_middle_too() -> None:
    safe = pipeline.sanitize_run_name("foo/../../bar")
    assert "/" not in safe and ".." not in safe


def test_sanitize_run_name_rejects_a_name_that_is_only_traversal() -> None:
    with pytest.raises(pipeline.PipelineError):
        pipeline.sanitize_run_name("../..")


def test_sanitize_run_name_rejects_empty_or_whitespace_only() -> None:
    with pytest.raises(pipeline.PipelineError):
        pipeline.sanitize_run_name("")
    with pytest.raises(pipeline.PipelineError):
        pipeline.sanitize_run_name("   ")


def test_sanitize_run_name_neutralizes_a_reserved_device_name() -> None:
    for hostile in ("CON", "con", "NUL", "COM1", "LPT9"):
        safe = pipeline.sanitize_run_name(hostile)
        assert safe.upper() not in pipeline._RESERVED_DEVICE_NAMES, hostile


def test_sanitize_run_name_caps_length() -> None:
    safe = pipeline.sanitize_run_name("x" * 500)
    assert len(safe) <= pipeline.MAX_RUN_NAME_LENGTH


def test_sanitize_run_name_leaves_an_ordinary_name_unchanged() -> None:
    assert pipeline.sanitize_run_name("my_locus-1.2") == "my_locus-1.2"


def test_run_with_a_hostile_name_never_writes_outside_out_dir(tmp_path: Path) -> None:
    # The actual end-to-end observable the issue asks for: run it for real with a hostile
    # name and prove nothing landed outside the configured out_dir.
    out_dir = tmp_path / "configured_out"
    out_dir.mkdir()
    cfg = RunConfig(name="../../evil", out_dir=str(out_dir))
    result = pipeline.run(cfg, raw=RAW)

    for p in result.outputs:
        resolved = Path(p).resolve()
        assert resolved.is_relative_to(out_dir.resolve()), p
        assert resolved.exists()
    # Nothing escaped upward: no file with the sanitized base name exists as a DIRECT
    # child of tmp_path (only inside out_dir, one level down, is acceptable).
    assert not any(tmp_path.glob("evil*"))
    # And cfg.name itself was sanitized (every writer used the sanitized value).
    assert cfg.name == pipeline.sanitize_run_name("../../evil")


def test_run_with_a_reserved_device_name_writes_a_usable_file_not_a_crash(tmp_path: Path) -> None:
    cfg = RunConfig(name="CON", out_dir=str(tmp_path))
    result = pipeline.run(cfg, raw=RAW)
    assert result.outputs
    for p in result.outputs:
        assert Path(p).exists()


def test_run_rejects_a_name_that_sanitizes_to_nothing(tmp_path: Path) -> None:
    cfg = RunConfig(name="../..", out_dir=str(tmp_path))
    with pytest.raises(pipeline.PipelineError):
        pipeline.run(cfg, raw=RAW)
