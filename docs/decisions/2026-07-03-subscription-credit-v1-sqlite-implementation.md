# Subscription Credit Rate Limit V1 — SQLite 實作決策

> 狀態：accepted
> 日期：2026-07-03
> 範圍：`spec/subscription-credit-rate-limit-v1.md` 的第一版 .NET 10 + SQLite 實作。規格只凍結外部行為；本文件記錄實作層必須自行裁決、且會影響外部可觀測結果的決策，以及每個決策的理由。

## 1. 專案切分

依 repo 既有的 `Abstract -> Core -> Tests` 慣例：

- `src/AndrewDemo.AgentRateLimit.Abstract/SubscriptionCredit/`：usage/administration contract、decision/status/audit/reconciliation model、reason 常數。無任何套件相依。
- `src/AndrewDemo.AgentRateLimit.Core/SubscriptionCredit/`：`SqliteSubscriptionCreditService`，使用 `Microsoft.Data.Sqlite`。
- `tests/AndrewDemo.AgentRateLimit.Core.Tests/`：驗收測試，1:1 對齊 `spec/testcases/subscription-credit-rate-limit-v1.md` 的 TC 編號。

規格 §9 明列 API route 為 out of scope，因此第一版不建 HTTP host；外部可觀測能力以 `ISubscriptionCreditUsageService` 與 `ISubscriptionCreditAdministrationService` 表達。

## 2. 儲存模型：單一 append-only ledger

三張表：

- `subscriptions`：limit 與目前 extra pool balance（denormalized，交易內更新）。
- `audit_records`：append-only ledger，是 audit trail、window usage 與 reconciliation 的唯一來源。
- `idempotency_records`：以 `(subscription_id, idempotency_key)` 為 primary key 的 decision snapshot。

關鍵決策：**window usage 直接從 audit ledger 的 accepted usage-decision 加總計算**，不另建 usage 表。理由：帳務優先順序第一是「帳務結果不得錯誤」；單一資料來源讓 audit 與帳務不可能彼此漂移。5h/7d rolling window 以 partial index（`record_type='usage-decision' AND decision_result='accepted'`）支撐查詢。

每筆會改變 extra pool 的紀錄（seed、adjustment、accepted usage 消耗 extra）都帶 `extra_pool_delta` 與 `extra_pool_balance_after`，讓 reconciliation 的期初/期末餘額可以由 ledger 單獨重建。訂閱建立時一律寫入 `extra-pool-seed` 紀錄（含 0 餘額），確保任何期間的期初餘額都有 baseline。

## 3. 一致性與持久性

- 所有寫入操作（consume、adjust、correction、provision）都在單一 `BEGIN IMMEDIATE` 交易內完成「讀狀態 → 決策 → 寫入」。SQLite 的單一 writer lock 保證同一 database 的併發請求序列化，滿足規格 §6「等價於某個明確先後順序」與「accepted 不可超過可覆蓋額度」。
- **決策時間在交易內取得，並以 ledger clamp 保底**：decision time 必須在取得 write lock 之後才讀取（否則被 lock 擋住的 writer 會用過期的 `now` 計算 window，看不到剛 commit 的 usage 而超額 accept——這是 adversarial audit 找到的 critical 缺陷）。另外 wall clock 可能倒退（NTP），因此 accounting 時間一律 clamp 到「該 subscription ledger 中最新紀錄的時間」以後，保證已 commit 的 usage 永遠不會從 window 查詢中消失。status 查詢同樣套用 clamp。
- 讀取操作（status、preview、reconciliation）使用 deferred transaction 取得一致 snapshot。
- `journal_mode=WAL` + `synchronous=FULL`：committed decision 在服務重啟後必然可見（規格 §6）。犧牲寫入延遲換取帳務持久性；規格的優先順序允許這個取捨。
- `busy_timeout=10000`：writer 競爭時等待而非立即失敗。

## 4. Window 語意

- 決策時間 `T` 的 window 計入 `T - Δ < usage_time <= T`（規格 4.2）；剛好滿 5h/7d 的 usage 排除。時間以 unix milliseconds 儲存與比較。
- Window used 是「所有 accepted credits」的加總，包含由 extra pool 覆蓋的部分（規格 4.2），因此 used 可以超過 limit；remaining 以 0 為下限。
- `next reset time` = window 內最舊 accepted usage 的時間 + window 長度，即 used credits 下一次減少的時刻；window 內無 usage 時為 null。

## 5. 驗證順序與 invalid 語意

規格未定義多重 invalid 條件同時成立時的優先序。實作採固定順序，先驗身分欄位再驗 credit 格式：

1. `missing-user-id`
2. `missing-subscription-id`
3. `missing-idempotency-key`
4. `credits-not-integer`（含負的小數）
5. `credits-not-positive`
6. `credits-out-of-range`（實作自定 reason：值為正整數但超過 `SubscriptionCreditBounds.MaxCreditAmount` = 10^15；規格 7.3 允許在最小集合之外增加 reason）

其他裁決：

- 空白字串視同 missing。
- **Credit 上限**：window limit、extra pool balance、requested credits 一律以 10^15 為上限（provision 與 adjustment 超界直接拒絕）。這讓所有帳務運算（含 7d window 的 SQL SUM 聚合）距離 Int64 溢位有 >9000 倍的安全邊際；accepted path 的 remaining-after 計算另以 saturating 加法保底，確保永遠不會在決策成立後才拋出溢位例外。
- Invalid decision 也寫入 audit record（規格 §6 要求 invalid 重啟後仍可回溯），user/subscription id 欄位以請求提供的值記錄（可為 null）。
- 每筆 usage-decision audit record 的 actor 記為發出請求的 user id（缺 user id 時記 `unauthenticated-caller`），滿足規格 3.4 每筆紀錄需有 actor 或 source。
- Decision 回傳的 `RequestedCredits` 型別為整數；當請求值無法表示為整數（如 1.5）時該欄位為 null，符合規格 4.1「所有回傳 credit 數字都必須是整數」。

