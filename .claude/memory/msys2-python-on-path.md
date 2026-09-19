# The `python` on PATH on the dev laptop is an MSYS2 build with no wheels

MEASURED on this project's dev machine. The `python` resolved by PATH is an
MSYS2 build, not a python.org or Microsoft Store CPython — it cannot install
most binary wheels (numpy, torch, etc. all fail or silently pull a slow
pure-Python/source-build fallback if they install at all).

**Always use the venv's own interpreter explicitly:**
`worker\.venv\Scripts\python.exe`, never a bare `python` or `py` on this
machine. The venv itself is created with `uv venv --python 3.12` (Hard Rule
20), which resolves a real CPython distribution regardless of what is on
PATH — the trap is only ever hit by *skipping* venv activation/explicit
invocation and falling through to PATH resolution.
