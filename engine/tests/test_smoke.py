"""不访问网络、不加载模型的冒烟测试。"""

import os
import subprocess
import sys
from importlib.metadata import version
from pathlib import Path

import pytest

from suyi_engine import __version__


def test_version_is_scaffold() -> None:
    assert version("suiyi-engine") == __version__ == "0.0.1"


def test_module_version_flag() -> None:
    completed = subprocess.run(
        [sys.executable, "-m", "suiyi_engine", "--version"],
        check=False,
        capture_output=True,
        text=True,
        encoding="utf-8",
    )
    assert completed.returncode == 0
    assert completed.stdout.strip() == "0.0.1"


def test_utf8_literals_do_not_depend_on_system_codepage() -> None:
    sample = "剪贴板翻译"
    assert sample.encode("utf-8") == bytes(
        [
            0xE5,
            0x89,
            0xAA,
            0xE8,
            0xB4,
            0xB4,
            0xE6,
            0x9D,
            0xBF,
            0xE7,
            0xBF,
            0xBB,
            0xE8,
            0xAF,
            0x91,
        ]
    )


@pytest.mark.model
def test_example_model_marker_sees_models_dir() -> None:
    """占位示例：仅当 SUIYI_MODELS_DIR 指向已存在目录时运行。"""
    models_dir = os.environ["SUIYI_MODELS_DIR"]
    assert Path(models_dir).is_dir()
