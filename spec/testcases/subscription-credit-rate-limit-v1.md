# Subscription Credit Rate Limit V1 驗收案例

> 狀態：draft-for-review  
> 日期：2026-07-01  
> 範圍：只驗收外部可觀測行為與結果，不驗收內部資料表、交易、鎖、索引、演算法或程式分層。

## Credit Validation

### TC-CREDIT-001 正整數 credit 可接受

- Given: user `user-a` 有 active subscription `sub-a`
- And: `sub-a` 的 5h remaining 為 100
- And: `sub-a` 的 7d remaining 為 1000
- When: consume usage request 的 requested credits 為 10
- Then: decision result 為 `accepted`
- And: 回傳的所有 credit 欄位都是整數

### TC-CREDIT-002 小數 credit 不合法

- Given: user `user-a` 有 active subscription `sub-a`
- When: consume usage request 的 requested credits 為 1.5
- Then: decision result 為 `invalid`
- And: invalid reason 為 `credits-not-integer`
- And: usage status 不變

### TC-CREDIT-003 zero credit 不合法

- Given: user `user-a` 有 active subscription `sub-a`
- When: consume usage request 的 requested credits 為 0
- Then: decision result 為 `invalid`
- And: invalid reason 為 `credits-not-positive`
- And: usage status 不變

### TC-CREDIT-004 negative credit 不合法

- Given: user `user-a` 有 active subscription `sub-a`
- When: consume usage request 的 requested credits 為 -1
- Then: decision result 為 `invalid`
- And: invalid reason 為 `credits-not-positive`
- And: usage status 不變

### TC-CREDIT-005 缺少 user id 不合法

- Given: usage request 缺少 user id
- When: consume usage request
- Then: decision result 為 `invalid`
- And: invalid reason 為 `missing-user-id`
- And: usage status 不變

### TC-CREDIT-006 缺少 subscription id 不合法

- Given: usage request 缺少 subscription id
- When: consume usage request
- Then: decision result 為 `invalid`
- And: invalid reason 為 `missing-subscription-id`
- And: usage status 不變

### TC-CREDIT-007 缺少 idempotency key 不合法

- Given: usage request 缺少 idempotency key
- When: consume usage request
- Then: decision result 為 `invalid`
- And: invalid reason 為 `missing-idempotency-key`
- And: usage status 不變

## Window Usage

### TC-WINDOW-001 5h 與 7d 額度都足夠時 accepted 且不消耗 extra pool

- Given: `sub-a` 的 5h remaining 為 100
- And: `sub-a` 的 7d remaining 為 500
- And: `sub-a` 的 extra pool remaining 為 50
- When: user `user-a` consume 20 credits
- Then: decision result 為 `accepted`
- And: credits covered by subscription window allowance 為 20
- And: credits covered by extra pool 為 0
- And: 5h remaining 變為 80
- And: 7d remaining 變為 480
- And: extra pool remaining 仍為 50

### TC-WINDOW-002 5h 額度不足時使用 extra pool 補足

- Given: `sub-a` 的 5h remaining 為 5
- And: `sub-a` 的 7d remaining 為 500
- And: `sub-a` 的 extra pool remaining 為 50
- And: 本次 operation 已取得使用 extra pool 的授權
- When: user `user-a` consume 20 credits
- Then: decision result 為 `accepted`
- And: credits covered by subscription window allowance 為 5
- And: credits covered by extra pool 為 15
- And: extra pool remaining 變為 35

### TC-WINDOW-003 7d 額度不足時使用 extra pool 補足

- Given: `sub-a` 的 5h remaining 為 100
- And: `sub-a` 的 7d remaining 為 8
- And: `sub-a` 的 extra pool remaining 為 50
- And: 本次 operation 已取得使用 extra pool 的授權
- When: user `user-a` consume 20 credits
- Then: decision result 為 `accepted`
- And: credits covered by subscription window allowance 為 8
- And: credits covered by extra pool 為 12
- And: extra pool remaining 變為 38

### TC-WINDOW-004 5h 與 7d 都不足時以較小 remaining 為 subscription allowance

- Given: `sub-a` 的 5h remaining 為 12
- And: `sub-a` 的 7d remaining 為 7
- And: `sub-a` 的 extra pool remaining 為 50
- And: 本次 operation 已取得使用 extra pool 的授權
- When: user `user-a` consume 20 credits
- Then: decision result 為 `accepted`
- And: credits covered by subscription window allowance 為 7
- And: credits covered by extra pool 為 13
- And: extra pool remaining 變為 37

