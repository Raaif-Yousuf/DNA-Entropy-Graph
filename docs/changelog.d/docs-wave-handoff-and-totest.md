- `NEXT_SESSION.md` rewritten for the 2026-09-19 six-lane wave: seventeen pull requests, the app
  suite from 51 tests to 316, and only five `v0.1 walking skeleton` issues left open.
- Four `docs/ToTest.md` rows added for #62, #66, #197 and #34, each naming its own false pass.
  The sharpest is #62's: a `.resw` not packed as a PRI resource gives a window whose every label
  is blank, which reads as an unfinished layout rather than a broken build, so the row names the
  six string ids to check by hand because not one has ever been seen rendered.
- Fixed a literal user-home path (`C:\Users\lab\Downloads`) committed in
  `SettingsStoreTests.cs`. `check_user_home_paths.py` caught it, but only on the end-of-wave
  sweep: the guard had been run before that lane merged and not after, which is the argument for
  running the full guard set per lane rather than per night.
