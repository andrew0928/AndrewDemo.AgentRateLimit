# Subscription Credit Rate Limit V1 行為規格

> 狀態：draft-for-review  
> 日期：2026-07-01  
> 範圍：只描述外部可觀測行為、輸入、輸出、限制結果與回溯要求；不描述資料表、交易、鎖、索引、演算法、內部元件或程式碼分層。

## 1. 目的

本規格定義一個基於 credit 的 subscription usage control 行為。系統以整數 credit 作為唯一用量單位，對每個 subscription 同時套用 5 小時與 7 天用量窗口；當窗口額度不足時，可依規則使用額外的 extra pool。

優先順序：

1. 帳務結果不得錯誤。
2. 所有用量決策與帳務變動都必須可回溯。
3. 建置成本優先，第一版驗收環境只要求單一 database persistence，不要求額外 stateful infrastructure。
4. 行為要足夠單純，適合用在 SQLite 等入門等級 database 環境。

## 2. 名詞

- `user`：使用服務的人或帳號。
- `subscription`：屬於某個 user 的使用量方案。每個 subscription 有自己的 5h 額度、7d 額度與 extra pool。
- `credit`：唯一計費與限流單位，必須為整數。
- `usage request`：要求消耗一筆 credit 的外部請求。
- `usage decision`：對 usage request 的結果，只有 accepted、rejected、conflict 或 invalid。
- `5h window`：在決策時間往前回看 5 小時的 rolling window。
- `7d window`：在決策時間往前回看 7 天的 rolling window。
- `extra pool`：subscription 額外可用 credit 餘額；當 5h 或 7d window 額度不足時，accepted usage 可從 extra pool 補足。
- `idempotency key`：外部呼叫者提供的唯一鍵，用來避免同一筆 usage request 被重複扣款。
- `audit record`：可查詢、可匯出、可用於回溯 usage decision 與 credit 變動的紀錄。

## 3. 外部可觀測能力

本規格不指定 API path、method、database schema 或程式類別，但系統必須提供等價能力。

### 3.1 Consume Usage

外部呼叫者提交一筆 usage request，至少包含：

- user id
- subscription id
- requested credits
- idempotency key
- request correlation id

系統回傳 usage decision，至少包含：

- decision result：accepted、rejected、conflict 或 invalid
- requested credits
- credits covered by subscription window allowance
- credits covered by extra pool
- remaining 5h credits after decision
- remaining 7d credits after decision
- remaining extra pool credits after decision
- rejection or conflict reason，若未 accepted
- audit reference

### 3.2 Preview Usage

外部呼叫者可以預覽同一筆 requested credits 在目前狀態下是否可被接受。

Preview 的結果格式應與 consume usage decision 一致，但不得改變任何 usage total、window usage、extra pool balance 或 audit reconciliation result。

Preview 可以留下查詢紀錄，但不得被視為帳務扣款紀錄。

### 3.3 Query Usage Status

外部呼叫者可以查詢某個 subscription 目前狀態，至少包含：

- 5h window limit
- 5h window used credits
- 5h window remaining credits
- 5h next reset time，若可由目前用量推算
- 7d window limit
- 7d window used credits
- 7d window remaining credits
- 7d next reset time，若可由目前用量推算
- extra pool remaining credits

### 3.4 Query Audit Trail

授權的外部呼叫者可以查詢某個 user 或 subscription 的 audit trail，至少能看到：

- accepted usage
- rejected usage
- idempotency conflict
- extra pool changes
- manual correction 或 adjustment
- 每筆紀錄的 time、user id、subscription id、credits、decision、reason、correlation id、idempotency key、actor 或 source

### 3.5 Export Reconciliation Report

系統必須能匯出指定時間範圍的 reconciliation report。報表必須足以回答：

- 每個 subscription 在期間內 accepted 多少 credits。
- 每個 subscription 在期間內 rejected 多少 credits。
- 有多少 credits 由 subscription window allowance 覆蓋。
- 有多少 credits 由 extra pool 覆蓋。
- extra pool 的期初、增加、消耗、調整、期末餘額。
- 是否存在 conflict、invalid request 或 manual correction。

## 4. Credit 與用量規則

### 4.1 Credit 單位

- requested credits 必須是正整數。
- fractional credit 不合法。
- zero 或 negative credit 不合法。
- 所有回傳與報表中的 credit 數字都必須是整數。

### 4.2 Window 計算

對決策時間 `T`：

