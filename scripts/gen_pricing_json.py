"""scripts/gen_pricing_json.py -- regenerate the shipped price list from a Cloud Billing Catalog dump (#214).

    python scripts/gen_pricing_json.py --catalog <skus.json> [--shipped <pricing.json>] [--as-of YYYY-MM-DD] [--out <pricing.json>]

`--catalog` is the JSON the Catalog API returns for Compute Engine (`GET v1/services/6F81-5844-456A/skus?currencyCode=USD`): one
page object, or a list of page objects (every page, in order). The script NEVER calls Google: someone with a project fetches the pages
(for example `gcloud billing`-free: a browser or curl with their own token) and hands the file over. The rules are the app's own
`CatalogPriceResolver` (app/src/DnaEntropyGraph.Core/Cost/CatalogPriceResolver.cs): a core + memory + GPU sum per machine, on demand and
Spot, zonal pd-balanced per GB-month, a component priced two ways or not at all leaves the machine to the previous file. A test
(scripts/tests/test_gen_pricing_json.py) fails if the machine shapes here drift from the C# ones.

Exit codes: 0 written, 1 the catalog priced nothing usable (the output file is left untouched), 2 bad usage or unreadable input.
"""

from __future__ import annotations

import argparse
import json
import sys
from dataclasses import dataclass
from datetime import UTC, datetime, time
from pathlib import Path

DEFAULT_FILE = Path(__file__).resolve().parent.parent / "app" / "src" / "DnaEntropyGraph.App" / "Assets" / "pricing.json"
ON_DEMAND = "OnDemand"
SPOT = "Preemptible"
EXCLUDED = ("commitment", "custom", "sole tenancy", "reserved", "reservation", "regional", "snapshot", "flex", "dws")


@dataclass(frozen=True)
class Shape:
    machine_type: str
    accelerator: str
    vcpus: int
    ram_gb: int
    core_token: str
    ram_token: str
    gpu_token: str
    gpus: int = 1


SHAPES = (
    Shape("g2-standard-8", "1x NVIDIA L4", 8, 32, "G2 Instance Core", "G2 Instance Ram", "Nvidia L4 GPU"),
    Shape("a2-highgpu-1g", "1x NVIDIA A100 40GB", 12, 85, "A2 Instance Core", "A2 Instance Ram", "Nvidia Tesla A100 GPU"),
    Shape("a2-ultragpu-1g", "1x NVIDIA A100 80GB", 12, 170, "A2 Instance Core", "A2 Instance Ram", "Nvidia Tesla A100 80GB GPU"),
)


def flatten(raw: object) -> list[dict]:
    """Every SKU object from a page, or a list of pages."""
    pages = raw if isinstance(raw, list) else [raw]
    skus: list[dict] = []
    for page in pages:
        if not isinstance(page, dict):
            raise ValueError("the catalog file holds something that is not a page object")
        skus.extend(s for s in page.get("skus") or [] if isinstance(s, dict))
    return skus


def _effective(info: dict) -> datetime:
    text = info.get("effectiveTime")
    try:
        parsed = datetime.fromisoformat(str(text).replace("Z", "+00:00"))
        return parsed if parsed.tzinfo else parsed.replace(tzinfo=UTC)
    except ValueError:
        return datetime.min.replace(tzinfo=UTC)


def current_pricing(entries: list[dict], now: datetime) -> dict:
    """The entry in force: the latest whose effective time has come; if all are in the future, the earliest (the app does the same)."""
    entries = [e for e in entries if isinstance(e, dict)]
    if not entries:
        return {}
    in_force = [e for e in entries if _effective(e) <= now]
    return max(in_force, key=_effective) if in_force else min(entries, key=_effective)


def to_rows(skus: list[dict], now: datetime) -> list[dict]:
    """The app's reduction: the pricing entry in force, first tier (starts at zero), dollars only, one row per priced SKU."""
    rows = []
    for sku in skus:
        info = current_pricing(sku.get("pricingInfo") or [], now)
        expression = info.get("pricingExpression") or {}
        tier = next((t for t in expression.get("tieredRates") or [] if float(t.get("startUsageAmount") or 0) == 0), None)
        price = (tier or {}).get("unitPrice") or {}
        if price.get("currencyCode") != "USD" or not sku.get("skuId"):
            continue
        category = sku.get("category") or {}
        rows.append(
            {
                "description": sku.get("description") or "",
                "family": category.get("resourceFamily") or "",
                "usage": category.get("usageType") or "",
                "regions": sku.get("serviceRegions") or [],
                "unit": expression.get("usageUnit") or "",
                "usd": int(price.get("units") or 0) + int(price.get("nanos") or 0) / 1_000_000_000,
            }
        )
    return rows


