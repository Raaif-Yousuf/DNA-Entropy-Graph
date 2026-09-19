"""CLI-shape tests for issue #277 (drop cloudrun/keep-gpu) and #278 (worker-run, real).

The approved design (docs/superpowers/specs/2026-09-18-dna-entropy-graph-design.md
section 3) forbids the worker shelling out to gcloud/SSH; `cloudrun` and `keep-gpu` (plus
`--prefer-local`) are gone along with `dna_entropy.cloud`. In their place, `worker-run` is
the real manifest-driven worker entrypoint (issue #278); see test_worker_runner.py for its
full end-to-end behaviour with `--root` against a `LocalBlobstore`.
"""

from __future__ import annotations

import json
from pathlib import Path

import typer.main
from typer.testing import CliRunner

from dna_entropy.cli import app

runner = CliRunner()


def test_help_lists_no_cloudrun_or_keep_gpu() -> None:
    result = runner.invoke(app, ["--help"])
    assert result.exit_code == 0
    assert "cloudrun" not in result.output
    assert "keep-gpu" not in result.output


def test_cloudrun_prints_a_clear_use_the_app_message() -> None:
    """issue #212: not just gone -- typing the old command must get a clear, ASCII-only
    ERROR message, not Typer's own generic "No such command" (which renders with Unicode
    box-drawing characters and would crash a real cp1252 Windows console)."""
    result = runner.invoke(app, ["cloudrun"])
    assert result.exit_code == 1
    assert "cloudrun" in result.output
    assert "ERROR" in result.output
    assert result.output.isascii()


def test_keep_gpu_prints_a_clear_use_the_app_message() -> None:
    result = runner.invoke(app, ["keep-gpu"])
    assert result.exit_code == 1
    assert "keep-gpu" in result.output
    assert "ERROR" in result.output
    assert result.output.isascii()


def test_worker_run_requires_either_root_or_bucket_and_prefix() -> None:
    result = runner.invoke(app, ["worker-run"])
    assert result.exit_code == 2
    assert "--root" in result.output or "--bucket" in result.output


def test_worker_run_help_lists_local_and_gcs_options() -> None:
    # Asserted against the command's declared options, not its rendered help text.
    #
    # MEASURED 2026-09-19: the substring version of this test passed on a developer
    # terminal and failed on CI under two different widths, because Typer draws help
    # through Rich and what reaches `result.output` depends on the terminal, the Rich
    # version and the ANSI styling. Pinning COLUMNS did not fix it. The option names are
    # the contract worth testing; the box drawing around them is not, and a test that
    # asserts on it fails for reasons that have nothing to do with the CLI.
    result = runner.invoke(app, ["worker-run", "--help"])
    assert result.exit_code == 0

    worker_run = typer.main.get_command(app).commands["worker-run"]
    declared = {opt for param in worker_run.params for opt in param.opts}
    assert {"--root", "--bucket", "--prefix"} <= declared


def test_worker_run_missing_manifest_is_a_clean_error_not_a_crash(tmp_path: Path) -> None:
    empty_job_dir = tmp_path / "empty_job"
    empty_job_dir.mkdir()
    result = runner.invoke(app, ["worker-run", "--root", str(empty_job_dir)])
    assert result.exit_code == 2
    assert "ERROR" in result.output
    assert "Traceback" not in result.output  # a clean, reported error, not a raw crash


def test_worker_run_real_local_job_end_to_end(tmp_path: Path) -> None:
    """The CLI surface for the same acceptance bar test_worker_runner.py exercises
    directly: a real manifest, run through the actual `dna-entropy worker-run` command,
    produces a real result.json on disk."""
    job_dir = tmp_path / "job"
    job_dir.mkdir()
    (job_dir / "input").mkdir()
    (job_dir / "input" / "locus.fasta").write_text(
        ">seq\nACGTACGTACGTACGTACGTACGTACGTACGT\n", encoding="utf-8"
    )
    manifest = {
        "schema": 1,
        "jobId": "clitest-job",
        "inputs": [{"id": "in1", "path": "input/locus.fasta", "name": "locus"}],
        "predictor": {"kind": "mock", "seed": 0},
        "analysis": {"contextLength": 128, "window": 256, "stride": 128, "direction": "forward-only"},
        "store": {"kind": "localdir", "root": str(job_dir)},
    }
    (job_dir / "manifest.json").write_text(json.dumps(manifest), encoding="utf-8")

    result = runner.invoke(app, ["worker-run", "--root", str(job_dir)])
    assert result.exit_code == 0, result.output
    assert "done" in result.output
    assert (job_dir / "result.json").exists()
    result_doc = json.loads((job_dir / "result.json").read_text(encoding="utf-8"))
    assert result_doc["status"] == "done"


