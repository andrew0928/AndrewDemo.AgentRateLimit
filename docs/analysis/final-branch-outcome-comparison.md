# 三種模式最終成果比較

## 評估口徑

本報告以 `architect-mode` 的最終規格、decision 過程與 final source code 作為理想實作參考，評估三個 branch 最後達成的效果。

比較對象：

| Branch | Final commit | 角色 |
|---|---|---|
| `goal-mode` | `018fd9e` - Implement dotnet10 sqlite subscription rate limit | 對照組：單一規格，一次 prompt，讓 agent 自行完成 |
| `architect-mode` | `a262d76` - Add subscription credit API blackbox tests | 實驗組：interface review、DX review、decision table、implementation、API wrapping |
| `fable5` | `21f6f66` - Implement subscription credit rate limit V1 on .NET 10 + SQLite | 第三組對照：Claude Code + Fable5，goal-mode，無人工中途介入 |

完成度分數不是一般工程品質分數，而是「相對 `architect-mode` final decisions 的吻合度」。

## Architect-mode final reference

`architect-mode` 最終有效決策可整理成以下幾個驗收軸：

1. `.Abstract` 必須能表達 `DecideAsync` / `ConsumeAsync`，且支援 unknown final credits。
2. Request contract 必須有 `CreditAmountMode`，區分 `exact-credits` 與 `minimum-available-balance`。
3. Request contract 必須有 `ExtraPoolAuthorization`，extra pool 不可在未授權時被靜默消耗。
4. Decision contract 必須有 `CreditsAbsorbedBySystem`，支援未授權 overrun 的 system absorbed 結算。
5. Window semantics 採 lazy quota window lease，不採 per-usage rolling window。
6. `subscription_consume_record` 是帳務 source of truth；window state 是 admission-control state。
7. End-to-End run outcome table 的 16 組 `TC-RUN-*` 是第一版核心驗收範圍。
8. HTTP API 必須用 Bearer access token resolve subscription scope，不信任 body 的 `userId` / `subscriptionId`。
9. API 需要黑箱測試，驗證 auth、scope、usage decision 與 docker/local seed DX。

## 總覽比較

| Branch | 完成度 | 做得好的地方 | 主要落差 | 最適合代表的能力 |
|---|---:|---|---|---|
| `goal-mode` | 35-45% | 速度最快、scope 小、程式碼容易讀；能把早期 observable spec 快速轉成 .NET 10 + SQLite + tests；有 CLI/smoke path，適合第一個 PoC | 停留在早期規格；沒有 `CreditAmountMode`、`ExtraPoolAuthorization`、`CreditsAbsorbedBySystem`；window 偏 rolling；沒有 API/deploy/test blackbox | 快速把一份規格變成可執行 prototype |
| `architect-mode` | 85-90% | 最貼近 final decisions；contract、DX、schema、decision table、Core、API、Docker、blackbox tests 都逐步收斂；人工 review 的語意有反映到 source code | 測試廣度與 adversarial audit 不如 fable5；HTTP token model 仍是 minimal/proposed；general harness simulation 尚未完成 | 以架構方法把含糊需求變成可驗收 contract，再實作 |
| `fable5` | 55-65% | 自主工程能力最強；測試量最大；有 56 tests、audit/reconciliation、idempotency、concurrency regression；修到 decision time/write-lock 與 clock regression 這類深層問題 | 實作成另一套規格：rolling/audit-ledger window，不是 lazy lease；缺 `ExtraPoolAuthorization` 與 system absorbed；沒有 API/deploy surface | 大型自主補完、測試展開、adversarial hardening |

## 能力矩陣

