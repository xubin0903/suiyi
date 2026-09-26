"""术语表匹配（#78）。"""

from __future__ import annotations

import pytest

from suiyi_engine.glossary import (
    GlossaryError,
    find_terms,
    parse_terms,
    placeholder,
    present_form,
    protect,
    restore,
    term_present,
)

TERMS = parse_terms(
    [
        {"id": "k8s", "kind": "keep", "en": ["Kubernetes"], "zh": ["Kubernetes"]},
        {"id": "cache", "kind": "fixed", "en": ["cache"], "zh": ["缓存"]},
        {
            "id": "cache-hit-rate",
            "kind": "fixed",
            "en": ["cache hit rate"],
            "zh": ["缓存命中率"],
        },
        {"id": "ft", "kind": "fixed", "en": ["fine-tuned", "fine-tune"], "zh": ["微调"]},
        {"id": "oauth-token", "kind": "fixed", "en": ["OAuth token"], "zh": ["OAuth 令牌"]},
        {"id": "race", "kind": "fixed", "en": ["race condition"], "zh": ["竞态条件", "竞争条件"]},
    ]
)
BY_ID = {term.id: term for term in TERMS}


def test_longest_match_wins_and_positions_are_in_original_text() -> None:
    text = "The cache hit rate dropped, so we flushed the cache."
    found = find_terms(text, "en", TERMS)
    assert [match.term.id for match in found] == ["cache-hit-rate", "cache"]
    assert text[found[0].start : found[0].end] == "cache hit rate"


def test_english_matching_handles_plural_hyphen_and_case() -> None:
    assert [m.term.id for m in find_terms("Store OAuth tokens safely.", "en", TERMS)] == [
        "oauth-token"
    ]
    assert find_terms("The model was fine tuned twice.", "en", TERMS)[0].term.id == "ft"
    assert find_terms("Race conditions are hard.", "en", TERMS)[0].term.id == "race"
    # keep 条目在原文里区分大小写；词边界避免误命中
    assert find_terms("kubernetes", "en", TERMS) == []
    assert find_terms("Kubernetesly", "en", TERMS) == []


def test_cjk_matching_ignores_whitespace() -> None:
    found = find_terms("缓存命中率 下降，OAuth令牌 过期。", "zh", TERMS)
    assert [m.term.id for m in found] == ["cache-hit-rate", "oauth-token"]
    assert term_present("请更新 OAuth令牌", BY_ID["oauth-token"], "zh")
    assert present_form("出现了竞争条件", BY_ID["race"], "zh") == "竞争条件"
    assert not term_present("出现了死锁", BY_ID["race"], "zh")


def test_target_check_is_case_insensitive() -> None:
    assert term_present("kubernetes 集群", BY_ID["k8s"], "zh")
    assert term_present("Cache hit rates fell.", BY_ID["cache-hit-rate"], "en")


def test_parse_rejects_bad_entries() -> None:
    with pytest.raises(GlossaryError):
        parse_terms([{"id": "x", "kind": "odd", "en": ["x"]}])
    with pytest.raises(GlossaryError):
        parse_terms([{"id": "x", "kind": "keep", "zh": ["x"]}])
    with pytest.raises(GlossaryError):
        parse_terms(
            [{"id": "x", "kind": "keep", "en": ["x"]}, {"id": "x", "kind": "keep", "en": ["y"]}]
        )


def test_find_terms_every_returns_repeated_occurrences() -> None:
    text = "Clear the cache, then warm the cache."
    assert len(find_terms(text, "en", TERMS)) == 1
    assert len(find_terms(text, "en", TERMS, every=True)) == 2


def test_placeholders_are_unique_and_letter_based() -> None:
    names = [placeholder(i) for i in range(12)]
    assert names[:3] == ["ZXQ", "ZXW", "ZXJ"]
    assert len(set(names)) == 12
    assert names[5] == "ZXQ1"


def test_protect_and_restore_round_trip_en_to_zh() -> None:
    text = "Kubernetes keeps the cache hit rate high."
    protected = protect(text, "en", "zh", TERMS)
    assert protected.text == "ZXQ keeps the ZXW high."
    assert [slot.target for slot in protected.slots] == ["Kubernetes", "缓存命中率"]
    restored, failed = restore("ZXQ 让 zxw 保持在高位。", protected, "zh")
    assert restored == "Kubernetes 让缓存命中率保持在高位。"
    assert failed == []


def test_restore_reports_lost_and_duplicated_placeholders() -> None:
    protected = protect("Kubernetes flushes the cache.", "en", "zh", TERMS)
    restored, failed = restore("ZXQ ZXQ 清空了。", protected, "zh")
    assert {slot.target for slot in failed} == {"Kubernetes", "缓存"}
    assert "ZXQ" in restored


def test_restore_capitalises_fixed_term_at_english_sentence_start() -> None:
    protected = protect("竞态条件很难复现。", "zh", "en", TERMS)
    assert protected.text == "ZXQ很难复现。"
    restored, failed = restore("ZXQ is hard to reproduce.", protected, "en")
    assert restored == "Race condition is hard to reproduce."
    assert failed == []


def test_protect_skips_text_that_already_looks_like_a_placeholder() -> None:
    protected = protect("Set ZXQ1 in the cache.", "en", "zh", TERMS)
    assert protected.slots == ()
    assert protected.text == "Set ZXQ1 in the cache."
