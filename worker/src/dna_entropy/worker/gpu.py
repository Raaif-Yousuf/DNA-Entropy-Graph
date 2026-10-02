"""The VM's GPU identity for ``status.json`` and ``result.json`` (issue #74).

The worker reports what ``nvidia-smi`` says, never what a manifest or an instance type
claims: "RUNNING is not working" (CLAUDE.md Critical Pitfalls), so the app's only proof the
GPU is really there is this name in the worker's first status write and first progress line.
No ``torch`` here (Hard Rule 1: only ``predictors/evo.py`` may import it); ``nvidia-smi`` is
a subprocess, injected in tests, and this function never raises.
"""

from __future__ import annotations

import os
import subprocess
from collections.abc import Callable, Mapping

from .status import GpuInfo

_QUERY = ["nvidia-smi", "--query-gpu=name,driver_version", "--format=csv,noheader"]
_TIMEOUT_SECONDS = 10


def detect_gpu(
    *,
    env: Mapping[str, str] | None = None,
    run: Callable[..., subprocess.CompletedProcess] = subprocess.run,
) -> GpuInfo:
    """Return the first GPU's name and driver, plus the zone from ``DEG_ZONE`` (set by
    ``worker/vm/startup.sh``'s ``docker run -e``). A missing ``nvidia-smi``, a timeout, a
    non-zero exit or unparseable output all mean "no GPU" (``name`` and ``driver`` None):
    that is a normal state on the CPU VM and a local run, not an error."""
    env = os.environ if env is None else env
    zone = env.get("DEG_ZONE") or None
    try:
        proc = run(
            _QUERY,
            capture_output=True,
            text=True,
            errors="replace",
            timeout=_TIMEOUT_SECONDS,
            check=False,
        )
    except (OSError, subprocess.SubprocessError, ValueError):  # ValueError: undecodable output
        return GpuInfo(zone=zone)
    if proc.returncode != 0:
        return GpuInfo(zone=zone)
    first = next((ln.strip() for ln in (proc.stdout or "").splitlines() if ln.strip()), "")
    name, sep, driver = first.partition(",")
    if not sep or not name.strip():
        return GpuInfo(zone=zone)
    return GpuInfo(name=name.strip(), zone=zone, driver=driver.strip() or None)
