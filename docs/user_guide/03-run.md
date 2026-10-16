# 3. Run a sequence

## Add your sequence

On the **New run** page, either:

- Drag files, or a folder of them, onto the drop area (GenBank, FASTA, or plain text; from a
  folder the app takes the sequence files directly inside it, not those in sub-folders; a `.txt` file in a folder is taken only when its first line starts with `>` or `LOCUS`, so notes and logs are left alone. To add a plain-letters `.txt` file, use **Add files**), or
- Click **Add files** (or press Ctrl+O), or
- In File Explorer, right-click a `.gb`, `.gbk`, `.fasta`, `.fa` or `.fna` file, choose **Open with**, then **DNA Entropy Graph**. The file is added to **New run** in the window that is already open (or the app starts with it). The entry is listed only if you turned on "Show DNA Entropy Graph in Open with" in Settings (Files); it never becomes the program a file opens in when you double-click it. If New run is open, the file is added below what you already entered. Files on a network address (for example `\\server\share`) are not opened this way; copy them to this PC or a mapped drive first. Or,
- Open **Paste a sequence**, type or paste letters, a FASTA record, or the path of a file, and
  click **Add sequence**. A counter under the box shows how many bases you pasted. Pasted
  letters are saved in the app's own folder, never next to your files.

You can add more than one file at once. For now each press of **Run** runs the file you
have selected in the list (running several files together as one batch is not built yet),
and the batch limits below describe where that work is heading. When it arrives it gives you
results for each, up to a limit on how many files or how many total letters one batch can
hold (50 files or 20 million letters, by default). That ceiling is there for cost, not
policy: a batch large enough to matter for your bill should be caught before you press
Run, not discovered afterward. If you go over it, the app tells you your batch's actual
numbers against the limit and lets you either split the batch or raise the limit yourself;
see [06-costs-and-cleanup.md](06-costs-and-cleanup.md) for why the ceiling is set where it
is. GenBank and FASTA files are detected automatically by their contents, not just their
file extension, so a renamed file still works.

If you have an NCBI accession (for example `NC_045512.2`) instead of a file, the worker can download the record for you and run it like a GenBank file: `dna-entropy run --accession NC_045512.2 --name my_run`. The accession and the download date are recorded in the run's `provenance.json`. An accession without a version (`NC_045512`) gets NCBI's current version, and the run records which version that was. If NCBI is busy the worker retries once, then tells you to wait and try again; a record larger than 64 MiB needs `--accession-range` to fetch just the part you want. A **Fetch from NCBI** button on this page is not built yet; until then, download the record from NCBI as a GenBank file and drop it in.

If your genes come as a separate GFF3 file next to a FASTA file, the worker can use them instead of predicting genes: `dna-entropy run -i my.fasta --gff3 my.gff3 --name my_run`. The names in the GFF3's first column must match the names in the FASTA headers. If a sequence is circular, say so in the GFF3 (`Is_circular=true` on its `region` line) or run with `--topology circular`, so a gene that crosses the start of the sequence is kept instead of dropped. Pairing the two files in the app is not built yet.

The worker also reads EMBL files (`.embl`) and SnapGene files (`.dna`) the same way as GenBank, using the genes the file already has. The app's own file picker does not offer these two yet, so for now export from SnapGene as GenBank when using the app.

If a gene in a GenBank or EMBL file has a position the worker cannot read (for example `abc` or `1..?`), the run still finishes with the other genes and tells you, in its notices, how many genes were left out and what their positions said. Correct the position in the file (for example `1..42`) and run again to include them.

Two optional extra files need no model and no graphics card: a GC-content track (`--gc`, how G/C-rich each stretch is) and a codon-usage table per gene (`--codons`). They are off by default. The app does not offer them as checkboxes yet.

Adding, removing and checking files never freezes the window, even for a big folder. **Remove** takes the file off the list; a pasted sequence's saved copy goes with it, and your own files are never touched. A notice that counts something says "1 letter" or "2 letters" as it should.

Each file you add gets its own small summary: how many records it contains, how many
letters (nucleotides), how many genes it found, and any notices. A green check means it
is ready to run. A grey note is something worth knowing but not a problem, for
example, "the first line starts with > so it was treated as a name and ignored," or that a real GenBank/FASTA file contained
a handful of `N`s or other ambiguous letters, which by default this app keeps and runs
with rather than rejecting (see [04-view.md](04-view.md) for what that means for the
entropy track). A red message is something you need to fix before the file can run, and
it tells you exactly what is wrong and where, for example, "found the letter U at
position 12: this looks like RNA," with a button to turn on **Treat as RNA** right there
if that is what you meant.