## 6. Idempotency 裁決

- **Payload fingerprint = (user id, subscription id, requested credits)**。Correlation id 是追蹤欄位，重試本來就會換新，不參與 payload 一致性判斷。credits 以正規化後的整數值計算 fingerprint（20 與 20.0 視為相同 payload）。
- **只有通過 ownership 驗證的 decision 才綁定 idempotency key**（accepted、rejected/disabled、rejected/insufficient）。規格 4.6「相同 key、相同 payload 重送必須回傳原本 decision」沒有限定 accepted；被 rejected 的請求以同 key 重送會永遠拿回原本的 rejected decision，即使額度已恢復。呼叫者要重試必須換新 key。
- **`subscription-not-found` 與 `user-subscription-mismatch` 的 rejection 不綁定 key**：非擁有者不得佔用（污染）他人 subscription 的 keyspace（adversarial audit finding）。這類 rejection 重送時會重新評估，結果依然 deterministic。
- **Invalid 與 conflict 不綁定 key**：invalid 請求未進入帳務；conflict 本身是 key 衝突的結果。
- Idempotency 檢查在 subscription 狀態檢查**之前**：已綁定的 decision 即使 subscription 之後被停用仍照原樣重播（規格 4.6 無條件要求回傳原 decision）。
- 重播回傳原 decision 的完整 snapshot：原數值、原 decision time、原 audit reference、原 correlation id，並以 `IsIdempotentReplay=true` 標示。
- Conflict decision 寫入 conflict audit record（規格 4.6），且**只在呼叫者是 subscription 擁有者時**回傳當下 remaining 值；非擁有者拿到的 conflict 不含任何 balance 資訊（規格 §5 不可洩漏其他 user 的 usage detail）。

## 7. Preview 語意

Preview 走與 consume **完全相同的決策管線**，但停用一切持久化：不寫 audit、不綁 key、不動 balance。因此：

- Preview 對已綁定的 key 會如實回報「consume 將會重播的原 decision」或 conflict，而不是誤導性的重新計算。
- Preview decision 的 `AuditReference` 為 null——preview 不是帳務紀錄（規格 3.2）。

## 8. Rejection 檢查順序

subscription 檢查順序：`subscription-not-found` → `user-subscription-mismatch` → `subscription-disabled`。mismatch 先於 disabled：呼叫者與 subscription 不匹配時不應得知該 subscription 的啟用狀態。

Rejected request 不消耗任何額度，但寫入 audit record；是否綁定 idempotency key 見 §6。remaining 欄位的揭露依 ownership 而定：

- `subscription-not-found`、`user-subscription-mismatch`：remaining 欄位為 null（不洩漏）。
- `subscription-disabled`、`insufficient-credits`：呼叫者是擁有者，回傳當下 remaining 5h/7d/extra pool（規格 3.1 要求 decision 至少包含這些欄位）。

## 9. Manual correction 與 extra pool adjustment

- `RecordManualCorrectionAsync` 只產生 evidence record（新 audit record，可帶 `RelatedAuditId` 指向原紀錄），**不改變** window usage 或 extra pool balance。原始紀錄永不消失（規格 §6、TC-AUDIT-003）。
- 會改變 extra pool 的操作只有 `AdjustExtraPoolAsync`（正負皆可）；結果為負時整筆拒絕（throw），balance 不變（規格 4.4「extra pool 不可變成負數」）。
- 兩者都要求 actor 與 reason，寫入 audit record。

## 10. Reconciliation 期間語意

- 期間為半開區間 `[fromInclusive, toExclusive)`，報表慣例。
- 每個 subscription 一列：期間內 accepted/rejected credits、allowance/extra 覆蓋量、extra pool 期初/增加/消耗/調整/期末、accepted/rejected/conflict/invalid/manual-correction counts。
- 期初餘額 = 期間開始前最後一筆帶 `extra_pool_balance_after` 的紀錄；期末餘額同理以期間結束為界。不變式：`期末 = 期初 + 增加 + 調整 - 消耗`（調整為負向調整的加總，≤ 0；seed 與正向 adjustment 計入「增加」）。
- 無法歸屬 subscription 的 invalid request（缺 subscription id）以報表層級的 `UnattributedInvalidRequestCount` 呈現。
- Audit trail 查詢必須指定 user 或 subscription 之一，避免跨 user 列舉（規格 §5）。同時指定兩者時為 AND 過濾；要看見「所有觸及某 subscription 的紀錄」（含其他 user 被拒的嘗試）應只以 subscription id 查詢。

## 11. 已知限制（第一版接受）

- 單一 SQLite database 是唯一 writer 邊界；跨 database/multi-writer 明列於規格 out of scope。
- Idempotency key 綁定以 `(subscription_id, key)` 為範圍；同一 subscription 的擁有者以相同 key、不同 payload 重送會得到 conflict——這是規格 4.6 的字面行為。非擁有者無法綁定 key（見 §6），但對「擁有者已綁定的 key」發出的請求仍會得到 conflict（不含 balance 資訊）。
- `idempotency_records` 無 TTL/清理機制；第一版驗收不要求。
- Preview 不留查詢紀錄（規格允許但不要求）。
- 帳務金額上限為 10^15 credits（見 §5）；超出範圍的 provision/adjustment 直接拒絕、usage request 回 `credits-out-of-range`。
