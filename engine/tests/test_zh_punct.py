"""中文译文标点规范化（#97）。"""

from __future__ import annotations

import pytest

from suiyi_engine.zh_punct import normalize_zh_punct


@pytest.mark.parametrize(
    ("raw", "expected"),
    [
        # Issue #97 的实机输出
        (
            "Kubernetes 是一个开源系统,用于自动化部署扩展和管理容器化应用程序。"
            "它是由CNCF(Cloud Native Computing Foundation,云计算基金会)主办的开源项目。",
            "Kubernetes 是一个开源系统，用于自动化部署扩展和管理容器化应用程序。"
            "它是由CNCF（Cloud Native Computing Foundation，云计算基金会）主办的开源项目。",
        ),
        (
            "它是由 CNCF (云原生计算基金会) 托管的开源项目。",
            "它是由 CNCF（云原生计算基金会）托管的开源项目。",
        ),
        ("精确度很高, 但召回很差", "精确度很高，但召回很差"),
        ("用于自动化部署;扩展和管理", "用于自动化部署；扩展和管理"),
        ("注意:这是测试", "注意：这是测试"),
        ("你好!真的吗?", "你好！真的吗？"),
        ("Kubernetes 也称为 K8s,是开源系统", "Kubernetes 也称为 K8s，是开源系统"),
        ("模型(GPT-4)很强", "模型（GPT-4）很强"),
        ("见第 3 章 (2023)。", "见第 3 章（2023）。"),
        ("混淆矩阵(confusion matrix)显示了频率", "混淆矩阵（confusion matrix）显示了频率"),
        (", ,求实数 a 的值; ,求实数", "，，求实数 a 的值；，求实数"),
    ],
)
def test_half_width_punct_next_to_chinese_becomes_full_width(raw: str, expected: str) -> None:
    assert normalize_zh_punct(raw) == expected


@pytest.mark.parametrize(
    "text",
    [
        "超过 2,000 个测试用例，耗时 12:30",
        "请访问 https://example.com/a,b?x=1&y=(2) 查看",
        "打开 www.example.com/a,b 页面",
        "发送邮件到 a.b@example.com 就行",
        "路径 C:\\Users\\me\\a,b.txt 不存在",
        "运行 `ls -l; echo (x)` 命令",
        "复杂度是 O(n log n)",
        "调用 foo(bar) 函数",
        "函数 f(X) 的值",
        "Hello, world! (Really?)",
        "这里没有半角标点。",
        "",
        "ZXQ, ZXW",
    ],
)
def test_code_urls_numbers_and_latin_only_text_are_untouched(text: str) -> None:
    assert normalize_zh_punct(text) == text


def test_only_the_parts_inside_chinese_change() -> None:
    raw = "调用 foo(a, b) 之后,访问 http://x.io/?q=1,2 即可(见文档)"
    assert normalize_zh_punct(raw) == "调用 foo(a, b) 之后，访问 http://x.io/?q=1,2 即可（见文档）"


def test_unbalanced_parentheses_are_left_alone() -> None:
    assert normalize_zh_punct("开始(未闭合,继续") == "开始(未闭合，继续"
