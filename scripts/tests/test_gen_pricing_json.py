"""Tests for scripts/gen_pricing_json.py (#214). Payloads: the documented Catalog API shape, invented prices, no live read."""

from __future__ import annotations

import json
import re
import sys
from pathlib import Path

import pytest

SCRIPTS_DIR = Path(__file__).resolve().parent.parent
REPO_ROOT = SCRIPTS_DIR.parent
sys.path.insert(0, str(SCRIPTS_DIR))

import gen_pricing_json as gen  # noqa: E402

RESOLVER_CS = REPO_ROOT / "app" / "src" / "DnaEntropyGraph.Core" / "Cost" / "CatalogPriceResolver.cs"
SHIPPED = REPO_ROOT / "app" / "src" / "DnaEntropyGraph.App" / "Assets" / "pricing.json"


def sku(
    description: str, family: str, usage: str, price: float, unit: str = "h", regions: tuple[str, ...] = ("us-central1",), currency: str = "USD"
) -> dict:
    units = int(price)
    nanos = round((price - units) * 1_000_000_000)
    return {
        "skuId": "id-" + description,
        "description": description,
        "category": {"resourceFamily": family, "resourceGroup": "x", "usageType": usage},
        "serviceRegions": list(regions),
        "pricingInfo": [
            {
                "pricingExpression": {
                    "usageUnit": unit,
                    "tieredRates": [
                        {"startUsageAmount": 100, "unitPrice": {"currencyCode": currency, "units": "0", "nanos": 1}},
                        {"startUsageAmount": 0, "unitPrice": {"currencyCode": currency, "units": str(units), "nanos": nanos}},
                    ],
                }
            }
        ],
    }


def g2_and_disk() -> list[dict]:
    return [
        sku("G2 Instance Core running in Americas", "Compute", "OnDemand", 0.03),
        sku("G2 Instance Ram running in Americas", "Compute", "OnDemand", 0.004, "GBy.h"),
        sku("Nvidia L4 GPU running in Americas", "Compute", "OnDemand", 0.5),
        sku("Spot Preemptible G2 Instance Core running in Americas", "Compute", "Preemptible", 0.01),
        sku("Spot Preemptible G2 Instance Ram running in Americas", "Compute", "Preemptible", 0.001, "GBy.h"),
        sku("Nvidia L4 GPU attached to Spot Preemptible VMs running in Americas", "Compute", "Preemptible", 0.1),
        sku("Balanced PD Capacity", "Storage", "OnDemand", 0.12, "GBy.mo"),
        sku("G2 Instance Core running in Frankfurt", "Compute", "OnDemand", 9.99, regions=("europe-west3",)),
        sku("Regional Balanced PD Capacity", "Storage", "OnDemand", 9.99, "GBy.mo"),
        sku("G2 Custom Instance Core running in Americas", "Compute", "OnDemand", 9.99),
        sku("G2 Instance Core running in Americas", "Compute", "OnDemand", 9.99, currency="EUR"),
    ]


def resolve_ok(skus: list[dict], shipped: dict, as_of: str) -> dict:
    doc = gen.resolve(skus, shipped, as_of)
    assert doc is not None
    return doc


@pytest.fixture
def shipped() -> dict:
    return json.loads(SHIPPED.read_text(encoding="utf-8"))


def test_g2_standard_8_is_cores_plus_memory_plus_one_gpu_on_demand_and_spot(shipped):
    doc = resolve_ok(g2_and_disk(), shipped, "2026-10-16")

    machine = doc["machines"]["g2-standard-8"]
    assert machine["onDemandUsdPerHour"] == pytest.approx(8 * 0.03 + 32 * 0.004 + 0.5)
    assert machine["spotUsdPerHour"] == pytest.approx(8 * 0.01 + 32 * 0.001 + 0.1)
    assert doc["disk"]["usdPerGbMonth"] == pytest.approx(0.12)
    assert doc["asOf"] == "2026-10-16"


def test_machines_the_catalog_does_not_price_keep_the_previous_row_and_the_source_names_them(shipped):
    doc = resolve_ok(g2_and_disk(), shipped, "2026-10-16")

    assert doc["machines"]["a2-highgpu-1g"] == shipped["machines"]["a2-highgpu-1g"]
    assert "a2-highgpu-1g" in doc["source"]


def test_an_unresolved_spot_price_keeps_the_previous_spot_price_and_says_so(shipped):
    skus = [s for s in g2_and_disk() if s["category"]["usageType"] != "Preemptible"]

    doc = resolve_ok(skus, shipped, "2026-10-16")

    assert doc["machines"]["g2-standard-8"]["spotUsdPerHour"] == shipped["machines"]["g2-standard-8"]["spotUsdPerHour"]
    assert "Spot price kept" in doc["source"] and "g2-standard-8" in doc["source"]


