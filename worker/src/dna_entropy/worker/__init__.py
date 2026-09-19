"""The manifest-driven worker subpackage (design §5.5, docs/job_contract.md).

Reads ``manifest.json``, runs :func:`dna_entropy.pipeline.run` once per input, and writes
``status.json``/``progress.jsonl``/``result.json`` — the contract the C# app's
``JobReconciler`` consumes. See ``worker/src/dna_entropy/worker/runner.py`` for the top
level and ``docs/job_contract.md`` for the file formats.
"""
