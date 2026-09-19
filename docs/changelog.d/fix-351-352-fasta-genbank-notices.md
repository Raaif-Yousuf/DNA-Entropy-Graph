- Fixed #351: `read_fasta`'s duplicate-header detection now keys on the record ID (the
  header up to its first whitespace, the part BLAST/samtools/IGV treat as the
  identifier) rather than the full header line, so two records sharing an ID but
  carrying different free-text descriptions are correctly flagged as a genuine ID
  collision.
- Fixed #352: `read_genbank`'s zero-feature summary notice now reads "0 gene feature(s)"
  instead of the grammatically broken "0 no gene feature(s)".
- Docs: `docs/science_and_formats.md` section 4 documents the ID-based duplicate check.
