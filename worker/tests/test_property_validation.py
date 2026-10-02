"""Issue #160's remaining gap: Hypothesis property tests for ``validate_sequence`` on its
own, with the IUPAC ambiguity alphabet generated (the windowing/direction file only ever
generated A/C/G/T).

Properties:

1. A string of bases mixed with whitespace and digits validates to exactly its bases,
   uppercased, in order, whatever the policy (no ambiguity codes present).
2. With ambiguity codes present the three policies behave as documented: ``error`` refuses
   naming the first code, ``mask`` rewrites every code to ``N`` and leaves A/C/G/T alone,
   ``keep`` leaves the sequence untouched.
3. Arbitrary text either validates or raises ``ValidationError`` and nothing else.
4. A validated sequence is always safe for the reverse pass: only IUPAC letters, and its
   reverse complement is an involution of the same length.
"""

from __future__ import annotations

import pytest
from hypothesis import HealthCheck, given, settings
from hypothesis import strategies as st

from dna_entropy.analysis.direction import reverse_complement
from dna_entropy.config import AmbiguityPolicy
from dna_entropy.validation.validators import ValidationError, validate_sequence

SETTINGS = settings(max_examples=200, deadline=None, suppress_health_check=[HealthCheck.too_slow])

_BASES = "ACGT"
_AMBIGUITY = "NRYSWKMBDHV"
_IUPAC = _BASES + _AMBIGUITY

# Characters that clean input may carry around the bases: whitespace and digits (line numbers).
_NOISE = st.sampled_from(list(" \n\t0123456789"))


def _with_noise(bases: str, noise: list[str]) -> str:
    """Interleave ``noise`` characters between ``bases`` deterministically."""
    out = []
    for i, b in enumerate(bases):
        out.append(b)
        if i < len(noise):
            out.append(noise[i])
    return "".join(out)


@given(
    bases=st.text(alphabet=_BASES, min_size=1, max_size=200),
    noise=st.lists(_NOISE, max_size=200),
    lower=st.booleans(),
    policy=st.sampled_from(list(AmbiguityPolicy)),
)
@SETTINGS
def test_property_clean_bases_survive_noise_and_case_under_every_policy(
    bases: str, noise: list[str], lower: bool, policy: AmbiguityPolicy
) -> None:
    raw = _with_noise(bases.lower() if lower else bases, noise)
    assert validate_sequence(raw, ambiguity_policy=policy).seq == bases


@given(seq=st.text(alphabet=_IUPAC, min_size=1, max_size=200))
@SETTINGS
def test_property_ambiguity_policies_do_what_they_document(seq: str) -> None:
    codes = [c for c in seq if c in _AMBIGUITY]
    kept = validate_sequence(seq, ambiguity_policy=AmbiguityPolicy.KEEP)
    assert kept.seq == seq

    masked = validate_sequence(seq, ambiguity_policy=AmbiguityPolicy.MASK)
    assert masked.seq == "".join("N" if c in _AMBIGUITY else c for c in seq)
    assert len(masked.seq) == len(seq)

    if codes:
        first = next(c for c in seq if c in _AMBIGUITY)
        with pytest.raises(ValidationError) as exc:
            validate_sequence(seq, ambiguity_policy=AmbiguityPolicy.ERROR)
        assert repr(first) in str(exc.value)
        assert any("ambiguity code" in n for n in kept.notices)
    else:
        assert validate_sequence(seq, ambiguity_policy=AmbiguityPolicy.ERROR).seq == seq
        assert kept.notices == [] or all("ambiguity" not in n for n in kept.notices)


@given(text=st.text(max_size=300), policy=st.sampled_from(list(AmbiguityPolicy)), rna=st.booleans())
@SETTINGS
def test_property_arbitrary_text_validates_or_raises_validation_error_only(
    text: str, policy: AmbiguityPolicy, rna: bool
) -> None:
    try:
        result = validate_sequence(text, ambiguity_policy=policy, rna=rna)
    except ValidationError:
        return
    assert result.seq and set(result.seq) <= set(_IUPAC)


@given(seq=st.text(alphabet=_IUPAC, min_size=1, max_size=300), policy=st.sampled_from(list(AmbiguityPolicy)))
@SETTINGS
def test_property_a_validated_sequence_is_always_reverse_complementable(
    seq: str, policy: AmbiguityPolicy
) -> None:
    if policy is AmbiguityPolicy.ERROR and any(c in _AMBIGUITY for c in seq):
        return
    validated = validate_sequence(seq, ambiguity_policy=policy).seq
    rc = reverse_complement(validated)
    assert len(rc) == len(validated)
    assert set(rc) <= set(_IUPAC)  # no letter left uncomplemented that is not a code
    assert reverse_complement(rc) == validated
