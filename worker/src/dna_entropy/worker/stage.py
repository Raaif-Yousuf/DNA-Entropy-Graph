"""Stage one manifest input onto the worker's local disk (issue #704).

Two things the runner used to do inline as ``store.download_file(input_spec.path, ...)``:

* a ``path`` input is downloaded from the job's store, plus its optional ``annotation`` GFF3
  (#135), which is staged beside it;
* an ``accession`` input (#133) has no object in the store at all: the worker fetches the
  GenBank record from NCBI itself (keyless, so NCBI's 3 requests per second limit applies, which
  one fetch per input never approaches; the manifest carries no API key), and the fetch
  provenance rides into ``provenance.json`` as ``input_source``.

A failure raises (a missing object, a failed fetch), and the runner already turns anything
raised while staging into a failed input with that message.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

from ..readers import entrez
from .blobstore import Blobstore
from .manifest import InputSpec, parse_accession_range


@dataclass
class StagedInput:
    local_input: Path
    local_annotation: Path | None = None
    provenance_extra: dict[str, Any] = field(default_factory=dict)


def stage_input(store: Blobstore, spec: InputSpec, dest_dir: Path, *, cache_dir: Path) -> StagedInput:
    """Make ``spec``'s input (and annotation) available under ``dest_dir``. ``cache_dir`` holds
    fetched records, so a second call for the same versioned accession (the runner's batch
    pre-scan, then the real run) costs no second request."""
    if spec.accession is not None:
        start, stop = parse_accession_range(spec.accession_range) if spec.accession_range else (None, None)
        fetched = entrez.fetch_genbank(spec.accession, cache_dir=cache_dir, start=start, stop=stop)
        return StagedInput(Path(fetched.path), None, {"input_source": fetched.provenance()})
    local_input = dest_dir / Path(spec.path).name
    store.download_file(spec.path, local_input)
    local_annotation = None
    if spec.annotation is not None:
        local_annotation = dest_dir / Path(spec.annotation).name
        store.download_file(spec.annotation, local_annotation)
    return StagedInput(local_input, local_annotation)
