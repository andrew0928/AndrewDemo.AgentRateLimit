# Subscription Credit Minimal Schema Design

> 狀態：draft-for-review  
> 日期：2026-07-01  
> 範圍：`Subscription Credit Rate Limit V1` 的最小 database schema。本文只描述能正常支援 `DecideAsync` / `ConsumeAsync` 的必要 table、責任與資料流；column type、relation、constraint、index、transaction/locking 細節晚一階段再補。

## 1. Design Intent

這版 schema 以使用者重新釐清的優先順序為準：

1. 真正重要的 source of truth 是 **實際 consume 記錄**。這是內部成本、infra log reconciliation、拆帳與分潤的基礎。
2. 5h / 7d time window 只是 admission control 手段，用來避免短時間大量使用；不需要作為可重算帳務事實保存。
3. 若未來要 reconciliation，核心問題是「真實 infra log 裡發生的 consume 是否都有對應 consume record」，不是「能否重算過去某一刻的 window state」。

穩定 schema 句子：

> Persist every actual consume as an immutable consume record; keep quota windows only as mutable admission state.

因此本版不再把 quota window lease、quota window usage、credit movement、audit projection 視為必要表格。

## 2. Minimum Table Count

能正常運作的最小表格是 **3 張**：

| Table | Kind | Required | Responsibility |
|---|---|---:|---|
| `subscription_account` | mutable operational state | yes | 保存 subscription、目前 5h / 7d admission state、extra pool current balance |
| `subscription_consume_record` | append-only source of truth | yes | 保存每一筆實際發生成本的 consume、infra reference、credit coverage 與 cost allocation snapshot |
| `subscription_extra_pool_record` | append-only source of truth | yes | 保存 extra pool top-up、grant、manual adjustment、correction 等供給與調整事實 |

可選表格：

| Table | Kind | When needed |
|---|---|---|
| `consume_reconciliation_run` | report metadata | 只有當 reconciliation report 本身需要可追溯版本與輸出紀錄時才需要 |

extra pool 是第一版必要要求，因此本服務必須保留 `subscription_extra_pool_record`。extra pool 被 consume 掉的事實不另外拆 movement table，直接保存在 `subscription_consume_record.credits_covered_by_extra_pool`。

## 3. Required Tables

### 3.1 `subscription_account`

mutable table：保存目前可用的 subscription state。

核心內容：

- subscription identity
- user identity
- status：active / disabled
- current 5h limit
- current 7d limit
- active 5h window opened time
- active 5h window expires time
- active 5h used credits
- active 7d window opened time
- active 7d window expires time
- active 7d used credits
- extra pool remaining credits
- state version / updated time

責任：

- `DecideAsync` 的 fast path。
- lazy quota window renewal：若 active window 過期，直接在這張表更新新的 opened / expires / used state。
- `ConsumeAsync` 結算後更新 active window used credits 與 extra pool remaining。
- 判斷 subscription 是否存在、是否屬於 user、是否可用。
- 作為 extra pool current balance 的 fast path；可由 `subscription_extra_pool_record` 與 `subscription_consume_record` 重算。

不負責：

- 不保存歷史 window lease。
- 不作為 consume 帳務真相。
- 不需要支援重算過去任一時間點的 window state。

重要語意：

- time window state 可以被更新、覆蓋、lazy reset。
- 如果 state 損壞，最壞情況是 admission control 暫時不準；不應影響已發生成本的 consume truth。
- plan / limit 若變更，只更新目前欄位；歷史 consume 要靠 `subscription_consume_record` 內的 snapshot 解釋。

### 3.2 `subscription_consume_record`

append-only table：本 schema 唯一真正 source of truth。

每一筆實際已發生成本的 consume 都必須有一列。這張表的設計目標是能和 infra log 對帳：

```text
infra log says request X consumed cost/credits
=> subscription_consume_record must contain matching consume record
```

核心內容：

- consume identity
- subscription identity
- user identity
- idempotency key
- request fingerprint
- correlation id
- source
- provider / model / infra resource identity
- provider request id / response id / trace id
- infra log reference
- admitted time，若有 admission probe
- started time，若有
- consumed time
- recorded time
- actual credits
- credits covered by subscription allowance
- credits covered by extra pool
- credits absorbed by system
- extra pool authorization：not-authorized / authorized
- extra pool authorization reference，若有 UI prompt 或 user consent
- 5h limit snapshot
- 7d limit snapshot
- 5h window opened / expires snapshot
- 5h used before / after snapshot
- 7d window opened / expires snapshot
- 7d used before / after snapshot
- extra pool before / after snapshot
- cost allocation / revenue split snapshot
- correction reference，若這筆是修正紀錄

責任：

- 保存實際 consume 的不可變事實。
- 支援 infra log reconciliation：找出 infra 有但本表沒有的 consume。
- 支援成本、拆帳、分潤與 system absorbed cost 報表。
- 支援 accepted consume idempotency：同一 subscription + idempotency key 不可二次建立不同 consume。
- 支援修正：若記錄錯誤，用新的 correction record 表達，不覆蓋原始 record。