def test_no_tsv_flag_actually_omits_the_tsv_file(tmp_path) -> None:
    """Regression guard: --tsv/--no-tsv was declared as a CLI option but never passed into
    RunConfig, so --no-tsv silently did nothing (caught by inspection, not by a failing
    test, while wiring #281 — this test is what should have caught it)."""
    out_dir = tmp_path / "out"
    result = runner.invoke(
        app,
        ["run", "--name", "clitsv", "--out", str(out_dir), "--no-tsv"],
        input="ATGCATGCATGCATGCATGCATGCATGCATGC\n",
    )
    assert result.exit_code == 0, result.output
    written = {p.name for p in (out_dir / "clitsv").iterdir()}
    assert not any(name.endswith(".entropy.tsv") for name in written)


def test_tsv_flag_default_on_writes_the_tsv_file(tmp_path) -> None:
    out_dir = tmp_path / "out"
    result = runner.invoke(
        app,
        ["run", "--name", "clitsvon", "--out", str(out_dir)],
        input="ATGCATGCATGCATGCATGCATGCATGCATGC\n",
    )
    assert result.exit_code == 0, result.output
    written = {p.name for p in (out_dir / "clitsvon").iterdir()}
    assert any(name.endswith(".entropy.tsv") for name in written)


def test_ambiguity_flag_error_actually_refuses_an_ambiguous_sequence(tmp_path) -> None:
    """Regression guard: --ambiguity is a real, wired-through option, not a flag that
    parses but is never passed into RunConfig (issue #249)."""
    out_dir = tmp_path / "out"
    result = runner.invoke(
        app,
        ["run", "--name", "cliamb", "--out", str(out_dir), "--ambiguity", "error"],
        input="ATGCATGCNNNNATGCATGCATGCATGCATGC\n",
    )
    assert result.exit_code != 0
    assert "ambiguity" in result.output.lower()


def test_ambiguity_flag_keep_default_accepts_an_ambiguous_sequence(tmp_path) -> None:
    out_dir = tmp_path / "out"
    result = runner.invoke(
        app,
        ["run", "--name", "cliambkeep", "--out", str(out_dir)],  # --ambiguity defaults to keep
        input="ATGCATGCNNNNATGCATGCATGCATGCATGC\n",
    )
    assert result.exit_code == 0, result.output


# --- CLI parity against the prototype (issue #212) -------------------------------------

FIXTURES = Path(__file__).resolve().parents[2] / "tests" / "contract-fixtures"
DATA = Path(__file__).parent / "data"


def test_validate_matches_the_prototype_exactly_on_every_recorded_case() -> None:
    """The issue's own Observable, made durable: every case in
    cli_validate_parity.json was captured from the REAL DNA-Entropy-Genbank prototype
    (commit 8026bf5c4afbe3021c1f7e79a17a93af4eaad84b) and must still match exactly,
    exit code and text both -- including the genbank_file_is_not_genbank_aware case,
    which is NOT a success case (see that case's own `note`: `validate` has never been
    format-aware, in the prototype or here, and this fixture documents that rather than
    silently fixing it as part of a parity task)."""
    cases = json.loads((FIXTURES / "cli_validate_parity.json").read_text(encoding="utf-8"))["cases"]
    assert len(cases) == 5
    for case in cases:
        # The fixture's args were captured relative to the PROTOTYPE's own repo root
        # ("tests/data/sample.gb"); translate the one file-based case to this repo's
        # equivalent absolute path rather than depending on pytest's own CWD.
        args = [str(DATA / "sample.gb") if a == "tests/data/sample.gb" else a for a in case["args"]]
        result = runner.invoke(app, args, input=case["stdin"])
        assert result.exit_code == case["exit_code"], f"{case['id']}: {result.output!r}"
        assert result.output == case["output"], case["id"]


