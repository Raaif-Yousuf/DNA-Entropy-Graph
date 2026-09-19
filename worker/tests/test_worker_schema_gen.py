"""Tests for worker/schema_gen.py: dataclass -> JSON Schema, the #39 generator's core."""

from __future__ import annotations

import enum
from dataclasses import dataclass, field

import pytest

from dna_entropy.worker.schema_gen import dataclass_to_schema, top_level_schema, type_to_schema

# --- scalar types ------------------------------------------------------------------


def test_str_type() -> None:
    assert type_to_schema(str) == {"type": "string"}


def test_int_type() -> None:
    assert type_to_schema(int) == {"type": "integer"}


def test_float_type() -> None:
    assert type_to_schema(float) == {"type": "number"}


def test_bool_type_is_boolean_not_integer() -> None:
    # bool is a subclass of int in Python -- must not be misclassified as {"type": "integer"}.
    assert type_to_schema(bool) == {"type": "boolean"}


def test_dict_type() -> None:
    assert type_to_schema(dict) == {"type": "object"}


# --- list[X] -------------------------------------------------------------------------


def test_list_of_str() -> None:
    assert type_to_schema(list[str]) == {"type": "array", "items": {"type": "string"}}


def test_list_of_int() -> None:
    assert type_to_schema(list[int]) == {"type": "array", "items": {"type": "integer"}}


# --- Optional / X | None ---------------------------------------------------------------


def test_optional_str_is_genuinely_nullable() -> None:
    # NOT {"type": "string"} — that would reject an actual `None` value, which is exactly
    # what an Optional field's default IS. See type_to_schema's own docstring for the bug
    # this regression-guards.
    assert type_to_schema(str | None) == {"type": ["string", "null"]}


def test_optional_int_is_genuinely_nullable() -> None:
    assert type_to_schema(int | None) == {"type": ["integer", "null"]}


def test_optional_dict_is_genuinely_nullable() -> None:
    assert type_to_schema(dict | None) == {"type": ["object", "null"]}


def test_a_none_value_actually_validates_against_an_optional_schema() -> None:
    jsonschema = pytest.importorskip("jsonschema")
    schema = type_to_schema(dict | None)
    jsonschema.Draft202012Validator(schema).validate(None)  # must not raise
    jsonschema.Draft202012Validator(schema).validate({"a": 1})  # the non-null case too


def test_non_optional_str_is_unaffected() -> None:
    assert type_to_schema(str) == {"type": "string"}


# --- Enum --------------------------------------------------------------------------


class _Color(str, enum.Enum):
    RED = "red"
    BLUE = "blue"


def test_str_enum_becomes_a_string_enum_schema() -> None:
    assert type_to_schema(_Color) == {"type": "string", "enum": ["red", "blue"]}


# --- nested dataclass ------------------------------------------------------------------


@dataclass
class _Inner:
    name: str
    count: int = 0


@dataclass
class _Outer:
    inner: _Inner
    label: str
    optional_label: str | None = None


def test_dataclass_becomes_an_object_schema_with_properties() -> None:
    schema = dataclass_to_schema(_Outer)
    assert schema["type"] == "object"
    assert schema["title"] == "_Outer"
    assert set(schema["properties"].keys()) == {"inner", "label", "optional_label"}
    assert schema["properties"]["inner"]["type"] == "object"
    assert schema["properties"]["inner"]["properties"]["name"] == {"type": "string"}


def test_required_fields_exclude_ones_with_defaults() -> None:
    schema = dataclass_to_schema(_Outer)
    assert "label" in schema["required"]
    assert "inner" in schema["required"]
    assert "optional_label" not in schema["required"]


def test_nested_dataclass_field_defaults_are_also_excluded_from_required() -> None:
    schema = dataclass_to_schema(_Inner)
    assert "name" in schema["required"]
    assert "count" not in schema["required"]


