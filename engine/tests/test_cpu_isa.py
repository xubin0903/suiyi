"""带 AMX 的 CPU 上自动设 MKL_ENABLE_INSTRUCTIONS（#103）。"""

from __future__ import annotations

import json
import os
import subprocess
import sys
from pathlib import Path

import pytest

from suiyi_engine import cpu_isa
from suiyi_engine.cpu_isa import IsaState, configure_mkl_isa, detect_amx

_SPR_FLAGS = "fpu vme avx512f avx512_vnni amx_bf16 amx_tile amx_int8 avx512_fp16"
_ICL_FLAGS = "fpu vme avx2 avx512f avx512_vnni avx512_bf16"


def _cpuinfo(tmp_path: Path, flags: str) -> Path:
    path = tmp_path / "cpuinfo"
    path.write_text(
        f"processor\t: 0\nmodel name\t: Intel(R) Xeon(R)\nflags\t\t: {flags}\n\n"
        f"processor\t: 1\nflags\t\t: {flags}\n",
        encoding="utf-8",
    )
    return path


def test_detect_amx_linux(tmp_path: Path) -> None:
    assert detect_amx("Linux", "x86_64", cpuinfo=_cpuinfo(tmp_path, _SPR_FLAGS)) is True
    assert detect_amx("Linux", "x86_64", cpuinfo=_cpuinfo(tmp_path, _ICL_FLAGS)) is False
    assert detect_amx("Linux", "x86_64", cpuinfo=tmp_path / "missing") is False
    assert detect_amx("Linux", "aarch64", cpuinfo=_cpuinfo(tmp_path, _SPR_FLAGS)) is False


def test_detect_amx_windows_uses_enabled_xstate() -> None:
    assert detect_amx("Windows", "AMD64", xstate=lambda: 0x60207) is True  # 含第 17、18 位
    assert detect_amx("Windows", "AMD64", xstate=lambda: 0xE7) is False  # 只有 x87/SSE/AVX/AVX-512
    assert detect_amx("Windows", "AMD64", xstate=lambda: None) is False
    assert detect_amx("Windows", "ARM64", xstate=lambda: 0x60207) is False


def test_detect_amx_other_systems() -> None:
    assert detect_amx("Darwin", "x86_64") is False


def test_auto_sets_safe_isa_only_with_amx() -> None:
    env: dict[str, str] = {}
    state = configure_mkl_isa(env, amx=True, modules={})
    assert env == {"MKL_ENABLE_INSTRUCTIONS": "AVX512_E1"}
    assert state == IsaState(True, "AVX512_E1", "auto", False)
    env = {}
    assert configure_mkl_isa(env, amx=False, modules={}) == IsaState(False, None, "unset")
    assert env == {}


@pytest.mark.parametrize("value", ["AVX512_E4", "AVX2", " AVX512 "])
def test_user_value_is_respected(value: str) -> None:
    env = {"MKL_ENABLE_INSTRUCTIONS": value}
    state = configure_mkl_isa(env, amx=True, modules={})
    assert env["MKL_ENABLE_INSTRUCTIONS"] == value
    assert state.source == "user" and state.value == value.strip()


def test_empty_user_value_counts_as_unset() -> None:
    env = {"MKL_ENABLE_INSTRUCTIONS": "  "}
    assert configure_mkl_isa(env, amx=True, modules={}).source == "auto"
    assert env["MKL_ENABLE_INSTRUCTIONS"] == "AVX512_E1"


def test_late_when_ctranslate2_already_imported() -> None:
    state = configure_mkl_isa({}, amx=True, modules={"ctranslate2": object()})
    assert state.late is True and state.value == "AVX512_E1"
    assert "AVX512_E1" in state.describe()


def test_health_fields_and_describe() -> None:
    state = IsaState(True, "AVX512_E1", "auto")
    assert state.health() == {
        "cpu_amx": True,
        "mkl_enable_instructions": "AVX512_E1",
        "mkl_enable_instructions_source": "auto",
    }
    assert "检测到 AMX" in state.describe()
    assert "不限制" in IsaState(False, None, "unset").describe()
    assert "用户" in IsaState(False, "AVX2", "user").describe()


def test_package_import_decides_before_ctranslate2() -> None:
    code = (
        "import json, os, sys\n"
        "import suiyi_engine\n"
        "from suiyi_engine import cpu_isa\n"
        "state = cpu_isa.current()\n"
        "print(json.dumps({'ct2': 'ctranslate2' in sys.modules, 'late': state.late,\n"
        "  'amx': state.amx, 'env': os.environ.get('MKL_ENABLE_INSTRUCTIONS'),\n"
        "  'value': state.value, 'source': state.source}))\n"
    )
    env = {k: v for k, v in os.environ.items() if k != "MKL_ENABLE_INSTRUCTIONS"}
    out = subprocess.run(
        [sys.executable, "-c", code], env=env, capture_output=True, text=True, check=True
    )
    result = json.loads(out.stdout)
    assert result["ct2"] is False and result["late"] is False
    assert result["env"] == result["value"]
    expected = "AVX512_E1" if result["amx"] else None
    assert result["value"] == expected
    assert result["source"] == ("auto" if result["amx"] else "unset")
    assert result["amx"] is detect_amx()


def test_current_returns_package_decision() -> None:
    assert cpu_isa.current() is cpu_isa.current()
