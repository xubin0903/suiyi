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


# ---------------------------------------------------------------- MKL 预打包（#113）

PACK_ENV = "CT2_PACKED_GEMM"
"""CTranslate2 是否在加载时把线性层权重按 MKL 格式预打包（CTranslate2 默认开）。"""
MIN_COMMIT_ENV = "SUIYI_PACKED_GEMM_MIN_COMMIT_MIB"
"""可用提交内存低于这么多 MiB 时自动关掉预打包；默认 :data:`DEFAULT_MIN_COMMIT_MIB`。"""
DEFAULT_MIN_COMMIT_MIB = 4096


@dataclass(frozen=True, slots=True)
class PackState:
    """``CT2_PACKED_GEMM`` 的决定。

    ``source``：``auto`` 因可用提交内存不足自动关掉，``user`` 用户设置，``unset`` 保持默认。
    ``commit_available_mib`` 是启动时系统剩余可提交内存（Windows 的提交上限减已提交；Linux
    只在 ``vm.overcommit_memory=2`` 时有意义），拿不到时为 ``None``。
    """

    value: str | None
    source: str
    commit_available_mib: float | None
    min_commit_mib: int = DEFAULT_MIN_COMMIT_MIB

    def health(self) -> dict[str, object]:
        commit = self.commit_available_mib
        return {
            "ct2_packed_gemm": self.value,
            "ct2_packed_gemm_source": self.source,
            "commit_available_mib": None if commit is None else round(commit),
        }

    def describe(self) -> str:
        commit = self.commit_available_mib
        free = "未知" if commit is None else f"{commit:.0f} MiB"
        if self.source == "auto":
            return (
                f"CT2：可用提交内存 {free}，低于 {self.min_commit_mib} MiB：已设 {PACK_ENV}=0"
                "（关掉 MKL 权重预打包，提交内存约少 1.6 GiB，译文不变，解码约慢 20–30%，#113）"
            )
        if self.source == "user":
            return f"CT2：使用用户设置的 {PACK_ENV}={self.value}（可用提交内存 {free}）"
        return f"CT2：MKL 权重预打包保持默认（可用提交内存 {free}）"


def _windows_commit_available_mib() -> float | None:
    """``GlobalMemoryStatusEx`` 的 ``ullAvailPageFile``：系统还能再提交多少内存。"""

    try:
        import ctypes

        class _Status(ctypes.Structure):
            _fields_ = [  # noqa: RUF012 —— ctypes 结构体定义
                ("dwLength", ctypes.c_uint32),
                ("dwMemoryLoad", ctypes.c_uint32),
                ("ullTotalPhys", ctypes.c_uint64),
                ("ullAvailPhys", ctypes.c_uint64),
                ("ullTotalPageFile", ctypes.c_uint64),
                ("ullAvailPageFile", ctypes.c_uint64),
                ("ullTotalVirtual", ctypes.c_uint64),
                ("ullAvailVirtual", ctypes.c_uint64),
                ("ullAvailExtendedVirtual", ctypes.c_uint64),
            ]

        status = _Status()
        status.dwLength = ctypes.sizeof(_Status)
        kernel32 = ctypes.WinDLL("kernel32")  # type: ignore[attr-defined]
        if not kernel32.GlobalMemoryStatusEx(ctypes.byref(status)):
            return None
        return status.ullAvailPageFile / (1024 * 1024)
    except (AttributeError, OSError):
        return None


def _linux_commit_available_mib(
    meminfo: Path = Path("/proc/meminfo"),
    overcommit: Path = Path("/proc/sys/vm/overcommit_memory"),
) -> float | None:
    """只在严格记账（``vm.overcommit_memory=2``）时返回 ``CommitLimit - Committed_AS``。

    默认的启发式超额分配下，没写过的页不会让分配失败，提交量不是瓶颈，返回 ``None``。
    """

    try:
        if overcommit.read_text(encoding="ascii").strip() != "2":
            return None
        values: dict[str, int] = {}
        for line in meminfo.read_text(encoding="ascii").splitlines():
            key, _, rest = line.partition(":")
            if key in ("CommitLimit", "Committed_AS"):
                values[key] = int(rest.split()[0])
        return (values["CommitLimit"] - values["Committed_AS"]) / 1024
    except (OSError, ValueError, KeyError, IndexError):
        return None


def commit_available_mib(system: str | None = None) -> float | None:
    """系统剩余可提交内存（MiB）；拿不到或不适用时为 ``None``。"""

    system = (system or platform.system()).lower()
    if system == "windows":
        return _windows_commit_available_mib()
    if system == "linux":
        return _linux_commit_available_mib()
    return None


_PACK: PackState | None = None


def configure_packed_gemm(
    environ: MutableMapping[str, str] | None = None,
    *,
    commit_mib: float | None | Callable[[], float | None] = commit_available_mib,
) -> PackState:
    """可用提交内存不足时关掉 CTranslate2 的 MKL 权重预打包（#113）。

    CTranslate2（4.8 起默认开）用 MKL 做 GEMM 时，加载模型就把每个线性层权重预打包，缓冲按
    ``cblas_gemm_s8u8s32_pack_get_size`` 申请：每个矩阵至少约 12.3 MB（与形状、线程数、指令集
    无关），MKL 实际只写入约权重本身大小。tc-big + zh-en 因此多申请约 1.7 GiB。Linux 默认超额
    分配，没写过的页不占内存也不会失败；Windows 上这些页全部算进提交量（Private Bytes），系统提交
    紧张时别的分配（如 OCR 子进程里的 onnxruntime）会失败。

    关掉预打包译文逐字不变（整数 GEMM），提交量降到接近常驻内存，但解码慢 20–30%。所以默认保持
    打包，只在启动时系统剩余可提交内存低于阈值（``SUIYI_PACKED_GEMM_MIN_COMMIT_MIB``，默认
    4096）时关掉。用户设了 ``CT2_PACKED_GEMM`` 就不覆盖。AMD CPU 上 CTranslate2 默认用 oneDNN，
    本来就不打包，这个变量不起作用。必须在 ``import ctranslate2`` 之前调用。
    """

    global _PACK
    record = environ is None
    env = os.environ if environ is None else environ
    try:
        threshold = int(env.get(MIN_COMMIT_ENV, "").strip() or DEFAULT_MIN_COMMIT_MIB)
    except ValueError:
        threshold = DEFAULT_MIN_COMMIT_MIB
    available = commit_mib() if callable(commit_mib) else commit_mib
    user = env.get(PACK_ENV, "").strip()
    if user:
        state = PackState(user, "user", available, threshold)
    elif available is not None and available < threshold:
        env[PACK_ENV] = "0"
        state = PackState("0", "auto", available, threshold)
    else:
        state = PackState(None, "unset", available, threshold)
    if record:
        _PACK = state
    return state


def current_pack() -> PackState:
    """包导入时对 ``CT2_PACKED_GEMM`` 的决定；还没决定过就现在决定。"""

    return _PACK if _PACK is not None else configure_packed_gemm()
