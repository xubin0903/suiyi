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

import logging
import os
import platform
import sys
import threading
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


# ---------------------------------------------------------------- MKL 预打包（#113 / #119）

PACK_ENV = "CT2_PACKED_GEMM"
"""CTranslate2 是否在加载时把线性层权重按 MKL 格式预打包（CTranslate2 默认开）。"""
MIN_COMMIT_ENV = "SUIYI_PACKED_GEMM_MIN_COMMIT_MIB"
"""「可用提交量 − 预计额外提交量」低于这么多 MiB 时关掉预打包（默认 4096）。"""
DEFAULT_MIN_COMMIT_MIB = 4096
SMALL_RAM_ENV = "SUIYI_PACKED_GEMM_SMALL_RAM_MIB"
"""物理内存不超过这么多 MiB 时默认关掉预打包；默认 :data:`DEFAULT_SMALL_RAM_MIB`。"""
DEFAULT_SMALL_RAM_MIB = 16896
"""16.5 GiB：标称 16 GB 的机器系统通常报 15.x GiB，都算进来；24 GB 及以上不算。"""
# 预打包多出的提交量，按 model.bin 大小估算（#119 实测标定，ctranslate2 4.8.2 + MKL）：
# Marian base（model.bin 74–79 MiB）多 768 MiB，tc-big（209–236 MiB）多 822–824 MiB。
# MKL 每个矩阵的打包缓冲至少约 12.3 MB，所以额外量主要由矩阵个数决定、与大小关系不大。
PACK_EXTRA_BASE_MIB = 740.0
PACK_EXTRA_PER_BIN_MIB = 0.35

logger = logging.getLogger(__name__)


def estimate_pack_extra_mib(model_bin_mib: float) -> float:
    """一个 int8 模型开预打包后比不开多提交的 MiB 数（估算）。"""

    return PACK_EXTRA_BASE_MIB + PACK_EXTRA_PER_BIN_MIB * max(0.0, model_bin_mib)


def model_bin_mib(model_dir: Path) -> float:
    try:
        return (model_dir / "model.bin").stat().st_size / (1024 * 1024)
    except OSError:
        return 0.0


def decide_pack(
    *,
    commit_mib: float | None,
    physical_mib: float | None,
    extra_mib: float,
    min_commit_mib: int = DEFAULT_MIN_COMMIT_MIB,
    small_ram_mib: int = DEFAULT_SMALL_RAM_MIB,
) -> tuple[bool, str]:
    """返回（是否打包，原因）。原因：``small_ram`` / ``low_commit`` / ``ok``。"""

    if physical_mib is not None and physical_mib <= small_ram_mib:
        return False, "small_ram"
    if commit_mib is not None and commit_mib - extra_mib < min_commit_mib:
        return False, "low_commit"
    return True, "ok"


def _memory_status() -> tuple[float | None, float | None]:
    """Windows ``GlobalMemoryStatusEx``：（``ullAvailPageFile``，``ullTotalPhys``），MiB。"""

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
            return None, None
        mib = 1024 * 1024
        return status.ullAvailPageFile / mib, status.ullTotalPhys / mib
    except (AttributeError, OSError):
        return None, None


def _windows_commit_available_mib() -> float | None:
    """``GlobalMemoryStatusEx`` 的 ``ullAvailPageFile``：系统还能再提交多少内存。"""

    return _memory_status()[0]


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
        values = _meminfo(meminfo, ("CommitLimit", "Committed_AS"))
        return (values["CommitLimit"] - values["Committed_AS"]) / 1024
    except (OSError, ValueError, KeyError, IndexError):
        return None


def _meminfo(path: Path, keys: tuple[str, ...]) -> dict[str, int]:
    values: dict[str, int] = {}
    for line in path.read_text(encoding="ascii").splitlines():
        key, _, rest = line.partition(":")
        if key in keys:
            values[key] = int(rest.split()[0])
    return values


def commit_available_mib(system: str | None = None) -> float | None:
    """系统剩余可提交内存（MiB）；拿不到或不适用时为 ``None``。"""

    system = (system or platform.system()).lower()
    if system == "windows":
        return _windows_commit_available_mib()
    if system == "linux":
        return _linux_commit_available_mib()
    return None


