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


@pytest.fixture
def shipped() -> dict:
    return json.loads(SHIPPED.read_text(encoding="utf-8"))


def test_g2_standard_8_is_cores_plus_memory_plus_one_gpu_on_demand_and_spot(shipped):
    doc = gen.resolve(g2_and_disk(), shipped, "2026-10-16")

    machine = doc["machines"]["g2-standard-8"]
    assert machine["onDemandUsdPerHour"] == pytest.approx(8 * 0.03 + 32 * 0.004 + 0.5)
    assert machine["spotUsdPerHour"] == pytest.approx(8 * 0.01 + 32 * 0.001 + 0.1)
    assert doc["disk"]["usdPerGbMonth"] == pytest.approx(0.12)
    assert doc["asOf"] == "2026-10-16"


def test_machines_the_catalog_does_not_price_keep_the_previous_row_and_the_source_names_them(shipped):
    doc = gen.resolve(g2_and_disk(), shipped, "2026-10-16")

    assert doc["machines"]["a2-highgpu-1g"] == shipped["machines"]["a2-highgpu-1g"]
    assert "a2-highgpu-1g" in doc["source"]


def test_an_unresolved_spot_price_keeps_the_previous_spot_price_and_says_so(shipped):
    skus = [s for s in g2_and_disk() if s["category"]["usageType"] != "Preemptible"]

    doc = gen.resolve(skus, shipped, "2026-10-16")

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

    doc = gen.resolve(skus, shipped, "2026-10-16")

    assert doc["machines"]["g2-standard-8"]["onDemandUsdPerHour"] == pytest.approx(8 * 0.03 + 32 * 0.004 + 0.5)


def test_a_catalog_with_no_disk_or_no_machine_is_refused(shipped):
    assert gen.resolve([], shipped, "2026-10-16") is None
    assert gen.resolve([s for s in g2_and_disk() if "Storage" not in s["category"]["resourceFamily"]], shipped, "2026-10-16") is None


def test_two_prices_for_one_component_is_not_resolved(shipped):
    skus = g2_and_disk() + [sku("Nvidia L4 GPU running in Americas", "Compute", "OnDemand", 7.0)]

    assert gen.resolve(skus, shipped, "2026-10-16") is None


def test_the_output_parses_as_a_price_list_of_the_apps_own_schema(shipped):
    doc = gen.resolve(g2_and_disk(), shipped, "2026-10-16")

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
    pattern = re.compile(r'new\("([\w-]+)", "([^"]+)", (\d+), (\d+), "([^"]+)", "([^"]+)", "([^"]+)"\)')
    found = [(m[0], m[1], int(m[2]), int(m[3]), m[4], m[5], m[6]) for m in pattern.findall(text)]

    assert found, "the scanner found no MachineShape in CatalogPriceResolver.cs, so it checks nothing"
    assert found == [(s.machine_type, s.accelerator, s.vcpus, s.ram_gb, s.core_token, s.ram_token, s.gpu_token) for s in gen.SHAPES]
