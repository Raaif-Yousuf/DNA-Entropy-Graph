# igv.js may need a derived reference, not the raw GenBank file (THEORY)

THEORY (unverified this session): desktop IGV can construct an ad hoc
genome/reference directly from a local GenBank file for the purpose of
displaying a track against it. igv.js (the embedded web version used in
this app's viewer, per the Stack table) may not support the same
GenBank-as-reference path and could need a derived FASTA plus an index, or
a minimal generated genome JSON, even when the user's original input was a
GenBank record.

**This needs verifying against the actual igv.js version pinned for this
app**, not assumed from general familiarity with the desktop tool. If true,
the viewer pipeline needs an explicit GenBank-to-FASTA(+index) derivation
step before handing anything to `SetVirtualHostNameToFolderMapping`. Record
the verified result as `MEASURED <date>:` in `docs/science_and_formats.md`
and correct or delete this note once checked (Hard Rule 18).