## Name your run

The results folder is named after your input file for now. Choosing your own name for a run is coming; the app does not offer it yet.

## The basic options

You do not need to change any of these for a first run; the defaults are chosen to be
reasonable for most sequences.

- **Model**: which version of the analysis model to use. The default, `evo2_7b`, is the
  right choice for almost everyone. Bigger models exist and cost more; the app will warn
  you and ask you to confirm before using one.
- **Find genes**: on by default for pasted or FASTA sequences (this predicts gene
  locations for you); off automatically for GenBank input, since a GenBank file already
  has its own genes recorded, and those are used instead.
- **Files you get back**: under the file list there is one tick box for each result file you can ask for (the
  list is the same for a GenBank file and for a FASTA or pasted sequence). Everything is ticked except the WIG
  track and the two large tables of the model's probability for every base, which you tick only if you want them. Your choice is
  remembered separately for GenBank files and for FASTA or pasted sequences, and the next time you add a file of
  that kind the same boxes are ticked. If you clear every box, the page asks you to tick at least one file before
  **Run** works. The entropy track comes in one format per run, so the IGV track and the WIG track are alternatives: ticking one clears the other. See
  [04-view.md](04-view.md) for what each file is for.
- **Save to**: where results land on your computer. Defaults to your Downloads folder.
- **Where to run**: in the cloud (the normal choice) or on this computer, if you installed
  the local engine in [02-connect-google-cloud.md](02-connect-google-cloud.md)'s Step 8.

Under the file list, once the selected file has passed its check, the app shows one
**Estimate** line before you commit to anything, for example "Estimate: about $0.13,
about 9 minutes, based on your earlier runs." With no earlier runs on this PC it shows a
range instead ("about $0.08 to $0.18, about 5 to 12 minutes"), because a computer that is
already warm is much quicker than one that has to start from nothing. It is a guess, not
a bill, and it never shows a number the app does not have: while a file is still being
checked, or has a problem, there is no estimate line. If the line says the price list
could not be read, reinstall the app; you can still press Run. See
[06-costs-and-cleanup.md](06-costs-and-cleanup.md) for what those numbers actually mean
and some worked examples.

## Advanced options (most people can skip this section)

An **Advanced** section holds settings for longer or unusual sequences: the amount of
surrounding sequence the model considers at each position (called the context length),
which direction the model reads your sequence in, and cloud settings like which graphics
card tier to use and what happens to the rented computer when the run finishes. The
defaults here were chosen deliberately and work well for typical sequences; you generally
only need this section for a very long sequence (tens of thousands of letters or more) or
if you want to save money by changing what happens to the computer afterward. See
[glossary.md](glossary.md) for "context length" and "reverse complement" if you are
curious.

## Press Run

Click the **Run** button (or press Ctrl+Enter). What happens next, in plain words:

1. **Checking your files**, a last check before anything is uploaded.
2. **Uploading**, your sequence goes to your own private cloud storage.
3. **Starting a computer**, the app rents a graphics-card computer in your project. The
   first time you do this in a brand-new project, this step narrates which region it is
   trying, since the very first computer in a region can occasionally take a couple of
   tries to find one that is free.
4. **Preparing the computer**, the analysis software and, the first time in a project, the
   model itself are loaded onto the computer. This first-time load is genuinely slow,
   often around eight extra minutes, because the model is a large file being downloaded
   and cached for next time. Every run after the first in the same project is much
   faster, because that cache is kept.
5. **Analysing**, the actual entropy calculation, narrated by which part of your sequence
   is being worked on right now.
6. **Saving results**, then **Downloading** them to your computer.
7. **Cleaning up**, the rented computer is stopped (the default) or deleted, depending on
   your settings; see [06-costs-and-cleanup.md](06-costs-and-cleanup.md).

**You can close the app at any point after the upload starts.** The run keeps going in
the cloud regardless, and your results will be waiting for you, downloaded automatically,
the next time you open the app. A banner on the progress page says exactly this while a
run is active.

You can also **Cancel** a run in progress, or use **Stop VM now** / **Delete VM now** if
you want to end the rented computer immediately rather than waiting for the run to finish
cleanly.

## Related

[04-view.md](04-view.md), what to do with the results once they arrive.
[06-costs-and-cleanup.md](06-costs-and-cleanup.md), what each stage above actually costs.
[07-when-something-goes-wrong.md](07-when-something-goes-wrong.md), if a run fails.