### TC-WINDOW-005 subscription allowance 加 extra pool 仍不足時 rejected

- Given: `sub-a` 的 5h remaining 為 5
- And: `sub-a` 的 7d remaining 為 8
- And: `sub-a` 的 extra pool remaining 為 10
- And: 本次 operation 已取得使用 extra pool 的授權
- When: user `user-a` consume 20 credits
- Then: decision result 為 `rejected`
- And: rejection reason 為 `insufficient-credits`
- And: 5h remaining 仍為 5
- And: 7d remaining 仍為 8
- And: extra pool remaining 仍為 10

### TC-WINDOW-006 5h lazy window lease 到期後由下一次 admission 重開

- Given: `sub-a` 在 `2026-07-01T00:00:00Z` 通過 accepted admission 並開啟 5h window lease
- And: 5h window limit 為 100
- And: 5h window lease expires time 為 `2026-07-01T05:00:00Z`
- And: `sub-a` 在該 lease 內 accepted consume 30 credits
- When: 在 `2026-07-01T04:59:59Z` 查詢 usage status
- Then: 5h window used credits 包含該 30 credits
- When: 到達 `2026-07-01T05:00:00Z` 且沒有新的 admission / consume
- Then: 系統不需要背景 reset 或寫入新的 window state
- When: `sub-a` 在 `2026-07-01T05:10:00Z` 再次通過 accepted admission
- Then: 新的 5h window lease 從 `2026-07-01T05:10:00Z` 開始
- And: 新的 5h window lease expires time 為 `2026-07-01T10:10:00Z`
- And: 新的 5h window used credits 在本次 admission 後仍為 0

### TC-WINDOW-007 7d lazy window lease 到期後由下一次 admission 重開

- Given: `sub-a` 在 `2026-07-01T00:00:00Z` 通過 accepted admission 並開啟 7d window lease
- And: 7d window limit 為 1000
- And: 7d window lease expires time 為 `2026-07-08T00:00:00Z`
- And: `sub-a` 在該 lease 內 accepted consume 30 credits
- When: 在 `2026-07-07T23:59:59Z` 查詢 usage status
- Then: 7d window used credits 包含該 30 credits
- When: 到達 `2026-07-08T00:00:00Z` 且沒有新的 admission / consume
- Then: 系統不需要背景 reset 或寫入新的 window state
- When: `sub-a` 在 `2026-07-08T08:00:00Z` 再次通過 accepted admission
- Then: 新的 7d window lease 從 `2026-07-08T08:00:00Z` 開始
- And: 新的 7d window lease expires time 為 `2026-07-15T08:00:00Z`
- And: 新的 7d window used credits 在本次 admission 後仍為 0

## Extra Pool

### TC-EXTRA-001 extra pool 不因 window lease 到期或重開自動恢復

- Given: `sub-a` 的 extra pool remaining 原本為 50
- And: user `user-a` 在 5h 額度不足時授權使用 extra pool，accepted usage 並消耗 15 extra credits
- When: 5h window lease 到期後，下一次 admission 重開 5h window lease
- Then: extra pool remaining 仍為 35

### TC-EXTRA-002 extra pool 剛好足夠時 accepted 並歸零

- Given: `sub-a` 的 5h remaining 為 0
- And: `sub-a` 的 7d remaining 為 0
- And: `sub-a` 的 extra pool remaining 為 20
- And: 本次 operation 已取得使用 extra pool 的授權
- When: user `user-a` consume 20 credits
- Then: decision result 為 `accepted`
- And: credits covered by extra pool 為 20
- And: extra pool remaining 變為 0

### TC-EXTRA-004 extra pool 足夠但未授權時要求 UI 確認

- Given: `sub-a` 的 5h remaining 為 0
- And: `sub-a` 的 7d remaining 為 500
- And: `sub-a` 的 extra pool remaining 為 50
- And: 本次 operation 尚未取得使用 extra pool 的授權
- When: user `user-a` request admission for 1 credit
- Then: decision result 為 `rejected`
- And: rejection reason 為 `extra-pool-authorization-required`
- And: 不可消耗 extra pool
- And: 不可建立 consume record

### TC-EXTRA-003 extra pool 調整必須出現在 audit trail

- Given: `sub-a` 的 extra pool remaining 為 0
- When: 授權 actor 增加 100 extra credits 並提供 reason `manual-top-up`
- Then: usage status 顯示 extra pool remaining 為 100
- And: audit trail 包含一筆 extra pool change
- And: audit record 包含 actor、reason、changed credits 與 resulting extra pool remaining

