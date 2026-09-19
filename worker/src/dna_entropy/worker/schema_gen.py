"""Generate JSON Schema documents FROM the worker's own dataclasses.

Issue #39: ``docs/contract/{manifest,status,result}.schema.json`` must be *generated*
from ``manifest.py``'s ``JobManifest``, ``status.py``'s ``StatusDocument``, and
``runner.py``'s ``JobResult`` — never hand-typed beside them, so a field rename/add/remove
on the Python side is impossible to forget to reflect in the checked-in schema (the
``--check`` mode in ``scripts/gen_manifest_schema.py`` fails CI the moment they drift).

This module has NO third-party dependencies (not even inside the worker package itself —
only the stdlib ``dataclasses``/``enum``/``typing``), so the standalone
``uv run --with jsonschema python scripts/gen_manifest_schema.py`` CI invocation (which
does not install the worker package at all) can import it by adding ``worker/src`` to
``sys.path``, exactly like the prototype's own ``keep_gpu.py`` did for the same reason.
"""

from __future__ import annotations

import dataclasses
import enum
import types
import typing

JsonSchema = dict


def _is_optional(tp: object) -> tuple[bool, object]:
    """Return ``(is_optional, inner_type)`` for ``X | None`` / ``Optional[X]``; otherwise
    ``(False, tp)``. Handles both ``typing.Union`` and PEP 604 ``types.UnionType``."""
    origin = typing.get_origin(tp)
    if origin is typing.Union or origin is getattr(types, "UnionType", None):
        args = [a for a in typing.get_args(tp) if a is not type(None)]
        if len(args) == 1 and type(None) in typing.get_args(tp):
            return True, args[0]
        if len(args) == 1:
            return False, args[0]  # a single-member Union with no None — treat as itself
    return False, tp


def type_to_schema(tp: object) -> JsonSchema:
    """Map a Python type annotation to a JSON Schema fragment.

    Supports exactly what this package's contract dataclasses use: ``str``/``int``/
    ``float``/``bool``, ``dict``, ``list[X]``, ``X | None``, a ``(str, Enum)`` subclass
    (-> a string enum), and a nested ``@dataclass`` (-> a nested object schema, recursively).

    ``X | None`` produces a genuinely NULLABLE schema (``"type": [<X's type>, "null"]``),
    not just ``X``'s own schema with the ``None`` silently dropped — a real bug this
    function had until ``test_real_result_json_validates_against_the_generated_schema``/
    ``test_real_status_json_validates_against_the_generated_schema`` caught it: an
    ``error: dict | None = None`` field's ACTUAL ``None`` value failed validation against
    a schema that only ever said ``{"type": "object"}``.
    """
    is_optional, inner = _is_optional(tp)
    schema = _concrete_type_to_schema(inner)
    if not is_optional:
        return schema
    base_type = schema.get("type")
    if isinstance(base_type, str):
        return {**schema, "type": [base_type, "null"]}
    # An enum (its own "type": "string" plus "enum") already falls into the branch above.
    # Anything without a simple string "type" (a recursion-unsupported fragment) falls
    # back to the always-correct, if less compact, anyOf form.
    return {"anyOf": [schema, {"type": "null"}]}


def _concrete_type_to_schema(tp: object) -> JsonSchema:
    """:func:`type_to_schema`'s logic for a type ALREADY known not to be ``X | None``
    (the ``None`` branch, if any, was stripped by the caller)."""
    if tp is str:
        return {"type": "string"}
    if tp is bool:  # MUST be checked before int: bool is a subclass of int in Python
        return {"type": "boolean"}
    if tp is int:
        return {"type": "integer"}
    if tp is float:
        return {"type": "number"}
    if tp is dict or typing.get_origin(tp) is dict:
        return {"type": "object"}
    if tp is list or typing.get_origin(tp) is list:
        args = typing.get_args(tp)
        item_schema = type_to_schema(args[0]) if args else {}
        return {"type": "array", "items": item_schema}
    if isinstance(tp, type) and issubclass(tp, enum.Enum):
        values = [member.value for member in tp]
        return {"type": "string", "enum": values}
    if dataclasses.is_dataclass(tp):
        return dataclass_to_schema(tp)
    # Anything genuinely unrecognized still produces a *valid* (unconstrained) schema
    # fragment rather than raising — a missing type mapping should show up as a loose
    # schema a human notices in review, not a generator that crashes on a new field.
    return {}


def dataclass_to_schema(cls: type, *, title: str | None = None) -> JsonSchema:
    """Build a JSON Schema ``object`` for one ``@dataclass``, recursively.

    A field is ``required`` unless it has a default (``dataclasses.MISSING`` check) OR its
    type is ``X | None`` (an explicitly optional field is never required even without a
    literal default, though every optional field in this codebase also has one).

    **Wire name vs. Python name:** several of this package's contract dataclasses (most of
    ``manifest.py``) keep Pythonic ``snake_case`` attribute names internally while
    ``docs/job_contract.md``'s actual JSON uses ``camelCase`` (e.g. Python ``job_id`` <->
    JSON ``jobId``). A field declares its real wire name via
    ``field(metadata={"json_name": "jobId"})``; this function reads that metadata and uses
    it for the schema's property name (and in ``required``) instead of the Python
    attribute name. ``field(metadata={"json_exclude": True})`` drops a field from the
    schema entirely (for Python-only bookkeeping fields with no wire representation, e.g.
    ``JobManifest.raw``).
    """
    hints = typing.get_type_hints(cls)
    properties: dict[str, JsonSchema] = {}
    required: list[str] = []

    for f in dataclasses.fields(cls):
        if f.metadata.get("json_exclude"):
            continue
        json_name = f.metadata.get("json_name", f.name)
        field_type = hints.get(f.name, f.type)
        properties[json_name] = type_to_schema(field_type)
        is_optional, _ = _is_optional(field_type)
        has_default = f.default is not dataclasses.MISSING or f.default_factory is not dataclasses.MISSING  # type: ignore[misc]
        if not is_optional and not has_default:
            required.append(json_name)

    schema: JsonSchema = {
        "type": "object",
        "title": title or cls.__name__,
        "properties": properties,
    }
    if required:
        schema["required"] = required
    return schema


def top_level_schema(cls: type, *, schema_id: str, title: str) -> JsonSchema:
    """Wrap :func:`dataclass_to_schema` with the ``$schema``/``$id`` envelope a top-level
    document (as opposed to a nested field) needs."""
    body = dataclass_to_schema(cls, title=title)
    return {
        "$schema": "https://json-schema.org/draft/2020-12/schema",
        "$id": schema_id,
        **body,
    }
