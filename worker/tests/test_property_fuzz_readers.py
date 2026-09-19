"""Issue #368 (parent #162): Hypothesis property-based fuzzing of the GenBank and FASTA
readers -- the property-based half `test_fuzz_readers.py`'s own docstring says is missing
from that file's corpus-driven fuzz test (`hypothesis` was not installed when it was
written).

The property under test matches `test_fuzz_readers.py`'s own bar exactly, generalized
from a fixed corpus to a generated one: every input either parses to a valid, non-empty
record set, or raises one of the three clean, registered exception types
(`FastaReadError` / `GenBankReadError` / `ValidationError`) -- never any other exception
(a raw `UnicodeDecodeError`, a bare Biopython `ValueError` subtype not wrapped, an
`IndexError`, an `AttributeError`, ...), and never a silently truncated or `None`-ish
record.

Two generation strategies, both named in #368's own body:

1. Arbitrary bytes (`st.binary()`) -- the "does this crash on genuine garbage" case
   `test_fuzz_readers.py`'s hand-picked corpus already covers a sample of; generated here
   over a much larger space.
2. Mutated real files (`mutated_bytes()`, a Hypothesis `@st.composite` strategy) -- byte
   flips, deletions, insertions, truncations and encoding swaps applied to this repo's own
   valid `sample.fasta`/`sample.gb` fixtures. This is the shape #368 asks for explicitly
   ("mutation of a real fixture") because a file that is ALMOST valid, corrupted in one
   place, exercises a different code path than pure noise -- pure noise almost always fails
   at the very first line, while a mutated real file can get deep into feature-table or
   multi-line-sequence parsing before it breaks.

Corpus-and-.gitattributes trap (checked, not re-created): this file writes its generated
bytes to a fresh `tempfile.mkstemp` path per example and never adds anything under
`worker/tests/data/`, so the CRLF-normalization trap that `test_fuzz_readers.py` and
`.gitattributes` already carry a fix for (`worker/tests/data/malformed/** -text`) does not
apply here -- there is no new corpus file for git to normalize. If a Hypothesis-found
failure below is promoted to a permanent fixture (per #368's own "Done when" convention:
`@example` plus a `worker/tests/data/malformed/` fixture and a regression test), whoever
adds it must extend the SAME `-text` glob or repeat the CRLF-stripping bug for the new
file; noting this here since this file does not add one itself.
"""

from __future__ import annotations

import contextlib
import os
import tempfile
from pathlib import Path

from hypothesis import HealthCheck, given, settings
from hypothesis import strategies as st

from dna_entropy.readers.fasta import FastaReadError, read_fasta
from dna_entropy.readers.genbank import GenBankReadError, read_genbank
from dna_entropy.validation.validators import ValidationError, validate_sequence

DATA_DIR = Path(__file__).parent / "data"
_REAL_FASTA = (DATA_DIR / "sample.fasta").read_bytes()
_REAL_GENBANK = (DATA_DIR / "sample.gb").read_bytes()

# Explicit, visible budget -- never the Hypothesis default. Each example writes a temp
# file and drives a real (Biopython, for GenBank) parse, which is heavier than a pure
# in-memory property, so this stays modest on a box running six concurrent agents tonight.
LANE_F_FUZZ_SETTINGS = settings(
    max_examples=150,
    deadline=None,
    suppress_health_check=[HealthCheck.too_slow, HealthCheck.data_too_large],
)