## Preview

### TC-PREVIEW-001 preview accepted 不改變 usage status

- Given: `sub-a` 的 5h remaining 為 100
- And: `sub-a` 的 7d remaining 為 1000
- When: preview usage request 的 requested credits 為 20
- Then: decision result 為 `accepted`
- And: preview 回傳 5h remaining after decision 為 80
- And: preview 回傳 7d remaining after decision 為 980
- When: 再次查詢 usage status
- Then: 實際 5h remaining 仍為 100
- And: 實際 7d remaining 仍為 1000

### TC-PREVIEW-002 preview rejected 不產生帳務扣款

- Given: `sub-a` 的 5h remaining 為 0
- And: `sub-a` 的 7d remaining 為 0
- And: `sub-a` 的 extra pool remaining 為 0
- When: preview usage request 的 requested credits 為 1
- Then: decision result 為 `rejected`
- And: rejection reason 為 `insufficient-credits`
- And: usage status 不變

## Unknown Final Credits And Settlement

> 本節是新增用途的 draft testcase：執行當下尚無法確認最終 requested credits，只能先確認 subscription 還有可用額度。實際費用在執行完成後才知道，且已發生成本時不可回頭 reject。

### TC-SETTLE-001 unknown final credits 先用 minimum balance 判定

- Given: `sub-a` 的 5h remaining 為 100
- And: `sub-a` 的 7d remaining 為 500
- And: 本次 operation 尚未取得使用 extra pool 的授權
- When: usage request 的 credit amount mode 為 `minimum-available-balance`
- And: minimum required credits 為 1
- Then: decision result 為 `accepted`
- And: 不可消耗 5h window、7d window 或 extra pool
- And: 回傳結果必須能讓 caller 知道這不是 exact requested credits 的扣款結果

### TC-SETTLE-002 final credits 超過剩餘額度時仍忠實記錄並由系統吸收

- Given: `sub-a` 的 5h remaining 為 100
- And: `sub-a` 的 7d remaining 為 500
- And: `sub-a` 的 extra pool remaining 為 1000
- And: caller 先前沒有取得使用 extra pool 的授權
- And: caller 已通過 minimum balance 判定並完成實際工作
- When: usage settlement 的 actual credits 為 120
- Then: decision result 為 `accepted`
- And: credits covered by subscription window allowance 為 100
- And: credits covered by extra pool 為 0
- And: credits absorbed by system 為 20
- And: extra pool remaining 仍為 1000
- And: 5h remaining 變為 0
- And: 7d remaining 變為 380
- And: audit trail 必須忠實記錄 actual credits、covered credits 與 system absorbed credits
- And: 系統吸收的 20 credits 不因本次 request 立即恢復，必須等 active window lease 到期後由下一次 admission 重開

## Idempotency

### TC-IDEMP-001 相同 idempotency key 與相同 payload 重送不重複扣款

- Given: user `user-a` consume 20 credits with idempotency key `k-001`
- And: first decision result 為 `accepted`
- When: 使用相同 idempotency key `k-001` 與相同 request payload 重送
- Then: 回傳 first decision
- And: usage status 只反映一次 20 credits consumption
- And: extra pool 不因重送再次消耗

### TC-IDEMP-002 相同 idempotency key 但不同 payload 回傳 conflict

- Given: user `user-a` consume 20 credits with idempotency key `k-002`
- And: first decision result 為 `accepted`
- When: 使用 idempotency key `k-002` 但 requested credits 改為 25 重送
- Then: decision result 為 `conflict`
- And: conflict reason 為 `idempotency-key-payload-mismatch`
- And: usage status 不因 conflict 改變
- And: audit trail 包含 conflict record

## Multi User And Subscription Isolation

### TC-ISOLATION-001 user A 用量不影響 user B

- Given: user `user-a` 的 subscription `sub-a` 5h remaining 為 100
- And: user `user-b` 的 subscription `sub-b` 5h remaining 為 100
- When: user `user-a` consume 80 credits
- Then: `sub-a` 的 5h remaining 變為 20
- And: `sub-b` 的 5h remaining 仍為 100

### TC-ISOLATION-002 同一 user 的不同 subscription 彼此隔離

- Given: user `user-a` 有 subscription `sub-a1` 與 `sub-a2`
- And: `sub-a1` 的 7d remaining 為 500
- And: `sub-a2` 的 7d remaining 為 500
- When: user `user-a` 在 `sub-a1` consume 100 credits
- Then: `sub-a1` 的 7d remaining 變為 400
- And: `sub-a2` 的 7d remaining 仍為 500