def _named(row: dict, token: str) -> bool:
    text = row["description"].lower()
    return token.lower() in text and not any(word in text for word in EXCLUDED)


def _single(matches: list[dict]) -> float | None:
    prices = {m["usd"] for m in matches}
    return next(iter(prices)) if len(prices) == 1 else None


def _price(rows: list[dict], shape: Shape, usage: str) -> float | None:
    def pick(unit: str, token: str) -> float | None:
        return _single([r for r in rows if r["family"] == "Compute" and r["usage"] == usage and r["unit"] == unit and _named(r, token)])

    core = pick("h", shape.core_token + " running")
    ram = pick("GBy.h", shape.ram_token + " running")
    gpu = pick("h", shape.gpu_token)
    if core is None or ram is None or gpu is None:
        return None
    return shape.vcpus * core + shape.ram_gb * ram + shape.gpus * gpu


def resolve(skus: list[dict], shipped: dict, as_of: str) -> dict | None:
    """A new price list document, or None when the catalog prices no disk or no machine (the caller keeps the old file)."""
    region = shipped["region"]
    now = datetime.combine(datetime.fromisoformat(as_of).date(), time.max, tzinfo=UTC)
    rows = [r for r in to_rows(skus, now) if r["usd"] > 0 and region.lower() in [x.lower() for x in r["regions"]]]
    disk = _single(
        [r for r in rows if r["family"] == "Storage" and r["usage"] == ON_DEMAND and r["unit"] == "GBy.mo" and _named(r, "Balanced PD Capacity")]
    )
    if disk is None:
        return None

    machines: dict[str, dict] = {}
    kept: list[str] = []
    spot_kept: list[str] = []
    for shape in SHAPES:
        on_demand = _price(rows, shape, ON_DEMAND)
        if on_demand is None:
            if shape.machine_type in shipped["machines"]:
                machines[shape.machine_type] = shipped["machines"][shape.machine_type]
                kept.append(shape.machine_type)
            continue
        spot = _price(rows, shape, SPOT)
        previous = (shipped["machines"].get(shape.machine_type) or {}).get("spotUsdPerHour")
        if spot is None and previous is not None:
            spot = previous
            spot_kept.append(shape.machine_type)
        machines[shape.machine_type] = {
            "accelerator": shape.accelerator,
            "onDemandUsdPerHour": round(on_demand, 6),
            "spotUsdPerHour": round(spot, 6) if spot is not None and spot <= on_demand else None,
        }
    known = {s.machine_type for s in SHAPES}
    for name, row in shipped["machines"].items():
        if name not in known:
            machines[name] = row
            kept.append(name)
    if len(machines) == len(kept):
        return None

    source = f"Cloud Billing Catalog API, {region}, read {as_of}. An estimate, not a quote."
    if kept:
        source += " Kept from the previous list (the catalog did not price them): " + ", ".join(kept) + "."
    if spot_kept:
        source += " Spot price kept from the previous list (the catalog gave none): " + ", ".join(spot_kept) + "."
    return {
        "schema": 1,
        "asOf": as_of,
        "currency": "USD",
        "region": region,
        "source": source,
        "disk": {"type": "pd-balanced", "usdPerGbMonth": round(disk, 6)},
        "machines": machines,
    }


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--catalog", type=Path, required=True)
    ap.add_argument("--shipped", type=Path, default=DEFAULT_FILE)
    ap.add_argument("--out", type=Path, default=None)
    ap.add_argument("--as-of", required=True, help="the day the catalog was read, YYYY-MM-DD")
    args = ap.parse_args(argv)
    try:
        skus = flatten(json.loads(args.catalog.read_text(encoding="utf-8")))
        shipped = json.loads(args.shipped.read_text(encoding="utf-8"))
    except (OSError, ValueError) as ex:
        print(f"ERROR: {ex}", file=sys.stderr)
        return 2
    document = resolve(skus, shipped, args.as_of)
    if document is None:
        print("ERROR: the catalog priced no disk or no machine; pricing.json left as it was.", file=sys.stderr)
        return 1
    out = args.out or args.shipped
    out.write_text(json.dumps(document, indent=2) + "\n", encoding="utf-8", newline="\n")
    print(f"OK: wrote {out} ({len(document['machines'])} machines, as of {args.as_of})")
    return 0


if __name__ == "__main__":
    sys.exit(main())
