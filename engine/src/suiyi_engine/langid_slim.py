"""py3langid 的精简运行时（#92）：只保留四个拉丁语种，模型放在缓存文件里 mmap 读取。

py3langid 加载后常驻约 56 MiB：numpy（约 12～16 MiB）、97 个语种的特征权重和一张 37 MiB 的
DFA 转移表。``langdetect`` 只让它区分 de / en / es / fr，而且只在拉丁文本上调用。

本模块把检测需要的数据（DFA、每个状态的输出特征、四个语种的权重）一次性导出成一个缓存文件。
之后每次启动只 ``mmap`` 这个文件，用纯 Python 打分：

- 不导入 numpy；
- 转移表只有真正走到的页才进内存，是可回收的文件页；
- 启动不再解压 4.5 MB 的 xz 模型。

导出在**子进程**里做（需要 numpy 和完整模型），服务进程本身不承担这部分内存。
缓存键是 py3langid 模型文件的大小与 sha256，py3langid 升级后自动重建。
任何一步失败，调用方退回原来的 py3langid（结果相同，只是内存大一些）。

打分与 ``LanguageIdentifier(norm_probs=True).set_languages(langs).rank`` 相同：
特征计数取 ``log1p`` 乘权重，加先验，乘 ``1/sqrt(字节数)`` 后做 softmax。
py3langid 用 float32 矩阵乘，这里用 Python float（双精度），概率差在 1e-5 量级。
"""

from __future__ import annotations

import hashlib
import json
import math
import mmap
import os
import struct
import subprocess
import sys
import tempfile
import unicodedata
from collections import Counter
from collections.abc import Sequence
from pathlib import Path

MAGIC = b"SUIYILID"
FORMAT_VERSION = 1
_HEADER_LEN = struct.Struct("<8sI")  # magic, JSON 头长度
_ALIGN = 16


def default_cache_dir(env: dict[str, str] | None = None) -> Path:
    """``SUIYI_CACHE_DIR`` → Windows ``%LOCALAPPDATA%\\suiyi\\cache`` →
    ``$XDG_CACHE_HOME/suiyi``（默认 ``~/.cache/suiyi``）。"""

    environ = os.environ if env is None else env
    override = environ.get("SUIYI_CACHE_DIR", "").strip()
    if override:
        return Path(override)
    if sys.platform == "win32":
        base = environ.get("LOCALAPPDATA", "").strip()
        if base:
            return Path(base) / "suiyi" / "cache"
    xdg = environ.get("XDG_CACHE_HOME", "").strip()
    return (Path(xdg) if xdg else Path.home() / ".cache") / "suiyi"


def source_model_file() -> Path:
    """py3langid 自带模型文件的路径（只找路径，不导入 numpy）。"""

    import importlib.util

    spec = importlib.util.find_spec("py3langid")
    if spec is None or spec.origin is None:
        raise FileNotFoundError("未安装 py3langid")
    return Path(spec.origin).parent / "data" / "model.npz.xz"


def model_key(model_file: Path, langs: Sequence[str]) -> str:
    digest = hashlib.sha256(model_file.read_bytes()).hexdigest()[:16]
    return f"v{FORMAT_VERSION}-{digest}-{'-'.join(sorted(langs))}"


def cache_path(cache_dir: Path, key: str) -> Path:
    return cache_dir / f"langid-{key}.bin"


# ---------------------------------------------------------------- 导出（子进程里跑）


def export_cache(dest: Path, langs: Sequence[str]) -> None:
    """用 py3langid 的完整模型导出缓存文件。需要 numpy；先写临时文件再原子替换。"""

    from py3langid.langid import MODEL_FILE, LanguageIdentifier

    identifier = LanguageIdentifier.from_model_file(MODEL_FILE, norm_probs=True)
    identifier.set_languages(list(langs))
    classes = list(identifier.nb_classes)
    ptc = identifier.nb_ptc.astype("<f4")
    pc = identifier.nb_pc.astype("<f4")
    nextmove = identifier.tk_nextmove  # array('H' 或 'I')：去重后的转移行，每行 256 项
    rows = identifier.tk_row  # array('H')：状态 → 行号
    output = identifier.tk_output  # list[int]：状态 → 特征号，-1 表示无
    import numpy as np

    sections = {
        "nextmove": np.asarray(nextmove, dtype="<u4").tobytes(),
        "rows": np.asarray(rows, dtype="<u4").tobytes(),
        "output": np.asarray(output, dtype="<i4").tobytes(),
        "ptc": np.ascontiguousarray(ptc).tobytes(),
        "pc": np.ascontiguousarray(pc).tobytes(),
    }
    layout: dict[str, list[int]] = {}
    header = {"format": FORMAT_VERSION, "classes": classes, "sections": layout}
    # 两遍：先估头长度，再按对齐算偏移。
    for _ in range(2):
        head = json.dumps(header, separators=(",", ":")).encode("utf-8")
        offset = _align(_HEADER_LEN.size + len(head))
        for name, blob in sections.items():
            layout[name] = [offset, len(blob)]
            offset = _align(offset + len(blob))
    head = json.dumps(header, separators=(",", ":")).encode("utf-8")
    dest.parent.mkdir(parents=True, exist_ok=True)
    fd, tmp = tempfile.mkstemp(prefix=dest.name, suffix=".tmp", dir=dest.parent)
    try:
        with os.fdopen(fd, "wb") as out:
            out.write(_HEADER_LEN.pack(MAGIC, len(head)))
            out.write(head)
            for name, blob in sections.items():
                out.write(b"\0" * (layout[name][0] - out.tell()))
                out.write(blob)
        os.replace(tmp, dest)
    except BaseException:
        Path(tmp).unlink(missing_ok=True)
        raise


