"""带 AMX 的 CPU 上自动设 MKL_ENABLE_INSTRUCTIONS（#103）。"""

from __future__ import annotations

import json
import logging
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


# ---------------------------------------------------------------- MKL 预打包（#113 / #119）

GIB = 1024.0


def test_estimate_is_calibrated_on_measured_models() -> None:
    from suiyi_engine.cpu_isa import estimate_pack_extra_mib

    # 实测（ctranslate2 4.8.2 + MKL）：base 模型 768 MiB，tc-big 822–824 MiB
    assert abs(estimate_pack_extra_mib(76) - 768) < 5
    assert abs(estimate_pack_extra_mib(236) - 823) < 5
    # tc-big + zh-en 合计约 1590；负责人笔记本实测 1991 − 406 = 1585
    assert abs(estimate_pack_extra_mib(236) + estimate_pack_extra_mib(76) - 1585) < 10


@pytest.mark.parametrize(
    ("commit", "physical", "expected"),
    [
        (6028.0, 15.7 * GIB, (False, "small_ram")),  # 负责人：刚重启、可用约 6 GB、16 GB 内存
        (60000.0, 15.4 * GIB, (False, "small_ram")),  # 标称 16 GB 的机器系统常报 15.x
        (60000.0, 16.0 * GIB, (False, "small_ram")),
        (6028.0, 32 * GIB, (True, "ok")),  # 6028 − 1590 = 4438 ≥ 4096
        (5000.0, 32 * GIB, (False, "low_commit")),  # 5000 − 1590 < 4096
        (2400.0, 64 * GIB, (False, "low_commit")),  # #113 的场景
        (60000.0, 24 * GIB, (True, "ok")),  # 内存富裕：行为不变
        (None, 64 * GIB, (True, "ok")),  # Linux 默认超额分配：提交量不是瓶颈
        (None, None, (True, "ok")),
    ],
)
def test_decide_pack_rules(
    commit: float | None, physical: float | None, expected: tuple[bool, str]
) -> None:
    from suiyi_engine.cpu_isa import decide_pack

    assert decide_pack(commit_mib=commit, physical_mib=physical, extra_mib=1590.0) == expected


class Clockwork:
    """可改的可用提交量 / 物理内存。"""

    def __init__(self, commit: float | None, physical: float | None) -> None:
        self.commit = commit
        self.physical = physical


def _governor(env: dict[str, str], box: Clockwork, mkl: bool = True):  # type: ignore[no-untyped-def]
    from suiyi_engine.cpu_isa import PackGovernor

    return PackGovernor(
        env, commit_mib=lambda: box.commit, physical_mib=lambda: box.physical, mkl=lambda: mkl
    )


def test_decision_waits_for_first_load_then_locks(caplog: pytest.LogCaptureFixture) -> None:
    env: dict[str, str] = {}
    box = Clockwork(6028.0, 32 * GIB)
    governor = _governor(env, box)
    assert governor.source == "pending" and env == {}
    assert governor.health()["ct2_packed_gemm"] is None

    with governor.planned({"tc-big": 236.0, "zh-en": 76.0}):
        assert governor.before_load("tc-big", 236.0) is True  # 按两个模型估算：4438 ≥ 4096
        assert governor.before_load("zh-en", 76.0) is True
    assert env == {"CT2_PACKED_GEMM": "1"}
    health = governor.health()
    assert health["ct2_packed_gemm"] == "1" and health["ct2_packed_gemm_source"] == "auto"
    assert health["ct2_models_packed"] == {"tc-big": True, "zh-en": True}
    assert health["commit_available_mib"] == 6028

    # 空闲卸载后重新加载：可用量已降到 2100，重新判断、/health 更新，但进程内已锁定，只提示
    box.commit = 2100.0
    with caplog.at_level(logging.WARNING, logger="suiyi_engine.cpu_isa"):
        assert governor.before_load("zh-en", 76.0) is True
    health = governor.health()
    assert health["commit_available_mib"] == 2100
    assert health["ct2_packed_gemm_recommended"] is False
    assert health["ct2_packed_gemm"] == "1" and env == {"CT2_PACKED_GEMM": "1"}
    assert "重启服务后生效" in caplog.text


def test_first_load_with_low_commit_turns_packing_off() -> None:
    env: dict[str, str] = {}
    governor = _governor(env, Clockwork(6028.0, 15.7 * GIB))
    assert governor.before_load("tc-big", 236.0) is False
    assert env == {"CT2_PACKED_GEMM": "0"}
    health = governor.health()
    assert health["ct2_packed_gemm_reason"] == "small_ram"
    assert health["physical_memory_mib"] == round(15.7 * GIB)
    assert "CT2_PACKED_GEMM=0" in governor.describe()