### TC-ISOLATION-003 user 與 subscription 不匹配時 rejected

- Given: subscription `sub-b` 屬於 user `user-b`
- When: user `user-a` 對 `sub-b` consume 10 credits
- Then: decision result 為 `rejected`
- And: rejection reason 為 `user-subscription-mismatch`
- And: `sub-b` usage status 不變

### TC-ISOLATION-004 subscription 不存在時 rejected

- Given: user `user-a` 不存在 subscription `sub-missing`
- When: user `user-a` 對 `sub-missing` consume 10 credits
- Then: decision result 為 `rejected`
- And: rejection reason 為 `subscription-not-found`
- And: 其他 subscription usage status 不變

### TC-ISOLATION-005 subscription 停用時 rejected

- Given: user `user-a` 有 disabled subscription `sub-disabled`
- When: user `user-a` 對 `sub-disabled` consume 10 credits
- Then: decision result 為 `rejected`
- And: rejection reason 為 `subscription-disabled`
- And: `sub-disabled` usage status 不因本次 request 增加 used credits

## Consistency And Persistence

### TC-CONSISTENCY-001 同時請求不可造成超額 accepted

- Given: `sub-a` 的 5h remaining 為 10
- And: `sub-a` 的 7d remaining 為 10
- And: `sub-a` 的 extra pool remaining 為 0
- When: 兩筆不同 idempotency key 的 10-credit consume usage request 同時送入
- Then: 最多只有一筆 decision result 為 `accepted`
- And: accepted credits total 不超過 10
- And: 另一筆 decision result 為 `rejected`

### TC-CONSISTENCY-002 重啟後 accepted usage 仍可查詢

- Given: user `user-a` consume 20 credits 且 decision result 為 `accepted`
- When: 服務使用同一 database persistence 重啟
- And: 查詢 `sub-a` usage status
- Then: 5h 與 7d window usage 仍包含該 20 credits，直到 active window lease 到期
- And: audit trail 仍可查到該 accepted usage

### TC-CONSISTENCY-003 重啟後 rejected usage 仍可回溯

- Given: user `user-a` consume 20 credits 且 decision result 為 `rejected`
- When: 服務使用同一 database persistence 重啟
- And: 查詢 `sub-a` audit trail
- Then: audit trail 仍可查到該 rejected usage
- And: usage status 沒有把該 rejected request 計入 used credits

## Audit And Reconciliation

### TC-AUDIT-001 accepted usage 有完整 audit record

- Given: user `user-a` consume 20 credits
- When: decision result 為 `accepted`
- Then: audit trail 包含該 usage
- And: audit record 包含 user id、subscription id、requested credits、covered by subscription allowance、covered by extra pool、decision time、correlation id、idempotency key、decision result

### TC-AUDIT-002 rejected usage 有完整 audit record

- Given: user `user-a` consume 20 credits
- When: decision result 為 `rejected`
- Then: audit trail 包含該 rejected usage
- And: audit record 包含 rejection reason
- And: reconciliation report 不把該 request 計入 accepted credits

### TC-AUDIT-003 帳務修正不可覆蓋原始紀錄

- Given: audit trail 已有一筆 accepted usage
- When: 授權 actor 建立 manual correction
- Then: audit trail 同時保留原始 accepted usage 與 correction record
- And: reconciliation report 能分別列出原始 usage 與 correction

### TC-AUDIT-004 reconciliation report 可重建期間 credit 變化

- Given: 指定期間內有 accepted usage、rejected usage、extra pool top-up、extra pool consumption、manual correction
- When: 匯出 reconciliation report
- Then: report 顯示 accepted credits total
- And: report 顯示 rejected credits total
- And: report 顯示 subscription allowance covered credits total
- And: report 顯示 extra pool beginning balance、added credits、consumed credits、adjusted credits、ending balance
- And: report 顯示 conflict、invalid request 與 manual correction counts

## Status Output

### TC-STATUS-001 usage status 顯示 5h 與 7d window 狀態

- Given: `sub-a` 有 accepted usage
- When: 查詢 usage status
- Then: response 包含 5h window limit、used、remaining
- And: response 包含 7d window limit、used、remaining
- And: response 包含 extra pool remaining

### TC-STATUS-002 usage status 顯示 active lease next reset time

- Given: `sub-a` 有 active 5h 與 7d window lease
- When: 查詢 usage status
- Then: response 包含 5h next reset time，值為 active 5h lease expires time
- And: response 包含 7d next reset time，值為 active 7d lease expires time
