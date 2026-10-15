# Example sequences

Small sequences to try the app on, so a first run does not need your own data.

| File | What it is | Length | Format |
| --- | --- | --- | --- |
| `phix174.gb` | Escherichia phage phiX174, complete genome, with its gene annotations | 5,386 bases | GenBank |
| `phix174.fasta` | The same sequence with no annotations | 5,386 bases | FASTA |

phiX174 is a small virus that infects bacteria. It is the classic first genome ever
sequenced, and its genes overlap, which makes the entropy track interesting to look at. It is
small enough that a run costs a few cents (see
[the cost page](../docs/user_guide/06-costs-and-cleanup.md)).

## Provenance and licence

- **Source:** NCBI RefSeq record `NC_001422.1`, downloaded through NCBI's E-utilities
  (`efetch`, `db=nuccore`, `rettype=gb` and `rettype=fasta`) on 2026-10-15. The files are
  unmodified.
- **Licence:** NCBI states that it places no restrictions on the use or distribution of the
  data in GenBank and RefSeq. Treat the files as public domain; please still cite the record
  (`NC_001422.1`) if you publish a result from it. See
  <https://www.ncbi.nlm.nih.gov/home/about/policies/>.
- The files are read-only inputs. The app keeps a copy of every input you run and never writes
  next to it.

## What a run should show

MEASURED 2026-10-15: both files pass the worker's validation and run end to end with the
stand-in (mock) predictor, which needs no GPU. That proves the files are readable, not what
the real model says about them.

THEORY (unverified): on the real Evo 2 model the entropy will be lower inside the long genes
and higher in the short gaps between them. No real-model expected output is committed here,
because none has been run yet; committing the stand-in's output as "expected" would show a
wrong picture. Once a real run exists, its output files go in this folder (issue #171 tracks it).
