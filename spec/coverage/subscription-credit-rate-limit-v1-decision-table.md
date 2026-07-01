# Subscription Credit Rate Limit V1 Coverage Decision Table

> 狀態：draft-for-review  
> 日期：2026-07-02
> 範圍：以一個 run 的完整過程整理 decision space。`Covered` 表示已有 `spec/testcases` 驗收案例；`Deferred` 表示第一版刻意不納入；`Open` 表示需要後續確認。

## Run Scope

一個 run 指：

```text
Detect / admission
  -> optional UI prompt for extra pool
  -> Consume / settlement
```

符號：

- `D`：detect/admission 時用來判定的 credits。unknown final credits 時通常是 `1`。
- `C`：consume/settlement 時的 actual credits。
- `R5`：lazy renew 後的 5h remaining。
- `R7`：lazy renew 後的 7d remaining。
- `A(x)`：subscription allowance 可覆蓋的 credits，`min(R5, R7, x)`。
- `shortage(x)`：`max(0, x - min(R5, R7))`。
- `5h quota available`：`R5 >= D`。
- `7d quota available`：`R7 >= D`。
- `5h limit enough`：`R5 >= C`。
- `7d limit enough`：`R7 >= C`。
- `extra pool available`：extra pool remaining 足以覆蓋 `shortage(...)`。

## Additional Result Conditions

使用者列出的條件之外，還有這些會影響結果：

| Condition | Why it matters |
|---|---|
| request validity | credits 非正整數、缺 user/subscription/idempotency key 時，quota 判定前就 invalid |
| subscription validity | subscription missing、disabled、user/subscription mismatch 時，不進入 quota consumption |
| idempotency state | same payload 要 replay 原 decision；different payload 要 conflict |
| detect credits `D` vs actual credits `C` | unknown final credits 可能 `D = 1` 但 `C > R5/R7`，settlement overrun 不可回頭 reject |
| cost already happened | 成本已發生時必須忠實記錄 consume；未授權 overrun 進 system absorbed |
| extra pool authorization | extra pool 足夠但未授權時，不可靜默消耗，必須回 `extra-pool-authorization-required` |
| concurrency serialization | 同一 subscription 的多個 run 必須等價於某個明確順序 |

## Gate Decision Table

這張表先排除不進入 5h / 7d quota 判定的 case。

| Case | Request valid | Subscription valid | Idempotency state | Expected result | Covered by |
|---|---|---|---|---|---|
| valid new run | yes | yes | new | enter run decision tables below | TC-CREDIT-001 |
| fractional credits | no | yes | new | invalid, `credits-not-integer` | TC-CREDIT-002 |
| zero credits | no | yes | new | invalid, `credits-not-positive` | TC-CREDIT-003 |
| negative credits | no | yes | new | invalid, `credits-not-positive` | TC-CREDIT-004 |
| missing user id | no | unknown | new | invalid, `missing-user-id` | TC-CREDIT-005 |
| missing subscription id | no | unknown | new | invalid, `missing-subscription-id` | TC-CREDIT-006 |
| missing idempotency key | no | yes | missing | invalid, `missing-idempotency-key` | TC-CREDIT-007 |
| same idempotency key and same payload | yes | yes | same payload | replay original decision | TC-IDEMP-001 |
| same idempotency key but different payload | yes | yes | different payload | conflict | TC-IDEMP-002 |
| subscription not found | yes | missing | new | rejected, `subscription-not-found` | TC-ISOLATION-004 |
| subscription disabled | yes | disabled | new | rejected, `subscription-disabled` | TC-ISOLATION-005 |
| user/subscription mismatch | yes | mismatch | new | rejected, `user-subscription-mismatch` | TC-ISOLATION-003 |

## Window Renewal Table

Window expiry 只決定 admission state 是否 lazy renew；它不是帳務 source of truth。

| Case | 5h expired before detect | 7d expired before detect | State action before quota check | Covered by |
|---|---|---|---|---|
| neither expired | no | no | keep current 5h and 7d state | TC-WINDOW-001..005 |
| only 5h expired | yes | no | renew 5h from detect time; keep 7d state | TC-WINDOW-006 |
| only 7d expired | no | yes | keep 5h state; renew 7d from detect time | TC-WINDOW-007 |
| both expired | yes | yes | renew both 5h and 7d from detect time | TC-RUN-004 |
| expired while idle, no run arrives | yes | any | no background reset required | TC-WINDOW-006, TC-WINDOW-007 |

