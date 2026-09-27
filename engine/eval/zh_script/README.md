# 中文译文乱码 / 繁体扫描集（#106）

- `short_en_v1.txt`：312 条英文短句、常见句（问候、界面提示、报错、`This is a/an <名词>.` 等），用于 en→zh。
- `short_ja_v1.txt`：25 条日文短句，用于 ja→zh（经英文中转）。

全部为本项目自写，许可与仓库相同，不含第三方语料。用 `scripts/scan_zh_script.py` 扫描：

```bash
python scripts/scan_zh_script.py --models-dir models --out scan.json
# 与 main 对比（判定与 chrF 用本仓库代码，翻译用 --src 指向的代码）
python scripts/scan_zh_script.py --models-dir models --src <main 的 worktree>/engine/src --out scan-main.json
```

脚本同时扫描专业领域评测集里目标为中文的条目（en→zh 154 条、ja→zh 20 条）并算 chrF。
乱码 / 繁体的判定见 `engine/src/suiyi_engine/zh_script.py`。
