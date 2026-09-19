"""Issue #162: fuzz the GenBank and FASTA readers with malformed files.

`hypothesis` is not installed in `worker\\.venv` and this round's brief says not to
install anything, so this is the corpus-driven version named as the fallback in that
brief: a directory of deliberately broken fixtures (`worker/tests/data/malformed/`)
plus a parametrized test, one case per fixture. The Hypothesis (property-based) version
is filed as its own follow-up issue rather than half-done here.

The bar for "done" (from the issue and this round's brief): every malformed input
produces either a clean `ValidationError`/`GenBankReadError`/`FastaReadError` naming one
action, or a clean success -- and never anything else (a raw `UnicodeDecodeError`, a
bare Biopython `ValueError`, an `IndexError`, an `AttributeError`, ...). A fixture that
succeeds cleanly is just as much a pass as one that fails cleanly; the only failure mode
this test looks for is an exception type outside the three clean ones.
"""

from __future__ import annotations

from pathlib import Path

import pytest

from dna_entropy.readers.fasta import FastaReadError, read_fasta
from dna_entropy.readers.genbank import GenBankReadError, read_genbank
from dna_entropy.validation.validators import ValidationError, validate_sequence

MALFORMED_DIR = Path(__file__).parent / "data" / "malformed"
FASTA_FIXTURES = sorted(MALFORMED_DIR.glob("fasta_*"))
GENBANK_FIXTURES = sorted(MALFORMED_DIR.glob("genbank_*"))


def test_malformed_corpus_is_not_empty() -> None:
    """Vacuity guard (fixing-a-bug skill): a fuzz test that silently collects zero
    fixtures (a typo'd glob, a directory that failed to ship) reports a clean tree
    while testing nothing at all."""
    assert len(FASTA_FIXTURES) >= 10, f"expected a real corpus, found {FASTA_FIXTURES}"
    assert len(GENBANK_FIXTURES) >= 10, f"expected a real corpus, found {GENBANK_FIXTURES}"


@pytest.mark.parametrize("path", FASTA_FIXTURES, ids=lambda p: p.name)
def test_fuzz_fasta_reader_never_raises_a_raw_exception(path: Path) -> None:
    try:
        records, notices = read_fasta(str(path))
        for rec in records:
            validate_sequence(rec.seq, ambiguity_policy="keep")
    except (FastaReadError, ValidationError):
        pass  # a clean, actionable failure is a PASS for a fuzz test
    except Exception as exc:  # noqa: BLE001 - the whole point is "catch anything else"
        pytest.fail(f"{path.name} raised a raw {type(exc).__name__}: {exc}")


@pytest.mark.parametrize("path", GENBANK_FIXTURES, ids=lambda p: p.name)
def test_fuzz_genbank_reader_never_raises_a_raw_exception(path: Path) -> None:
    try:
        records, notices = read_genbank(str(path))
        for rec in records:
            validate_sequence(rec.seq, ambiguity_policy="keep")
    except (GenBankReadError, ValidationError):
        pass
    except Exception as exc:  # noqa: BLE001
        pytest.fail(f"{path.name} raised a raw {type(exc).__name__}: {exc}")


# ---------------------------------------------------------------------------
# The corpus has to survive the round trip through git, or it is testing
# something other than what it says.
#
# MEASURED 2026-09-19: `.gitattributes` declared `*.fasta text` with
# `eol=lf`, so `fasta_crlf_and_lonecr_mixed.fasta` was committed with its
# CRLFs already rewritten to LF. The test suite passed either way, because it
# ran against the working tree, where the bytes were still right. A fresh
# clone would have got a different file and tested a different thing, and
# nothing would have said so.
#
# These two assert the bytes that give three fixtures their whole reason to
# exist. They fail if that normalization ever comes back.
# ---------------------------------------------------------------------------


def test_the_crlf_fixture_still_contains_crlf() -> None:
    raw = (MALFORMED_DIR / "fasta_crlf_and_lonecr_mixed.fasta").read_bytes()
    assert b"\r\n" in raw, (
        "the CRLF fixture has been normalized to LF, so it no longer tests mixed line "
        "endings -- check .gitattributes for a `text`/`eol=lf` rule covering "
        "worker/tests/data/malformed/"
    )
    assert b"\rT" in raw, "the lone-CR part of the mixed-line-ending fixture is gone"


def test_the_byte_level_fixtures_still_contain_their_bytes() -> None:
    assert b"\x00" in (MALFORMED_DIR / "fasta_null_bytes.fasta").read_bytes()
    assert (MALFORMED_DIR / "fasta_utf16_truncated.fasta").read_bytes()[:2] in (
        b"\xff\xfe",
        b"\xfe\xff",
    ), "the UTF-16 fixture has lost its byte-order mark"
    assert b"\r" in (MALFORMED_DIR / "genbank_lone_cr.gb").read_bytes()
