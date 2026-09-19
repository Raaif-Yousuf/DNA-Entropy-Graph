"""VM self-lifecycle: stop/delete/keep via the Compute API (docs/cloud_design.md §8).

**Guest ``shutdown`` does NOT fire ``instanceTerminationAction``** — this is Compute
Engine's own documented behaviour, and the exact pitfall CLAUDE.md's Critical Pitfalls and
this repo's migration inventory both call out as already having cost the project once.
This module is the PRIMARY cleanup mechanism: at the end of a job, the worker calls the
Compute API **on itself**, using its own metadata-token credentials, to stop or delete
per ``manifest.lifecycle.afterTask`` (docs/job_contract.md §3). It never uses SSH
(docs/cloud_design.md §9 — there is no SSH anywhere in this design) and never treats
``shutdown -h`` as the primary path; a ``shutdown -h +N`` deadman is armed elsewhere (the
startup script) purely as a last resort for when this API call itself fails to run.

**Not called anywhere in this codebase tonight** — no cloud spend, no cloud calls (the
overnight brief's hard rule: "implement it; do not call it"). Every test here injects a
fake HTTP transport; none makes a real network call.
"""

from __future__ import annotations

import json
import urllib.error
import urllib.request
from typing import Literal

from .blobstore import HttpOpener, MetadataTokenProvider, default_http_opener

_METADATA_BASE = "http://metadata.google.internal/computeMetadata/v1"
_METADATA_HEADERS = {"Metadata-Flavor": "Google"}
_COMPUTE_API = "https://compute.googleapis.com/compute/v1"

AfterTask = Literal["stop", "delete", "keep"]
_VALID_ACTIONS = ("stop", "delete", "keep")


class LifecycleError(RuntimeError):
    """Raised when reading self-identity or applying a lifecycle action fails."""


def _metadata_text(path: str, *, opener: HttpOpener) -> str:
    req = urllib.request.Request(f"{_METADATA_BASE}/{path}", headers=_METADATA_HEADERS)
    try:
        with opener(req) as resp:  # type: ignore[union-attr]
            return resp.read().decode("utf-8")
    except urllib.error.URLError as exc:
        raise LifecycleError(f"could not read metadata {path!r}: {exc}") from exc


def self_instance_identity(*, opener: HttpOpener = default_http_opener) -> tuple[str, str, str]:
    """Return ``(project, zone, instance_name)`` for the CURRENT VM, read from the
    metadata server — never from a config file or an argument the caller could get wrong,
    since this module's whole point is acting on "the VM this code is actually running
    on". ``instance/zone`` comes back as ``projects/<num>/zones/<zone>``; only the last
    path segment is the zone name the Compute instances API expects.
    """
    project = _metadata_text("project/project-id", opener=opener)
    zone_path = _metadata_text("instance/zone", opener=opener)
    zone = zone_path.rsplit("/", 1)[-1]
    name = _metadata_text("instance/name", opener=opener)
    return project, zone, name


def apply_lifecycle(
    after: AfterTask,
    *,
    opener: HttpOpener = default_http_opener,
    token_provider: MetadataTokenProvider | None = None,
) -> dict | None:
    """Apply ``after`` to the CURRENT VM: ``"stop"``/``"delete"`` call the Compute API on
    self; ``"keep"`` is a no-op (the VM stays RUNNING, e.g. for
    ``manifest.lifecycle.afterTask == "keep"`` before an ``idle``/keep-alive wait).
    Returns the Compute API's own response body for ``"stop"``/``"delete"`` (``None`` for
    ``"keep"``, which makes no request).

    This is the PRIMARY cleanup mechanism, not a backstop — ``instanceTerminationAction
    =DELETE`` (fired only at the VM's ``maxRunDuration`` deadline) and a startup-script
    ``shutdown -h`` deadman are both backstops for when THIS call fails to run at all, per
    docs/cloud_design.md §8.

    **issue #321**: a 2xx HTTP status here only means the Compute API *accepted* the
    ``stop``/``delete`` request — both are long-running asynchronous Operations, and a 2xx
    response does not mean the instance has actually stopped or been deleted yet, only
    that Google queued the attempt. This function does **not** poll the operation to
    completion: issue #256 already owns proper operation polling on the app side, and
    polling here would delay this VM's own exit for marginal benefit on a
    self-terminating action that two independent backstops already cover (see the
    docstring above). What it DOES do, which the previous version silently skipped: read
    the response body and raise :class:`LifecycleError` if the body itself already
    reports an ``error`` (some failure modes surface there rather than as an HTTP error
    status), and hand the parsed body back to the caller so at least the operation's own
    id is recorded (``runner.py`` logs it via a notice) rather than discarded outright.
    """
    if after not in _VALID_ACTIONS:
        raise LifecycleError(f"unknown lifecycle action {after!r}, expected one of {_VALID_ACTIONS}")
    if after == "keep":
        return None

    tokens = token_provider or MetadataTokenProvider(opener=opener)
    project, zone, name = self_instance_identity(opener=opener)
    url = f"{_COMPUTE_API}/projects/{project}/zones/{zone}/instances/{name}/{after}"
    req = urllib.request.Request(
        url,
        method="POST",
        headers={"Authorization": f"Bearer {tokens.get()}"},
    )
    try:
        with opener(req) as resp:  # type: ignore[union-attr]
            body = json.loads(resp.read().decode("utf-8"))
    except urllib.error.HTTPError as exc:
        raise LifecycleError(f"Compute API {after} failed ({exc.code}): {exc.reason}") from exc
    except urllib.error.URLError as exc:
        raise LifecycleError(f"Compute API {after} failed: {exc}") from exc

    if isinstance(body, dict) and body.get("error"):
        raise LifecycleError(f"Compute API {after} accepted but reports an error: {body['error']}")
    return body if isinstance(body, dict) else None
