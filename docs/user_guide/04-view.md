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

**If a stretch of your track looks odd over a run of `N`s or other ambiguous letters**,
that is expected: by default this app feeds those positions to the model exactly as
written, so the entropy value there reflects an unusual letter the model rarely or never
saw during training, not an ordinary confidence reading. See
[`docs/science_and_formats.md`](../science_and_formats.md) for the other ways to handle
them and how to choose one.

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

## The Results page

When a run finishes, choose **View results** on the run page. You can also open any
finished run later with **Open** on its row of the Runs page. The Results page shows:

- **Entropy by sequence.** One line per sequence in your file: its length, its mean,
  lowest and highest entropy in bits, and the reading direction used. These numbers are
  read from the summary file the run wrote into your results folder, so they are exactly
  what is in that file. If the run did not write a summary file, or it cannot be read,
  the page says so instead of showing blanks, and **Open folder** shows you what the run
  did write.
- **Files.** Every file in the run's results folder, with its size. For each one,
  **Open** opens it in the program Windows uses for that kind of file (a program or
  script is never opened from here), **Show in folder** selects it in File Explorer, and
  **Copy path** puts its full path on the clipboard.
- **Open folder** opens the whole results folder, and **Open in viewer** takes you to the
  genome viewer for this run.

Nothing on this page changes your files. If the folder was moved or deleted, the page
tells you to use **Download again** on the Runs page.

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

The embedded viewer shows your sequence, a bar graph of the entropy (always drawn from 0 to
2 bits, so two runs look comparable at a glance), and a gene track when your run has genes.
It works with no internet connection, because everything it needs ships inside the app and
reads your results from the folder on your computer. It follows the app's light or dark
theme.

Open it from a finished run with the **Open viewer** button on the run page. If the viewer
says it could not draw the run, choose **Try again**. Sequences larger than 50 MB are too
big for the built-in viewer; open the sequence file and the entropy track in IGV on your
computer instead.

**If the viewer says it needs a Microsoft component:** the viewer uses Microsoft's WebView2
Runtime, which comes with Windows 11 and most up-to-date Windows 10 computers. If yours
does not have it, the viewer page offers an **Install the WebView2 Runtime** link; install
it, then open the run again. **If it says the results are not on this computer**, the
results folder was moved or deleted; download the results again from the run.

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
