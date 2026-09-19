"""Normalize and validate a raw pasted sequence into clean A/C/G/T.

Runs *before* any predictor (CLAUDE.md hard rule #2). Fails fast with messages that
point at the exact problem; non-fatal observations come back as ``notices``.

Rules are specified in docs/DESIGN.md §5.
"""

from __future__ import annotations

import re
from dataclasses import dataclass, field

from ..config import DEFAULT_MAX_LEN, AmbiguityPolicy
from ..redact import describe_len

_ACGT: frozenset[str] = frozenset("ACGT")
# issue #348: readers/encoding.py's own fallback for a byte that isn't valid UTF-8
# turns it into exactly this one character (Unicode's own "could not decode" signal) --
# never something a biologist typed themselves, so its presence is a reliable signal
# that the real problem is the file's encoding, not its content.
_REPLACEMENT_CHAR = "�"
# The 11 single-letter IUPAC nucleotide ambiguity codes (issue #249): distinguished from a
# genuinely invalid character so `ambiguity_policy` (keep/mask/error) can apply to them
# specifically, rather than treating "not ACGT" as one undifferentiated error bucket.
_AMBIGUITY: frozenset[str] = frozenset("NRYSWKMBDHV")

# Below this length, entropy is dominated by the model's prior near the start; warn only.
DEFAULT_MIN_LEN: int = 10


class ValidationError(ValueError):
    """Raised when input cannot be turned into a valid A/C/G/T sequence."""


@dataclass
class ValidatedSequence:
    """A clean, model-ready sequence plus any non-fatal notices for the user."""

    seq: str
    notices: list[str] = field(default_factory=list)

    def __len__(self) -> int:
        return len(self.seq)


def _strip_leading_header(raw: str) -> tuple[str, list[str]]:
    """Drop a single leading FASTA-style header line (``>...``) if present."""
    notices: list[str] = []
    lines = raw.splitlines()
    for idx, line in enumerate(lines):
        if line.strip() == "":
            continue  # skip blank lines before the first content line
        if line.lstrip().startswith(">"):
            # Never the header text itself (issue #253) — just that one was dropped, and
            # how long it was.
            notices.append(f"Ignored a leading FASTA header line ({describe_len(line.strip())}).")
            del lines[idx]
        break
    return "\n".join(lines), notices


def _normalize(text: str) -> tuple[str, list[str]]:
    """Remove whitespace and digits (e.g. line numbers); uppercase. No U->T here."""
    notices: list[str] = []
    no_ws = re.sub(r"\s", "", text)
    n_digits = sum(c.isdigit() for c in no_ws)
    if n_digits:
        notices.append(f"Removed {n_digits} digit character(s) (e.g. line numbers).")
    seq = re.sub(r"\d", "", no_ws).upper()
    return seq, notices


