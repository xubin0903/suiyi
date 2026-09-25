"""语种检测：规则层、短文本、中英日准确率与懒加载。"""

import json
import subprocess
import sys
import threading
import time
from pathlib import Path

import pytest

from suiyi_engine.langdetect import (
    CJK_ZH_RATIO_THRESHOLD,
    DEFAULT_CANDIDATES,
    Detection,
    detect,
)

_REPO_ROOT = Path(__file__).resolve().parents[2]
_ZH_CORE_SAMPLES = _REPO_ROOT / "tests" / "samples" / "zh_core_v1.jsonl"

# 各 ≥ 10 条、每条 ≥ 5 个字符。日语句子都带假名；纯汉字日语单独测。
_ZH_SENTENCES = [
    "今天天气很好，我们去公园吧。",
    "这部电影的结局让人意想不到。",
    "请把会议纪要发给所有参会人员。",
    "我明天早上九点到办公室。",
    "人工智能正在改变教育方式。",
    "这家餐厅的菜味道不错。",
    "请问最近的地铁站怎么走？",
    "他已经完成了今天的作业。",
    "周末我们打算去上海博物馆。",
    "这份报告需要在周五之前提交。",
    "学习一门新语言需要每天练习。",
    "剪贴板里的文字应该被正确翻译。",
]

_EN_SENTENCES = [
    "The weather is nice today, so we will go to the park.",
    "Please send the meeting notes to everyone who attended.",
    "I will arrive at the office at nine tomorrow morning.",
    "Artificial intelligence is changing the way people learn.",
    "This restaurant serves food that tastes quite good.",
    "Could you tell me how to get to the nearest subway station?",
    "He has already finished his homework for today.",
    "We plan to visit the museum in Shanghai this weekend.",
    "The report needs to be submitted before Friday afternoon.",
    "Learning a new language requires practice every day.",
    "Clipboard text should be translated correctly and quickly.",
    "She opened the window because the room felt too warm.",
]

_JA_SENTENCES = [
    "今日は天気がいいので、公園に行きます。",
    "会議の議事録を参加者全員に送ってください。",
    "明日の朝九時にオフィスに着きます。",
    "人工知能は教育のあり方を変えつつあります。",
    "このレストランの料理はとても美味しいです。",
    "最寄りの地下鉄駅への行き方を教えてください。",
    "彼は今日の宿題をもう終えました。",
    "週末は上海の博物館を訪ねる予定です。",
    "この報告書は金曜日までに提出する必要があります。",
    "新しい言語を学ぶには毎日の練習が必要です。",
    "クリップボードの文章は正しく翻訳されるべきです。",
    "窓を開けたら部屋が少し涼しくなりました。",
]

_FR_SENTENCES = [
    "Bonjour, comment allez-vous aujourd'hui ?",
    "Je voudrais un café et un croissant, s'il vous plaît.",
    "Le train pour Lyon part dans dix minutes.",
    "Cette bibliothèque est ouverte tous les jours sauf le lundi.",
    "Il fait beau, nous allons nous promener au bord de la rivière.",
]

_DE_SENTENCES = [
    "Guten Morgen, wie geht es Ihnen heute?",
    "Der Zug nach Berlin fährt in zehn Minuten ab.",
    "Ich möchte bitte einen Kaffee und ein Stück Kuchen.",
    "Die Bibliothek ist montags geschlossen und an anderen Tagen offen.",
    "Das Wetter ist schön, wir gehen am Fluss spazieren.",
]

_ES_SENTENCES = [
    "El tren a Madrid sale dentro de diez minutos.",
    "Quisiera un café y un trozo de pastel, por favor.",
    "La biblioteca está abierta todos los días excepto el lunes.",
    "Hace buen tiempo y vamos a pasear junto al río.",
    "El museo abre sus puertas a las diez en punto.",
]