# --- exhaustive flag coverage (issue #212): FEATURES.md section 8.1 plus everything ----
# added since (windowing/direction/ambiguity/tsv). "An earlier audit flagged that nobody
# had checked the list exhaustively" -- this section is that check, both structurally
# (every declared option has a name on this list, nothing silently added or removed) and
# behaviorally (each flag's actual effect, not just that it parses).


def _declared_option_names(command_name: str) -> set[str]:
    click_app = typer.main.get_command(app)
    cmd = click_app.commands[command_name]
    names: set[str] = set()
    for param in cmd.params:
        names.update(param.opts)
        names.update(getattr(param, "secondary_opts", None) or [])
    return names


def test_run_declares_exactly_the_expected_option_surface() -> None:
    # FEATURES.md section 8.1's original list (--input/-i, --name, --informat,
    # --predictor, --model, --device, --out/-o, --format, --start, --max-len, --rna,
    # --genes/--no-genes, --seed) plus everything added since: --max-total-len and
    # --context-length/-k (windowing, issue #279), --direction (issue #279),
    # --ambiguity (issue #249), --tsv/--no-tsv (issue #281).
    expected = {
        "--input",
        "-i",
        "--name",
        "--informat",
        "--predictor",
        "--model",
        "--device",
        "--out",
        "-o",
        "--format",
        "--start",
        "--max-len",
        "--max-total-len",
        "--context-length",
        "-k",
        "--direction",
        "--rna",
        "--ambiguity",
        "--genes",
        "--no-genes",
        "--tsv",
        "--no-tsv",
        "--seed",
    }
    assert _declared_option_names("run") == expected


def test_validate_declares_exactly_the_expected_option_surface() -> None:
    expected = {"--input", "-i", "--rna", "--max-len"}
    assert _declared_option_names("validate") == expected


def test_start_flag_shifts_the_reported_track_coordinate(tmp_path) -> None:
    out_dir = tmp_path / "out"
    result = runner.invoke(
        app,
        ["run", "--name", "clistart", "--out", str(out_dir), "--start", "1001", "--format", "wig"],
        input="ACGT" * 30,
    )
    assert result.exit_code == 0, result.output
    wig = (out_dir / "clistart" / "clistart.entropy.wig").read_text(encoding="utf-8")
    assert "start=1001" in wig


def test_format_flag_selects_bedgraph_vs_wig_output_file(tmp_path) -> None:
    out_dir = tmp_path / "out"
    result = runner.invoke(
        app, ["run", "--name", "clifmt", "--out", str(out_dir), "--format", "wig"], input="ACGT" * 30
    )
    assert result.exit_code == 0, result.output
    written = {p.name for p in (out_dir / "clifmt").iterdir()}
    assert any(n.endswith(".wig") for n in written)
    assert not any(n.endswith(".bedgraph") for n in written)


def test_hostile_name_with_traversal_never_writes_outside_the_out_folder(tmp_path) -> None:
    # Issue #366 (MEASURED 2026-09-19): before the fix, --name '../../evil' escaped the
    # configured --out folder entirely. This is the real, end-to-end CLI reproduction.
    out_dir = tmp_path / "configured_out"
    result = runner.invoke(app, ["run", "--name", "../../evil", "--out", str(out_dir)], input="ACGT" * 30)
    assert result.exit_code == 0, result.output
    # Every written file must be somewhere inside out_dir.
    written = list(out_dir.rglob("*"))
    assert written, "expected at least one output file"
    for p in written:
        if p.is_file():
            assert p.resolve().is_relative_to(out_dir.resolve()), p
    # And nothing with the hostile literal name landed as a sibling of out_dir.
    assert not any(tmp_path.glob("evil*"))


def test_hostile_name_that_is_only_traversal_is_a_clean_error(tmp_path) -> None:
    out_dir = tmp_path / "out"
    result = runner.invoke(app, ["run", "--name", "../..", "--out", str(out_dir)], input="ACGT" * 30)
    assert result.exit_code != 0
    assert "ERROR" in result.output
    assert not out_dir.exists() or not any(out_dir.rglob("*"))