| 評估面向 | goal-mode | architect-mode | fable5 |
|---|---|---|---|
| Final spec alignment | 低：命中早期 spec，但未吸收後期 decisions | 高：每個人工 gate 都落到 code/test/docs | 中：覆蓋廣，但核心語意偏離 final decisions |
| `.Abstract` contract | 簡單，只有基本 request/decision/status/audit model | 精準，能表達 exact/minimum、extra auth、system absorbed | 豐富，但暴露 administration/audit/reconciliation，偏超出使用者後來收斂的 `.Abstract` |
| Unknown final credits | 缺 | 有，`minimum-available-balance` + settlement | 缺 |
| Extra pool authorization | 缺，shortfall 會直接扣 extra pool | 有，未授權時回 `extra-pool-authorization-required` 或 system absorbed | 缺，shortfall 會直接扣 extra pool |
| Window semantics | Rolling window 查 audit | Lazy quota window lease | Rolling/audit ledger sum |
| Accounting source of truth | audit + usage request 混合，概念可用但不等於 final minimal schema | `subscription_consume_record` 為 consume source of truth，extra pool record 分離 | audit ledger 為單一 ledger，reconciliation 能力強，但不是 architect final minimal schema |
| Concurrency/durability | 有 `BEGIN IMMEDIATE`，基本 SQLite 交易 | 有 store-level gate + `BEGIN IMMEDIATE`，符合最終 acceptance | 最強，有 write-lock decision time、ledger clamp、clock regression/concurrency regression tests |
| Test coverage | 小而快，約 17 個 test entry | 聚焦 final table：Core 16 run outcome + API auth/scope/usage blackbox | 最廣，56 tests，含 edge/regression/audit/reconciliation |
| API/deploy | 無 | 有 ASP.NET Core Minimal API、Docker compose、database-init、`.http`、API tests | 無 |
| Decision traceability | 有 implementation ADR，但較像 agent 自行決定 | 最好，decision index 顯示 supersede 與有效決策 | 有 ADR，品質高，但記錄的是偏離 architect final 的裁決 |
| Delivery speed | 最快 | 最慢，但每一步有 review gate | 自主執行時間長，但輸出最大 |
| Maintainability | 小，容易重寫；但 contract 後續要大改 | 中高，設計明確但檔案較多 | 中，test 多但 surface 較寬，且若要改成 architect final spec 會動很大 |

## Goal-mode 的可取之處

`goal-mode` 的最大優點是快速與簡潔。它沒有花太多時間展開 design surface，直接把早期 spec 轉成可跑的 .NET 10 + SQLite implementation。對「先證明這件事能不能跑」很有效。

值得保留的部分：

- Code surface 小，理解成本低。
- SQLite transaction 與 idempotency 基本方向是對的。
- 有 audit/reconciliation/status 的初步概念。
- 有 CLI/smoke path，適合作為 local experiment 或 demo。
- 若需求仍停留在最初版 observable spec，這個 branch 的 cost/performance ratio 最好。

它的限制不是「agent 不會寫 code」，而是 goal-mode 沒有中間 review，因此它會把早期 spec 當成最終真相。後來被人工釐清的幾件事：lazy lease、extra pool explicit authorization、unknown final credits、system absorbed、API token scope，都沒有自然出現在 final code。

## Architect-mode 的可取之處

`architect-mode` 的優勢在「需求被精準轉成 contract」。每個 commit gate 都有作用：

- `.Abstract` review 砍掉不必要管理介面，避免第一版 surface 過大。
- DX test 逼出 `minimum-available-balance`，解決 unknown final credits 不能用 `0` 偽裝的問題。
- Schema/decision 討論把 source of truth 收斂到 consume record，而不是為了可重算建立過多表。
- Decision table 把第一版 acceptance scope 固定成 16 組 run outcome。
- API spec 再把 Core 包成 HTTP surface，並把 subscription scope 放到 token boundary。
- Blackbox tests 驗證 HTTP auth/scope/usage，不只測 Core。

它的不足是：測試與 hardening 沒有像 fable5 那麼廣。特別是 fable5 找到的 write-lock decision-time、clock regression、non-owner idempotency poisoning 等問題，architect-mode 目前沒有同等規模的 adversarial audit。

