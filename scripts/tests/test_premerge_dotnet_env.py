"""premerge.py's dotnet gates run with node reuse and shared compilation off (the one definition lives in dotnet_env.py)."""

from __future__ import annotations

import sys
from pathlib import Path

import pytest

SCRIPTS_DIR = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(SCRIPTS_DIR))

import dotnet_env as de  # noqa: E402
import heavy  # noqa: E402
import premerge as pm  # noqa: E402

DEFAULTS = {"MSBUILDDISABLENODEREUSE": "1", "DOTNET_CLI_USE_MSBUILD_SERVER": "0", "UseSharedCompilation": "false"}


def _captured_env(monkeypatch: pytest.MonkeyPatch, argv: list[str], base: dict[str, str]) -> dict[str, str]:
    seen: dict[str, str] = {}

    def fake_capture(_argv: list[str], _cwd: Path, _timeout: float, env: dict[str, str]) -> pm.Captured:
        seen.update(env)
        return pm.Captured(0, b"")

    monkeypatch.setattr(pm, "_resolve_exe", lambda exe: exe)
    monkeypatch.setattr(pm, "_run_capture", fake_capture)
    pm.run_gate(pm.Gate("g", argv, Path.cwd()), env=base)
    return seen


def test_a_dotnet_gate_child_gets_the_node_isolation_defaults(monkeypatch):
    env = _captured_env(monkeypatch, ["dotnet", "test", "x.csproj"], {"PATH": "p"})
    assert {k: env.get(k) for k in DEFAULTS} == DEFAULTS


def test_a_caller_value_wins_over_the_default(monkeypatch):
    env = _captured_env(monkeypatch, ["dotnet", "format"], {"MSBUILDDISABLENODEREUSE": "0"})
    assert env["MSBUILDDISABLENODEREUSE"] == "0"
    assert env["UseSharedCompilation"] == "false"


def test_a_non_dotnet_gate_env_is_not_given_the_defaults(monkeypatch):
    env = _captured_env(monkeypatch, [sys.executable, "-c", "pass"], {"PATH": "p"})
    assert not set(DEFAULTS) & set(env)


def test_heavy_and_premerge_share_one_definition():
    assert heavy.DOTNET_ENV_DEFAULTS is de.DOTNET_ENV_DEFAULTS
    assert heavy.dotnet_env is de.dotnet_env
    assert de.DOTNET_ENV_DEFAULTS == DEFAULTS