def physical_memory_mib(
    system: str | None = None, meminfo: Path = Path("/proc/meminfo")
) -> float | None:
    """物理内存总量（MiB）：Windows ``ullTotalPhys``，Linux ``MemTotal``；其他系统 ``None``。"""

    system = (system or platform.system()).lower()
    if system == "windows":
        return _memory_status()[1]
    if system == "linux":
        try:
            return _meminfo(meminfo, ("MemTotal",))["MemTotal"] / 1024
        except (OSError, ValueError, KeyError, IndexError):
            return None
    return None


def mkl_in_use(environ: Mapping[str, str] | None = None) -> bool:
    """CTranslate2 这次会不会用 MKL 做 GEMM（只有 MKL 才预打包）。

    与 CTranslate2 的 ``mayiuse_mkl()`` 一致：设了 ``CT2_USE_MKL`` 就按它，否则 Intel CPU 用 MKL。
    """

    env = os.environ if environ is None else environ
    raw = env.get("CT2_USE_MKL", "").strip().lower()
    if raw:
        return raw in ("1", "true", "yes", "on")
    return _cpu_vendor() == "GenuineIntel"


def _cpu_vendor() -> str:
    if sys.platform.startswith("linux"):
        try:
            for line in Path("/proc/cpuinfo").read_text(encoding="utf-8").splitlines():
                if line.startswith("vendor_id"):
                    return line.partition(":")[2].strip()
        except OSError:
            return ""
        return ""
    processor = platform.processor()
    if "GenuineIntel" in processor:
        return "GenuineIntel"
    if "AuthenticAMD" in processor:
        return "AuthenticAMD"
    return ""


def _int_env(env: Mapping[str, str], name: str, default: int) -> int:
    try:
        return int(env.get(name, "").strip() or default)
    except ValueError:
        return default


