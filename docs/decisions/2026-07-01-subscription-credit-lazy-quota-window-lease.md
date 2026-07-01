# 決策：subscription credit 採 lazy quota window lease，不採 per-usage rolling window

- 決策時間：2026-07-01
- 狀態：accepted for behavior; storage details partially superseded
- 範圍：`Subscription Credit Rate Limit V1` window semantics

> Storage source-of-truth details are superseded by `2026-07-01-subscription-credit-consume-record-source-of-truth.md`. This decision still defines the active 5h / 7d lazy window behavior.

## Context

使用者確認 Claude Code-like credit limit 行為不是每筆 usage 依照自己的使用時間逐筆滑出 rolling window。長時間沒有使用時，系統不應發生背景 reset、背景扣款或背景狀態改寫。

當使用者在某個瞬間重新開始使用時，如果既有 5h 或 7d 額度週期已經過期，新的週期應從當下開始，並分別延伸 5 小時與 7 天。

這個行為比較接近 lazy admission control 的 bucket/lease 模型：系統只在有 admission / consume 需求時檢查與更新 active quota window，而不是持續掃描過去 usage history。

## Decision

V1 採用 **lazy quota window lease**：

- 5h 與 7d 都有自己的 active lease。
- lease interval 為 `[opened_at, expires_at)`。
- 若長時間沒有使用，系統不需要背景 job reset 或寫入新 state。
- 下一次 `DecideAsync` / admission 或 `ConsumeAsync` 進入 quota 判定時，如果 active lease 不存在或已過期，才從該決策時間開啟新的 lease。
- `DecideAsync` / admission 可以開啟新的 lease，但不得產生 billing usage、window usage、extra pool consumption 或 reconciliation effect。
- `ConsumeAsync` 才把 actual credits 記錄到當時 active 5h / 7d lease。
- 若 actual credits 已發生且超過 remaining，超過部分由 system absorbed；該 absorbed credits 仍計入 active lease used，直到 lease 到期。

## Consequences

正面影響：

- `DecideAsync` 不需要每次 scan 過去 5 小時與 7 天 usage history。
- 剩餘額度可以由 `subscription_quota_window_state` 快速判定。
- window state 可以由目前 subscription admission state 維護，不必作為帳務 source of truth 重算。
- 長時間 idle 的 subscription 不需要背景維護成本。

代價：

- 這不是 per-usage rolling window；同一個 active lease 內較早的 usage 不會在 5 小時後單獨釋放。
- status 的 next reset time 來自 active lease expires time，不是由每筆 usage age 推算。
- operation 若跨越 lease expires time，settlement 應保留 admission 關聯，避免把已 admission 的工作錯掛到新的 lease；這點在 implementation spec 階段需要補交易與關聯細節。