不負責：

- 不重建 time window。
- 不保存 rejected / invalid / conflict 的完整 decision audit，除非那些事件本身已造成實際成本。
- 不拆成多張 movement table；第一版把本次 allocation snapshot 放在同一列。

重要語意：

- 這張表必須 append-only。
- 不允許 update actual credits、coverage、infra reference、cost allocation 欄位。
- 若需要修正，只能 append correction record。
- transaction 設計必須把「insert consume record」視為 settlement 的核心動作；如果 infra cost 已發生但 insert 失敗，必須靠 retry / reconciliation 補回。

### 3.3 `subscription_extra_pool_record`

append-only table：extra pool 供給與調整的 source of truth。

核心內容：

- record identity
- subscription identity
- user identity
- record kind：top-up / grant / manual-adjustment / correction
- credits delta
- reason
- actor / source
- occurred time
- recorded time
- correlation id
- external reference，若來自 billing / admin / promotion system

責任：

- 保存 extra pool 的增加、調整與修正。
- 支援重算 extra pool balance。
- 支援 reconciliation report 顯示 extra pool beginning / added / adjusted / ending。

不負責：

- 不保存 extra pool consumption；consume 發生時寫在 `subscription_consume_record.credits_covered_by_extra_pool`。
- 不保存 time window 狀態。

## 4. Fast Path

### DecideAsync

`DecideAsync` 只需要讀寫 `subscription_account`：

```text
load subscription_account
if 5h window missing or now >= 5h_expires_at:
    set 5h_opened_at = now
    set 5h_expires_at = now + 5h
    set 5h_used_credits = 0
if 7d window missing or now >= 7d_expires_at:
    set 7d_opened_at = now
    set 7d_expires_at = now + 7d
    set 7d_used_credits = 0
if min(5h_remaining, 7d_remaining) >= minimum_required_credits:
    accept without extra pool
else if extra pool can cover the shortage and request has extra pool authorization:
    accept with extra pool
else if extra pool can cover the shortage:
    reject with extra-pool-authorization-required so UI can ask the user
else:
    reject with insufficient-credits
```

`DecideAsync` 不新增 consume record，因為沒有實際成本發生。

### ConsumeAsync

`ConsumeAsync` 在同一個 consistency boundary 內處理兩件事：

```text
load and lock subscription_account
lazy renew expired window state if needed
calculate allowance coverage / extra pool coverage / system absorbed credits
insert subscription_consume_record
update subscription_account current window used and extra pool remaining
commit
```

若 actual credits 超過目前 remaining：

```text
subscription allowance covered = current allowance remaining
extra pool covered = available extra pool coverage only if the request has prior extra pool authorization
system absorbed = actual credits - allowance covered - extra pool covered
```

若 caller 先前只做 `minimum-available-balance = 1` admission，且當時沒有要求或取得 extra pool authorization，settlement overrun 不得靜默消耗 extra pool。這類 overrun 必須記為 system absorbed。下一次新的 admission 發現 5h / 7d allowance 不足時，UI 才應提示使用者選擇使用 extra pool 或等 window reset。

system absorbed credits 仍然會反映在 `subscription_account` 的 current window used state，直到 window 過期後 lazy reset。這是 control behavior，不是帳務 source of truth。

## 5. Idempotency

最小 schema 不需要獨立 `usage_idempotency_record`。

accepted consume idempotency 直接由 `subscription_consume_record` 支援：

- unique scope：subscription identity + idempotency key
- same fingerprint replay：回傳既有 consume record
- different fingerprint replay：回傳 conflict，不新增 consume record

代價：

- 如果第一筆 request 是 rejected / invalid / conflict，最小 schema 不保證重送時回傳完全相同的舊 decision。
- 若未來要求所有 decision result 都有嚴格 idempotency replay，再新增 `usage_decision_record` 或 `usage_idempotency_record`。

這個取捨符合目前優先順序：保護實際 consume，不把未發生成本的 control decision 擴張成帳務 source of truth。

## 6. Removed Tables From Earlier Draft

| Removed table | Replacement |
|---|---|
| `subscription_credit_policy_snapshot` | current limits 放在 `subscription_account`；consume 當下的 limit snapshot 寫入 `subscription_consume_record` |
| `subscription_quota_window_lease` | window history 不保存；current window state 放在 `subscription_account` |
| `subscription_quota_window_state` | 合併進 `subscription_account` |
| `usage_decision` | accepted consume 合併為 `subscription_consume_record`；未發生成本的 decision 不是第一版 source of truth |
| `quota_window_usage` | 不保存 window usage source facts |
| `usage_credit_movement` | allocation snapshot 合併進 `subscription_consume_record` |
| `extra_pool_movement` | extra pool 供給與調整改由 `subscription_extra_pool_record`；consume 用量合併進 `subscription_consume_record` |
| `usage_idempotency_record` | accepted consume idempotency 由 `subscription_consume_record` unique key 支援 |
| `usage_audit_entry` | consume audit 直接讀 `subscription_consume_record` |
| `subscription_usage_projection` | status 直接讀 `subscription_account` |
| `reconciliation_run` | 第一版不需要；若報表輸出本身要追蹤再新增 optional table |

