"""Parse and validate ``manifest.json`` (docs/job_contract.md §3, schema version 1).

The manifest is written by the app before any GCP resource exists and is **immutable**
once the worker starts reading it (job_contract.md §3). This module only parses and
validates it — staging inputs locally and actually running the pipeline is
``worker/runner.py``'s job, since that needs a :class:`~dna_entropy.worker.blobstore.Blobstore`
this module deliberately does not depend on (manifest parsing must work identically
whether the bytes came from GCS or a local file).

**Naming resolved (2026-09-19, issue #254 follow-up):** an earlier draft of this module
flagged that ``docs/job_contract.md``'s manifest example used ``"forward"``/``"reverse"``
for ``analysis.direction`` while :class:`dna_entropy.config.Direction` (shipped and
tested under issue #279, per the coordinator's own explicit instruction at the time) uses
``"forward-only"``/``"reverse-only"``, and tolerated both spellings on input pending a
decision. The decision: the shipped ``Direction`` enum is canonical — it was deliberately
named this way (matching the "Forward only"/"Reverse only" *display* names throughout
``docs/science_and_formats.md``, and distinguishing them from a plain "forward"/"reverse"
which reads ambiguously next to ``"both-combined"``/``"both-averaged"``) and was already
shipped and tested before ``docs/job_contract.md``'s example was written. Only the
canonical spellings are accepted now; ``docs/job_contract.md``'s manifest example (section
3) should be corrected from ``"forward"``/``"reverse"`` to ``"forward-only"``/
``"reverse-only"`` to match — flagged in this session's report rather than edited
directly, since that file is outside this package's own paths.
"""

from __future__ import annotations

import json
from dataclasses import dataclass, field

from ..config import AmbiguityPolicy, Direction, PredictorKind, RunConfig, TrackFormat
from .batch_limits import DEFAULT_MAX_INPUTS, DEFAULT_MAX_TOTAL_NT

# Exactly one schema version exists today (job_contract.md §8). A worker that sees a
# HIGHER schema than it understands must refuse; there is no compatibility shim for any
# LOWER schema yet either, since none has ever existed.
CURRENT_SCHEMA_VERSION = 1
SUPPORTED_SCHEMA_VERSIONS: tuple[int, ...] = (1,)

# Only the canonical Direction spellings are accepted (see the module docstring's
# "Naming resolved" note) — no "forward"/"reverse" aliases; a manifest using the old
# spelling now fails validation with the valid-values list in the error message.
_DIRECTION_ALIASES: dict[str, Direction] = {d.value: d for d in Direction}


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


