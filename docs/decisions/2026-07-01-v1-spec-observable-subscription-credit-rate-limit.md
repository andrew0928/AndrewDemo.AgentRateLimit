# 決策：V1 規格只凍結外部可觀測的 subscription credit rate limit 行為

- 決策時間：2026-07-01
- 狀態：superseded
- 範圍：`Subscription Credit Rate Limit V1` 規格

> Superseded by `2026-07-01-subscription-credit-lazy-quota-window-lease.md` for the window semantics. The observable-behavior-first boundary still applies, but the 5h / 7d model is no longer rolling window.

## Context

第一版目標是把 subscription usage control 的期待轉成正式規格與驗收案例。使用者明確要求這版只描述外在能觀測的行為與結果，不描述任何系統內部實作或設計。

同時，使用者的主要約束是：

- credit 使用整數。
- 同時有 5h 與 7d rolling window。
- 超過 window allowance 時可使用 extra pool。
- 單一 database 管理多個 user 的 subscription usage。
- 可以在 SQLite 等入門 database 環境運作。
- 帳務不得出錯，且異常時必須能完整回溯。
- 建置成本優先，infrastructure 越精簡越好。

## Decision

V1 規格凍結在外部可觀測行為：

- usage decision 結果與 reason
- rolling window 計算結果
- extra pool 消耗結果
- idempotency 與 conflict 結果
- 多 user / subscription 隔離結果
- 同時請求下不可超扣的可觀測結果
- restart 後 usage status、audit trail、reconciliation report 仍可回溯

V1 規格不描述：

- database schema
- transaction 或 locking strategy
- queue/cache/message broker
- application class/module boundary
- API route 或 SDK shape

## Consequences

正面影響：

- 規格可先由產品與帳務語意 review，不被內部設計細節干擾。
- 後續可以用同一批 acceptance cases 驗證不同實作。
- SQLite-friendly 的低建置成本目標被寫成驗收約束，而不是先綁死內部做法。

代價：

- 實作階段仍需另行設計如何在目標 database 中達成一致性。
- API contract 與 storage contract 尚未凍結。
- subscription lifecycle、金流、invoice、refund 暫不納入第一版。
