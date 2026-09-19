# Quota is not stockout

Quota is per-region, fixable by the user (a quota increase request), and
pre-filterable (check it before you start, not after a failure). Stockout is
per-zone, transient, and the only one of the two worth an automatic retry or
a zone-ladder walk.

`GPUS_ALL_REGIONS` is a project-wide ceiling and overrides a regional
quota that looks nonzero: a per-region GPU quota of 1 with
`GPUS_ALL_REGIONS=0` still means zero real GPU capacity anywhere in the
project. Check the project-wide number, not just the region you happen to
be looking at, before concluding quota is fine.

See `billing-off-looks-like-stockout.md` for the classifier this distinction
feeds into.
