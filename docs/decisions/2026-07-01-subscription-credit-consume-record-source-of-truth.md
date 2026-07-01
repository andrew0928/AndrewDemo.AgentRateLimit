# 決策：subscription credit 以 consume record 作為唯一帳務 source of truth

- 決策時間：2026-07-01
- 狀態：accepted
- 範圍：`Subscription Credit Rate Limit V1` minimal storage source of truth

## Context

使用者重新釐清資料保存優先順序：

- 真正重要的是實際 consume 記錄，因為它代表內部成本、infra log reconciliation、拆帳與分潤基礎。
- 5h / 7d time window 只是 admission control 手段，用來避免短時間大量使用。
- 若需要事後檢查，核心問題是「真實 infra log 有發生成本，但系統是否少記一筆 consume record」，不是「能否重算某個過去時間點的 time window」。

因此，先前 schema draft 中為了重建 window state 而拆出的 quota window lease、quota window usage、credit movement、audit projection 等表格，對目前目標來說過度設計。

## Decision

V1 minimal schema 採 **consume-record-first**：

- 唯一真正帳務 source of truth 是 append-only `subscription_consume_record`。
- `subscription_consume_record` 必須保存 actual credits、infra log reference、coverage snapshot、extra pool authorization snapshot、system absorbed credits、cost allocation / revenue split snapshot。
- `subscription_account` 保存目前 subscription 與 mutable admission state，包括 current 5h / 7d window state 與 extra pool remaining。
- `subscription_extra_pool_record` 保存 extra pool top-up、grant、manual adjustment、correction 等供給與調整事實。
- time window state 可以在 `subscription_account` 內 lazy reset / overwrite，不作為歷史 source facts 保存。
- 不建立必要的 `subscription_quota_window_lease`、`quota_window_usage`、`usage_credit_movement`、`extra_pool_movement`、`usage_audit_entry`、`subscription_usage_projection`。
- accepted consume idempotency 先由 `subscription_consume_record` 的 unique key 支援，不另建 `usage_idempotency_record`。

## Consequences

正面影響：

- 必備表格從先前 draft 的 12 張 logical tables 降為 3 張。
- source of truth 與 reconciliation 目標非常明確：consume record 對 infra log。
- time-window control 不污染帳務資料模型。
- implementation 複雜度與 migration 成本明顯降低。

代價：

- 無法從資料庫 source facts 重建歷史 5h / 7d window state；這是刻意接受的限制。
- rejected / invalid / conflict decision 不再預設是帳務 source of truth；若未來要求完整 decision replay，需要新增 decision/idempotency table。
- extra pool consumption 不另拆 movement table；它必須以 `subscription_consume_record.credits_covered_by_extra_pool` 表達。
- consume record 欄位必須帶足 snapshot，否則未來 cost allocation 與 infra reconciliation 會缺上下文。
