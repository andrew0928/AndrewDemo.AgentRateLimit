# 決策：extra pool 必須 explicit authorization 後才可消耗

- 決策時間：2026-07-01
- 狀態：accepted
- 範圍：`Subscription Credit Rate Limit V1` extra pool usage semantics

## Context

使用者修正 unknown final credits 的結算語意：如果某次 operation 在 admission 時尚未發現 5h / 7d allowance 不足，也沒有詢問使用者是否願意使用 extra pool，最後 actual credits 超過 subscription allowance 時，不應靜默扣 extra pool。

這類 overrun 已經發生成本，但使用者未同意使用 extra pool，因此超額部分應由系統端吸收。下一次新的 admission 才會看到 active 5h / 7d allowance 已不足，UI 才能提示使用者選擇：

- 使用 extra pool 繼續。
- 等到 5h / 7d window reset 後再繼續。

## Decision

V1 採用 explicit extra pool authorization：

- `DecideAsync` / admission 不得自動消耗 extra pool。
- `ConsumeAsync` 只有在 request 帶有本次 operation 的 extra pool authorization 時，才可把不足部分記為 `credits_covered_by_extra_pool`。
- 若 operation 只通過 `minimum-available-balance = 1` admission，且沒有取得 extra pool authorization，settlement overrun 必須記為 `credits_absorbed_by_system`。
- 若下一次 admission 發現 subscription allowance 不足但 extra pool 足夠，decision 應回 `extra-pool-authorization-required`，讓 UI prompt。
- 使用者同意後，caller 以 authorized request 重新 admission / consume，才可扣 extra pool。

## Consequences

正面影響：

- 不會在使用者未同意時消耗 extra pool。
- unknown final credits 的成本可以忠實記錄，同時保留使用者授權邊界。
- UI 可以在真正需要 extra pool 的下一次 operation 前提示清楚選項。

代價：

- `.Abstract` request 需要表達 extra pool authorization。
- decision/rejection reason 需要表達 `extra-pool-authorization-required`。
- settlement allocation 需要區分「未授權 overrun -> system absorbed」與「已授權不足 -> extra pool covered」。
