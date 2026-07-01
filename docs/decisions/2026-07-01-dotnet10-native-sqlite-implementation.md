# 決策：V1 實作採 .NET 10 與 native SQLite persistence

- 決策時間：2026-07-01
- 狀態：accepted
- 範圍：`Subscription Credit Rate Limit V1` 第一版實作

## Context

使用者要求以 .NET 10 與 SQLite 實作第一版 subscription credit rate limit。規格要求低建置成本、單一 database persistence、多 user subscription usage、整數 credit、5h/7d rolling window、extra pool、idempotency、audit trail 與 reconciliation。

## Decision

第一版實作採：

- .NET 10 projects：`Abstract`、`Core`、`Cli`、`Core.Tests`
- SQLite database 作為唯一 persistence
- Core 以 native SQLite binding 存取本機 `libsqlite3`
- 不引入外部 queue、cache、message broker 或額外帳務資料庫
- 不引入 SQLite NuGet provider，降低 restore 與部署依賴
- xUnit 驗收測試對齊 `spec/testcases/subscription-credit-rate-limit-v1.md`

## Consequences

正面影響：

- 符合低建置成本與 SQLite-first 目標。
- 不需要下載或部署額外 SQLite provider 套件。
- 核心行為可用 `dotnet test` 在本機直接驗證。

代價：

- native binding 對執行環境的 `libsqlite3` 可用性有要求。
- 若未來要跨平台封裝成 NuGet/library，可能需要改成正式 SQLite provider 或提供 native library resolution policy。