@st.composite
def mutated_bytes(draw: st.DrawFn, original: bytes) -> bytes:
    """Apply 1-8 random mutations (byte flip, delete, insert, truncate, null-out, or an
    encoding swap) to a copy of `original`. Generalizes #368's own suggested corpus-mutation
    approach (byte flips, truncation, encoding swaps) into one composite strategy so
    Hypothesis can shrink a failure down to the smallest mutation set that still
    reproduces it."""
    b = bytearray(original)
    n_mutations = draw(st.integers(min_value=1, max_value=8))
    for _ in range(n_mutations):
        if not b:
            break
        op = draw(st.sampled_from(["flip", "delete", "insert", "truncate", "nullify", "reencode"]))
        if op == "flip":
            idx = draw(st.integers(min_value=0, max_value=len(b) - 1))
            b[idx] = draw(st.integers(min_value=0, max_value=255))
        elif op == "delete":
            idx = draw(st.integers(min_value=0, max_value=len(b) - 1))
            del b[idx]
        elif op == "insert":
            idx = draw(st.integers(min_value=0, max_value=len(b)))
            b.insert(idx, draw(st.integers(min_value=0, max_value=255)))
        elif op == "truncate":
            cut = draw(st.integers(min_value=0, max_value=len(b)))
            b = bytearray(b[:cut])
        elif op == "nullify":
            idx = draw(st.integers(min_value=0, max_value=len(b) - 1))
            b[idx] = 0
        elif op == "reencode":
            enc = draw(st.sampled_from(["utf-16", "utf-16-le", "utf-16-be", "cp1252", "latin-1"]))
            text = bytes(b).decode("utf-8", errors="ignore")
            b = bytearray(text.encode(enc, errors="ignore"))
    return bytes(b)


def _drive_reader(read_fn, error_cls, data: bytes, suffix: str) -> None:
    """Write `data` to a fresh temp file and run it through `read_fn` + `validate_sequence`,
    asserting the clean-failure-or-clean-success bar. Uses `tempfile.mkstemp` (not the
    `tmp_path` pytest fixture) deliberately: a function-scoped fixture reused across many
    Hypothesis examples inside one test invocation trips Hypothesis's own
    `HealthCheck.function_scoped_fixture` and produces a warning-worthy anti-pattern; a
    fresh temp file per example, cleaned up in `finally`, has no such hazard."""
    fd, path = tempfile.mkstemp(suffix=suffix)
    try:
        with os.fdopen(fd, "wb") as fh:
            fh.write(data)
        try:
            records, _notices = read_fn(path)
        except (error_cls, ValidationError):
            return  # a clean, actionable failure is a PASS
        except Exception as exc:  # noqa: BLE001 - the whole point is "catch anything else"
            raise AssertionError(
                f"{suffix} reader raised a raw {type(exc).__name__} on {data!r}: {exc}"
            ) from exc
        # A clean success must be a genuine, non-empty record set -- never a silent
        # truncation, an empty list masquerading as success, or a record with a None/empty
        # sequence (both readers already filter these internally and raise instead; this
        # re-asserts that guarantee holds for a GENERATED input too, not just the corpus).
        assert records, f"{suffix} reader returned an empty record list without raising"
        for rec in records:
            assert rec.seq is not None
            assert isinstance(rec.seq, str)
            assert len(rec.seq) > 0, f"{suffix} reader returned a record with an empty sequence"
            with contextlib.suppress(ValidationError):
                validate_sequence(rec.seq, ambiguity_policy="keep")  # a clean failure is also a PASS
    finally:
        os.remove(path)


# ---------------------------------------------------------------------------------------
# Arbitrary bytes: the pure-noise case.
# ---------------------------------------------------------------------------------------


@given(data=st.binary(min_size=0, max_size=2000))
@LANE_F_FUZZ_SETTINGS
def test_property_fasta_reader_on_arbitrary_bytes_never_raises_a_raw_exception(data: bytes) -> None:
    _drive_reader(read_fasta, FastaReadError, data, ".fasta")


@given(data=st.binary(min_size=0, max_size=2000))
@LANE_F_FUZZ_SETTINGS
def test_property_genbank_reader_on_arbitrary_bytes_never_raises_a_raw_exception(data: bytes) -> None:
    _drive_reader(read_genbank, GenBankReadError, data, ".gb")


# ---------------------------------------------------------------------------------------
# Mutated real files: the almost-valid case #368 specifically asks for.
# ---------------------------------------------------------------------------------------


@given(data=mutated_bytes(_REAL_FASTA))
@LANE_F_FUZZ_SETTINGS
def test_property_fasta_reader_on_a_mutated_valid_file_never_raises_a_raw_exception(data: bytes) -> None:
    _drive_reader(read_fasta, FastaReadError, data, ".fasta")


@given(data=mutated_bytes(_REAL_GENBANK))
@LANE_F_FUZZ_SETTINGS
def test_property_genbank_reader_on_a_mutated_valid_file_never_raises_a_raw_exception(data: bytes) -> None:
    _drive_reader(read_genbank, GenBankReadError, data, ".gb")