@dataclass
class _WithList:
    items: list[str] = field(default_factory=list)
    required_field: int = 1


def test_default_factory_field_is_not_required() -> None:
    schema = dataclass_to_schema(_WithList)
    assert "items" not in schema.get("required", [])


# --- json_name / json_exclude metadata: Python snake_case vs. wire camelCase ------------


@dataclass
class _WireNamed:
    job_id: str = field(metadata={"json_name": "jobId"})
    plain: int = 0
    internal_only: str = field(default="", metadata={"json_exclude": True})


def test_json_name_metadata_renames_the_schema_property() -> None:
    schema = dataclass_to_schema(_WireNamed)
    assert "jobId" in schema["properties"]
    assert "job_id" not in schema["properties"]
    assert "plain" in schema["properties"]  # unrenamed fields keep their Python name


def test_json_name_metadata_renames_in_required_too() -> None:
    schema = dataclass_to_schema(_WireNamed)
    assert "jobId" in schema["required"]
    assert "job_id" not in schema["required"]


def test_json_exclude_metadata_drops_the_field_entirely() -> None:
    schema = dataclass_to_schema(_WireNamed)
    assert "internal_only" not in schema["properties"]
    assert "internal_only" not in schema.get("required", [])


# --- top_level_schema ------------------------------------------------------------------


def test_top_level_schema_has_schema_and_id_envelope() -> None:
    schema = top_level_schema(_Outer, schema_id="https://example.com/outer.schema.json", title="Outer")
    assert schema["$schema"] == "https://json-schema.org/draft/2020-12/schema"
    assert schema["$id"] == "https://example.com/outer.schema.json"
    assert schema["title"] == "Outer"
    assert schema["type"] == "object"
    assert "properties" in schema


# --- the real contract dataclasses, end to end -----------------------------------------


def test_job_manifest_schema_generates_without_error() -> None:
    from dna_entropy.worker.manifest import JobManifest

    schema = top_level_schema(JobManifest, schema_id="x", title="JobManifest")
    assert "schema" in schema["properties"]
    assert "inputs" in schema["properties"]
    assert schema["properties"]["inputs"]["type"] == "array"
    assert "jobId" in schema["required"]


def test_status_document_schema_generates_without_error() -> None:
    from dna_entropy.worker.status import StatusDocument

    schema = top_level_schema(StatusDocument, schema_id="x", title="StatusDocument")
    assert "stage" in schema["properties"]
    assert "heartbeatSeq" in schema["properties"]
    assert schema["properties"]["vm"]["type"] == "object"


def test_job_result_schema_generates_without_error() -> None:
    from dna_entropy.worker.runner import JobResult

    schema = top_level_schema(JobResult, schema_id="x", title="JobResult")
    assert "status" in schema["properties"]
    assert schema["properties"]["inputs"]["type"] == "array"
    assert schema["properties"]["inputs"]["items"]["type"] == "object"


# --- validating real sample data against the generated schema (jsonschema) -------------


def test_generated_manifest_schema_validates_a_real_manifest_dict() -> None:
    jsonschema = pytest.importorskip("jsonschema")
    from dna_entropy.worker.manifest import JobManifest

    schema = top_level_schema(JobManifest, schema_id="x", title="JobManifest")
    sample = {
        "schema": 1,
        "jobId": "20260918-142233-k7q2vx",
        "inputs": [{"id": "in1", "path": "input/x.gb", "name": "x"}],
        "predictor": {"kind": "mock", "seed": 0},
        "analysis": {"contextLength": 4096, "window": 8192, "stride": 4096, "direction": "both-combined"},
        "outputs": ["fasta"],
        "limits": {"maxRunSeconds": 14400},
        "lifecycle": {"afterTask": "stop"},
        "store": {"kind": "localdir"},
        "worker": {"image": "x", "version": "1.0.0"},
    }
    jsonschema.Draft202012Validator(schema).validate(sample)