def test_reserved_device_name_writes_a_usable_run_not_a_crash(tmp_path) -> None:
    out_dir = tmp_path / "out"
    result = runner.invoke(app, ["run", "--name", "CON", "--out", str(out_dir)], input="ACGT" * 30)
    assert result.exit_code == 0, result.output
    assert any(out_dir.rglob("*.fasta"))


def test_seed_flag_makes_the_mock_predictor_reproducible(tmp_path) -> None:
    seq = "ACGT" * 30
    out_a = tmp_path / "a"
    out_b = tmp_path / "b"
    for out_dir, name in ((out_a, "seeda"), (out_b, "seedb")):
        r = runner.invoke(app, ["run", "--name", name, "--out", str(out_dir), "--seed", "7"], input=seq)
        assert r.exit_code == 0, r.output
    a = (out_a / "seeda" / "seeda.entropy.bedgraph").read_text(encoding="utf-8")
    b = (out_b / "seedb" / "seedb.entropy.bedgraph").read_text(encoding="utf-8")
    # Same seed, same sequence -> byte-identical entropy track (modulo the track name).
    assert a.replace("seeda", "X") == b.replace("seedb", "X")


def test_direction_flag_is_echoed_in_the_summary_line(tmp_path) -> None:
    out_dir = tmp_path / "out"
    result = runner.invoke(
        app,
        [
            "run",
            "--name",
            "clidir",
            "--out",
            str(out_dir),
            "--direction",
            "forward-only",
            "--context-length",
            "128",
        ],
        input="ACGT" * 60,
    )
    assert result.exit_code == 0, result.output
    assert "direction=forward-only" in result.output
    assert "K=128" in result.output


def test_genes_flag_reports_a_gene_count_in_the_summary(tmp_path) -> None:
    out_dir = tmp_path / "out"
    result = runner.invoke(
        app,
        ["run", "--name", "cligenes", "--out", str(out_dir), "--genes"],
        input="ATGAAACGTATTTTTAAACCCGGGTAA" * 10,
    )
    assert result.exit_code == 0, result.output
    # Whether or not Prodigal actually finds a gene in this synthetic sequence, the
    # summary must at least not have crashed asking for one; a real gene count line only
    # appears when genes were found, which this loose sequence may or may not produce --
    # the flag's own wiring (not the annotator's accuracy) is what this test guards.
    written = {p.name for p in (out_dir / "cligenes").iterdir()}
    assert any(n.endswith(".genes.gff3") for n in written)


def test_rna_flag_on_run_converts_u_to_t(tmp_path) -> None:
    out_dir = tmp_path / "out"
    result = runner.invoke(
        app, ["run", "--name", "clirna", "--out", str(out_dir), "--rna"], input="ACGU" * 10
    )
    assert result.exit_code == 0, result.output
    fasta = (out_dir / "clirna" / "clirna.fasta").read_text(encoding="utf-8")
    assert "U" not in fasta.split("\n", 1)[1]  # header line aside, no U survives in the sequence body


def test_informat_flag_forces_paste_even_with_a_gb_like_name(tmp_path) -> None:
    """--informat overrides extension/content sniffing (FEATURES.md 8.1)."""
    src = tmp_path / "weird.gb"
    src.write_text("ACGT" * 30, encoding="utf-8")  # not real GenBank text, forced as paste anyway
    out_dir = tmp_path / "out"
    result = runner.invoke(
        app,
        ["run", "--name", "cliinformat", "--out", str(out_dir), "-i", str(src), "--informat", "paste"],
    )
    assert result.exit_code == 0, result.output


def test_validate_rna_flag_converts_u_to_t() -> None:
    result = runner.invoke(app, ["validate", "--rna"], input="ACGUACGUACGUACGU\n")
    assert result.exit_code == 0, result.output
    assert "Converted" in result.output and "U->T" in result.output


def test_validate_max_len_flag_rejects_an_oversized_sequence() -> None:
    result = runner.invoke(app, ["validate", "--max-len", "10"], input="ACGT" * 20)
    assert result.exit_code == 1
    assert "exceeds" in result.output
