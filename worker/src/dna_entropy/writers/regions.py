"""Region track writer (issue #125): ``<name>.regions.bed`` and ``<name>.regions.gff3``.

Both load as a feature track in IGV (and Geneious/SnapGene/Benchling accept GFF3). The BED is
the plain 4-column form (``chrom start end name``, 0-based half-open, no ``track`` line, so
bedtools and every genome browser read it as is); the GFF3 is 1-based inclusive and carries
the region's length and mean entropy as attributes.

Names are ``low_entropy_<n>`` / ``high_entropy_<n>``, numbered per kind across the whole file.
A region that wraps the origin of a circular molecule is ONE region with two pieces: two BED
lines and two GFF3 lines sharing one name/ID (a GFF3 discontinuous feature), the GFF3 pieces
marked ``wraps_origin=true``. Coordinates carry the run's ``start`` offset like every other
track (Hard Rule 5: UTF-8, LF).
"""

from __future__ import annotations

from collections.abc import Sequence
from pathlib import Path

from ..analysis.regions import Region
from .base import write_text_lf

SOURCE = "dna-entropy"


class RegionWriter:
    """Writes ``<name>.regions.bed`` and ``<name>.regions.gff3``."""

    def write_multi(
        self,
        *,
        name: str,
        blocks: Sequence[tuple[str, int, Sequence[Region]]],
        start: int,
        out_dir: str,
    ) -> list[str]:
        """Write both files from one ``(chrom, length, regions)`` block per contig; return
        ``[bed_path, gff3_path]``. An empty BED / header-only GFF3 means "no region found"."""
        offset = start - 1
        bed_lines: list[str] = []
        gff_lines = ["##gff-version 3"]
        gff_lines += [
            f"##sequence-region {chrom} {start} {start + length - 1}" for chrom, length, _ in blocks
        ]
        counters = {"low": 0, "high": 0}
        for chrom, length, regions in blocks:
            for region in regions:
                counters[region.kind] += 1
                label = f"{region.kind}_entropy_{counters[region.kind]}"
                pieces = region.segments(length)
                for begin, end in pieces:
                    bed_lines.append(f"{chrom}\t{begin + offset}\t{end + offset}\t{label}")
                    attrs = (
                        f"ID={label};Name={label};kind={region.kind}_entropy;"
                        f"length={region.length};mean_entropy={region.mean_entropy:.4f}"
                    )
                    if len(pieces) > 1:
                        attrs += ";wraps_origin=true"
                    gff_lines.append(
                        "\t".join(
                            [
                                chrom,
                                SOURCE,
                                "misc_feature",
                                str(begin + 1 + offset),
                                str(end + offset),
                                ".",
                                ".",
                                ".",
                                attrs,
                            ]
                        )
                    )
        bed = write_text_lf(Path(out_dir) / f"{name}.regions.bed", "".join(line + "\n" for line in bed_lines))
        gff = write_text_lf(Path(out_dir) / f"{name}.regions.gff3", "\n".join(gff_lines) + "\n")
        return [bed, gff]