## Fable5 的可取之處

`fable5` 的最大亮點是自主補完與 hardening。它沒有人工中途 review，仍然產出大量 tests、audit/reconciliation、concurrency regression，並且捕捉到非常實際的 correctness risk。

值得保留的部分：

- 56 tests，測試廣度明顯大於其他兩組。
- 有 audit/reconciliation/reporting 的完整模型。
- 有 `BEGIN IMMEDIATE` / serializable transaction / WAL / durability 設計。
- 有 adversarial audit 後修正的 decision-time-inside-write-lock。
- 有 clock regression clamp，避免 wall clock 倒退讓 committed usage 從 window 查詢消失。
- 對 idempotency、non-owner balance disclosure、overflow bounds 有更嚴格的防禦。

但它的主要問題也很明確：它優化的是自己推導出的規格，而不是 architect-mode 最終決策。它的 ADR 明確採用 audit ledger rolling sum，這和 final reference 的 lazy quota window lease 不一致；它也沒有 extra pool authorization 與 system absorbed contract。因此它是一個工程硬度很高、但需求對齊度不足的成果。

## 重要差異細節

### 1. Extra pool 語意

`architect-mode` 最終規格要求 extra pool 必須 explicit authorization 後才能消耗。若 unknown final credits 的 operation 事前沒有授權，settlement overrun 應記為 `CreditsAbsorbedBySystem`。

- `architect-mode`: 正確表達並測試。
- `goal-mode`: shortfall 直接扣 extra pool。
- `fable5`: shortfall 直接扣 extra pool。

這是三者最關鍵的需求差異。

### 2. Window 語意

`architect-mode` 最終 decision 是 lazy quota window lease：長時間 idle 不做背景 reset；下一次 admission / consume 才從當下開新 5h/7d lease。

- `architect-mode`: 使用 `subscription_account` 保存 active lease state。
- `goal-mode`: 從 audit records 以 `now - 5h/7d` 查 rolling usage。
- `fable5`: 明確使用 audit ledger rolling sum。

所以 `fable5` 的 tests 很多，但大量測的是另一種 window semantics。

### 3. API/deploy 完成度

部署要求是 architect-mode 後期才展開的需求：把 Core 封裝成 HTTP REST API，並加上 bearer token subscription scope。

- `architect-mode`: 有 API project、Docker compose、database-init、`.http`、API blackbox tests。
- `goal-mode`: 無 API。
- `fable5`: 無 API。

若最終交付目標包含可部署 API，只有 `architect-mode` 到位。

### 4. 測試策略

- `goal-mode`: tests 少，但足以保護早期 prototype。
- `architect-mode`: tests 聚焦 final acceptance table，測的是「被確認過的需求」。
- `fable5`: tests 最多，且有 adversarial regression；但一部分測試建立在不同規格假設上。

因此不能只看 test count。比較合理的判斷是：

- test alignment: `architect-mode` 最好。
- test depth/hardening: `fable5` 最好。
- test cost/performance: `goal-mode` 最好。

## 結論

這次比較不是單純「哪個 agent 寫得最好」，而是三種模式各自擅長不同事情：

- `goal-mode` 擅長快速 prototype，把早期規格變成可跑 code。
- `architect-mode` 擅長把需求逐步釐清，並讓釐清結果進入 contract/test/source。
- `fable5` 擅長大規模自主補完與 correctness hardening。

若目標是最終符合 Andrew-style architecture/spec-first 方法論，`architect-mode` 是最佳成果。

若目標是取得額外 hardening ideas，`fable5` 很有價值，特別是 concurrency、clock regression、idempotency/balance disclosure 的防禦可以反向移植回 architect-mode。

若目標是低成本快速驗證技術可行性，`goal-mode` 是最有效率的起點，但不應直接視為 final implementation。