- 5h window 計入 `T - 5h < usage time <= T` 的 accepted usage credits。
- 7d window 計入 `T - 7d < usage time <= T` 的 accepted usage credits。
- rejected、invalid、conflict、preview 不計入 window usage。
- accepted usage 不論由 subscription allowance 或 extra pool 覆蓋，都必須計入 5h 與 7d window usage。

### 4.3 Accepted 條件

一筆 usage request 可以被 accepted，必須符合：

- requested credits 是正整數。
- subscription 存在且屬於指定 user。
- idempotency key 沒有衝突。
- `subscription allowance 可覆蓋 credits + extra pool 可覆蓋 credits >= requested credits`。

其中 subscription allowance 可覆蓋 credits 由當下 5h remaining 與 7d remaining 共同限制，以較小者為準。

### 4.4 Extra Pool 使用

- 當 5h 與 7d remaining 都足以覆蓋 requested credits 時，不可消耗 extra pool。
- 當任一 window remaining 不足時，accepted usage 的不足部分必須消耗 extra pool。
- extra pool 不可變成負數。
- extra pool 不因 5h 或 7d window reset 自動恢復。
- extra pool 的增加、消耗與調整都必須可在 audit trail 與 reconciliation report 中看見。

### 4.5 Rejected 條件

一筆 usage request 必須被 rejected，若：

- requested credits 超過當下 subscription allowance 與 extra pool 可合計覆蓋的數量。
- subscription 不存在或不屬於指定 user。
- subscription 已停用或不可用。

Rejected request：

- 不可消耗 subscription window allowance。
- 不可消耗 extra pool。
- 必須留下可查詢的 audit record。

### 4.6 Idempotency

同一個 subscription 中：

- 相同 idempotency key、相同 request payload 被重送時，必須回傳原本 decision。
- 重送不得造成二次扣款。
- 相同 idempotency key、不同 request payload 必須回傳 conflict。
- conflict 不可改變 usage total 或 extra pool balance。
- conflict 必須留下可查詢的 audit record。

## 5. 多 User 與 Subscription 隔離

- 單一 database persistence 必須能管理多個 user 的 subscription usage。
- user A 的 accepted usage 不可影響 user B 的 window usage、remaining credits 或 extra pool。
- 同一 user 若有多個 subscription，各 subscription 的 usage、limit、extra pool 與 audit trail 必須彼此隔離。
- 查詢 user 或 subscription 狀態時，不可洩漏其他 user 的 usage detail。

## 6. 一致性與持久性要求

本節只描述外部結果，不指定內部實作方式。

- 同一 subscription 的多筆同時 usage request，其可觀測結果必須等價於某個明確的先後順序。
- 不論同時請求數量多少，accepted credits 不可超過當下可由 subscription allowance 與 extra pool 合計覆蓋的數量。
- 已回傳 accepted 的 usage decision，在服務重啟後仍必須出現在 usage status、audit trail 與 reconciliation report 中。
- 已回傳 rejected、invalid 或 conflict 的 decision，在服務重啟後仍必須可於 audit trail 查詢。
- 若帳務需要修正，修正必須以新的 audit record 表達；原始紀錄不可從外部查詢結果中消失。

## 7. 狀態與錯誤原因

### 7.1 Decision Result

- `accepted`
- `rejected`
- `conflict`
- `invalid`

### 7.2 Rejection Reason

第一版至少支援：

- `insufficient-credits`
- `subscription-not-found`
- `subscription-disabled`
- `user-subscription-mismatch`

### 7.3 Invalid Reason

第一版至少支援：

- `credits-not-integer`
- `credits-not-positive`
- `missing-user-id`
- `missing-subscription-id`
- `missing-idempotency-key`

### 7.4 Conflict Reason

第一版至少支援：

- `idempotency-key-payload-mismatch`

## 8. Deployment Acceptance Constraint

第一版驗收環境的目標是低建置成本：

- 必須能以單一 database persistence 驗收所有行為。
- 第一版驗收不得要求外部 queue、cache、distributed lock service、message broker 或額外帳務資料庫。
- 在 SQLite 等入門等級 database 環境中，仍必須滿足本規格的一致性、持久性與回溯要求。

## 9. Out Of Scope

第一版不定義：

- 實際 API route、controller、SDK 或 database schema。
- 內部交易、鎖、索引、快取或資料表設計。
- 金流付款、發票、退款、稅務。
- 自動 subscription plan upgrade/downgrade。
- 跨 database、跨 region 或 multi-writer replication。
- 非整數 credit。
- calendar bucket window；第一版只採 rolling 5h 與 rolling 7d。
