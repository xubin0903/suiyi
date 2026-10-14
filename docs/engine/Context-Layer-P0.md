# Context Layer P0：本篇专名一致（#134）

MVP / 引擎验收把 Context Layer **P0** 定为：切分 + 本篇术语表 + 译后专名一致（不做 LLM）。切分见 [分句规则](分句规则.md)；术语表与占位符保护见 [术语保护](术语保护.md)。本页只写 **本篇级专名一致** 的边界与实现。

## 要解决的问题

一次 `translate` 多句时，旧路径对各句独立做「首遍是否已有表内写法 → 占位符写回」。首遍若落到**不同的表内变体**（例如「竞争条件」与「竞态条件」），跨句同一专名就会漂移。

## P0 做法（本篇术语会话）

在 `suiyi_engine.terms` 里：

1. **建会话** `build_document_term_session(sentences, src, tgt, terms)`：对本次请求的全部分句统一 `find_terms`，得到「术语 id → 本篇目标规范写法」（`fixed` 用表内第一写法；`keep` 用 `target_form`）。
2. **按句保护**：仍走现有同批推测 / 占位符回退（与用户表、内置表、不翻译片段兼容）；坏表不炸翻译。
3. **译后统一** `_enforce_document_consistency`：各句译文里若出现该术语的任一表内写法，全部改成会话里的规范写法（`glossary.rewrite_term_forms`）。

入口仍是 `translate_with_terms`（`Translator` 分句后一次批量调用），无需改 HTTP 字段或用户术语表格式。

### 一致率小评测

`term_consistency_rate(outputs, term, tgt)`：在「出现过该术语任一写法」的句子中，使用规范写法的比例。单测 `test_sentence_isolated_baseline_drifts_document_session_does_not` 用 FakeModel 对比：

| | 一致率 |
|--|--------|
| 句级孤立（裸首遍变体） | &lt; 1（样例约 1/3） |
| 本篇会话（一次多句 + 译后统一） | 1.0 |

## 本期边界

| 做 | 不做 |
|----|------|
| 一次请求内的文档级术语会话 | **P1** 用户改译写回 / 跨请求会话记忆 |
| 表内变体 → 本篇规范写法 | **P2** 滑动窗口 / 两遍译 |
| zh↔en 直连（与现有术语保护同范围） | LLM、改客户端 UI |
| 与占位符回退、用户 TSV、内置表兼容 | 改用户表格式 |

## 相关符号

- `DocumentTermSession` / `build_document_term_session`
- `TermStats.consistency_fixes`：按「句 × 术语」计的统一次数
- `glossary.rewrite_term_forms`
