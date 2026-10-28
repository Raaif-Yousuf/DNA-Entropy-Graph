# 3. Run a sequence

## Not sure what to try first? Use the example

If you would rather not use your own data for a first run, use the example: a small virus
genome called phiX174 (5,386 letters), which costs a few cents to run. Download
[`phix174.gb`](https://github.com/Raaif-Yousuf/DNA-Entropy-Graph/blob/main/examples/phix174.gb)
(click **Download raw file**), save it anywhere, and follow the steps below with that file. The
[examples folder](https://github.com/Raaif-Yousuf/DNA-Entropy-Graph/blob/main/examples/README.md)
says where the sequence came from and that it is free to use. A "Try an example" button inside
the app is planned.

## Add your sequence

On the **New run** page, either:

- Drag files, or a folder of them, onto the drop area (GenBank, EMBL, SnapGene, FASTA, or plain text; from a
  folder the app takes the sequence files directly inside it, not those in sub-folders; a `.txt` file in a folder is taken only when its first line starts with `>` or `LOCUS`, so notes and logs are left alone. To add a plain-letters `.txt` file, use **Add files**), or
- Click **Add files** (or press Ctrl+O), or
- In File Explorer, right-click a `.gb`, `.gbk`, `.fasta`, `.fa` or `.fna` file, choose **Open with**, then **DNA Entropy Graph**. The file is added to **New run** in the window that is already open (or the app starts with it). The entry is listed only if you turned on "Show DNA Entropy Graph in Open with" in Settings (Files); it never becomes the program a file opens in when you double-click it. If New run is open, the file is added below what you already entered. Files on a network address (for example `\\server\share`) are not opened this way; copy them to this PC or a mapped drive first. Or,
- Open **Paste a sequence**, type or paste letters, a FASTA record, or the path of a file, and
  click **Add sequence**. A counter under the box shows how many bases you pasted. Pasted
  letters are saved in the app's own folder, never next to your files.
- Open **Fetch from NCBI**, type an accession such as `NC_045512.2` (and, if you want only part of the
  record, the first and last base), and click **Fetch record**. The app asks NCBI, from your own PC,
  whether that record exists and how long it is, so a mistyped accession, or a record too large for one
  run, is refused on the spot and nothing is rented. The list then shows it as an NCBI record with its
  base count, and the estimate uses that count. The record itself is downloaded by the rented computer
  when the run starts. Its results go in a folder named after the accession, version and range
  (for example `NC_045512_2_100-500`). The Runs page search finds the run by its accession.

You can add more than one file at once. For now each press of **Run** runs the file you
have selected in the list (running several files together as one batch is not built yet),
and the batch limits below describe where that work is heading. When it arrives it gives you
results for each, up to a limit on how many files or how many total letters one batch can
hold (50 files or 20 million letters, by default). That ceiling is there for cost, not
policy: a batch large enough to matter for your bill should be caught before you press
Run, not discovered afterward. If you go over it, the app tells you your batch's actual
numbers against the limit and lets you either split the batch or raise the limit yourself;
see [06-costs-and-cleanup.md](06-costs-and-cleanup.md) for why the ceiling is set where it
is. GenBank, EMBL, SnapGene and FASTA files are detected automatically by their contents, not just their
file extension, so a renamed file still works. An EMBL file (`.embl`) or a SnapGene file (`.dna`) is read like a GenBank file: the genes it already carries are used as they are. If a SnapGene file is damaged, the app says so before anything is created; export it from SnapGene as GenBank and add that file instead.

If a gene in a GenBank or EMBL file has a position that cannot be read (for example `abc` or `1..?`), the file still counts as ready. The file's line says how many genes were left out, and the run uses the rest. Fix the position in the file (for example `1..42` or `complement(85..126)`) and add the file again to include them. A position that is broken in a way the worker cannot step over (for example `join()`) refuses the file before anything is created. The page then says it found no sequence in the file and asks you to check that it is a FASTA or GenBank file with at least one sequence and choose it again; correct or remove that feature in the file first.

If you have a FASTA file and a GFF3 file that lists its genes, drop them together, or choose **Add GFF3** under the file list. The app pairs a GFF3 with the FASTA that has the same name (plasmid.fasta and plasmid.gff3, capital letters do not matter, and .gff works as well as .gff3), whether you drop them together or drop the GFF3 later next to a FASTA already on the page. If the names differ but there is only one FASTA on the page, it pairs them and tells you so (but never when a FASTA with the GFF3's name is on the page: if that FASTA has a problem, the page names it and nothing is paired with a different file). When you drop two GFF3 files and one has the FASTA's name, that one is paired and the other is refused with a line saying so. The app does not guess anywhere else: with several FASTA files and no matching name, with two FASTA files of the same name (even one added earlier), with two GFF3 files for one FASTA, or when the FASTA already has a GFF3, nothing is paired and the page says why and what to do. In that case select the FASTA file first and then choose **Add GFF3**. The file's line then shows how many genes the GFF3 has, and the run uses those genes instead of finding them itself. **Remove GFF3** takes it off again. A GFF3 whose sequence names do not match the FASTA, or whose genes all run past the end of the sequence, is refused before anything is created, with one thing to fix. Running again from the Runs page uses the same GFF3. This needs a worker version that reads GFF3 files; until then the button does not appear.

If you have an NCBI accession (for example `NC_045512.2`) instead of a file, the worker can download the record for you and run it like a GenBank file: `dna-entropy run --accession NC_045512.2 --name my_run`. The accession and the download date are recorded in the run's `provenance.json`. An accession without a version (`NC_045512`) gets NCBI's current version, and the run records which version that was. If NCBI is busy the worker retries once, then tells you to wait and try again; a record larger than 64 MiB needs `--accession-range` to fetch just the part you want. In the app, use **Fetch from NCBI** on this page (above).

If your genes come as a separate GFF3 file next to a FASTA file, the worker can use them instead of predicting genes: `dna-entropy run -i my.fasta --gff3 my.gff3 --name my_run`. The names in the GFF3's first column must match the names in the FASTA headers. If a sequence is circular, say so in the GFF3 (`Is_circular=true` on its `region` line) or run with `--topology circular`, so a gene that crosses the start of the sequence is kept instead of dropped. In the app, drop the two files together (see above).

The worker also reads EMBL files (`.embl`) and SnapGene files (`.dna`) the same way as GenBank, using the genes the file already has. The app's own file picker does not offer these two yet, so for now export from SnapGene as GenBank when using the app.

If a gene in a GenBank or EMBL file has a position the worker cannot read (for example `abc` or `1..?`), the run still finishes with the other genes and tells you, in its notices, how many genes were left out and what their positions said. Correct the position in the file (for example `1..42`) and run again to include them.

If your sequence is circular (a plasmid) and a gene runs across the point where the sequence starts and ends, the .gb and genes.gff3 files the run writes show that gene in two pieces (for example join(120..126,1..10)) instead of as the whole plasmid, so SnapGene, Benchling and IGV draw it where it is. If a gene's own position cannot be read but its protein-coding (CDS) entry has the same gene name, the worker uses the CDS entry for that gene and says so in its notices.

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
- **Graphics card (GPU)**: under **Model and graphics card (GPU)** you can choose which rented graphics card the run uses. **Automatic** is the
  cheapest one that can run the model and is the right choice for almost everyone. The list also offers an A100 with 40 GB or 80 GB, which costs more
  and is only worth it for very long sequences; the **Estimate** line follows the card you pick. When more than one model is offered, the
  model picker appears here too, and the two are linked: choosing a model that needs a bigger card switches the card for you (a line under the
  pickers says so), and choosing a card switches the model if it cannot run the current one. A card that cannot run the chosen model shows a
  lock and says why. **Lock model** and **Lock graphics card** keep one choice where it is; if that makes a pair that will not run, the
  page warns you and **Run** waits until you tick **Run anyway**.
- **Find genes**: on by default for pasted or FASTA sequences (this predicts gene
  locations for you); off automatically for GenBank input, since a GenBank file already
  has its own genes recorded, and those are used instead.
- **Files you get back**: under the file list there is one tick box for each result file you can ask for (the
  list is the same for a GenBank file and for a FASTA or pasted sequence). Everything is ticked except the WIG
  track, the two large tables of the model's probability for every base, the GC percent track and the codon usage table, which you tick only if you want them. Your choice is
  remembered separately for GenBank files and for FASTA or pasted sequences, and the next time you add a file of
  that kind the same boxes are ticked. If you clear every box, the page asks you to tick at least one file before
  **Run** works. The entropy track comes in one format per run, so the IGV track and the WIG track are alternatives: ticking one clears the other. See
  [04-view.md](04-view.md) for what each file is for.
- **Option presets**: above the file list, the **Option presets** section has a picker that sets several options at once. **Quick look** reads the forward strand only and returns a few files, the fastest and cheapest run. **Publication** reads both strands combined and returns every default file, including the smoothed tracks, region calls and gene summary. **Plasmid QC** reads the sequence as a circle on both strands, so the start and end of the plasmid get real context and there is no seam, and it calls the low and high entropy regions. Under the picker, a line lists the strands, the shape, the context length and the files the next run will use. Changing a file tick box by hand drops the preset, because the options are then yours. To keep your own set, choose your files, type a name and choose **Save as preset**; **Delete preset** removes one of yours (the three built in cannot be deleted). **Copy my presets** puts your presets on the clipboard as text you can send to a colleague, and **Add presets from the clipboard** adds presets someone sent you. A run started from a preset shows "Preset: <name>" on its card on the Runs page.
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
