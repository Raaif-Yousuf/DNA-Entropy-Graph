- The malformed-input fuzz corpus is now marked `-text` in `.gitattributes` and two tests assert
  its bytes. MEASURED 2026-09-19: `*.fasta text` with `eol=lf` meant
  `fasta_crlf_and_lonecr_mixed.fasta` was committed with its CRLFs already rewritten to LF, while
  the suite kept passing because it ran against the working tree, where the bytes were still
  right. A fresh clone would have tested a different file and nothing would have said so. The new
  tests fail if that normalization ever returns, and were watched failing on a deliberately
  normalized copy before being believed.
