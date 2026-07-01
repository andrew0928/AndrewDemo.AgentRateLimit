# Subscription Credit Rate Limit V1 Coverage Decision Table

> 狀態：draft-for-review  
> 日期：2026-07-01  
> 範圍：外部行為覆蓋表。`Covered` 表示已有 `spec/testcases` 驗收案例；`Deferred` 表示第一版刻意不納入；`Open` 表示需要後續確認。

## Usage Decision Table

| Case | Valid credits | Subscription valid | Idempotency state | 5h/7d allowance | Extra pool | Extra authorization | Expected result | Covered by |
|---|---|---|---|---|---|---|---|---|
| 正整數且額度足夠 | yes | yes | new | enough | any | any | accepted, extra not used | TC-CREDIT-001, TC-WINDOW-001 |
| 小數 credit | no | yes | new | any | any | any | invalid | TC-CREDIT-002 |
| zero credit | no | yes | new | any | any | any | invalid | TC-CREDIT-003 |
| negative credit | no | yes | new | any | any | any | invalid | TC-CREDIT-004 |
| missing user id | no | unknown | new | any | any | any | invalid | TC-CREDIT-005 |
| missing subscription id | no | unknown | new | any | any | any | invalid | TC-CREDIT-006 |
| missing idempotency key | no | yes | missing | any | any | any | invalid | TC-CREDIT-007 |
| 5h 不足但 extra 足夠且已授權 | yes | yes | new | 5h short | enough | authorized | accepted, extra used | TC-WINDOW-002 |
| 7d 不足但 extra 足夠且已授權 | yes | yes | new | 7d short | enough | authorized | accepted, extra used | TC-WINDOW-003 |
| 5h/7d 都不足但 extra 足夠且已授權 | yes | yes | new | both short | enough | authorized | accepted, extra used | TC-WINDOW-004 |
| allowance 不足、extra 足夠但未授權 | yes | yes | new | short | enough | not authorized | rejected, extra-pool-authorization-required | TC-EXTRA-004 |
| subscription allowance + extra 仍不足 | yes | yes | new | short | not enough | authorized | rejected | TC-WINDOW-005 |
| 相同 idempotency key 與相同 payload | yes | yes | same payload | any | any | any | original decision | TC-IDEMP-001 |
| 相同 idempotency key 但不同 payload | yes | yes | different payload | any | any | any | conflict | TC-IDEMP-002 |
| user/subscription 不匹配 | yes | no | new | any | any | any | rejected | TC-ISOLATION-003 |
| subscription disabled | yes | disabled | new | any | any | any | rejected | TC-ISOLATION-005 |
| subscription not found | yes | missing | new | any | any | any | rejected | TC-ISOLATION-004 |

## Window And Time Table

| Case | Active lease state at decision time | Expected window behavior | Covered by |
|---|---|---|---|
| active 5h lease not expired | `T < 5h expires time` | consume usage is applied to current 5h lease | TC-WINDOW-006 |
| active 5h lease expired and no request arrives | `T >= 5h expires time`, no admission / consume | no background reset or new lease is required | TC-WINDOW-006 |
| active 5h lease expired and next admission arrives | `T >= 5h expires time`, admission accepted | new 5h lease opens from admission time | TC-WINDOW-006 |
| active 7d lease not expired | `T < 7d expires time` | consume usage is applied to current 7d lease | TC-WINDOW-007 |
| active 7d lease expired and no request arrives | `T >= 7d expires time`, no admission / consume | no background reset or new lease is required | TC-WINDOW-007 |
| active 7d lease expired and next admission arrives | `T >= 7d expires time`, admission accepted | new 7d lease opens from admission time | TC-WINDOW-007 |
| rejected usage | any | excluded from both windows | TC-WINDOW-005, TC-CONSISTENCY-003 |
| preview / admission usage | any | excluded from window usage, but may open a new active lease | TC-PREVIEW-001, TC-PREVIEW-002, TC-SETTLE-001 |

## Accounting Safety Table

| Case | Expected externally observable result | Covered by |
|---|---|---|
| Concurrent requests exceed available credits | accepted total never exceeds available credits | TC-CONSISTENCY-001 |
| Restart after accepted usage | usage and audit remain visible | TC-CONSISTENCY-002 |
| Restart after rejected usage | rejection audit remains visible, usage totals unchanged | TC-CONSISTENCY-003 |
| Manual correction | original record remains, correction appears as separate record | TC-AUDIT-003 |
| Reconciliation export | period totals reconstruct credit changes | TC-AUDIT-004 |

## Intentionally Deferred

| Topic | Reason |
|---|---|
| API route shape | 第一版只凍結外部行為，不凍結 HTTP 或 SDK surface |
| Database schema | 使用者明確要求本版不描述內部實作與設計 |
| Internal locking or transaction strategy | 本版只驗收外部一致性結果 |
| Payment, invoice, tax, refund | 不屬於 usage control 行為 |
| Automatic plan upgrade/downgrade | 需要額外 product/billing 規格 |
| Multi-region active-active consistency | 與低建置成本目標衝突，第一版不納入 |