def test_the_pricing_entry_in_force_wins_whatever_the_order(shipped):
    skus = g2_and_disk()
    core = skus[0]
    base = core["pricingInfo"][0]
    old = json.loads(json.dumps(base))
    old["effectiveTime"] = "2020-01-01T00:00:00Z"
    future = json.loads(json.dumps(base))
    future["effectiveTime"] = "2099-01-01T00:00:00Z"
    future["pricingExpression"]["tieredRates"][1]["unitPrice"]["nanos"] = 900_000_000
    now_entry = json.loads(json.dumps(base))
    now_entry["effectiveTime"] = "2026-10-01T00:00:00Z"
    core["pricingInfo"] = [now_entry, old, future]

    doc = resolve_ok(skus, shipped, "2026-10-16")

    assert doc["machines"]["g2-standard-8"]["onDemandUsdPerHour"] == pytest.approx(8 * 0.03 + 32 * 0.004 + 0.5)


def test_a_catalog_with_no_machine_is_refused(shipped):
    assert gen.resolve([], shipped, "2026-10-16") is None


def test_a_missing_or_ambiguous_disk_price_keeps_the_shipped_disk_price_and_the_machines(shipped):
    # Issue #214 review M2: one piece the catalog cannot give must not cost the pieces it can.
    no_disk = [s for s in g2_and_disk() if "Storage" not in s["category"]["resourceFamily"]]
    two_disks = g2_and_disk() + [sku("Balanced PD Capacity", "Storage", "OnDemand", 0.99, "GBy.mo")]

    for skus in (no_disk, two_disks):
        doc = resolve_ok(skus, shipped, "2026-10-16")
        assert doc["disk"]["usdPerGbMonth"] == shipped["disk"]["usdPerGbMonth"]
        assert doc["machines"]["g2-standard-8"]["onDemandUsdPerHour"] == pytest.approx(8 * 0.03 + 32 * 0.004 + 0.5)
        assert "disk price is kept" in doc["source"]


def test_a2_ultra_is_kept_from_the_shipped_list_while_its_shape_is_a_theory(shipped):
    # Issue #214 review M3: the C# resolver does the same (CatalogPriced: false).
    skus = g2_and_disk() + [
        sku("A2 Instance Core running in Americas", "Compute", "OnDemand", 0.05),
        sku("A2 Instance Ram running in Americas", "Compute", "OnDemand", 0.006, "GBy.h"),
        sku("Nvidia Tesla A100 80GB GPU running in Americas", "Compute", "OnDemand", 4.0),
    ]
    shipped = json.loads(json.dumps(shipped))
    shipped["machines"]["a2-ultragpu-1g"] = {"accelerator": "1x NVIDIA A100 80GB", "onDemandUsdPerHour": 5.05, "spotUsdPerHour": None}

    doc = resolve_ok(skus, shipped, "2026-10-16")

    assert doc["machines"]["a2-ultragpu-1g"]["onDemandUsdPerHour"] == 5.05
    assert "a2-ultragpu-1g" in doc["source"]


def test_two_prices_for_one_component_is_not_resolved(shipped):
    skus = g2_and_disk() + [sku("Nvidia L4 GPU running in Americas", "Compute", "OnDemand", 7.0)]

    assert gen.resolve(skus, shipped, "2026-10-16") is None


def test_the_output_parses_as_a_price_list_of_the_apps_own_schema(shipped):
    doc = resolve_ok(g2_and_disk(), shipped, "2026-10-16")

    assert doc["schema"] == 1 and doc["currency"] == "USD"
    assert all(m["onDemandUsdPerHour"] > 0 for m in doc["machines"].values())
    assert all(m["spotUsdPerHour"] is None or m["spotUsdPerHour"] <= m["onDemandUsdPerHour"] for m in doc["machines"].values())


def test_main_writes_the_file_from_pages_and_leaves_it_alone_on_a_bad_catalog(tmp_path, shipped, capsys):
    out = tmp_path / "pricing.json"
    out.write_text(json.dumps(shipped), encoding="utf-8")
    pages = tmp_path / "pages.json"
    pages.write_text(json.dumps([{"skus": g2_and_disk()[:3], "nextPageToken": "p2"}, {"skus": g2_and_disk()[3:]}]), encoding="utf-8")

    assert gen.main(["--catalog", str(pages), "--shipped", str(SHIPPED), "--out", str(out), "--as-of", "2026-10-16"]) == 0
    written = json.loads(out.read_text(encoding="utf-8"))
    assert written["asOf"] == "2026-10-16"
    assert out.read_bytes().endswith(b"\n") and b"\r" not in out.read_bytes()

    bad = tmp_path / "bad.json"
    bad.write_text(json.dumps({"skus": []}), encoding="utf-8")
    before = out.read_bytes()
    assert gen.main(["--catalog", str(bad), "--shipped", str(SHIPPED), "--out", str(out), "--as-of", "2026-10-17"]) == 1
    assert out.read_bytes() == before
    assert gen.main(["--catalog", str(tmp_path / "missing.json"), "--as-of", "2026-10-17"]) == 2
    capsys.readouterr()