def validate_sequence(
    raw: str,
    *,
    max_len: int = DEFAULT_MAX_LEN,
    rna: bool = False,
    min_len: int = DEFAULT_MIN_LEN,
    ambiguity_policy: AmbiguityPolicy | str = AmbiguityPolicy.ERROR,
) -> ValidatedSequence:
    """Clean and validate ``raw`` into a :class:`ValidatedSequence`.

    Args:
        raw: raw pasted text (may contain a header, whitespace, line numbers).
        max_len: single-pass context cap; longer sequences are rejected.
        rna: if True, convert ``U`` -> ``T`` instead of rejecting RNA input.
        min_len: warn (do not fail) below this length.
        ambiguity_policy: what to do with an IUPAC ambiguity code (``N``, ``R``, ...) —
            see :class:`~dna_entropy.config.AmbiguityPolicy` and
            docs/science_and_formats.md for what each policy does to the entropy at that
            position (issue #249). This function's own default is the strict one
            (``ERROR``); ``RunConfig.ambiguity_policy`` (``KEEP`` by default) is the one
            that actually reaches a real run — every caller here passes its own choice
            explicitly rather than relying on this default.

    Raises:
        ValidationError: on RNA without ``rna=True``, empty input, a non-ACGT
            non-ambiguity-code character (always an error, regardless of policy — that is
            not an ambiguity question), an ambiguity code under ``ERROR`` policy, or
            length over ``max_len``.
    """
    try:
        ambiguity_policy = AmbiguityPolicy(ambiguity_policy)
    except ValueError as exc:
        valid = sorted(p.value for p in AmbiguityPolicy)
        raise ValidationError(f"ambiguity_policy {ambiguity_policy!r} is not one of {valid}") from exc
    notices: list[str] = []

    text, n = _strip_leading_header(raw)
    notices += n
    seq, n = _normalize(text)
    notices += n

    # RNA handling must come before the strict A/C/G/T check, since U is not in ACGT.
    if "U" in seq:
        if rna:
            count = seq.count("U")
            seq = seq.replace("U", "T")
            notices.append(f"Converted {count} U->T (RNA input).")
        else:
            pos = seq.index("U") + 1
            raise ValidationError(
                f"Found 'U' at position {pos}: this looks like RNA. "
                "Re-run with --rna to convert U->T, or paste a DNA sequence."
            )

    if not seq:
        raise ValidationError("No nucleotides found after cleaning the input (empty sequence).")

    bad = [i for i, c in enumerate(seq) if c not in _ACGT]
    if bad:
        non_iupac = [i for i in bad if seq[i] not in _AMBIGUITY]
        if non_iupac:
            # issue #348: a replacement character means the underlying bytes were never
            # valid UTF-8 in the first place (readers/encoding.py's own errors="replace"
            # fallback produced it) — that is an encoding problem, not a wrong base, and
            # deserves a distinct diagnosis naming the real cause and the real fix,
            # rather than being lumped in with a genuine typo like 'B' or 'X'.
            n_replacement = sum(1 for i in bad if seq[i] == _REPLACEMENT_CHAR)
            if n_replacement:
                i = next(i for i in bad if seq[i] == _REPLACEMENT_CHAR)
                raise ValidationError(
                    f"Found {n_replacement} character(s) that could not be decoded as "
                    f"text (position {i + 1} is the first), which usually means the file "
                    "was not saved as UTF-8 (e.g. Windows-1252 or another codepage). "
                    "Re-save the file with UTF-8 encoding and try again."
                )
            # A character that is neither A/C/G/T NOR a recognized IUPAC ambiguity code
            # is always an error, whatever ambiguity_policy says — that is a genuinely
            # invalid character, not an ambiguity question.
            i = non_iupac[0]
            c = seq[i]
            raise ValidationError(
                f"Invalid character {c!r} at position {i + 1} "
                f"({len(bad)} non-ACGT character(s) total). Only A, C, G, T are allowed."
            )

        # Every offending char IS a recognized IUPAC ambiguity code from here on — which
        # of the three policies applies (issue #249; docs/science_and_formats.md explains
        # what each does to the entropy numbers at these positions):
        codes = sorted({seq[i] for i in bad})
        if ambiguity_policy is AmbiguityPolicy.ERROR:
            i = bad[0]
            c = seq[i]
            raise ValidationError(
                f"Ambiguity code {c!r} at position {i + 1} "
                f"({len(bad)} total: {', '.join(codes)}). This run's ambiguity policy is "
                "'error' (refuse). Choose 'keep' or 'mask' to run anyway, or clean the "
                "input to plain A/C/G/T."
            )
        if ambiguity_policy is AmbiguityPolicy.MASK:
            seq = "".join("N" if c not in _ACGT else c for c in seq)
            notices.append(
                f"Masked {len(bad)} ambiguity code(s) ({', '.join(codes)}) to 'N'; entropy "
                "at those positions reflects the model's prediction for 'N', not the "
                "original code (docs/science_and_formats.md)."
            )
        else:  # AmbiguityPolicy.KEEP
            notices.append(
                f"Kept {len(bad)} ambiguity code(s) ({', '.join(codes)}); entropy at those "
                "positions reflects the model's prediction for that exact code, not a "
                "definite base (docs/science_and_formats.md)."
            )

    if len(seq) > max_len:
        raise ValidationError(
            f"Sequence length {len(seq)} exceeds the single-pass cap of {max_len} nt. "
            "Longer loci need windowing (future work); raise --max-len only if the GPU "
            "has the VRAM."
        )

    if len(seq) < min_len:
        notices.append(
            f"Warning: sequence is short ({len(seq)} nt); entropy near the start is "
            "dominated by the model's prior."
        )

    return ValidatedSequence(seq=seq, notices=notices)