## Detect / Admission Decision Table

這張表決定 run 能不能開始。對 unknown final credits，`D = 1`。對 caller 已知 requested credits 的 preview/admission，`D = requested credits`。

| Case | 5h quota available for `D` | 7d quota available for `D` | Extra pool available for `shortage(D)` | Extra authorization | Detect result | Next step | Covered by |
|---|---|---|---|---|---|---|---|
| D1 allowance enough | yes | yes | any | any | accepted, no extra pool needed | run may start | TC-WINDOW-001, TC-SETTLE-001 |
| D2 5h short, no extra | no | yes | no | any | rejected, `insufficient-credits` | do not start run | TC-WINDOW-005 |
| D3 7d short, no extra | yes | no | no | any | rejected, `insufficient-credits` | do not start run | TC-WINDOW-005 |
| D4 both short, no extra | no | no | no | any | rejected, `insufficient-credits` | do not start run | TC-WINDOW-005 |
| D5 5h short, extra enough, not authorized | no | yes | yes | no | rejected, `extra-pool-authorization-required` | UI asks use extra pool or wait reset | TC-EXTRA-004 |
| D6 7d short, extra enough, not authorized | yes | no | yes | no | rejected, `extra-pool-authorization-required` | UI asks use extra pool or wait reset | TC-RUN-008 |
| D7 both short, extra enough, not authorized | no | no | yes | no | rejected, `extra-pool-authorization-required` | UI asks use extra pool or wait reset | TC-RUN-007 |
| D8 5h short, extra enough, authorized | no | yes | yes | yes | accepted, extra pool authorized | run may start | TC-WINDOW-002 |
| D9 7d short, extra enough, authorized | yes | no | yes | yes | accepted, extra pool authorized | run may start | TC-WINDOW-003 |
| D10 both short, extra enough, authorized | no | no | yes | yes | accepted, extra pool authorized | run may start | TC-WINDOW-004 |

## Consume / Settlement Decision Table

這張表只適用於 run 已經開始後的 settlement。若 actual credits `C` 是工作完成後才知道，成本已發生，因此 settlement 必須忠實記錄 consume。

| Case | Detect mode | 5h limit enough for `C` | 7d limit enough for `C` | Extra authorization | Extra pool available for `shortage(C)` | Consume result | Allocation | Covered by |
|---|---|---|---|---|---|---|---|---|
| S1 actual within both limits | any accepted detect | yes | yes | any | any | accepted | allowance covers `C`; extra 0; absorbed 0 | TC-WINDOW-001 |
| S2 unknown final credits overrun, not authorized | minimum probe | no | yes | no | any | accepted | allowance covers `A(C)`; extra 0; system absorbs shortage | TC-SETTLE-002 |
| S3 unknown final credits overrun, not authorized | minimum probe | yes | no | no | any | accepted | allowance covers `A(C)`; extra 0; system absorbs shortage | TC-RUN-012 |
| S4 unknown final credits overrun both windows, not authorized | minimum probe | no | no | no | any | accepted | allowance covers `A(C)`; extra 0; system absorbs shortage | TC-RUN-013 |
| S5 authorized 5h short, extra enough | authorized detect | no | yes | yes | yes | accepted | allowance covers `A(C)`; extra covers shortage; absorbed 0 | TC-WINDOW-002 |
| S6 authorized 7d short, extra enough | authorized detect | yes | no | yes | yes | accepted | allowance covers `A(C)`; extra covers shortage; absorbed 0 | TC-WINDOW-003 |
| S7 authorized both short, extra enough | authorized detect | no | no | yes | yes | accepted | allowance covers `A(C)`; extra covers shortage; absorbed 0 | TC-WINDOW-004, TC-EXTRA-002 |
| S8 authorized but actual exceeds allowance + extra | authorized detect | no | any | yes | no | accepted if cost already happened | allowance covers `A(C)`; extra covers available extra; system absorbs remaining | TC-RUN-016 |
| S9 exact requested known and allowance + authorized extra insufficient before work | exact detect | no | any | yes | no | rejected before consume | no consume record | TC-WINDOW-005 |