def test_planned_batch_counts_every_model() -> None:
    env: dict[str, str] = {}
    box = Clockwork(5000.0, 32 * GIB)
    governor = _governor(env, box)
    with governor.planned({"tc-big": 236.0, "zh-en": 76.0}):
        governor.before_load("tc-big", 236.0)
    assert env == {"CT2_PACKED_GEMM": "0"}  # 5000 − 1590 < 4096；只算一个模型时 5000 − 823 ≥ 4096
    assert governor.health()["packed_gemm_extra_mib"] == 1589  # 740×2 + 0.35×312


def test_user_value_is_never_overridden() -> None:
    for value in ("0", "1"):
        env = {"CT2_PACKED_GEMM": value}
        governor = _governor(env, Clockwork(100.0, 8 * GIB))
        assert governor.source == "user"
        assert governor.before_load("m", 76.0) is (value == "1")
        assert env == {"CT2_PACKED_GEMM": value}
        assert governor.health()["ct2_packed_gemm_reason"] == "user"


def test_threshold_envs_and_non_mkl_backend() -> None:
    env = {"SUIYI_PACKED_GEMM_SMALL_RAM_MIB": "1", "SUIYI_PACKED_GEMM_MIN_COMMIT_MIB": "oops"}
    governor = _governor(env, Clockwork(6028.0, 15.7 * GIB), mkl=False)
    assert governor.before_load("m", 236.0) is False  # 非 MKL（AMD 走 oneDNN）从不打包
    assert env["CT2_PACKED_GEMM"] == "1"  # 小内存阈值调低后不算小内存；默认 4096 仍够
    assert governor.health()["ct2_models_packed"] == {"m": False}


def test_health_lists_only_loaded_models() -> None:
    governor = _governor({}, Clockwork(None, 64 * GIB))
    governor.before_load("a", 76.0)
    governor.before_load("b", 76.0)
    assert governor.health(["b"])["ct2_models_packed"] == {"b": True}


def test_linux_physical_memory(tmp_path: Path) -> None:
    from suiyi_engine.cpu_isa import physical_memory_mib

    meminfo = tmp_path / "meminfo"
    meminfo.write_text("MemTotal:       16397312 kB\n", encoding="ascii")
    assert physical_memory_mib("Linux", meminfo) == 16013.0
    assert physical_memory_mib("Darwin") is None
    value = physical_memory_mib()
    assert value is None or value > 0


def test_linux_commit_only_counts_under_strict_overcommit(tmp_path: Path) -> None:
    from suiyi_engine.cpu_isa import _linux_commit_available_mib

    meminfo = tmp_path / "meminfo"
    meminfo.write_text(
        "MemTotal:       16000000 kB\nCommitLimit:    10485760 kB\nCommitted_AS:    8388608 kB\n",
        encoding="ascii",
    )
    mode = tmp_path / "overcommit"
    mode.write_text("2\n", encoding="ascii")
    assert _linux_commit_available_mib(meminfo, mode) == 2048.0
    mode.write_text("0\n", encoding="ascii")
    assert _linux_commit_available_mib(meminfo, mode) is None
    assert _linux_commit_available_mib(tmp_path / "missing", tmp_path / "missing") is None


def test_commit_available_on_this_system_is_number_or_none() -> None:
    from suiyi_engine.cpu_isa import commit_available_mib

    value = commit_available_mib()
    assert value is None or value > 0
    assert commit_available_mib("Darwin") is None


def test_mkl_in_use_follows_ct2_use_mkl() -> None:
    from suiyi_engine.cpu_isa import mkl_in_use

    assert mkl_in_use({"CT2_USE_MKL": "1"}) is True
    assert mkl_in_use({"CT2_USE_MKL": "0"}) is False
    assert isinstance(mkl_in_use({}), bool)


def test_package_import_does_not_decide_packing_yet() -> None:
    code = (
        "import json, os, sys\n"
        "import suiyi_engine\n"
        "from suiyi_engine import cpu_isa\n"
        "print(json.dumps({'env': os.environ.get('CT2_PACKED_GEMM'),"
        " 'state': cpu_isa.pack_governor().health(),"
        " 'ct2_loaded': 'ctranslate2' in sys.modules}))\n"
    )
    env = {k: v for k, v in os.environ.items() if k != "CT2_PACKED_GEMM"}
    out = subprocess.run(
        [sys.executable, "-c", code], capture_output=True, text=True, env=env, check=True
    ).stdout
    report = json.loads(out.strip().splitlines()[-1])
    assert report["env"] is None and report["ct2_loaded"] is False
    assert report["state"]["ct2_packed_gemm_source"] == "pending"


def test_describe_keeps_first_load_basis_while_health_shows_latest() -> None:
    governor = _governor({}, Clockwork(None, 32 * GIB))
    with governor.planned({"tc-big": 236.0, "zh-en": 76.0}):
        governor.before_load("zh-en", 76.0)
        governor.before_load("tc-big", 236.0)
    assert "预计额外 1589 MiB" in governor.describe()  # 锁定时按整批估算
    assert governor.health()["packed_gemm_extra_mib"] == 823  # 最近一次加载只算 tc-big
