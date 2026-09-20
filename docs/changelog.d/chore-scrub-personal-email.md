- Replaced my real university email address with `a.researcher@university.example` in
  `scripts/sync_memory.py`'s self-test cases and in every fixture in
  `scripts/tests/test_sync_memory.py`. The scanner's whole job is to keep personal
  identifiers out of committed files, so using a live personal address as its own sample
  text was the one string in that file that should not have been literal. The pattern under
  test is unchanged and `.example` is a reserved TLD, so the fixtures exercise the same
  branch and can never match a real person. Verified with 48 passing tests in
  `scripts/tests/test_sync_memory.py` and a PASS from `sync_memory.py --self-test`.
