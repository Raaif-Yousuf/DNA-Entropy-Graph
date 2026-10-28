"""scripts/write_oauth_client.py -- write a MINIMAL oauth_client.local.json from the OAUTH_CLIENT_JSON secret (#518, #178).

    OAUTH_CLIENT_JSON=<secret> python scripts/write_oauth_client.py --out app/publish/oauth_client.local.json

The secret is the JSON Google's console gives for a Desktop app client. Only `installed.client_id` and
`installed.client_secret` are read, validated (non-empty strings) and written; every other field (project id, URIs, a
stray refresh token, a `web` block) is dropped, so the file shipped inside the installer carries nothing else. Both
values are masked (`::add-mask::`) BEFORE anything else is printed or written. Error text never contains the secret.

Exit codes: 0 written, 1 a missing or invalid secret (nothing is written).
"""

from __future__ import annotations

import argparse
import json
import os
import sys
from pathlib import Path


def minimal_document(raw: str) -> dict:
    """The two-field document, or ValueError whose text names no value from `raw`."""
    try:
        doc = json.loads(raw)
    except (ValueError, TypeError):
        raise ValueError("the secret is not valid JSON") from None
    installed = doc.get("installed") if isinstance(doc, dict) else None
    if not isinstance(installed, dict):
        raise ValueError("the secret has no `installed` object (it must be a Desktop app client download)")
    out: dict[str, str] = {}
    for key in ("client_id", "client_secret"):
        value = installed.get(key)
        if not isinstance(value, str) or not value.strip():
            raise ValueError(f"installed.{key} is missing, empty or not a string")
        out[key] = value
    return {"installed": out}


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=(__doc__ or "").split("\n")[0])
    ap.add_argument("--out", type=Path, required=True)
    args = ap.parse_args(argv)
    raw = os.environ.get("OAUTH_CLIENT_JSON", "")
    try:
        doc = minimal_document(raw)
    except ValueError as exc:
        print(f"ERROR: OAUTH_CLIENT_JSON is unusable: {exc}. Replace the secret with Google's Desktop app JSON, then re-run.")
        return 1
    installed = doc["installed"]
    print(f"::add-mask::{installed['client_id']}")
    print(f"::add-mask::{installed['client_secret']}")
    args.out.parent.mkdir(parents=True, exist_ok=True)
    with args.out.open("w", encoding="utf-8", newline="\n") as fh:
        fh.write(json.dumps(doc, indent=2) + "\n")
    print(f"OK: wrote {args.out.name} with client_id and client_secret only")
    return 0


if __name__ == "__main__":
    sys.exit(main())