_AMBIGUITY_POLICIES: dict[str, AmbiguityPolicy] = {p.value: p for p in AmbiguityPolicy}


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
    # issue #249: keep | mask | error — see config.AmbiguityPolicy and
    # docs/science_and_formats.md for what each does to the entropy numbers. Replaces the
    # old boolean ``allowAmbiguity`` field, which was parsed but never actually consulted
    # anywhere downstream (readers/input.py hardcoded True for GenBank/FASTA and never
    # passed it at all for paste) — a real "wired to nothing" gap this issue also closes,
    # not just a rename. An old manifest still sending ``allowAmbiguity`` is unaffected:
    # unknown fields are tolerated (job_contract.md §8), same as any other schema drift.
    ambiguity_policy: AmbiguityPolicy = field(
        default=AmbiguityPolicy.KEEP, metadata={"json_name": "ambiguityPolicy"}
    )
    fasta_records: str = field(
        default="all", metadata={"json_name": "fastaRecords"}
    )  # all | first (D14: "all" is the default; "first" is parity-only)

    @staticmethod
    def from_dict(d: dict) -> InputSpec:
        raw_policy = str(d.get("ambiguityPolicy", AmbiguityPolicy.KEEP.value))
        policy = _AMBIGUITY_POLICIES.get(raw_policy)
        if policy is None:
            valid = sorted(_AMBIGUITY_POLICIES.keys())
            raise ManifestError(
                f"manifest.json inputs[].ambiguityPolicy {raw_policy!r} is not one of {valid}"
            )
        return InputSpec(
            id=str(_require(d, "id", where="inputs[]")),
            path=str(_require(d, "path", where="inputs[]")),
            name=str(_require(d, "name", where="inputs[]")),
            informat=str(d.get("informat", "auto")),
            start=int(d.get("start", 1)),
            rna=bool(d.get("rna", False)),
            genes=bool(d.get("genes", False)),
            ambiguity_policy=policy,
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
    def from_dict(d: dict) -> PredictorSpec:
        return PredictorSpec(
            kind=str(d.get("kind", "mock")),
            model=str(d.get("model", "evo2_7b")),
            precision=str(d.get("precision", "bf16")),
            device=str(d.get("device", "cuda")),
            seed=int(d.get("seed", 0)),
        )


@dataclass
class AnalysisSpec:
    context_length: int = field(default=4096, metadata={"json_name": "contextLength"})
    window: int = 8192  # derived by the app; recorded here, never re-derived (job_contract.md §3)
    stride: int = 4096
    direction: Direction = Direction.BOTH_COMBINED
    # The wire field is literally "format", not "trackFormat" (job_contract.md §3's example).
    track_format: str = field(default="bedgraph", metadata={"json_name": "format"})  # bedgraph | wig

    @staticmethod
    def from_dict(d: dict) -> AnalysisSpec:
        raw_direction = str(d.get("direction", Direction.BOTH_COMBINED.value))
        direction = _DIRECTION_ALIASES.get(raw_direction)
        if direction is None:
            valid = sorted({*_DIRECTION_ALIASES.keys()})
            raise ManifestError(f"manifest.json analysis.direction {raw_direction!r} is not one of {valid}")
        return AnalysisSpec(
            context_length=int(d.get("contextLength", 4096)),
            window=int(d.get("window", 8192)),
            stride=int(d.get("stride", 4096)),
            direction=direction,
            track_format=str(d.get("format", "bedgraph")),
        )


@dataclass
class Limits:
    max_run_seconds: int = field(default=14400, metadata={"json_name": "maxRunSeconds"})
    cancel_poll_seconds: int = field(default=10, metadata={"json_name": "cancelPollSeconds"})
    heartbeat_seconds: int = field(default=30, metadata={"json_name": "heartbeatSeconds"})
    # Cost guardrails (issue #248) — a batch-level policy cap, not a technical ceiling
    # (windowing already tiles any length); see worker/batch_limits.py's module docstring
    # for why the DEFAULTS below are a reasonable starting point, not a derived number.
    # Living in the manifest, not hardcoded in the worker, so the app and the worker
    # re-validate against the SAME number rather than two that could drift.
    max_inputs: int = field(default=DEFAULT_MAX_INPUTS, metadata={"json_name": "maxInputs"})
    max_total_nt: int = field(default=DEFAULT_MAX_TOTAL_NT, metadata={"json_name": "maxTotalNt"})

    @staticmethod
    def from_dict(d: dict) -> Limits:
        return Limits(
            max_run_seconds=int(d.get("maxRunSeconds", 14400)),
            cancel_poll_seconds=int(d.get("cancelPollSeconds", 10)),
            heartbeat_seconds=int(d.get("heartbeatSeconds", 30)),
            max_inputs=int(d.get("maxInputs", DEFAULT_MAX_INPUTS)),
            max_total_nt=int(d.get("maxTotalNt", DEFAULT_MAX_TOTAL_NT)),
        )


@dataclass
class Lifecycle:
    after_task: str = field(default="stop", metadata={"json_name": "afterTask"})  # stop | delete | keep
    keep_alive_minutes: int = field(default=0, metadata={"json_name": "keepAliveMinutes"})
    after_keep_alive: str = field(default="stop", metadata={"json_name": "afterKeepAlive"})

    @staticmethod
    def from_dict(d: dict) -> Lifecycle:
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
    def from_dict(d: dict) -> StoreSpec:
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
    def from_dict(d: dict) -> WorkerRef:
        return WorkerRef(image=str(d.get("image", "")), version=str(d.get("version", "")))


@dataclass
class JobManifest:
    """A fully parsed, validated ``manifest.json``."""

    schema: int
    job_id: str = field(metadata={"json_name": "jobId"})
    inputs: list[InputSpec]
    predictor: PredictorSpec
    analysis: AnalysisSpec
    outputs: list[str]
    limits: Limits
    lifecycle: Lifecycle
    store: StoreSpec
    worker: WorkerRef = field(default_factory=WorkerRef)
    # Not part of the wire contract — internal bookkeeping only — so excluded from the
    # generated schema entirely rather than appearing as a nonsensical free-form "raw" field.
    raw: dict = field(repr=False, default_factory=dict, metadata={"json_exclude": True})

    @staticmethod
    def parse(text: str) -> JobManifest:
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
            schema=schema,
            job_id=job_id,
            inputs=inputs,
            predictor=predictor,
            analysis=analysis,
            outputs=outputs,
            limits=limits,
            lifecycle=lifecycle,
            store=store,
            worker=worker,
            raw=d,
        )

    def build_run_config(
        self,
        input_spec: InputSpec,
        *,
        local_input_path: str,
        local_out_dir: str,
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
            TrackFormat.WIG
            if "wig" in self.outputs and "bedgraph" not in self.outputs
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
            ambiguity_policy=input_spec.ambiguity_policy,
        )
