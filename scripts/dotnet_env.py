"""The one definition of the env a dotnet child runs with (heavy.py and premerge.py both import it).

Node reuse, the MSBuild server and the shared compiler server are off for the dotnet commands that pass
through it: heavy.py's wrapped dotnet command, premerge.py's dotnet gates and gen_third_party_notices.py's
`dotnet list package`. Dotnet run any other way (a hand-typed `dotnet build`, another script) is NOT covered.
MEASURED 2026-10-31 (#1070, scratch experiment, 4 parallel Exec projects, `dotnet msbuild -m:4` inside a
kill-on-close job): with node reuse on, `dotnet.exe ... /nodemode:1` workers were still alive in the
gate's job after the build exited (3 of them) and died only when the job closed; with the defaults below
none were left. Foreign idle nodes (other lanes') were also adopted by a reuse-on build.
THEORY (unverified): a job close killing an adopted node under a second build is the MSB4166 cause; it
was not reproduced, the defaults remove the precondition.
"""

from __future__ import annotations

import os
from pathlib import Path

DOTNET_ENV_DEFAULTS = {"MSBUILDDISABLENODEREUSE": "1", "DOTNET_CLI_USE_MSBUILD_SERVER": "0", "UseSharedCompilation": "false"}


def _is_dotnet(argv: list[str]) -> bool:
    return bool(argv) and Path(argv[0].replace("\\", "/")).name.lower() in ("dotnet", "dotnet.exe")


def dotnet_env(argv: list[str], env: dict[str, str]) -> dict[str, str]:
    """For a dotnet command, return a copy of env with node reuse and shared compilation off.

    Pure. A value the caller already set wins. Non-dotnet commands get env back unchanged.
    """
    if not _is_dotnet(argv):
        return env
    out = dict(env)
    present = {k.upper() for k in out} if os.name == "nt" else set(out)  # Windows env names are case-insensitive
    for key, value in DOTNET_ENV_DEFAULTS.items():
        if (key.upper() if os.name == "nt" else key) not in present:
            out[key] = value
    return out