def test_machine_shapes_match_the_csharp_resolver():
    text = RESOLVER_CS.read_text(encoding="utf-8")
    pattern = re.compile(r'new\("([\w-]+)", "([^"]+)", (\d+), (\d+), "([^"]+)", "([^"]+)", "([^"]+)"(?:, CatalogPriced: (true|false))?\)')
    found = [(m[0], m[1], int(m[2]), int(m[3]), m[4], m[5], m[6], m[7] != "false") for m in pattern.findall(text)]

    assert found, "the scanner found no MachineShape in CatalogPriceResolver.cs, so it checks nothing"
    assert found == [
        (s.machine_type, s.accelerator, s.vcpus, s.ram_gb, s.core_token, s.ram_token, s.gpu_token, s.catalog_priced) for s in gen.SHAPES
    ]


def _first(pattern: str, text: str) -> str:
    found = re.search(pattern, text)
    assert found, f"the scanner found nothing for {pattern} in CatalogPriceResolver.cs, so it checks nothing"
    return found[1]


def test_the_matching_rules_match_the_csharp_resolver_not_only_the_shapes():
    # Issue #214 review L8: the excluded words, usage types, units and name suffixes are duplicated; changing one side alone goes red.
    text = RESOLVER_CS.read_text(encoding="utf-8")
    excluded = re.search(r"Excluded = \[([^\]]+)\]", text)
    assert excluded, "the scanner found no Excluded list in CatalogPriceResolver.cs, so it checks nothing"
    assert tuple(re.findall(r'"([^"]+)"', excluded[1])) == gen.EXCLUDED
    assert _first(r'const string OnDemand = "([^"]+)"', text) == gen.ON_DEMAND
    assert _first(r'const string Spot = "([^"]+)"', text) == gen.SPOT
    assert _first(r"Version = (\d+);", text) == str(gen.RESOLVER_VERSION)
    # The disk pick comes first in the C# file, then the core, memory and GPU picks (the order of gen.PICKS).
    picks = re.findall(r's\.UsageUnit == "([^"]+)" && Named\(s, (?:shape\.\w+(?: \+ "( running)")?|"([^"]+)")\)', text)
    assert [(unit, suffix or disk) for unit, suffix, disk in picks] == [(gen.DISK_UNIT, gen.DISK_TOKEN), *gen.PICKS]
    assert gen.PICKS == (("h", " running"), ("GBy.h", " running"), ("h", ""))
    assert gen.DISK_TOKEN == "Balanced PD Capacity"
    assert gen.DISK_UNIT == "GBy.mo"


def test_a_date_only_or_naive_effective_time_is_read_as_utc_and_never_raises(shipped):
    skus = g2_and_disk()
    base = skus[0]["pricingInfo"][0]
    dated = json.loads(json.dumps(base))
    dated["effectiveTime"] = "2026-10-01"
    naive = json.loads(json.dumps(base))
    naive["effectiveTime"] = "2020-01-01T00:00:00"
    future = json.loads(json.dumps(base))
    future["effectiveTime"] = "2099-01-01"
    future["pricingExpression"]["tieredRates"][1]["unitPrice"]["nanos"] = 900_000_000
    skus[0]["pricingInfo"] = [naive, future, dated]

    doc = resolve_ok(skus, shipped, "2026-10-16")

    assert doc["machines"]["g2-standard-8"]["onDemandUsdPerHour"] == pytest.approx(8 * 0.03 + 32 * 0.004 + 0.5)


def test_nine_digit_fractional_seconds_in_an_effective_time_are_read_not_dropped(shipped):
    # Issue #214 review L10: MEASURED on Python 3.12, datetime.fromisoformat already truncates 9 digits; this keeps it so.
    skus = g2_and_disk()
    base = skus[0]["pricingInfo"][0]
    old = json.loads(json.dumps(base))
    old["effectiveTime"] = "2020-01-01T00:00:00Z"
    newer = json.loads(json.dumps(base))
    newer["effectiveTime"] = "2026-10-01T00:00:00.123456789Z"
    newer["pricingExpression"]["tieredRates"][1]["unitPrice"]["nanos"] = 40_000_000
    skus[0]["pricingInfo"] = [old, newer]

    doc = resolve_ok(skus, shipped, "2026-10-16")

    assert doc["machines"]["g2-standard-8"]["onDemandUsdPerHour"] == pytest.approx(8 * 0.04 + 32 * 0.004 + 0.5)
