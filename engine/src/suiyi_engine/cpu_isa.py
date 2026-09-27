"""带 AMX 的 CPU 上默认关掉 MKL 的 AMX 路径（#103）。

在带 AMX 的 Intel CPU 上（Sapphire Rapids 及以后的 Xeon），CTranslate2 的 int8 推理走 MKL 的
AMX int8 GEMM 时结果不确定：多线程或 CPU 被别的程序占用时同一句每次译文不同，甚至是乱码。
把 ``MKL_ENABLE_INSTRUCTIONS`` 设为 ``AVX512_E1``（MKL 指令集上限为 AVX-512 + VNNI）后结果稳定、
速度不变（见 docs/engine/性能基线.md）。

规则：

- 用户设了 ``MKL_ENABLE_INSTRUCTIONS``（非空）就用用户的值，不覆盖。想强制用 AMX 可设
  ``AVX512_E4``。
- 否则检测到 AMX 时设为 ``AVX512_E1``；没检测到（普通笔记本、AMD、Apple、ARM）什么都不做。
- MKL 在第一次初始化时读这个变量，所以必须在 ``import ctranslate2`` 之前设置。包的
  ``__init__`` 一导入就调用 :func:`configure_mkl_isa`；若那时 ctranslate2 已被导入，照样设置
  但记下 ``late``，启动日志告警。

检测只用标准库：Linux 读 ``/proc/cpuinfo`` 的 ``amx_tile`` / ``amx_int8``；Windows 调
``kernel32.GetEnabledXStateFeatures``，看操作系统是否启用了 AMX 的 XTILECFG / XTILEDATA
状态（只有 CPU 支持 AMX 时系统才会启用）；其他系统视为没有 AMX。
"""

from __future__ import annotations

import os
import platform
import sys
from collections.abc import Callable, Mapping, MutableMapping
from dataclasses import dataclass
from pathlib import Path

ENV = "MKL_ENABLE_INSTRUCTIONS"
SAFE_ISA = "AVX512_E1"
# XSTATE 特性位：17 = XTILECFG，18 = XTILEDATA（Intel SDM 卷 1 第 13 章）
_XSTATE_AMX = (1 << 17) | (1 << 18)
_LINUX_FLAGS = frozenset({"amx_tile", "amx_int8"})
_X86 = frozenset({"x86_64", "amd64", "x64", "i386", "i686", "x86"})


@dataclass(frozen=True, slots=True)
class IsaState:
    """启动时的决定。``source``：``auto`` 自动设置，``user`` 用户设置，``unset`` 没设。"""

    amx: bool
    value: str | None
    source: str
    late: bool = False

    def health(self) -> dict[str, object]:
        """``/health`` 的字段。"""

        return {
            "cpu_amx": self.amx,
            "mkl_enable_instructions": self.value,
            "mkl_enable_instructions_source": self.source,
        }

    def describe(self) -> str:
        """启动日志里的一行。"""

        amx = "检测到 AMX" if self.amx else "未检测到 AMX"
        if self.source == "auto":
            return f"CPU：{amx}，已设 {ENV}={self.value}（避开 AMX int8 结果不稳定，#103）"
        if self.source == "user":
            return f"CPU：{amx}，使用用户设置的 {ENV}={self.value}"
        return f"CPU：{amx}，MKL 指令集不限制"


def _linux_amx(cpuinfo: Path) -> bool:
    try:
        text = cpuinfo.read_text(encoding="utf-8", errors="replace")
    except OSError:
        return False
    for line in text.splitlines():
        key, _, value = line.partition(":")
        if key.strip() == "flags":
            return bool(_LINUX_FLAGS & set(value.split()))
    return False


def _windows_xstate() -> int | None:
    """``GetEnabledXStateFeatures()`` 的返回值；拿不到时为 ``None``。"""

    try:
        import ctypes

        kernel32 = ctypes.WinDLL("kernel32")  # type: ignore[attr-defined]
        func = kernel32.GetEnabledXStateFeatures
    except (AttributeError, OSError):
        return None
    func.restype = ctypes.c_uint64
    func.argtypes = []
    try:
        return int(func())
    except OSError:
        return None


def detect_amx(
    system: str | None = None,
    machine: str | None = None,
    *,
    cpuinfo: Path | str = "/proc/cpuinfo",
    xstate: Callable[[], int | None] = _windows_xstate,
) -> bool:
    """CPU 和操作系统是否都支持 AMX。检测失败时返回 ``False``（保持原行为）。"""

    system = (system or platform.system()).lower()
    machine = (machine or platform.machine()).lower()
    if machine not in _X86:
        return False
    if system == "linux":
        return _linux_amx(Path(cpuinfo))
    if system == "windows":
        mask = xstate()
        return mask is not None and bool(mask & _XSTATE_AMX)
    return False


_STATE: IsaState | None = None


def configure_mkl_isa(
    environ: MutableMapping[str, str] | None = None,
    *,
    amx: bool | None = None,
    modules: Mapping[str, object] | None = None,
) -> IsaState:
    """按上面的规则设置 ``MKL_ENABLE_INSTRUCTIONS``，返回决定。

    ``environ`` / ``amx`` / ``modules`` 只给测试用；默认是 ``os.environ``、实际检测结果和
    ``sys.modules``。用默认参数调用时结果记下来，供 :func:`current` 使用。
    """

    global _STATE
    record = environ is None
    env = os.environ if environ is None else environ
    loaded = sys.modules if modules is None else modules
    has_amx = detect_amx() if amx is None else amx
    late = "ctranslate2" in loaded
    user = env.get(ENV, "").strip()
    if user:
        state = IsaState(has_amx, user, "user")
    elif has_amx:
        env[ENV] = SAFE_ISA
        state = IsaState(True, SAFE_ISA, "auto", late)
    else:
        state = IsaState(False, None, "unset")
    if record:
        _STATE = state
    return state


def current() -> IsaState:
    """包导入时的决定；还没决定过就现在决定。"""

    return _STATE if _STATE is not None else configure_mkl_isa()