_KO_SENTENCES = [
    "오늘 날씨가 좋아서 공원에 갑니다.",
    "회의록을 참가자 모두에게 보내 주세요.",
    "내일 아침 아홉 시에 사무실에 도착합니다.",
    "이 식당의 음식은 맛이 아주 좋습니다.",
    "가장 가까운 지하철역으로 가는 길을 알려 주세요.",
]

_RU_SENTENCES = [
    "Сегодня хорошая погода, мы идём в парк.",
    "Пожалуйста, отправьте протокол всем участникам.",
    "Я приду в офис завтра в девять утра.",
    "Этот ресторан готовит очень вкусную еду.",
    "Подскажите, как пройти к ближайшей станции метро.",
]


def _assert_lang(sentences: list[str], lang: str, method: str | None = None) -> None:
    assert len(sentences) >= 5
    for sentence in sentences:
        assert len(sentence) >= 5
        result = detect(sentence)
        assert result.lang == lang, (sentence, result)
        assert 0.0 <= result.confidence <= 1.0
        if method is not None:
            assert result.method == method, (sentence, result)


def test_chinese_sentences_are_perfect() -> None:
    assert len(_ZH_SENTENCES) >= 10
    _assert_lang(_ZH_SENTENCES, "zh", "script")


def test_english_sentences_are_perfect() -> None:
    assert len(_EN_SENTENCES) >= 10
    _assert_lang(_EN_SENTENCES, "en", "model")


def test_japanese_sentences_with_kana_are_perfect() -> None:
    assert len(_JA_SENTENCES) >= 10
    _assert_lang(_JA_SENTENCES, "ja", "script")


def test_french_german_spanish_korean_russian_sentences() -> None:
    _assert_lang(_FR_SENTENCES, "fr", "model")
    _assert_lang(_DE_SENTENCES, "de", "model")
    _assert_lang(_ES_SENTENCES, "es", "model")
    _assert_lang(_KO_SENTENCES, "ko", "script")
    _assert_lang(_RU_SENTENCES, "ru", "script")


def test_traditional_chinese_is_zh() -> None:
    result = detect("今天天氣很好，我們打算去臺北故宮。")
    assert result == Detection("zh", result.confidence, "script")
    assert result.confidence > 0.9


@pytest.mark.parametrize(
    ("text", "lang"),
    [
        ("Hello", "en"),
        ("Thanks", "en"),
        ("Hi", "en"),
        ("OK", "en"),
        ("Yes", "en"),
        ("Bonjour", "fr"),
        ("Merci", "fr"),
        ("Hola", "es"),
        ("Gracias", "es"),
        ("Danke", "de"),
        ("Bitte", "de"),
        ("你好", "zh"),
        ("こんにちは", "ja"),
        ("コンピュータ", "ja"),
        ("안녕", "ko"),
        ("Привет", "ru"),
    ],
)
def test_short_text_has_reasonable_language(text: str, lang: str) -> None:
    words = text.split()
    assert 1 <= len(words) <= 3
    result = detect(text)
    assert result.lang == lang
    assert result.method in {"script", "model", "fallback"}


def test_low_confidence_short_latin_falls_back_to_english() -> None:
    # 「No」不在封闭词表里，四语种模型近乎均匀分布。
    result = detect("No")
    assert result.lang == "en"
    assert result.method == "fallback"
    assert result.confidence <= 0.30


@pytest.mark.parametrize("text", ["", "   ", "12345", "!!!", "2024-09-25", "😀😀", "..."])
def test_numbers_and_symbols_are_undetermined(text: str) -> None:
    assert detect(text) == Detection("und", 0.0, "fallback")


def test_mixed_chinese_english_is_chinese() -> None:
    result = detect("这个 bug 怎么 fix")
    assert result.lang == "zh"
    assert result.method == "script"
    assert detect("用 Python 写个脚本").lang == "zh"
    assert detect("打开 Settings 看看").lang == "zh"


