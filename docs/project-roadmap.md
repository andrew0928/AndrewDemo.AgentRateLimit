# Project Roadmap

> 狀態：initial roadmap  
> 日期：2026-07-01

## Phase 0：Harness Contract Freeze

目標：固定設計邊界與驗收口徑。

- [x] 建立 repo 文件骨架。
- [x] 萃取 `mud-agents` 與 `andrewshop.apidemo` 可重用原則。
- [x] 建立 harness architecture spec。
- [x] 建立 `spec/testcases` 初始驗收。
- [x] 建立只描述外部可觀測行為的 `Subscription Credit Rate Limit V1` 規格。
- [ ] review 並凍結第一版 scenario manifest contract。

## Phase 1：V1 SQLite Implementation

目標：用 .NET 10 + SQLite 實作 `Subscription Credit Rate Limit V1` 的第一版可執行行為。

- [x] 建立 `.NET 10` solution 與 `Abstract/Core/Cli/Core.Tests` project。
- [x] 實作 SQLite-backed subscription usage service。
- [x] 實作整數 credit validation。
- [x] 實作 5h / 7d rolling window。
- [x] 實作 extra pool top-up、consume 與 manual correction。
- [x] 實作 idempotency replay 與 conflict。
- [x] 實作 audit trail 與 reconciliation report。
- [x] 將 V1 testcases 轉成 xUnit 驗收測試。
- [x] 建立 CLI smoke run。

## Phase 2：Policy And Metrics Expansion

目標：讓 harness 能比較策略，而不是只跑單一策略。

- [ ] 加入 token budget 與 concurrent execution quota。
- [ ] 加入 retry-after 與 bounded retry budget。
- [ ] 加入 fairness violation metrics。
- [ ] 輸出 CSV timeline。
- [ ] 加入 CLI smoke scenario。

## Phase 3：Provider Adapter And Operational View

目標：接近真實 agent provider 使用情境。

- [ ] 定義 provider adapter boundary。
- [ ] 加入 provider-specific quota mapping。
- [ ] 建立 dashboard-friendly summary。
- [ ] 撰寫 operational response guide。

## Refactor Trigger

若出現以下狀況，先回到 architecture spec：

- 新 policy 需要改 runner 主流程。
- provider adapter 直接決定 admission。
- test 需要 realtime sleep。
- metrics 無法分辨 wait、execution、reject、retry、provider 429。
