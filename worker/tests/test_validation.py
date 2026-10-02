"""Tests for sequence normalization and validation (docs/DESIGN.md §5)."""

from __future__ import annotations

import pytest

from dna_entropy.validation import (
    ValidatedSequence,
    ValidationError,
    validate_sequence,
)


def test_plain_valid_sequence() -> None:
    result = validate_sequence("ATGCATGCAT")
    assert isinstance(result, ValidatedSequence)
    assert result.seq == "ATGCATGCAT"


def test_lowercase_is_uppercased() -> None:
    assert validate_sequence("atgcatgcat").seq == "ATGCATGCAT"


def test_whitespace_and_newlines_removed() -> None:
    assert validate_sequence("AT GC\nAC GT\tAA\r\nCC").seq == "ATGCACGTAACC"


def test_digits_removed_with_notice() -> None:
    result = validate_sequence("1 ATGCATGC 60\n61 ACGTACGT 120")
    assert result.seq == "ATGCATGCACGTACGT"
    assert any("digit" in n.lower() for n in result.notices)


def test_fasta_header_stripped_with_notice() -> None:
    result = validate_sequence(">seq1 some description\nATGCATGCAT")
    assert result.seq == "ATGCATGCAT"
    assert any("header" in n.lower() for n in result.notices)


def test_blank_lines_before_header_ok() -> None:
    result = validate_sequence("\n\n>hdr\nATGCATGCAT")
    assert result.seq == "ATGCATGCAT"


def test_invalid_char_reports_first_position() -> None:
    with pytest.raises(ValidationError) as exc:
        validate_sequence("ATGBCATGCA")  # 'B' at index 3 -> position 4
    msg = str(exc.value)
    assert "position 4" in msg
    assert "B" in msg


def test_invalid_char_reports_total_count() -> None:
    with pytest.raises(ValidationError) as exc:
        validate_sequence("ATXGCXTGCX")  # three 'X'
    assert "3 non-ACGT" in str(exc.value)


def test_replacement_character_gives_an_encoding_diagnosis_not_a_generic_invalid_char() -> None:
    """#348: a literal U+FFFD (the Unicode decode-failure replacement character) means
    the file was not valid UTF-8, not that the biologist typed a wrong base. The error
    must say so and name an action, distinctly from an ordinary typo like 'B' or 'X'."""
    with pytest.raises(ValidationError) as exc:
        validate_sequence("ACGTACGT�ACGTACGT")  # position 9
    msg = str(exc.value)
    assert "position 9" in msg
    assert "utf-8" in msg.lower()
    assert "invalid character" not in msg.lower()  # distinct wording from a real typo


def test_replacement_character_reports_the_total_count() -> None:
    with pytest.raises(ValidationError) as exc:
        validate_sequence("A�C�G�T")
    assert "3" in str(exc.value)


def test_ambiguity_code_gives_hint() -> None:
    with pytest.raises(ValidationError) as exc:
        validate_sequence("ATGCNATGCA")  # 'N' is IUPAC ambiguity
    assert "ambiguity" in str(exc.value).lower()


def test_rna_without_flag_is_rejected() -> None:
    with pytest.raises(ValidationError) as exc:
        validate_sequence("AUGCAUGCAU")
    assert "rna" in str(exc.value).lower()


def test_rna_with_flag_is_converted() -> None:
    result = validate_sequence("AUGCAUGCAU", rna=True)
    assert result.seq == "ATGCATGCAT"
    assert any("U->T" in n for n in result.notices)


def test_empty_after_cleaning_is_rejected() -> None:
    with pytest.raises(ValidationError):
        validate_sequence("   \n  123  \n  ")


def test_header_only_is_rejected_as_empty() -> None:
    with pytest.raises(ValidationError):
        validate_sequence(">only a header\n")


def test_over_max_len_is_rejected() -> None:
    with pytest.raises(ValidationError) as exc:
        validate_sequence("A" * 100, max_len=50)
    assert "exceeds" in str(exc.value)


def test_short_sequence_warns_but_passes() -> None:
    result = validate_sequence("ATG", min_len=10)
    assert result.seq == "ATG"
    assert any("short" in n.lower() for n in result.notices)


def test_clean_sequence_has_no_notices() -> None:
    assert validate_sequence("ATGCATGCATGC").notices == []


# --- issue #468: a non-ASCII letter is refused, never case-folded into an ASCII one ------


@pytest.mark.parametrize(
    ("letter", "codepoint"),
    [
        ("\u00df", "U+00DF"),  # sharp s: str.upper() gives "SS", two valid IUPAC codes
        ("\u017f", "U+017F"),  # long s: str.upper() gives "S", a valid IUPAC code
        ("\ufb01", "U+FB01"),  # the "fi" ligature: str.upper() gives "FI"
        ("\u0131", "U+0131"),  # dotless i: str.upper() gives "I"
        ("\u212a", "U+212A"),  # Kelvin sign: already uppercase, lowercases to "k"
    ],
)
@pytest.mark.parametrize("policy", ["error", "keep", "mask"])
def test_non_ascii_letter_is_refused_naming_it_and_its_position(
    letter: str, codepoint: str, policy: str
) -> None:
    with pytest.raises(ValidationError) as exc:
        validate_sequence("ACGT" + letter + "ACGT", ambiguity_policy=policy)
    message = str(exc.value)
    assert codepoint in message
    assert "position 5" in message  # not shifted by a lengthened sequence
    assert message.isascii()  # Hard Rule 5: the console never sees a glyph


def test_issue_468_reproduction_string_is_refused() -> None:
    with pytest.raises(ValidationError, match="position 5"):
        validate_sequence("ACGT" + "\u00df" + "ACGT" + "\u017f" + "ACGT", ambiguity_policy="keep")


def test_ascii_lowercase_is_still_uppercased_including_iupac_codes() -> None:
    result = validate_sequence("acgtnrysw", ambiguity_policy="keep")
    assert result.seq == "ACGTNRYSW"