def test_cjk_ratio_threshold_is_strict() -> None:
    assert CJK_ZH_RATIO_THRESHOLD == 0.20
    n_not_above = 1
    while 1 / (1 + n_not_above) > CJK_ZH_RATIO_THRESHOLD:
        n_not_above += 1
    above = ("a" * (n_not_above - 1)) + "好"
    at_or_below = ("b" * n_not_above) + "好"
    assert detect(above).lang == "zh"
    assert detect(above).method == "script"
    assert detect(at_or_below).lang != "zh"


def test_kanji_only_japanese_is_accepted_as_chinese() -> None:
    for text in ("東京大学", "京都", "株式会社", "東京都庁"):
        result = detect(text)
        assert result.lang == "zh"
        assert result.method == "script"


def test_kana_overrides_kanji() -> None:
    assert detect("東京大学は日本です。").lang == "ja"
    assert detect("バグを直して").lang == "ja"


def test_halfwidth_katakana_and_fullwidth_latin() -> None:
    assert detect("ｺﾝﾆﾁﾊ").lang == "ja"
    assert detect("Ｈｅｌｌｏ　ｗｏｒｌｄ").lang == "en"
    assert detect("ＯＫ").lang == "en"


def test_chinese_middle_dot_is_not_japanese() -> None:
    assert detect("比尔・盖茨").lang == "zh"
    assert detect("约翰･洛克").lang == "zh"


def test_ocr_noise() -> None:
    assert detect("今天天气很好,我们去公园 吧").lang == "zh"
    assert detect("人工智 能的发展").lang == "zh"
    assert detect("今天\n天气很好").lang == "zh"
    assert detect("これは　テストです。").lang == "ja"


def test_orthography_rules() -> None:
    assert detect("Straße").lang == "de"
    assert detect("niño").lang == "es"
    assert detect("¿cómo está?").lang == "es"
    assert detect("œil").lang == "fr"


def test_default_candidates_match_explicit_list() -> None:
    text = "The weather is nice today, so we will go to the park."
    assert detect(text) == detect(text, candidates=list(DEFAULT_CANDIDATES))


def test_candidates_can_exclude_a_script_language() -> None:
    assert detect("こんにちは", candidates=["zh", "en"]).lang == "und"
    assert detect("Привет", candidates=["en", "zh"]).lang == "und"
    assert detect("你好", candidates=["en", "ja"]).lang == "und"


def test_latin_candidates_limit_the_model() -> None:
    assert detect("Bonjour", candidates=["en", "fr"]).lang == "fr"
    french = detect("Hello", candidates=["fr", "de"])
    assert french.lang in {"fr", "de"}


def test_unknown_candidate_raises() -> None:
    with pytest.raises(ValueError, match="pt"):
        detect("hello", candidates=["en", "pt"])


def test_empty_candidates_are_undetermined() -> None:
    assert detect("hello", candidates=[]).lang == "und"


def test_bad_types_raise() -> None:
    with pytest.raises(TypeError):
        detect(123)  # type: ignore[arg-type]
    with pytest.raises(TypeError):
        detect("hello", candidates="en")  # type: ignore[arg-type]


def test_detection_is_frozen() -> None:
    result = detect("你好")
    with pytest.raises(AttributeError):
        result.lang = "en"  # type: ignore[misc]


def test_script_path_does_not_import_the_model() -> None:
    script = """
import sys
from suiyi_engine.langdetect import detect
assert detect("今天天气很好，我们去公园吧。").lang == "zh"
assert detect("こんにちは、今日はいい天気です。").lang == "ja"
assert detect("안녕하세요").lang == "ko"
assert detect("Сегодня хорошая погода。").lang == "ru"
assert detect("Hello").lang == "en"
assert "numpy" not in sys.modules
assert "py3langid" not in sys.modules
"""
    completed = subprocess.run(
        [sys.executable, "-c", script],
        check=False,
        capture_output=True,
        text=True,
        encoding="utf-8",
    )
    assert completed.returncode == 0, completed.stderr