class PackGovernor:
    """``CT2_PACKED_GEMM`` 的决定（#113 / #119）。

    CTranslate2 只在进程里第一次加载模型时读 ``CT2_PACKED_GEMM`` 并缓存
    （``cpu/backend.cc`` 的 ``static const bool``），之后改环境变量对新模型无效，也没有按模型的
    选项。所以：

    - 第一次加载翻译模型之前判断并设置环境变量，之后这个进程就锁定了（``locked``）；
    - 以后每次加载模型仍按当时的可用提交量重新判断一次，更新 :meth:`health`；新判断与锁定值
      不同时记一条日志，提示重启服务后生效；
    - 用户设了 ``CT2_PACKED_GEMM`` 就用它，不判断。

    判断规则见 :func:`decide_pack`；预计额外提交量按 :func:`estimate_pack_extra_mib` 估算，
    预热时按这一批要加载的全部模型累加（:meth:`planned`）。
    """

    def __init__(
        self,
        environ: MutableMapping[str, str] | None = None,
        *,
        commit_mib: Callable[[], float | None] = commit_available_mib,
        physical_mib: Callable[[], float | None] = physical_memory_mib,
        mkl: Callable[[], bool] | None = None,
    ) -> None:
        self._env = os.environ if environ is None else environ
        self._commit = commit_mib
        self._physical = physical_mib
        self._mkl = mkl or (lambda: mkl_in_use(self._env))
        self._lock = threading.Lock()
        user = self._env.get(PACK_ENV, "").strip()
        self.user_value: str | None = user or None
        self.value: str | None = self.user_value
        self.reason: str | None = "user" if user else None
        self.locked = False
        self.recommended: bool | None = None
        self.commit_available_mib: float | None = None
        self.physical_mib: float | None = None
        self.extra_mib: float | None = None
        self.models: dict[str, bool] = {}
        # 锁定那一次（第一次加载）的判断依据，describe() 用；health() 报最近一次加载的值
        self._decided: tuple[float | None, float | None, float] = (None, None, 0.0)
        self._planned: dict[str, float] = {}

    @property
    def source(self) -> str:
        if self.user_value is not None:
            return "user"
        return "auto" if self.locked else "pending"

    def planned(self, models: Mapping[str, float]) -> _Planned:
        """``with governor.planned({id: model.bin MiB}):`` 里第一次加载时按这一批整体估算。"""

        return _Planned(self, dict(models))

    def before_load(self, model_id: str, bin_mib: float) -> bool:
        """加载一个 CT2 模型之前调用；返回这个模型会不会被预打包。"""

        with self._lock:
            pending = dict(self._planned)
            pending.setdefault(model_id, bin_mib)
            extra = sum(estimate_pack_extra_mib(size) for size in pending.values())
            self._planned.pop(model_id, None)
            commit = self._commit()
            physical = self._physical()
            pack, reason = decide_pack(
                commit_mib=commit,
                physical_mib=physical,
                extra_mib=extra,
                min_commit_mib=_int_env(self._env, MIN_COMMIT_ENV, DEFAULT_MIN_COMMIT_MIB),
                small_ram_mib=_int_env(self._env, SMALL_RAM_ENV, DEFAULT_SMALL_RAM_MIB),
            )
            self.commit_available_mib = commit
            self.physical_mib = physical
            self.extra_mib = extra
            self.recommended = pack
            if self.user_value is None and not self.locked:
                self.value = "1" if pack else "0"
                self.reason = reason
                self._env[PACK_ENV] = self.value
                self.locked = True
                self._decided = (commit, physical, extra)
                logger.info("%s", self.describe())
            elif self.user_value is None and pack != (self.value == "1"):
                logger.warning(
                    "CT2：加载 %s 时按当前内存判断应%s预打包（%s），但本进程第一次加载模型时已定为 "
                    "%s=%s，CTranslate2 进程内只读一次；重启服务后生效",
                    model_id,
                    "开" if pack else "关",
                    _reason_text(reason, commit, physical, extra),
                    PACK_ENV,
                    self.value,
                )
            effective = _truthy(self.value if self.value is not None else "1") and self._mkl()
            self.models[model_id] = effective
            return effective

    def health(self, loaded: list[str] | None = None) -> dict[str, object]:
        commit = self.commit_available_mib
        models = self.models if loaded is None else {
            model_id: self.models[model_id] for model_id in loaded if model_id in self.models
        }  # fmt: skip
        return {
            "ct2_packed_gemm": self.value,
            "ct2_packed_gemm_source": self.source,
            "ct2_packed_gemm_reason": self.reason,
            "ct2_packed_gemm_recommended": self.recommended,
            "ct2_models_packed": models,
            "commit_available_mib": None if commit is None else round(commit),
            "physical_memory_mib": None if self.physical_mib is None else round(self.physical_mib),
            "packed_gemm_extra_mib": None if self.extra_mib is None else round(self.extra_mib),
        }

    def describe(self) -> str:
        if self.user_value is not None:
            return f"CT2：使用用户设置的 {PACK_ENV}={self.user_value}"
        if not self.locked:
            return "CT2：MKL 权重预打包在第一次加载翻译模型时按物理内存和可用提交内存决定（#119）"
        text = _reason_text(self.reason or "", *self._decided)
        if self.value == "0":
            return (
                f"CT2：{text}，已设 {PACK_ENV}=0（关掉 MKL 权重预打包，每个模型少提交约 800 MiB，"
                "译文不变，解码约慢 20–30%，#119）"
            )
        return f"CT2：{text}，MKL 权重预打包保持开启"


class _Planned:
    def __init__(self, governor: PackGovernor, models: dict[str, float]) -> None:
        self._governor = governor
        self._models = models

    def __enter__(self) -> None:
        with self._governor._lock:
            self._governor._planned = dict(self._models)

    def __exit__(self, *_exc: object) -> None:
        with self._governor._lock:
            self._governor._planned = {}


def _truthy(value: str) -> bool:
    return value.strip().lower() in ("1", "true", "yes", "on")


def _reason_text(reason: str, commit: float | None, physical: float | None, extra: float) -> str:
    free = "未知" if commit is None else f"{commit:.0f} MiB"
    ram = "未知" if physical is None else f"{physical:.0f} MiB"
    if reason == "small_ram":
        return f"物理内存 {ram}，不超过小内存阈值"
    if reason == "low_commit":
        return f"可用提交内存 {free} − 预计额外 {extra:.0f} MiB 低于阈值"
    return f"物理内存 {ram}，可用提交内存 {free}，预计额外 {extra:.0f} MiB"


_GOVERNOR: PackGovernor | None = None


def pack_governor() -> PackGovernor:
    """进程内唯一的 :class:`PackGovernor`；包导入时创建（记下用户是否设了 ``CT2_PACKED_GEMM``）。"""

    global _GOVERNOR
    if _GOVERNOR is None:
        _GOVERNOR = PackGovernor()
    return _GOVERNOR
