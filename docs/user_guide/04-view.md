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

- **The entropy picture.** Under the buttons, a chart of entropy (0 to 2 bits) along the
  first sequence in your file, drawn straight from the track the run wrote, so it appears
  at once without opening the genome viewer. A dashed line, when there is one, marks the
  seam (see above). **Save picture** writes it as a PNG named after the sequence (for example
  `MyGene.entropy.png`) in the results folder and shows it in File Explorer (a run you brought in from the earlier command-line version keeps your own folder untouched, so its pictures go into one new folder inside your Downloads folder, named after the run followed by "exports" and a short code that keeps two runs of the same name apart (for example `MyGene exports 3fa9c1`), and your own folders are never used; if that name is already taken, the new picture is numbered, for example `MyGene.entropy (2).png`, and nothing is overwritten). The picture has a title
  (the run and the sequence), position along the bottom, and entropy up the side in bits from 0 to 2. A run that wrote no entropy track has no picture; run it again with the
  entropy track ticked on New run.
- **Entropy by sequence.** One line per sequence in your file: its length, its mean,
  lowest and highest entropy in bits, and the reading direction used. These numbers are
  read from the summary file the run wrote into your results folder, so they are exactly
  what is in that file. If the run did not write a summary file, or it cannot be read,
  the page says so instead of showing blanks, and **Open folder** shows you what the run
  did write.
- **Genes.** When the run wrote a gene table (`<name>.genes.tsv`), one row per gene: its
  record, start, end, strand, length, and the mean, lowest and highest entropy over that
  gene's own bases, plus the share of its bases with low entropy. Choose a column heading
  to sort by it (lowest first); choose it again to reverse. Numbers sort as numbers, so
  the order matches sorting the same column of the file in a spreadsheet. A long table
  shows its first 500 genes; **Open** next to the file in the Files list opens all of them.
- **Files.** Every file in the run's results folder, with its size. For each one,
  **Open** opens it in the program Windows uses for that kind of file (only the kinds of
  file this app writes are opened from here; use Show in folder for anything else), **Show in folder** selects it in File Explorer, and
  **Copy path** puts its full path on the clipboard.
- **Open folder** opens the whole results folder, and **Open in viewer** takes you to the
  genome viewer for this run.
- **Open in IGV** and **Open in Geneious** send this run's files to the desktop programs.
  They are described under Viewing your results below.

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

By default a run also writes a few companions, so the folder holds more than the files
above. This is the whole default set for one sequence called `my_run`:

- `my_run.fasta`, `my_run.gb`, `my_run.entropy.bedgraph`, `my_run.entropy.geneious.gff3`,
  `my_run.entropy.tsv`, and `stats.txt` (or `my_run.summary.txt` for a FASTA or pasted
  sequence): the files described above.
- **A surprisal track** (`my_run.surprisal.bedgraph` and `my_run.surprisal.geneious.gff3`,
  and a column in the spreadsheet): where entropy says how unsure the model was at a
  position, surprisal says how unexpected the base that is really there was. It costs no
  extra time or money, so it is on by default. The worker can leave it out
  (`--no-surprisal`); a switch for it in the app is planned.
- **A smoothed entropy track** (`my_run.entropy.smooth51.bedgraph`): a rolling average over
  51 bases that shows the landscape. The raw track is always written too.
- **Low- and high-entropy regions** (`my_run.regions.bed`, `my_run.regions.gff3`): the
  stretches the app calls unusually predictable or unpredictable.
- **A gene table** (`my_run.genes.tsv`, `my_run.genes.csv`, `my_run.genes.gff3`), when your
  run has genes: mean, lowest and highest entropy per gene.
- **`provenance.json`**: which model, settings and version made these files, so a run can
  be reproduced.

Bigger extras (the per-base probabilities, which take 16 bytes per base) are off unless
you ask for them.

## Viewing your results

The app shows two views itself:

- A fast overview chart, right on the Results page, with no setup needed.
- Drag along that chart with the mouse to open the viewer at that part of the sequence. A plain click does nothing.
- An embedded genome browser (the same open-source viewer, IGV, that many labs already
  use as a standalone program), which loads automatically and lets you scroll and zoom
  along your sequence with the entropy track and any genes shown together.

The embedded viewer shows your sequence, a bar graph of the entropy (always drawn from 0 to
2 bits, so two runs look comparable at a glance), and a gene track when your run has genes.
It works with no internet connection, because everything it needs ships inside the app and
reads your results from the folder on your computer. It follows the app's light or dark
theme.

