# 4. View your results

## What the entropy number means

At every position in your sequence, the model makes a guess at what the letter there
"should" be, based on everything around it, and the entropy number says how confident
that guess was. It runs from **0 to 2 bits**:

- **Low entropy (close to 0)** means the model was confident, this position looks like it
  "has to be" what it is, given the surrounding sequence. This often lines up with
  well-conserved, functionally important regions, for example inside a gene.
- **High entropy (close to 2)** means the model had no strong opinion, any of the four
  letters looked about as likely as any other. This often shows up in less constrained
  regions, like the third position of a wobble codon, or sequence between genes.

Entropy is not, by itself, a claim that a position is "good" or "bad," it is a measure of
predictability, one useful signal among others you bring to reading your sequence, not a
verdict.

## The seam marker

By default, the app reads your sequence in both directions and stitches the two readings
together (this is called "bidirectional, combined" mode; see the glossary entry for
[reverse complement](glossary.md)). Where the two readings are joined, the results viewer
draws a small **seam marker**. This is not a defect, it is just where the app switched
from one reading direction to the other, and it is drawn so you know where that switch
happened rather than mistaking it for a real feature of your sequence.

**Why the very first bases of your sequence are trustworthy now, and were not before:** a
model that only reads forward has to guess at the first few bases with nothing at all to
go on, since there is no earlier sequence to look at. The earlier prototype version of
this tool worked exactly that way, and its first base was always shown as maximally
uncertain (the full 2 bits) by design, not because that base was actually unpredictable.
This app instead also reads your sequence backward, so the bases at the very start get
their confidence from what comes *after* them in the backward reading. The result: every
position in your sequence, including the very first one, gets a real, informative entropy
value instead of an artificial "unknown" at the start. If you specifically want the older,
forward-only behaviour (for comparing against a result made with the old prototype, for
example), it is available as an Advanced option.

## What you get back

Depending on what your input was and which output files you kept turned on:

- **A FASTA file**: your sequence, as its own genome, so the viewer tools below have
  something to load your other files onto.
- **A GenBank file**: your sequence and its genes (either the genes already in your
  GenBank input, or genes the app predicted for you from a FASTA or pasted sequence),
  each one labelled with its average entropy.
- **An entropy track** (as bedGraph and/or WIG files): the full, position-by-position
  entropy values, the file IGV and similar tools use to draw the graph itself.
- **A gene feature file** (GFF3): the gene locations, separate from the entropy track, so
  a viewer can show both together.
- **A stats or summary file**: the plain-text numbers, mean, minimum, and maximum entropy
  for your sequence, without needing to open a viewer at all.
- **An entropy spreadsheet** (TSV), if you turned it on: the same position-by-position
  numbers in a plain table, openable directly in Excel or similar, for anyone who would
  rather look at numbers than a picture.

## Viewing your results

The app shows two views itself:

- A fast overview chart, right on the Results page, with no setup needed.
- An embedded genome browser (the same open-source viewer, IGV, that many labs already
  use as a standalone program), which loads automatically and lets you scroll and zoom
  along your sequence with the entropy track and any genes shown together.

You can also open your results in other tools you may already have:

- **IGV (desktop)**: use **Open in IGV** if you have IGV installed and running; the app
  sends your files to it directly. Otherwise, open IGV yourself and load the FASTA file as
  your genome, then load the entropy track and gene files as tracks.
- **Geneious Prime**: open the GenBank file for your sequence and its genes with their
  entropy notes. Geneious does not load the bedGraph/WIG track format as a graph, so this
  app also produces a separate, Geneious-specific version of the entropy track (a GFF3
  file); load that one and use Geneious's **Color by / Heatmap** feature to shade it.
- **SnapGene** and **Benchling**: open the GenBank file the same way; you will see your
  sequence and genes with their average entropy noted on each gene. Neither tool has a
  position-by-position graph view built for this kind of track, so the detailed,
  base-by-base picture is best viewed in IGV or the app's own embedded viewer instead.

## Related

[glossary.md](glossary.md) for "bit," "context length," "reverse complement," "GenBank,"
"FASTA," "bedGraph," and "GFF3."
[`docs/science_and_formats.md`](../science_and_formats.md), the full developer-level
account of the science and every file format, if you want more depth than this page.