def test_import_does_not_load_the_model() -> None:
    script = """
import sys
import suiyi_engine.langdetect as langdetect
assert langdetect._identifier is None
assert "numpy" not in sys.modules
"""
    completed = subprocess.run(
        [sys.executable, "-c", script],
        check=False,
        capture_output=True,
        text=True,
        encoding="utf-8",
    )
    assert completed.returncode == 0, completed.stderr


def test_concurrent_first_load_agrees() -> None:
    script = """
import threading
from suiyi_engine.langdetect import detect
text = "Learning a new language requires practice every day."
out = []
def run():
    out.append(detect(text).lang)
threads = [threading.Thread(target=run) for _ in range(8)]
for thread in threads:
    thread.start()
for thread in threads:
    thread.join()
assert out == ["en"] * 8, out
"""
    completed = subprocess.run(
        [sys.executable, "-c", script],
        check=False,
        capture_output=True,
        text=True,
        encoding="utf-8",
    )
    assert completed.returncode == 0, completed.stderr


def test_warm_detection_is_faster_than_20ms() -> None:
    sample = "The weather is nice today, so we will go to the park."
    assert len(sample) <= 200
    detect(sample)
    elapsed_ms = []
    for _ in range(30):
        started = time.perf_counter()
        detect(sample)
        elapsed_ms.append((time.perf_counter() - started) * 1000)
    assert max(elapsed_ms) < 20


def test_parallel_detect_in_process() -> None:
    text = "She opened the window because the room felt too warm."
    found: list[Detection] = []

    def worker() -> None:
        found.append(detect(text))

    threads = [threading.Thread(target=worker) for _ in range(4)]
    for thread in threads:
        thread.start()
    for thread in threads:
        thread.join()
    assert found
    assert len(set(found)) == 1
    assert found[0].lang == "en"


@pytest.mark.skipif(not _ZH_CORE_SAMPLES.is_file(), reason="样例集尚未合并（Issue #5），不阻塞")
def test_optional_zh_core_sample_accuracy() -> None:
    """样例集若已存在，则用 source/src_lang 做额外准确率检查。"""
    checked = 0
    misses: list[str] = []
    with _ZH_CORE_SAMPLES.open(encoding="utf-8") as handle:
        for line_no, line in enumerate(handle, start=1):
            if not line.strip():
                continue
            row = json.loads(line)
            source = row.get("source") or ""
            src_lang = row.get("src_lang") or ""
            if src_lang not in {"zh", "en", "ja"} or len(source) < 5:
                continue
            got = detect(source).lang
            checked += 1
            if src_lang == "ja" and got == "zh":
                continue
            if got != src_lang:
                misses.append(f"{line_no}:{row.get('id')}:{src_lang}->{got}")
    if checked == 0:
        pytest.skip("样例集里没有可检查的中英日句子")
    assert not misses, misses


def test_warmup_loads_model_once_and_keeps_results() -> None:
    script = """
import time
import suiyi_engine.langdetect as langdetect
assert not langdetect.is_warm()
first = langdetect.warmup()
assert langdetect.is_warm()
identifier = langdetect._identifier
second = langdetect.warmup()
assert langdetect._identifier is identifier
assert second < first
start = time.perf_counter()
result = langdetect.detect("Good morning, everyone.")
detect_ms = (time.perf_counter() - start) * 1000
assert result.lang == "en" and result.method == "model", result
assert detect_ms < 200, detect_ms
print(f"{first:.1f} {second:.3f} {detect_ms:.3f}")
"""
    completed = subprocess.run(
        [sys.executable, "-c", script],
        check=False,
        capture_output=True,
        text=True,
        encoding="utf-8",
    )
    assert completed.returncode == 0, completed.stderr
