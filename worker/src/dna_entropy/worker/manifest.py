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

from ..analysis.composition import DEFAULT_GC_WINDOW
from ..analysis.regions import (
    DEFAULT_MERGE_GAP,
    DEFAULT_MIN_LENGTH,
    DEFAULT_THRESHOLD_BITS,
    validate_region_options,
)
from ..analysis.smoothing import DEFAULT_SMOOTHING_WINDOWS, validate_smoothing_windows
from ..config import AmbiguityPolicy, Direction, PredictorKind, RunConfig, Topology, TrackFormat
from ..predictors.hardware import MODEL_REQUIREMENTS, model_requirement
from ..readers import entrez
from ..validation.validators import ValidationError
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

    def __init__(self, message: str, *, public: str | None = None) -> None:
        """``public`` is the VALUE-FREE text safe to persist in result.json (issue #1014): the
        field path and what was expected, never the offending value (it may be a file name).
        Omitted, the message is taken to carry no manifest value and is used as is."""
        super().__init__(message)
        self.public_message = public if public is not None else message


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


def _label(where: str, key: str) -> str:
    return f"manifest.json {where}.{key}" if where else f"manifest.json {key!r}"


def _int_field(d: dict, key: str, default: int, *, where: str) -> int:
    """A whole-number field (issue #878): absent -> ``default``; a string, null, list, object,
    bool or fractional number is refused with a :class:`ManifestError`, never coerced by ``int()``."""
    raw = d.get(key, default)
    if isinstance(raw, bool) or not isinstance(raw, int):
        raise ManifestError(
            f"{_label(where, key)} must be a whole number, got {raw!r}",
            public=f"{_label(where, key)} must be a whole number",
        )
    return raw


def _str_field(d: dict, key: str, default: str, *, where: str) -> str:
    """A text field (issue #878): absent -> ``default``; anything that is not a string is refused
    rather than turned into text by ``str()`` (``str(None)`` would be the text ``'None'``)."""
    raw = d.get(key, default)
    if not isinstance(raw, str):
        raise ManifestError(
            f"{_label(where, key)} must be text, got {raw!r}", public=f"{_label(where, key)} must be text"
        )
    return raw


def _required_str(d: dict, key: str, *, where: str) -> str:
    _require(d, key, where=where)
    return _str_field(d, key, "", where=where)


def _bool_field(d: dict, key: str, default: bool, *, where: str) -> bool:
    raw = d.get(key, default)
    if not isinstance(raw, bool):
        raise ManifestError(
            f"{_label(where, key)} must be true or false, got {raw!r}",
            public=f"{_label(where, key)} must be true or false",
        )
    return raw


def _object_field(d: dict, key: str, *, where: str) -> dict:
    """An optional JSON object section: absent -> ``{}``; anything else is refused."""
    raw = d.get(key, {})
    if not isinstance(raw, dict):
        raise ManifestError(
            f"{_label(where, key)} must be an object, got {raw!r}",
            public=f"{_label(where, key)} must be an object",
        )
    return raw


_AMBIGUITY_POLICIES: dict[str, AmbiguityPolicy] = {p.value: p for p in AmbiguityPolicy}

# issue #306: "all" | "first" — validated the same way ambiguityPolicy/direction already
# are, so a typo'd value fails loudly at manifest-parse time instead of being silently
# read as "all" by str(d.get(...)) with no check at all.
_FASTA_RECORDS_VALUES: frozenset[str] = frozenset({"all", "first"})

# Found during the #304 audit: analysis.format was accepted as any string with no
# validation (unlike direction/ambiguityPolicy/fastaRecords right above), and — a
# separate, worse bug — build_run_config never actually consulted the parsed value at
# all (see build_run_config's own note). Validated here the same way as everywhere else.
_TRACK_FORMAT_VALUES: dict[str, TrackFormat] = {t.value: t for t in TrackFormat}

# issue #704: inputs[].informat. "auto" sniffs; the rest force a reader (readers/input.py).
_INFORMAT_VALUES: tuple[str, ...] = ("auto", "genbank", "fasta", "paste", "embl", "snapgene")


