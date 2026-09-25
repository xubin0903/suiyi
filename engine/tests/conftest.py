"""让带 model 标记的测试在模型目录不可用时跳过。"""

import os
from pathlib import Path

import pytest

_SKIP_REASON = "需要真实模型：请将环境变量 SUIYI_MODELS_DIR 设为已存在的模型目录后再运行"


def _models_dir_ready() -> bool:
    raw = os.environ.get("SUIYI_MODELS_DIR", "").strip()
    if not raw:
        return False
    return Path(raw).is_dir()


def pytest_collection_modifyitems(items: list[pytest.Item]) -> None:
    if _models_dir_ready():
        return
    skip_model = pytest.mark.skip(reason=_SKIP_REASON)
    for item in items:
        if item.get_closest_marker("model") is not None:
            item.add_marker(skip_model)
