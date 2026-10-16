- Portable mode (#144): an empty `portable.flag` beside the program makes the app keep all its state in a `data` folder beside it instead of `%LOCALAPPDATA%`. `--profile` and `DEG_DATA_DIR` still win. Sign-in does not travel between PCs (DPAPI); the new PC asks to sign in again.

