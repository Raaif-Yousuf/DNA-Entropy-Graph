"""Privacy-safe stand-ins for user content in a worker log line (issue #253).

The product's privacy claim (``docs/threat_model.md``: "Worker logs never contain
sequence content, file names, or the user's email; only ids, stage names, and error
classes") is only true if nothing that reaches :meth:`~.worker.status.StatusWriter.notice`
(and therefore ``progress.jsonl``/``status.json``, both of which leave the VM in the job's
own bucket, docs/job_contract.md §4-5) ever embeds raw text a user typed or pasted — a
FASTA/GenBank header, a pasted sequence, an original file name. Those are exactly the
things a support bundle (issue #106) would otherwise leak.

These helpers exist so a notice/log message can still say *something useful* about a
piece of user text (how long it was, that two were identical) without ever printing the
text itself. Every call site that used to interpolate raw header/record-id text into a
notice (``readers/fasta.py``, ``readers/genbank.py``, ``validation/validators.py``) now
goes through one of these instead of ``!r``/``[:N]`` string slicing.
"""

from __future__ import annotations

import hashlib


def describe_len(text: str) -> str:
    """``"12 chars"`` — never the text itself, just how long it was."""
    n = len(text)
    return "1 char" if n == 1 else f"{n} chars"


def fingerprint(text: str) -> str:
    """An 8-hex-char SHA-256 prefix of ``text``: enough to tell "two of these were the
    same string" apart in a notice (e.g. two duplicate FASTA headers) without the string
    itself ever being written anywhere. Not a security control (8 hex chars collide
    fairly easily) — just a correlation aid, the same spirit as a truncated request id."""
    return hashlib.sha256(text.encode("utf-8", errors="replace")).hexdigest()[:8]