When your run wrote them, two more things appear. **Entropy regions** (the stretches the
run called as low or high entropy, from the `.regions.bed` file) show as a feature track under
the entropy graph. **Smoothed tracks** (a rolling average of the entropy over, for example, 51
bases) are listed as tick boxes above the viewer, all off to begin with so the raw entropy
graph stays the one you read; tick one to draw it under the raw graph on the same 0 to 2 bit
scale, untick it to remove it. A smoothed dip is an easier thing to over-read than the raw
graph: it is not by itself the edge of a gene. The tick boxes work once the viewer has drawn the run.

If your file holds more than one sequence record, a **Sequence record** list appears above
the viewer. Choose a record and the viewer jumps straight to it; if you have scrolled away, press **Go to record** to jump back to the one that is chosen. For a run that read both
directions and combined them, the viewer also marks the seam (see above) with a thin red
band at that base on each record; point at it to read what it means. A run that has no
seam shows no marker.

Open it from a finished run with the **Open viewer** button on the run page. If the viewer
says it could not draw the run, choose **Try again**. A sequence file larger than 50 MB takes a moment longer to open the first time, because
the app builds a small lookup file for it (kept in the app's own folder, never next to your
files). Sequences larger than 1 GB are too big for the built-in viewer; open the sequence
file and the entropy track in IGV on your computer instead.

**If the viewer says it needs a Microsoft component:** the viewer uses Microsoft's WebView2
Runtime, which comes with Windows 11 and most up-to-date Windows 10 computers. If yours
does not have it, the viewer page offers an **Install the WebView2 Runtime** link; install
it, then open the run again. Until then the page still draws the entropy track of the run as a plain chart (no zoom, no sequence), so you are never left with a blank page. **If it says the results are not on this computer**, the
results folder was moved or deleted; download the results again from the run.

You can also open your results in other tools you may already have:

- **IGV (desktop)**: choose **Open in IGV** on the Results page. If IGV is already running
  with its batch port on (the default, port 60151), the app starts a fresh session in it
  and loads the FASTA file as the genome, then the entropy track and the gene file. If IGV
  is not running, the app starts it with those files. The first time, the app looks for IGV
  where Windows installers put it; if it cannot find it, it asks you to pick IGV's program
  file, and remembers your choice. If IGV is open but does not answer, wait for it to finish
  what it is doing (a large sequence can take a while to load) and choose the button again;
  the app never starts a second IGV while one is already there. IGV shows one sequence at a
  time, so if your run had several input files, the first one opens and the page tells you
  so. A run that did not save a FASTA file cannot be opened this way, because the tracks
  need a sequence to sit on. If the app was pointed at a `.bat` file for IGV and your
  output folder's name contains one of `& | ^ % < > ( ) !` or a quote, the app will not
  start it (Windows would read those characters as commands); pick `igv.exe` instead, or
  move the folder. The port is IGV's default; changing it from this app comes with the
  Settings page and is not available yet. You can always load the files by hand instead:
  open IGV, load the FASTA file as your genome, then the track and gene files.
- **Geneious Prime**: choose **Open in Geneious**; the app starts Geneious with the run's
  GenBank file and GFF3 files (finding Geneious, or asking you to pick it once, the same
  way as for IGV). Or open the GenBank file yourself: open the GenBank file for your
  sequence and its genes with their
  entropy notes. Geneious does not load the bedGraph/WIG track format as a graph, so this
  app also produces a separate, Geneious-specific version of the entropy track (a GFF3
  file); load that one and use Geneious's **Color by / Heatmap** feature to shade it.
- **SnapGene** and **Benchling**: open the GenBank file the same way; you will see your
  sequence and genes with their average entropy noted on each gene. Neither tool has a
  position-by-position graph view built for this kind of track, so the detailed,
  base-by-base picture is best viewed in IGV or the app's own embedded viewer instead.

## Checking a batch, comparing two runs, and testing a change

These three are worker commands for now; the app has no buttons for them yet.

- **A batch of sequences.** When one run has three or more sequences, the run's summary file
  (`stats.txt` for GenBank, `<name>.summary.txt` for FASTA) marks any sequence whose average entropy
  is far from the rest as `OUTLIER`. Use it to spot the one odd clone among many.
- **Two runs.** `dna-entropy compare A.entropy.tsv B.entropy.tsv` writes a track of B minus A (open it
  in IGV under the two runs) and a short list of the positions that changed most. Use `--offset` when
  the two sequences are not the same length.
- **A single change.** `dna-entropy variants -i my.fasta --positions 120` scores the three possible
  substitutions at position 120 and writes `variants.tsv`. It prints the cost first (each change is one
  model run) and stops at 50 changes unless you raise `--max-variants`.
## Related

[glossary.md](glossary.md) for "bit," "context length," "reverse complement," "GenBank,"
"FASTA," "bedGraph," and "GFF3."
[`docs/science_and_formats.md`](../science_and_formats.md), the full developer-level
account of the science and every file format, if you want more depth than this page.
