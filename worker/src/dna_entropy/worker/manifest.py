"""Parse and validate ``manifest.json`` (docs/job_contract.md §3, schema version 1).

The manifest is written by the app before any GCP resource exists and is **immutable**
once the worker starts reading it (job_contract.md §3). This module only parses and
validates it — staging inputs locally and actually running the pipeline is
``worker/runner.py``'s job, since that needs a :class:`~dna_entropy.worker.blobstore.Blobstore`
this module deliberately does not depend on (manifest parsing must work identically
whether the bytes came from GCS or a local file).

**Naming note (worth flagging to whoever next edits ``docs/job_contract.md``):** that doc's
manifest example uses ``"forward"``/``"reverse"`` for ``analysis.direction``, but
:class:`dna_entropy.config.Direction` (shipped and tested under issue #279, per the
coordinator's own explicit instruction at the time) uses ``"forward-only"``/``"reverse-only"``.
This module accepts BOTH spellings on input (a manifest author's typo here should not be
a hard failure of an otherwise-valid job) but always reports the canonical
``Direction`` value in error messages and everywhere else. The doc, not the code, should
be corrected to match — see this module's docstring instead of re-deriving the question.
"""

from __future__ import annotations

import json
from dataclasses import dataclass, field
from pathlib import Path

from ..config import Direction, PredictorKind, RunConfig, TrackFormat

# Exactly one schema version exists today (job_contract.md §8). A worker that sees a
# HIGHER schema than it understands must refuse; there is no compatibility shim for any
# LOWER schema yet either, since none has ever existed.
CURRENT_SCHEMA_VERSION = 1
SUPPORTED_SCHEMA_VERSIONS: tuple[int, ...] = (1,)

# Manifest spellings that map onto the shipped Direction enum — accepts job_contract.md's
# "forward"/"reverse" as aliases for "forward-only"/"reverse-only" (see module docstring).
_DIRECTION_ALIASES: dict[str, Direction] = {
    "forward": Direction.FORWARD_ONLY,
    "reverse": Direction.REVERSE_ONLY,
    **{d.value: d for d in Direction},
}


class ManifestError(ValueError):
    """Raised when manifest.json is malformed or missing a required field."""

    code = "MANIFEST_INVALID"


class ManifestSchemaError(ManifestError):
    """``WORKER_VERSION_MISMATCH`` (issue #250): the manifest's schema version is not one
    this worker build understands. Always names BOTH versions so the app can show one
    clear action (update the app / re-run) rather than a worker-side stack trace."""

    code = "WORKER_VERSION_MISMATCH"

    def __init__(self, manifest_schema: int) -> None:
        self.manifest_schema = manifest_schema
        self.worker_schema = CURRENT_SCHEMA_VERSION
        super().__init__(
            f"This worker build understands manifest schema "
            f"{sorted(SUPPORTED_SCHEMA_VERSIONS)} (current: {CURRENT_SCHEMA_VERSION}), "
            f"but the manifest declares schema {manifest_schema}. Update the app "
            f"(if the manifest is newer) or re-run the job (if it is older and unsupported)."
        )


def _require(d: dict, key: str, *, where: str) -> object:
    if key not in d:
        raise ManifestError(f"manifest.json is missing required field {where}.{key!r}")
    return d[key]


@dataclass
class InputSpec:
    """One entry of ``manifest.json``'s ``inputs`` array."""

    id: str
    path: str  # store-relative, e.g. "input/SetTnpB-Evo.gb"
    name: str
    informat: str = "auto"  # auto | genbank | fasta | paste
    start: int = 1
    rna: bool = False
    genes: bool = False
    allow_ambiguity: bool = True
    fasta_records: str = "all"  # all | first (D14: "all" is the default; "first" is parity-only)

    @staticmethod
    def from_dict(d: dict) -> "InputSpec":
        return InputSpec(
            id=str(_require(d, "id", where="inputs[]")),
            path=str(_require(d, "path", where="inputs[]")),
            name=str(_require(d, "name", where="inputs[]")),
            informat=str(d.get("informat", "auto")),
            start=int(d.get("start", 1)),
            rna=bool(d.get("rna", False)),
            genes=bool(d.get("genes", False)),
            allow_ambiguity=bool(d.get("allowAmbiguity", True)),
            fasta_records=str(d.get("fastaRecords", "all")),
        )