def _optional_text(d: dict, key: str, *, where: str) -> str | None:
    """An optional non-empty string field: absent -> None; anything else that is not a
    non-empty string (a bool, a number, a list, "") is refused rather than coerced."""
    if key not in d:
        return None
    raw = d[key]
    if not isinstance(raw, str) or not raw.strip():
        raise ManifestError(
            f"manifest.json {where}.{key} must be a non-empty string, got {raw!r}",
            public=f"manifest.json {where}.{key} must be a non-empty string",
        )
    return raw


def parse_accession_range(text: str) -> tuple[int, int]:
    """``"100-500"`` (or ``"100..500"``) -> ``(100, 500)``; raises :class:`ManifestError`."""
    parts = text.replace("..", "-").split("-")
    if len(parts) != 2 or not all(p.strip().isdigit() for p in parts):
        raise ManifestError(
            f"manifest.json inputs[].accessionRange {text!r} must look like 100-500",
            public="manifest.json inputs[].accessionRange must look like 100-500",
        )
    return int(parts[0]), int(parts[1])


@dataclass
class InputSpec:
    """One entry of ``manifest.json``'s ``inputs`` array."""

    id: str
    name: str
    # store-relative, e.g. "input/SetTnpB-Evo.gb". Required unless `accession` is given (#133),
    # which the worker fetches from NCBI itself; then it is empty.
    path: str = ""
    informat: str = "auto"  # auto | genbank | fasta | paste | embl | snapgene
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
    # issue #704 / #135: store-relative object name of a GFF3 staged beside a FASTA input; its
    # genes replace Prodigal (RunConfig.annotation_path). Absent means none.
    annotation: str | None = None
    # issue #704 / #133: an NCBI accession the worker fetches (Entrez, keyless) when `path` is
    # absent, and an optional "start-end" (1-based, inclusive) range of it. The NCBI API key is
    # never part of the manifest (docs/job_contract.md section 3).
    accession: str | None = None
    accession_range: str | None = field(default=None, metadata={"json_name": "accessionRange"})

    @staticmethod
    def from_dict(d: dict) -> InputSpec:
        raw_policy = _str_field(d, "ambiguityPolicy", AmbiguityPolicy.KEEP.value, where="inputs[]")
        policy = _AMBIGUITY_POLICIES.get(raw_policy)
        if policy is None:
            valid = sorted(_AMBIGUITY_POLICIES.keys())
            raise ManifestError(
                f"manifest.json inputs[].ambiguityPolicy {raw_policy!r} is not one of {valid}",
                public=f"manifest.json inputs[].ambiguityPolicy is not one of {valid}",
            )
        raw_fasta_records = _str_field(d, "fastaRecords", "all", where="inputs[]")
        if raw_fasta_records not in _FASTA_RECORDS_VALUES:
            valid = sorted(_FASTA_RECORDS_VALUES)
            raise ManifestError(
                f"manifest.json inputs[].fastaRecords {raw_fasta_records!r} is not one of {valid}",
                public=f"manifest.json inputs[].fastaRecords is not one of {valid}",
            )
        informat = _str_field(d, "informat", "auto", where="inputs[]")
        if informat not in _INFORMAT_VALUES:
            raise ManifestError(
                f"manifest.json inputs[].informat {informat!r} is not one of {sorted(_INFORMAT_VALUES)}",
                public=f"manifest.json inputs[].informat is not one of {sorted(_INFORMAT_VALUES)}",
            )
        annotation = _optional_text(d, "annotation", where="inputs[]")
        accession = _optional_text(d, "accession", where="inputs[]")
        range_text = _optional_text(d, "accessionRange", where="inputs[]")
        if accession is None:
            if range_text is not None:
                raise ManifestError("manifest.json inputs[].accessionRange needs inputs[].accession")
            path = _required_str(d, "path", where="inputs[]")
        else:
            if d.get("path"):
                raise ManifestError(
                    "manifest.json inputs[] has both path and accession: use one (an accession "
                    "input is fetched by the worker, so it has no path)"
                )
            if informat not in ("auto", "genbank"):
                raise ManifestError(
                    f"manifest.json inputs[].informat {informat!r} cannot be combined with accession "
                    "(a fetched record is always GenBank; use auto or genbank)",
                    public="manifest.json inputs[].informat cannot be combined with accession "
                    "(a fetched record is always GenBank; use auto or genbank)",
                )
            if annotation is not None:
                raise ManifestError(
                    "manifest.json inputs[].annotation cannot be combined with accession "
                    "(a GFF3 pairs with a FASTA file, and a fetched record is GenBank)"
                )
            span = parse_accession_range(range_text) if range_text is not None else (None, None)
            try:
                accession = entrez.validate_request(accession, span[0], span[1])
            except ValidationError as exc:
                raise ManifestError(
                    f"manifest.json inputs[].accession or accessionRange: {exc}",
                    public="manifest.json inputs[].accession or accessionRange is not valid",
                ) from exc
            path = ""
        if annotation is not None and informat in ("genbank", "embl", "snapgene"):
            raise ManifestError(
                f"manifest.json inputs[].annotation needs a FASTA input, but informat is {informat!r}",
                public="manifest.json inputs[].annotation needs a FASTA input",
            )
        return InputSpec(
            id=_required_str(d, "id", where="inputs[]"),
            path=path,
            name=_required_str(d, "name", where="inputs[]"),
            informat=informat,
            start=_int_field(d, "start", 1, where="inputs[]"),
            rna=_bool_field(d, "rna", False, where="inputs[]"),
            genes=_bool_field(d, "genes", False, where="inputs[]"),
            ambiguity_policy=policy,
            fasta_records=raw_fasta_records,
            annotation=annotation,
            accession=accession,
            accession_range=range_text,
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
        kind = _str_field(d, "kind", "mock", where="predictor")
        model = _str_field(d, "model", "evo2_7b", where="predictor")
        precision = _str_field(d, "precision", "bf16", where="predictor")
        # issue #343, decided: predictor.precision is cross-checked against the model's
        # OWN real hardware requirement (predictors/hardware.py's MODEL_REQUIREMENTS),
        # not treated as pure provenance. Nothing downstream reads this field to
        # CONFIGURE the predictor at all -- the model id alone determines the real
        # precision used (predictors/hardware.py's own docstring: "gates on the device's
        # ACTUAL reported compute capability", never on a manifest-declared string) -- so
        # an app that believes it is choosing "bf16" for a model that will actually run
        # fp8 (or vice versa) is silently wrong with nothing to tell it, until this check.
        # Only cross-checked for the real ("evo") predictor, never "mock" (precision is
        # meaningless there), and only for a model id predictors/hardware.py actually
        # recognizes -- an unrecognized model id is predictors.hardware's OWN refusal
        # (UnknownModelError/MODEL_UNKNOWN, issue #346), raised later with a clearer,
        # dedicated message; this check must not preempt that with a confusing
        # "precision mismatch" for a model it does not even know.
        if kind == "evo" and model in MODEL_REQUIREMENTS:
            expected = model_requirement(model).precision
            if precision != expected:
                raise ManifestError(
                    f"manifest.json predictor.precision {precision!r} does not match "
                    f"model {model!r}'s required precision {expected!r} "
                    "(predictors/hardware.py's model/GPU matrix is authoritative -- "
                    "nothing reads this field to configure the predictor, so a "
                    "mismatched value would be silently wrong rather than honored).",
                    public="manifest.json predictor.precision does not match the model's required precision",
                )
        return PredictorSpec(
            kind=kind,
            model=model,
            precision=precision,
            device=_str_field(d, "device", "cuda", where="predictor"),
            seed=_int_field(d, "seed", 0, where="predictor"),
        )


def _region_number(d: dict, key: str, default: float, *, whole: bool) -> float:
    """Read a region option from the raw JSON WITHOUT coercing it: a bool, a string, or (for a
    whole-number option) a fractional number is refused rather than turned into 20 or 1."""
    raw = d.get(key, default)
    if isinstance(raw, bool) or not isinstance(raw, int | float) or (whole and not isinstance(raw, int)):
        kind = "a whole number" if whole else "a number"
        raise ManifestError(
            f"manifest.json analysis region options: {key} must be {kind}, got {raw!r}",
            public=f"manifest.json analysis.{key} must be {kind}",
        )
    return raw


@dataclass
class AnalysisSpec:
    context_length: int = field(default=4096, metadata={"json_name": "contextLength"})
    window: int = 8192  # derived by the app; recorded here, never re-derived (job_contract.md §3)
    stride: int = 4096
    direction: Direction = Direction.BOTH_COMBINED
    # The wire field is literally "format", not "trackFormat" (job_contract.md §3's example).
    track_format: TrackFormat = field(default=TrackFormat.BEDGRAPH, metadata={"json_name": "format"})
    # issue #128: "auto" (the GenBank LOCUS line decides) | "linear" | "circular".
    topology: Topology = Topology.AUTO
    # issue #125: the region caller's options (analysis/regions.py). Wire names are the
    # camelCase ones; absent means the defaults.
    region_threshold: float = field(default=DEFAULT_THRESHOLD_BITS, metadata={"json_name": "regionThreshold"})
    region_min_length: int = field(default=DEFAULT_MIN_LENGTH, metadata={"json_name": "regionMinLength"})
    region_merge_gap: int = field(default=DEFAULT_MERGE_GAP, metadata={"json_name": "regionMergeGap"})

    # issue #704 / #191: the GC-percent track's centred window, in bases (odd); read only when
    # the `gc` output is named.
    gc_window: int = field(default=DEFAULT_GC_WINDOW, metadata={"json_name": "gcWindow"})

    # issue #126: window sizes (odd numbers of bases) of the smoothed entropy tracks; [] = none.
    smoothing_windows: list[int] = field(
        default_factory=lambda: list(DEFAULT_SMOOTHING_WINDOWS), metadata={"json_name": "smoothingWindows"}
    )

    @staticmethod
    def from_dict(d: dict) -> AnalysisSpec:
        raw_direction = _str_field(d, "direction", Direction.BOTH_COMBINED.value, where="analysis")
        direction = _DIRECTION_ALIASES.get(raw_direction)
        if direction is None:
            valid = sorted({*_DIRECTION_ALIASES.keys()})
            raise ManifestError(
                f"manifest.json analysis.direction {raw_direction!r} is not one of {valid}",
                public=f"manifest.json analysis.direction is not one of {valid}",
            )
        raw_track_format = _str_field(d, "format", TrackFormat.BEDGRAPH.value, where="analysis")
        track_format = _TRACK_FORMAT_VALUES.get(raw_track_format)
        if track_format is None:
            valid = sorted(_TRACK_FORMAT_VALUES.keys())
            raise ManifestError(
                f"manifest.json analysis.format {raw_track_format!r} is not one of {valid}",
                public=f"manifest.json analysis.format is not one of {valid}",
            )
        raw_topology = _str_field(d, "topology", Topology.AUTO.value, where="analysis")
        try:
            topology = Topology(raw_topology)
        except ValueError:
            valid = sorted(t.value for t in Topology)
            raise ManifestError(
                f"manifest.json analysis.topology {raw_topology!r} is not one of {valid}",
                public=f"manifest.json analysis.topology is not one of {valid}",
            ) from None
        raw_windows = d.get("smoothingWindows", list(DEFAULT_SMOOTHING_WINDOWS))
        if not isinstance(raw_windows, list):
            raise ManifestError(
                f"manifest.json analysis.smoothingWindows {raw_windows!r} must be a list of odd whole "
                "numbers (smoothing windows, in bases), for example [51]",
                public="manifest.json analysis.smoothingWindows must be a list of odd whole "
                "numbers (smoothing windows, in bases), for example [51]",
            )
        try:
            smoothing_windows = list(validate_smoothing_windows(raw_windows))
        except ValueError as exc:
            raise ManifestError(
                f"manifest.json analysis.smoothingWindows: {exc}",
                public="manifest.json analysis.smoothingWindows is not valid",
            ) from exc
        gc_window = d.get("gcWindow", DEFAULT_GC_WINDOW)
        if isinstance(gc_window, bool) or not isinstance(gc_window, int):
            raise ManifestError(
                f"manifest.json analysis.gcWindow {gc_window!r} must be an odd whole number",
                public="manifest.json analysis.gcWindow must be an odd whole number",
            )
        try:
            validate_smoothing_windows([gc_window])
        except ValueError as exc:
            raise ManifestError(
                f"manifest.json analysis.gcWindow: {exc}",
                public="manifest.json analysis.gcWindow is not valid",
            ) from exc
        region_threshold = _region_number(d, "regionThreshold", DEFAULT_THRESHOLD_BITS, whole=False)
        region_min_length = _region_number(d, "regionMinLength", DEFAULT_MIN_LENGTH, whole=True)
        region_merge_gap = _region_number(d, "regionMergeGap", DEFAULT_MERGE_GAP, whole=True)
        try:
            validate_region_options(
                threshold=float(region_threshold),
                min_length=int(region_min_length),
                merge_gap=int(region_merge_gap),
            )
        except ValueError as exc:
            raise ManifestError(
                f"manifest.json analysis region options: {exc}",
                public="manifest.json analysis region options are not valid",
            ) from exc
        context_length = _int_field(d, "contextLength", 4096, where="analysis")
        window = _int_field(d, "window", 8192, where="analysis")
        stride = _int_field(d, "stride", 4096, where="analysis")
        # issue #345, RE-DECIDED 2026-09-19: an earlier pass on this issue concluded no
        # cross-check was needed, reasoning that S = W - K is a pure function of the same
        # two inputs (context_length, ceiling) that already determine window, so nothing
        # could ever independently disagree. That reasoning addressed whether the WORKER's
        # own windowing math could drift -- it cannot -- but missed the actual bug: a
        # manifest's declared `stride` is a THIRD, independently-writable value nothing
        # here ever reads (build_run_config only reads `window`, never `stride`), so a
        # manifest declaring a `stride` that disagrees with `window - contextLength` was
        # silently ignored, not refused. Cross-checked here the same way
        # `predictor.precision` (issue #343) already is: a disagreement is the app's own
        # arithmetic mistake in a value nothing downstream needs, but a silently-ignored
        # wrong value is still a wrong contract, not a harmless one.
        expected_stride = window - context_length
        if stride != expected_stride:
            raise ManifestError(
                f"manifest.json analysis.stride {stride!r} does not match "
                f"window ({window}) - contextLength ({context_length}) = {expected_stride} "
                "(stride is derived, never independently chosen -- see docs/science_and_formats.md "
                "section 3; recompute it as window - contextLength before writing the manifest).",
                public="manifest.json analysis.stride does not match window - contextLength "
                "(stride is derived: recompute it as window - contextLength)",
            )
        return AnalysisSpec(
            context_length=context_length,
            window=window,
            stride=stride,
            direction=direction,
            track_format=track_format,
            topology=topology,
            smoothing_windows=smoothing_windows,
            gc_window=gc_window,
            region_threshold=region_threshold,
            # whole numbers: _region_number(whole=True) already refused any other value
            region_min_length=int(region_min_length),
            region_merge_gap=int(region_merge_gap),
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
            max_run_seconds=_int_field(d, "maxRunSeconds", 14400, where="limits"),
            cancel_poll_seconds=_int_field(d, "cancelPollSeconds", 10, where="limits"),
            heartbeat_seconds=_int_field(d, "heartbeatSeconds", 30, where="limits"),
            max_inputs=_int_field(d, "maxInputs", DEFAULT_MAX_INPUTS, where="limits"),
            max_total_nt=_int_field(d, "maxTotalNt", DEFAULT_MAX_TOTAL_NT, where="limits"),
        )


@dataclass
class Lifecycle:
    after_task: str = field(default="stop", metadata={"json_name": "afterTask"})  # stop | delete | keep
    keep_alive_minutes: int = field(default=0, metadata={"json_name": "keepAliveMinutes"})
    after_keep_alive: str = field(default="stop", metadata={"json_name": "afterKeepAlive"})

    @staticmethod
    def from_dict(d: dict) -> Lifecycle:
        return Lifecycle(
            after_task=_str_field(d, "afterTask", "stop", where="lifecycle"),
            keep_alive_minutes=_int_field(d, "keepAliveMinutes", 0, where="lifecycle"),
            after_keep_alive=_str_field(d, "afterKeepAlive", "stop", where="lifecycle"),
        )


@dataclass
class StoreSpec:
    kind: str  # "gcs" | "localdir"
    bucket: str = ""
    prefix: str = ""
    root: str = ""

    @staticmethod
    def from_dict(d: dict) -> StoreSpec:
        kind = _required_str(d, "kind", where="store")
        if kind == "gcs":
            return StoreSpec(
                kind=kind,
                bucket=_required_str(d, "bucket", where="store"),
                prefix=_required_str(d, "prefix", where="store"),
            )
        if kind == "localdir":
            return StoreSpec(kind=kind, root=_required_str(d, "root", where="store"))
        raise ManifestError(
            f"manifest.json store.kind {kind!r} is not 'gcs' or 'localdir'",
            public="manifest.json store.kind is not 'gcs' or 'localdir'",
        )


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
        return WorkerRef(
            image=_str_field(d, "image", "", where="worker"),
            version=_str_field(d, "version", "", where="worker"),
        )


@dataclass
class PresetSpec:
    """``manifest.json``'s optional ``preset`` (issue #704 / #136): which option preset the app
    used. Provenance only: the options themselves are already in the other fields."""

    id: str
    name: str

    @staticmethod
    def from_dict(d: object) -> PresetSpec:
        if not isinstance(d, dict):
            raise ManifestError(
                f"manifest.json preset must be an object with id and name, got {d!r}",
                public="manifest.json preset must be an object with id and name",
            )
        out: dict[str, str] = {}
        for key in ("id", "name"):
            raw = d.get(key)
            if not isinstance(raw, str) or not raw.strip():
                raise ManifestError(
                    f"manifest.json preset.{key} must be a non-empty string, got {raw!r}",
                    public=f"manifest.json preset.{key} must be a non-empty string",
                )
            out[key] = raw
        return PresetSpec(id=out["id"], name=out["name"])


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
    preset: PresetSpec | None = None

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
        except ValueError as exc:
            # e.g. an integer of more than 4300 digits: a plain ValueError, not a JSONDecodeError.
            raise ManifestError(
                f"manifest.json is not valid JSON: {exc}",
                public="manifest.json is not valid JSON (a number is too large to read)",
            ) from exc

        if not isinstance(d, dict):
            raise ManifestError("manifest.json must be a JSON object")
        schema = _require(d, "schema", where="")
        if isinstance(schema, bool) or not isinstance(schema, int):
            raise ManifestError(
                f"manifest.json 'schema' must be a whole number, got {schema!r}",
                public="manifest.json 'schema' must be a whole number",
            )
        if schema not in SUPPORTED_SCHEMA_VERSIONS:
            raise ManifestSchemaError(schema)

        job_id = _required_str(d, "jobId", where="")
        inputs_raw = _require(d, "inputs", where="")
        if not isinstance(inputs_raw, list) or not inputs_raw:
            raise ManifestError("manifest.json 'inputs' must be a non-empty array")
        for entry in inputs_raw:
            if not isinstance(entry, dict):
                raise ManifestError(
                    f"manifest.json 'inputs' entries must be objects, got {entry!r}",
                    public="manifest.json 'inputs' entries must be objects",
                )
        inputs = [InputSpec.from_dict(i) for i in inputs_raw]

        predictor = PredictorSpec.from_dict(_object_field(d, "predictor", where=""))
        analysis = AnalysisSpec.from_dict(_object_field(d, "analysis", where=""))
        outputs_raw = d.get("outputs", [])
        if not isinstance(outputs_raw, list) or not all(isinstance(o, str) for o in outputs_raw):
            raise ManifestError(
                f"manifest.json 'outputs' must be a list of text names, got {outputs_raw!r}",
                public="manifest.json 'outputs' must be a list of text names",
            )
        outputs = list(outputs_raw)
        limits = Limits.from_dict(_object_field(d, "limits", where=""))
        lifecycle = Lifecycle.from_dict(_object_field(d, "lifecycle", where=""))
        store_raw = _require(d, "store", where="")
        if not isinstance(store_raw, dict):
            raise ManifestError(
                f"manifest.json 'store' must be an object, got {store_raw!r}",
                public="manifest.json 'store' must be an object",
            )
        store = StoreSpec.from_dict(store_raw)
        worker = WorkerRef.from_dict(_object_field(d, "worker", where=""))
        preset = PresetSpec.from_dict(d["preset"]) if "preset" in d else None

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
            preset=preset,
        )

    def build_run_config(
        self,
        input_spec: InputSpec,
        *,
        local_input_path: str,
        local_out_dir: str,
        local_annotation_path: str | None = None,
    ) -> RunConfig:
        """Build the :class:`~dna_entropy.config.RunConfig` for one input, given where the
        runner has already staged it locally (via the blobstore) and where local outputs
        should land before being uploaded.

        Note on ``outputs`` selection (issue #304): the manifest's ``outputs`` array names
        every desired output FILE individually (e.g. ``"bedgraph"``, ``"tsv"``,
        ``"genes_gff3"``) — see ``docs/job_contract.md`` §3 for the full vocabulary. An
        **empty/omitted** ``outputs`` array means "unspecified", not "nothing wanted", and
        is treated as "everything" (matching this method's pre-existing ``include_tsv``
        fallback) so a manifest that never mentions ``outputs`` at all keeps getting the
        pipeline's full normal output set. A **non-empty** ``outputs`` array is taken
        literally: anything not named is suppressed via the matching ``RunConfig.include_*``
        flag (``config.py``), so a manifest naming fewer desired outputs actually produces
        fewer files on disk, not the full set regardless.
        """
        informat = None if input_spec.informat == "auto" else input_spec.informat
        if input_spec.annotation is not None and local_annotation_path is None:
            raise ManifestError(
                f"inputs[{input_spec.id}].annotation is declared but was not staged "
                "(pass local_annotation_path)"
            )
        outputs_specified = bool(self.outputs)

        def _wanted(name: str) -> bool:
            return (name in self.outputs) if outputs_specified else True

        # analysis.format (found during the #304 audit) was parsed onto AnalysisSpec and
        # never consulted here at all -- this method used to derive the format purely
        # from `outputs` membership, always falling back to bedgraph regardless of what
        # analysis.format said, so a manifest declaring analysis.format="wig" with no
        # `outputs` key silently got bedgraph anyway. `outputs`, when non-empty, remains
        # the literal, authoritative suppression list (issue #304's own scope: a manifest
        # asking for exactly ["bedgraph"] must not get wig just because analysis.format
        # said so); analysis.format is the fallback for the common case where `outputs`
        # says nothing about format at all.
        if outputs_specified:
            track_format = (
                TrackFormat.WIG
                if "wig" in self.outputs and "bedgraph" not in self.outputs
                else TrackFormat.BEDGRAPH
            )
        else:
            track_format = self.analysis.track_format
        # include_track: whichever single format track_format selected must itself have
        # been asked for (not just "some track format or other") before the flag stays on.
        include_track = _wanted(track_format.value)
        # Two DIFFERENT questions, deliberately kept separate: whether Prodigal should run
        # at all (a real compute cost, opt-in only — unaffected by "outputs unspecified
        # means everything", exactly like before this method supported suppression at
        # all) vs. whether the resulting genes.gff3 FILE gets written once genes exist
        # (a plain suppression toggle, like every other include_* flag here).
        explicit_genes_gff3_requested = "genes_gff3" in self.outputs
        include_genes_gff3 = _wanted("genes_gff3")
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
            topology=self.analysis.topology,
            genes=input_spec.genes or explicit_genes_gff3_requested,
            rna=input_spec.rna,
            seed=self.predictor.seed,
            include_tsv=_wanted("tsv"),
            ambiguity_policy=input_spec.ambiguity_policy,
            fasta_records=input_spec.fasta_records,
            include_fasta=_wanted("fasta"),
            include_track=include_track,
            include_geneious=_wanted("geneious_gff3"),
            include_stats=_wanted("stats"),
            include_genbank=_wanted("genbank"),
            include_genes_gff3=include_genes_gff3,
            # issue #127: opt-in only, even when `outputs` is unspecified ("everything")
            include_probs="probs" in self.outputs,
            include_probs_npy="probs_npy" in self.outputs,
            include_smoothed=_wanted("smoothed"),
            smoothing_windows=tuple(self.analysis.smoothing_windows),
            include_regions=_wanted("regions"),
            region_threshold=self.analysis.region_threshold,
            region_min_length=self.analysis.region_min_length,
            region_merge_gap=self.analysis.region_merge_gap,
            include_gene_summary=_wanted("gene_summary"),
            # issue #704 / #135, #191, #136
            annotation_path=local_annotation_path if input_spec.annotation is not None else None,
            include_gc="gc" in self.outputs,  # opt-in only, like probs
            gc_window=self.analysis.gc_window,
            include_codons="codons" in self.outputs,
            preset_id=self.preset.id if self.preset else None,
            preset_name=self.preset.name if self.preset else None,
        )
