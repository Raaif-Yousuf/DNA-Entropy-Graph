- MEASURED 2026-09-19: `ci-worker.yml`'s "manifest schema drift" step ran with `--with jsonschema`
  alone and broke the moment `worker/manifest.py` began importing `predictors.hardware` to validate
  a model id. That pulls `predictors/__init__.py`, which imports numpy, so the step died with
  `ModuleNotFoundError` instead of reporting drift. It now installs the worker package itself, since
  the generator introspects the worker's own dataclasses and its dependency footprint is therefore
  the worker's, not a hand-maintained subset.
- `scripts/tests/test_check_version_lockstep.py` asserted that `app/` did not exist and that the
  guard said so, on the reasoning that this was "a stable fact for tonight". It stopped being true
  about two hours later when issue #61 landed the solution skeleton, and the test went red on the
  first CI run afterwards. It now asserts the rule itself: `app/Directory.Build.props` and
  `worker/pyproject.toml` carry the same version string, and the guard is enforcing rather than
  noticing.