@dataclass
class PredictorSpec:
    kind: str = "mock"  # mock | evo
    model: str = "evo2_7b"
    precision: str = "bf16"
    device: str = "cuda"
    seed: int = 0

    @staticmethod
    def from_dict(d: dict) -> "PredictorSpec":
        return PredictorSpec(
            kind=str(d.get("kind", "mock")),
            model=str(d.get("model", "evo2_7b")),
            precision=str(d.get("precision", "bf16")),
            device=str(d.get("device", "cuda")),
            seed=int(d.get("seed", 0)),
        )


@dataclass
class AnalysisSpec:
    context_length: int = 4096
    window: int = 8192  # derived by the app; recorded here, never re-derived (job_contract.md §3)
    stride: int = 4096
    direction: Direction = Direction.BOTH_COMBINED
    track_format: str = "bedgraph"  # bedgraph | wig

    @staticmethod
    def from_dict(d: dict) -> "AnalysisSpec":
        raw_direction = str(d.get("direction", Direction.BOTH_COMBINED.value))
        direction = _DIRECTION_ALIASES.get(raw_direction)
        if direction is None:
            valid = sorted({*_DIRECTION_ALIASES.keys()})
            raise ManifestError(
                f"manifest.json analysis.direction {raw_direction!r} is not one of {valid}"
            )
        return AnalysisSpec(
            context_length=int(d.get("contextLength", 4096)),
            window=int(d.get("window", 8192)),
            stride=int(d.get("stride", 4096)),
            direction=direction,
            track_format=str(d.get("format", "bedgraph")),
        )


@dataclass
class Limits:
    max_run_seconds: int = 14400
    cancel_poll_seconds: int = 10
    heartbeat_seconds: int = 30

    @staticmethod
    def from_dict(d: dict) -> "Limits":
        return Limits(
            max_run_seconds=int(d.get("maxRunSeconds", 14400)),
            cancel_poll_seconds=int(d.get("cancelPollSeconds", 10)),
            heartbeat_seconds=int(d.get("heartbeatSeconds", 30)),
        )


@dataclass
class Lifecycle:
    after_task: str = "stop"  # stop | delete | keep
    keep_alive_minutes: int = 0
    after_keep_alive: str = "stop"

    @staticmethod
    def from_dict(d: dict) -> "Lifecycle":
        return Lifecycle(
            after_task=str(d.get("afterTask", "stop")),
            keep_alive_minutes=int(d.get("keepAliveMinutes", 0)),
            after_keep_alive=str(d.get("afterKeepAlive", "stop")),
        )


@dataclass
class StoreSpec:
    kind: str  # "gcs" | "localdir"
    bucket: str = ""
    prefix: str = ""
    root: str = ""

    @staticmethod
    def from_dict(d: dict) -> "StoreSpec":
        kind = str(_require(d, "kind", where="store"))
        if kind == "gcs":
            return StoreSpec(
                kind=kind,
                bucket=str(_require(d, "bucket", where="store")),
                prefix=str(_require(d, "prefix", where="store")),
            )
        if kind == "localdir":
            return StoreSpec(kind=kind, root=str(_require(d, "root", where="store")))
        raise ManifestError(f"manifest.json store.kind {kind!r} is not 'gcs' or 'localdir'")


@dataclass
class WorkerRef:
    """``manifest.json``'s ``worker`` field: the image/version the app expects to run.

    Not necessarily the ACTUAL running worker's own build (that comparison, and what to
    do on a mismatch, is the container/startup-script's concern, not this dataclass's) —
    this is only the app's declared expectation, carried through for provenance and for
    ``status.json.worker`` to echo back.
    """

    image: str = ""
    version: str = ""

    @staticmethod
    def from_dict(d: dict) -> "WorkerRef":
        return WorkerRef(image=str(d.get("image", "")), version=str(d.get("version", "")))


