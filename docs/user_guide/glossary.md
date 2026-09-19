# Glossary

Every term this guide uses, in plain words, alphabetically. If a definition here uses
another term from this list, that term is written in *italics* the first time it appears
in the entry.

**A100.** A more powerful, more expensive type of graphics card than the app's default
(see *L4*). Used automatically for larger models, or by choice for very long sequences
that need a bigger *window*. Costs roughly four to six times as much per hour as the
default.

**bedGraph.** One of the file formats this app writes for the entropy track (the
position-by-position confidence values). A plain text file, one line per stretch of
sequence, readable by IGV and most genome browsers.

**billing account.** The payment method (usually a credit card) linked to a Google Cloud
*project*. Google will not start any *VM* for a project with no billing account linked.

**bit.** The unit the entropy number is measured in. This app's entropy values run from 0
bits (the model was completely certain) to 2 bits (the model had no idea, all four
letters looked equally likely).

**bucket.** A private storage folder in your own Google Cloud *project*, created
automatically during setup. Your uploaded sequences and downloaded results pass through
it; nobody outside your own account can see inside it.

**contig.** A single, continuous stretch of DNA sequence, treated as one unit. A file with
several separate sequences in it (several *records*) produces one entropy track per
contig.

**container image.** A single, self-contained package of the exact software this app runs
on a rented computer, built and published once by this app's developers and never
modified afterward. You never interact with it directly; it is how the app makes sure
every run uses the same, tested version of the analysis software.

**context length.** How much surrounding sequence the model considers before it makes a
prediction at any one position. A bigger context length gives the model more to go on,
but needs more computer memory and time. The app's default works well for most
sequences; see [03-run.md](03-run.md)'s Advanced options if you want to change it.

**entropy.** How unpredictable a position in your sequence looked to the model. See *bit*
for the scale, and [04-view.md](04-view.md) for how to read it.

**FASTA.** A simple, widely used file format for a DNA sequence with no extra
annotations, just letters and a name. One of the two input formats this app accepts
directly (the other is *GenBank*).

**GenBank.** A file format that carries a DNA sequence together with its known genes and
other annotations. If you give this app a GenBank file, it uses the genes already in it
rather than predicting new ones.

**GFF3.** A file format this app uses for gene locations, and separately for a
position-by-position entropy track aimed at tools (like Geneious) that do not read
*bedGraph* files as a graph.

**GPU.** Short for graphics processing unit, a type of computer chip originally built for
video game graphics that also happens to be very good at the kind of math this app's
model needs. "Renting a GPU" means renting a computer that has one, by the minute.

**L4.** The default, least expensive type of graphics card this app rents. Good enough for
the default model and the great majority of sequences. See also *A100*.

**OAuth.** The standard, secure way of signing in to one app using your existing Google
account, without ever giving that app your Google password directly. When you sign in
in [02-connect-google-cloud.md](02-connect-google-cloud.md), this is the technology doing
it; your password is only ever typed into Google's own sign-in page.

**project.** A private, named folder inside your own Google account that holds every
computer, storage *bucket*, and setting this app creates on your behalf. Everything
lives inside one project, and nobody outside your own Google account can see into it.

**quota.** A one-time permission limit Google places on new accounts, most relevantly for
this app, "how many graphics-card computers can you rent at once." New accounts start at
zero for graphics cards and have to request an increase; see
[02-connect-google-cloud.md](02-connect-google-cloud.md) Step 6.

**record.** One named sequence entry inside a file. A file can hold one record or many; a
multi-record file produces one *contig*'s worth of results per record.

**reverse complement.** Reading a DNA sequence backward *and* swapping each letter for its
chemical partner (A for T, C for G, and so on), which is how the opposite strand of DNA
actually reads. This app reads your sequence both forward and as its reverse complement
and combines the two, which is why even the very first bases of your sequence get a
trustworthy entropy value; see [04-view.md](04-view.md).

**spot.** A cheaper way to rent a computer, at the cost of Google being allowed to
interrupt it at any time if it needs the capacity back. A good way to save money on a run
that is not urgent; a full-price computer is not interruptible.

**VM.** Short for virtual machine, in plain words, a rented computer with a graphics
card. This app rents one for the few minutes your analysis needs, then stops or deletes
it, never leaving one running longer than you asked for.

**window.** A stretch of your sequence, up to a fixed size, that the model looks at in one
pass. Longer sequences are broken into overlapping windows automatically; see
*context length* and [04-view.md](04-view.md)'s note on the seam marker.

## Related

[faq.md](faq.md), for common questions in a more conversational form than this list.
