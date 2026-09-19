- Fixed #350: `_safe_contig_name` (`readers/input.py`) now disambiguates a name that
  sanitizes to a Windows-reserved device name (`CON`, `NUL`, `PRN`, `COM1`-`9`,
  `LPT1`-`9`) and caps length for a realistic output path, truncating only the base
  portion so the `_<n>` disambiguator that keeps records in one multi-record file from
  colliding always survives truncation intact. A defensive `_assert_unique_contig_names`
  guard raises rather than silently letting a name collision overwrite one record's
  output with another's.
- CORRECTED per Hard Rule 18: #350's own original claim that "Windows refuses to create
  CON.fasta" does not reproduce on this dev box (Windows 11 Home 10.0.26200) -- measured
  directly via Python, .NET and PowerShell, all of which created it successfully. Kept
  the hardening anyway as free insurance (see the code comment and the correction
  comment on #350 for the full reasoning) and filed #366 for the real gap found while
  verifying this end-to-end: `pipeline.py` builds the actual output folder/file name
  from `cfg.name` directly, never from this sanitized form.
- Docs: `docs/science_and_formats.md` section 4 documents the hardening and the
  correction.
