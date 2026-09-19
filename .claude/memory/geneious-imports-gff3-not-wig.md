# Geneious needs GFF3, not WIG/bedGraph, for a graph track (MEASURED)

MEASURED (prototype team; recorded in `worker/docs-legacy/DESIGN.md`, the
docstring of `worker/src/dna_entropy/writers/geneious.py`, and
`docs/science_and_formats.md`'s viewer-support matrix, section 6): third-party
genome tools do not all accept the same track file formats. IGV accepts
`.wig`/bedGraph-style tracks natively as a graph track; **Geneious Prime does
not** — it imports GFF3 but not WIG or bedGraph as a graph track (those are
export-only, from its Graphs tab), which is exactly why this app produces a
separate, Geneious-specific GFF3 file (`writers/geneious.py`) carrying the
entropy value in both the score column and an `entropy` qualifier, rather
than pointing Geneious at the same bedGraph/WIG file IGV uses.

**This is settled for Geneious specifically; SnapGene and Benchling are not.**
`docs/science_and_formats.md`'s viewer-support matrix still marks both of
those `THEORY (unverified)`: the GenBank `/note` qualifier is *expected* to
display like any other annotation in each tool's sequence-editor view, but
neither has been confirmed against a real, current install. The underlying
lesson still applies there and to any future viewer this app targets: check
real, current import behaviour against a real exported file before shipping
an export claim, and record the result as `MEASURED <date>:` in that
matrix rather than trusting a secondhand or anecdotal claim (Hard Rule 18:
disproving a theory replaces it).