## End-To-End Run Outcome Table

這張表把 detect 和 consume 合併成 reviewer 可掃描的 run 結果。

| Run | 5h expired | 7d expired | 5h quota available for `D` | 7d quota available for `D` | 5h limit enough for `C` | 7d limit enough for `C` | Extra pool available | Extra authorization | Final observable result | Covered by |
|---|---|---|---|---|---|---|---|---|---|---|
| R1 | any | any | yes | yes | yes | yes | any | any | accepted; allowance covers all actual credits | TC-RUN-001 |
| R2 | yes | any | yes after renew | yes | yes | yes | any | any | accepted after 5h lazy renew | TC-RUN-002 |
| R3 | any | yes | yes | yes after renew | yes | yes | any | any | accepted after 7d lazy renew | TC-RUN-003 |
| R4 | yes | yes | yes after renew | yes after renew | yes | yes | any | any | accepted after both windows lazy renew | TC-RUN-004 |
| R5 | no | any | no | yes | n/a | n/a | no | any | rejected, `insufficient-credits`; no consume | TC-RUN-005 |
| R6 | any | no | yes | no | n/a | n/a | no | any | rejected, `insufficient-credits`; no consume | TC-RUN-006 |
| R7 | any | any | no | any | n/a | n/a | yes | no | rejected, `extra-pool-authorization-required`; UI prompt | TC-RUN-007 |
| R8 | any | any | any | no | n/a | n/a | yes | no | rejected, `extra-pool-authorization-required`; UI prompt | TC-RUN-008 |
| R9 | any | any | no | any | no | any | yes | yes | accepted with extra pool if authorized before run | TC-RUN-009 |
| R10 | any | any | any | no | any | no | yes | yes | accepted with extra pool if authorized before run | TC-RUN-010 |
| R11 | any | any | yes | yes | no | yes | any | no | accepted settlement; overrun system absorbed; no extra pool consumed | TC-RUN-011 |
| R12 | any | any | yes | yes | yes | no | any | no | accepted settlement; overrun system absorbed; no extra pool consumed | TC-RUN-012 |
| R13 | any | any | yes | yes | no | no | any | no | accepted settlement; overrun system absorbed; no extra pool consumed | TC-RUN-013 |
| R14 | any | any | yes | yes | no | any | yes | yes | accepted settlement; extra pool covers authorized shortage | TC-RUN-014 |
| R15 | any | any | yes | yes | any | no | yes | yes | accepted settlement; extra pool covers authorized shortage | TC-RUN-015 |
| R16 | any | any | yes | yes | no | any | no | yes | accepted if cost already happened; extra covers 0; system absorbs shortage | TC-RUN-016 |

## Accounting Safety Table

| Case | Expected externally observable result | Covered by |
|---|---|---|
| Concurrent requests exceed available admission credits | accepted admission total never exceeds available allowance + authorized extra pool | TC-CONSISTENCY-001 |
| Unknown actual credits exceed admitted allowance | consume record persists actual credits and system absorbed overrun | TC-SETTLE-002 |
| Restart after accepted usage | usage and audit remain visible | TC-CONSISTENCY-002 |
| Restart after rejected usage | rejection audit remains visible, usage totals unchanged | TC-CONSISTENCY-003 |
| Manual correction | original record remains, correction appears as separate record | TC-AUDIT-003 |
| Reconciliation export | period totals reconstruct consume and extra pool changes | TC-AUDIT-004 |

## Intentionally Deferred

| Topic | Reason |
|---|---|
| API route shape | 第一版只凍結外部行為，不凍結 HTTP 或 SDK surface |
| Internal locking or transaction strategy | 本版只驗收外部一致性結果 |
| Payment, invoice, tax, refund | 不屬於 usage control 行為 |
| Automatic plan upgrade/downgrade | 需要額外 product/billing 規格 |
| Multi-region active-active consistency | 與低建置成本目標衝突，第一版不納入 |