@dataclass
class JobManifest:
    """A fully parsed, validated ``manifest.json``."""

    schema: int
    job_id: str
    inputs: list[InputSpec]
    predictor: PredictorSpec
    analysis: AnalysisSpec
    outputs: list[str]
    limits: Limits
    lifecycle: Lifecycle
    store: StoreSpec
    worker: WorkerRef = field(default_factory=WorkerRef)
    raw: dict = field(repr=False, default_factory=dict)  # the original parsed dict, for provenance

    @staticmethod
    def parse(text: str) -> "JobManifest":
        """Parse and validate manifest JSON text. Raises :class:`ManifestSchemaError` for
        an unsupported schema (checked FIRST, before any other field is even read, so a
        version mismatch is reported as exactly that and not as some other confusing
        missing-field error from a future schema's shape) and :class:`ManifestError` for
        any other structural problem."""
        try:
            d = json.loads(text)
        except json.JSONDecodeError as exc:
            raise ManifestError(f"manifest.json is not valid JSON: {exc}") from exc

        schema = int(_require(d, "schema", where=""))
        if schema not in SUPPORTED_SCHEMA_VERSIONS:
            raise ManifestSchemaError(schema)

        job_id = str(_require(d, "jobId", where=""))
        inputs_raw = _require(d, "inputs", where="")
        if not isinstance(inputs_raw, list) or not inputs_raw:
            raise ManifestError("manifest.json 'inputs' must be a non-empty array")
        inputs = [InputSpec.from_dict(i) for i in inputs_raw]

        predictor = PredictorSpec.from_dict(d.get("predictor", {}))
        analysis = AnalysisSpec.from_dict(d.get("analysis", {}))
        outputs = [str(o) for o in d.get("outputs", [])]
        limits = Limits.from_dict(d.get("limits", {}))
        lifecycle = Lifecycle.from_dict(d.get("lifecycle", {}))
        store = StoreSpec.from_dict(_require(d, "store", where=""))
        worker = WorkerRef.from_dict(d.get("worker", {}))

        return JobManifest(
            schema=schema, job_id=job_id, inputs=inputs, predictor=predictor,
            analysis=analysis, outputs=outputs, limits=limits, lifecycle=lifecycle,
            store=store, worker=worker, raw=d,
        )

    def build_run_config(
        self, input_spec: InputSpec, *, local_input_path: str, local_out_dir: str,
    ) -> RunConfig:
        """Build the :class:`~dna_entropy.config.RunConfig` for one input, given where the
        runner has already staged it locally (via the blobstore) and where local outputs
        should land before being uploaded.

        Note on ``outputs`` selection: the manifest's ``outputs`` array names the desired
        output FILES (e.g. ``"bedgraph"``, ``"tsv"``, ``"genes_gff3"``), but
        ``pipeline.run()`` does not yet support suppressing individual writers beyond
        ``track_format`` (bedgraph vs. wig), ``include_tsv``, and ``genes`` (which also
        gates the genes GFF3) — see the filed follow-up issue for full per-output
        selection. This method maps what it can onto those three knobs and otherwise
        writes the pipeline's normal full output set.
        """
        informat = None if input_spec.informat == "auto" else input_spec.informat
        track_format = (
            TrackFormat.WIG if "wig" in self.outputs and "bedgraph" not in self.outputs
            else TrackFormat.BEDGRAPH
        )
        return RunConfig(
            name=input_spec.name,
            input_path=local_input_path,
            informat=informat,
            predictor=PredictorKind(self.predictor.kind),
            model=self.predictor.model,
            device=self.predictor.device,
            out_dir=local_out_dir,
            track_format=track_format,
            start=input_spec.start,
            max_len=self.analysis.window,  # the GPU ceiling IS the app-derived window
            context_length=self.analysis.context_length,
            direction=self.analysis.direction,
            genes=input_spec.genes or ("genes_gff3" in self.outputs),
            rna=input_spec.rna,
            seed=self.predictor.seed,
            include_tsv=("tsv" in self.outputs) if self.outputs else True,
        )