## 7. Worked Example

假設：

- subscription：`sub-a`
- 5h limit：100
- 7d limit：1000
- extra pool：初始 top-up 1000
- 每次工作前都用 `DecideAsync(minimum-available-balance = 1)` 做 admission。
- 工作完成後才知道 actual credits，並用 `ConsumeAsync(exact-credits)` 忠實結算。
- 只有當 admission 發現 subscription allowance 不足且 UI 取得使用者同意時，request 才帶 extra pool authorization。

### 7.1 Timeline

| Time | Operation | Result |
|---|---|---|
| 2026-07-01 23:01:23 | first `DecideAsync(1)` | updates `subscription_account`: 5h window `23:01:23 -> 04:01:23`, 7d window `07/01 23:01:23 -> 07/08 23:01:23`; accepted |
| 2026-07-01 23:10:00 | `DecideAsync(1)`, then `ConsumeAsync(30)` | appends consume record for 30; updates 5h used 30 / remaining 70, 7d used 30 / remaining 970 |
| 2026-07-01 23:30:00 | `DecideAsync(1)`, then `ConsumeAsync(80)` | admission accepted because 5h still has 70; settlement overrun 10 was not pre-authorized for extra pool, so system absorbs 10; 5h used becomes 110 / remaining 0 |
| 2026-07-01 23:45:00 | next `DecideAsync(1)` without extra pool authorization | rejected with `extra-pool-authorization-required`; UI asks whether to use extra pool or wait until 5h reset |
| 2026-07-01 23:45:00 | user agrees, then `DecideAsync(1)` / `ConsumeAsync(20)` with extra pool authorization | accepted; appends consume record for 20; extra pool covers 20 |
| 2026-07-02 08:00:00 | next `DecideAsync(1)`, then `ConsumeAsync(50)` | 5h window expired while idle, so `subscription_account` opens new 5h window `08:00 -> 13:00`; settlement appends consume record for 50 |

### 7.2 `subscription_extra_pool_record`

| record id | subscription id | occurred at | kind | credits delta |
|---|---|---|---|---:|
| `extra-001` | `sub-a` | before first use | top-up | 1000 |

### 7.3 `subscription_consume_record`

| consume id | consumed at | actual credits | allowance covered | extra pool covered | system absorbed | extra pool authorization | infra log reference |
|---|---|---:|---:|---:|---:|---|---|
| `consume-231000` | 2026-07-01 23:10:00 | 30 | 30 | 0 | 0 | not-authorized | provider trace / request id |
| `consume-233000` | 2026-07-01 23:30:00 | 80 | 70 | 0 | 10 | not-authorized | provider trace / request id |
| `consume-234500` | 2026-07-01 23:45:00 | 20 | 0 | 20 | 0 | authorized | provider trace / request id |
| `consume-080000` | 2026-07-02 08:00:00 | 50 | 50 | 0 | 0 | not-authorized | provider trace / request id |

23:30 的 10-credit overrun 沒有事前 prompt，因此不能扣 extra pool，必須歸 internal system absorbed。23:45 是新的 independent operation；admission 已經發現 5h allowance 不足，所以 UI 有機會提示使用者。只有使用者同意後，這筆 20 credits 才會扣 extra pool。

### 7.4 `subscription_account` after 2026-07-02 08:00 settlement

| subscription id | 5h opened | 5h expires | 5h used | 5h remaining | 7d opened | 7d expires | 7d used | 7d remaining | extra pool remaining |
|---|---|---|---:|---:|---|---|---:|---:|---:|
| `sub-a` | 2026-07-02 08:00:00 | 2026-07-02 13:00:00 | 50 | 50 | 2026-07-01 23:01:23 | 2026-07-08 23:01:23 | 180 | 820 | 980 |

extra pool remaining 可由 source of truth 重算：

```text
1000 - 20 = 980
```

## 8. Reconciliation Model

reconciliation 的主軸是 `subscription_consume_record` 對 infra log：

```text
for each infra log entry that represents billable/costly usage:
    find matching subscription_consume_record by provider request id / trace id / correlation id
    if missing:
        report missing consume record
    if credits/cost mismatch:
        report mismatch and append correction record if needed
```

這個模型不需要重算 5h / 7d window。window 只是當時 admission control 的狀態，不是成本真相。

## 9. Deferred Details

下一階段再補：

- precise column type
- unique constraint for idempotency and infra reference
- transaction boundary for consume settlement
- SQLite-friendly lock / version strategy
- correction record shape
- cost allocation snapshot structure
- infra log matching key priority
- whether extra pool adjustment needs its own ledger