def _align(value: int) -> int:
    return (value + _ALIGN - 1) // _ALIGN * _ALIGN


def ensure_cache(cache_dir: Path, langs: Sequence[str], *, timeout_s: float = 120.0) -> Path:
    """缓存不存在时在子进程里导出，返回缓存路径。失败抛 ``OSError`` / ``RuntimeError``。"""

    key = model_key(source_model_file(), langs)
    path = cache_path(cache_dir, key)
    if path.is_file():
        return path
    command = [sys.executable, "-m", "suiyi_engine.langid_slim", str(path), *langs]
    done = subprocess.run(command, capture_output=True, text=True, timeout=timeout_s, check=False)
    if done.returncode != 0 or not path.is_file():
        tail = (done.stderr or "").strip().splitlines()[-1:] or ["无输出"]
        raise RuntimeError(f"导出语种检测缓存失败（{done.returncode}）：{tail[0]}")
    return path


# ---------------------------------------------------------------- 运行时


class SlimIdentifier:
    """只读 mmap 缓存文件的检测器，接口与 py3langid 的 ``rank`` 相同。"""

    def __init__(self, path: Path) -> None:
        with open(path, "rb") as handle:
            self._mmap = mmap.mmap(handle.fileno(), 0, access=mmap.ACCESS_READ)
        try:
            magic, head_len = _HEADER_LEN.unpack_from(self._mmap, 0)
            if magic != MAGIC:
                raise ValueError("不是随译语种检测缓存")
            header = json.loads(self._mmap[_HEADER_LEN.size : _HEADER_LEN.size + head_len])
            if header.get("format") != FORMAT_VERSION:
                raise ValueError("缓存格式版本不符")
            self.classes: list[str] = list(header["classes"])
            view = memoryview(self._mmap)

            def section(name: str, fmt: str) -> memoryview:
                offset, length = header["sections"][name]
                if offset + length > len(self._mmap):
                    raise ValueError(f"缓存文件截断：{name}")
                return view[offset : offset + length].cast(fmt)

            self._nextmove = section("nextmove", "I")
            self._rows = section("rows", "I")
            self._output = section("output", "i")
            width = len(self.classes)
            ptc = section("ptc", "f")
            self._ptc = ptc
            self._pc = tuple(section("pc", "f"))
            self._width = width
            if len(ptc) % width or len(self._pc) != width:
                raise ValueError("缓存文件的权重形状不符")
        except Exception:
            self.close()
            raise

    def close(self) -> None:
        try:
            self._mmap.close()
        except (BufferError, AttributeError):
            pass

    @staticmethod
    def _encode(text: str) -> bytes:
        if text.isupper():
            text = text.lower()
        return unicodedata.normalize("NFC", text).encode("utf8", errors="surrogatepass")

    def rank(self, text: str) -> list[tuple[str, float]]:
        data = self._encode(text)
        nextmove, rows, output = self._nextmove, self._rows, self._output
        state = 0
        features: list[int] = []
        for byte in data:
            state = nextmove[(rows[state] << 8) + byte]
            feature = output[state]
            if feature >= 0:
                features.append(feature)
        width, ptc = self._width, self._ptc
        if features:
            scores = list(self._pc)
            for feature, count in Counter(features).items():
                weight = math.log1p(count)
                base = feature * width
                for k in range(width):
                    scores[k] += weight * ptc[base + k]
            scale = 1.0 / math.sqrt(len(data) or 1)
            scaled = [score * scale for score in scores]
            top = max(scaled)
            exps = [math.exp(value - top) for value in scaled]
            total = sum(exps)
            probs = [value / total for value in exps]
        else:
            probs = [1.0 / width] * width  # py3langid：没有特征时均匀分布
        return sorted(zip(self.classes, probs, strict=True), key=lambda item: item[1], reverse=True)


def main(argv: list[str] | None = None) -> int:
    """``python -m suiyi_engine.langid_slim <缓存路径> <语种...>``：导出缓存。"""

    args = sys.argv[1:] if argv is None else argv
    if len(args) < 2:
        print("用法：python -m suiyi_engine.langid_slim <缓存路径> <语种...>", file=sys.stderr)
        return 2
    export_cache(Path(args[0]), args[1:])
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
