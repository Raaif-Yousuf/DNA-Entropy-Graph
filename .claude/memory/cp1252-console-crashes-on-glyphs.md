# The Windows console mangles non-ASCII worker output

The default Windows console codepage (cp1252, or a legacy codepage
depending on locale) cannot render arbitrary Unicode glyphs the way a UTF-8
terminal can, and a Python `print()` of a glyph-bearing string can raise a
`UnicodeEncodeError` or silently mangle the text depending on how the
process's stdout encoding is configured.

Hard Rule 5 exists because of this class: **ASCII-safe console output in the
worker** (`OK:` / `ERROR:` / `-`, never glyphs), while **files are UTF-8
with LF** (every file writer passes `encoding="utf-8", newline="\n"`
explicitly, never relying on a platform default).

If something that looks like a Unicode/encoding bug shows up, check the
*stored* value (read the file back, or `ascii()` the in-memory string)
before concluding the value itself is wrong — the console can mangle a
value that round-trips perfectly through a file.
