# Geneious's track-import support needs verifying per format (THEORY)

THEORY (unverified this session): third-party genome tools do not all
accept the same track file formats. IGV accepts `.wig`/bedGraph-style
tracks natively; some other desktop tools in this app's target list
(Geneious, SnapGene, Benchling) are reported anecdotally to prefer or
require GFF3-style annotation tracks for some import paths rather than a
raw signal/`.wig` track, but this has **not been directly verified against
a real current version of any of them** as part of this session's work.

**Before shipping export support for a given viewer, check its real, current
import behaviour against a real exported file** — do not trust this note or
any other secondhand claim about a specific tool's format support. Record
the verified result as `MEASURED <date>:` in
`docs/science_and_formats.md`'s viewer-support matrix once checked, and
delete or correct this THEORY note to match (Hard Rule 18: disproving a
theory replaces it).
