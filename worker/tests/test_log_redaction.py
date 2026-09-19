"""Log redaction guard (issue #253).

The product's privacy claim (``docs/threat_model.md``: "Worker logs never contain
sequence content, file names, or the user's email; only ids, stage names, and error
classes") only holds if nothing a user typed or pasted — a FASTA/GenBank header, a pasted
sequence, an original file name — ever reaches ``progress.jsonl``/``status.json``, both of
which leave the VM in the job's own bucket (docs/job_contract.md §4-5) and are exactly
what gets pasted into a support issue (issue #106's diagnostics bundle).

This is a MECHANICAL guard, not a convention: every test here plants a distinctive marker
string in a piece of user content that historically DID leak (before this issue's fix),
runs the real code path, and greps the real emitted artifact for the marker. A future
regression that reintroduces ``f"...{header!r}..."`` (or similar) fails one of these, not
a code-review guess.
"""

from __future__ import annotations

import json
from pathlib import Path

from dna_entropy.readers.fasta import read_fasta
from dna_entropy.readers.genbank import read_genbank
from dna_entropy.redact import describe_len, fingerprint
from dna_entropy.validation.validators import validate_sequence
from dna_entropy.worker.blobstore import LocalBlobstore

MARKER = "ZzMARKER9x_do_not_log_me_TopSecretSample"


# --- unit level: each notice-producing reader/validator, in isolation -----------------


def test_fasta_empty_record_notice_never_contains_the_header_text(tmp_path: Path) -> None:
    p = tmp_path / "in.fasta"
    p.write_text(f">has_seq\nACGT\n>{MARKER}\n>also_has_seq\nTTTT\n", encoding="utf-8")
    _records, notices = read_fasta(str(p))
    joined = "\n".join(notices)
    assert MARKER not in joined
    assert "Skipped FASTA record 2" in joined  # still says WHICH record, just not its text


def test_fasta_duplicate_header_notice_never_contains_the_header_text(tmp_path: Path) -> None:
    p = tmp_path / "dupes.fasta"
    p.write_text(f">{MARKER}\nACGT\n>{MARKER}\nTTTT\n", encoding="utf-8")
    _records, notices = read_fasta(str(p))
    joined = "\n".join(notices)
    assert MARKER not in joined
    assert "repeat across records" in joined
    assert fingerprint(MARKER) in joined  # correlatable without being reversible


def test_genbank_skipped_record_notice_never_contains_the_record_id(tmp_path: Path) -> None:
    p = tmp_path / "in.gb"
    p.write_text(
        f"LOCUS       {MARKER}    0 bp    DNA\nORIGIN\n//\n"
        "LOCUS       has_seq    4 bp    DNA\nORIGIN\n        1 acgt\n//\n",
        encoding="utf-8",
    )
    _records, notices = read_genbank(str(p))
    joined = "\n".join(notices)
    assert MARKER not in joined
    assert "Skipped GenBank record 1" in joined


def test_paste_leading_header_notice_never_contains_the_header_text() -> None:
    v = validate_sequence(f">{MARKER}\nACGT")
    joined = "\n".join(v.notices)
    assert MARKER not in joined
    assert "Ignored a leading FASTA header line" in joined


# --- redact.py helpers themselves ------------------------------------------------------


def test_describe_len_never_returns_the_text_it_describes() -> None:
    desc = describe_len(MARKER)
    assert MARKER not in desc
    assert str(len(MARKER)) in desc


def test_fingerprint_never_returns_the_text_it_fingerprints() -> None:
    fp = fingerprint(MARKER)
    assert MARKER not in fp
    assert fp != MARKER
    assert len(fp) == 8


def test_fingerprint_is_stable_and_distinguishes_different_text() -> None:
    assert fingerprint(MARKER) == fingerprint(MARKER)
    assert fingerprint(MARKER) != fingerprint(MARKER + "x")


# --- integration level: a full job, every artifact it writes, grepped for the marker --


def _write_manifest_with_two_fasta_inputs(store: LocalBlobstore) -> None:
    manifest = {
        "schema": 1,
        "jobId": "log-redaction-test-job",
        "inputs": [{"id": "in1", "path": "input/locus.fasta", "name": "locus"}],
        "predictor": {"kind": "mock", "seed": 0},
        "analysis": {
            "contextLength": 128,
            "window": 256,
            "stride": 128,
            "direction": "forward-only",
        },
        "outputs": ["fasta", "bedgraph", "tsv"],
        "store": {"kind": "localdir", "root": "unused-by-the-test"},
    }
    store.write_text("manifest.json", json.dumps(manifest))


def test_a_full_job_never_leaks_a_marker_header_into_status_or_progress(tmp_path: Path) -> None:
    """The end-to-end version of the guard: plant the marker as a duplicated FASTA header
    (guaranteed to produce a notice, per the unit test above) in a REAL job run through
    ``run_job()``, then read every byte of ``status.json``/``progress.jsonl`` off disk —
    the same two files ``docs/job_contract.md`` says leave the VM — and assert the marker
    is nowhere in them. ``result.json`` is checked too: its ``detail.inputs`` embeds the
    same notices (runner.py's final ``status.update``)."""
    from dna_entropy.worker.runner import run_job

    store = LocalBlobstore(tmp_path)
    _write_manifest_with_two_fasta_inputs(store)
    store.write_text(
        "input/locus.fasta",
        f">{MARKER}\n{'ACGT' * 20}\n>{MARKER}\n{'ACGT' * 20}\n",
    )

    result = run_job(store)
    assert result.status == "done"  # the job must actually run this path, not error out

    for artifact in ("status.json", "progress.jsonl", "result.json"):
        text = store.read_text(artifact)
        assert MARKER not in text, f"{artifact} leaked the planted marker header"

    # Prove the guard isn't vacuous: the duplicate-header notice DID fire somewhere.
    progress_lines = [json.loads(line) for line in store.read_text("progress.jsonl").splitlines() if line]
    messages = " ".join(p.get("message", "") for p in progress_lines)
    assert "repeat across records" in messages


# --- source-scan: the exact patterns that leaked before this fix must not reappear -----


SRC = Path(__file__).parent.parent / "src" / "dna_entropy"

# Each of these literal snippets is exactly what issue #253 found and fixed (see git
# history on readers/fasta.py, readers/genbank.py, validation/validators.py). If any of
# them reappears anywhere under src/, a header/record-id/pasted-header-line is being
# embedded in a notice again — this must fail loudly, not wait for a marker test to
# happen to cover the new call site.
_FORBIDDEN_SNIPPETS = (
    "{label!r}",
    "{rec.id!r}",
    "{rec_header!r}",
    "{header!r}",
    ".strip()[:60]!r",
)


def test_the_original_leaking_patterns_do_not_reappear_anywhere_in_src() -> None:
    offenders = []
    for path in SRC.rglob("*.py"):
        text = path.read_text(encoding="utf-8")
        for snippet in _FORBIDDEN_SNIPPETS:
            if snippet in text:
                offenders.append(f"{path.relative_to(SRC)}: {snippet!r}")
    assert not offenders, (
        "A pattern that previously leaked user-typed content into a worker log artifact "
        f"has reappeared: {offenders}. Use dna_entropy.redact.describe_len/fingerprint "
        "instead of embedding the raw text."
    )
