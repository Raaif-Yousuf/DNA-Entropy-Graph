# Citing DNA Entropy Graph

If this app helped with work you publish, please cite it, and cite the model that does the
analysis. Two things did the work: this app, and a genomic language model called Evo 2.

## How to cite the app

On the project's GitHub page, the **Cite this repository** button (right-hand side, under
"About") gives you a ready-made citation in APA or BibTeX. It reads the file `CITATION.cff` in
the project, so it always matches the version of the project you are looking at.

A plain-text version:

> Yousuf, R. DNA Entropy Graph [Computer software].
> https://github.com/Raaif-Yousuf/DNA-Entropy-Graph

Add the version number (it is in the installer's file name) and the
year you downloaded it, so a reader can tell which version produced your tracks.

A permanent DOI (a citable, never-changing identifier) for each release is planned through
Zenodo, a free archive for research software. Until the first release is archived there is no
DOI to quote; this page is updated when one exists.

## How to cite the model

The entropy values come from Evo 2. Please cite its paper:

> Brixi G, et al. Genome modeling and design across all domains of life with Evo 2.
> bioRxiv 2025. https://doi.org/10.1101/2025.02.18.638918

Check the paper's page for its latest published form before you copy this into a manuscript.

## What to say in your methods

Two things matter to a reader trying to reproduce your result: the **direction mode** (forward
only, reverse only, or the default that combines both) and the **model** and **window size**.
The model and computer type are saved with every run; see
[Runs](05-history.md). Write down the other options you chose yourself. A sentence like this is enough:

> Per-base Shannon entropy was computed with DNA Entropy Graph (version X, direction mode Y)
> using the Evo 2 7B model.

For what the entropy number means, see [View the results](04-view.md) and the
[glossary](glossary.md).
