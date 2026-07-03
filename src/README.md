# Source Layout

目前已建立 `Subscription Credit Rate Limit V1` 的實作切片；rate-limit harness（Simulation/Cli）尚未開始。

## Current Projects

```text
src/
├── AndrewDemo.AgentRateLimit.Abstract/
│   └── SubscriptionCredit/    # usage/administration contracts、decision/status/audit/reconciliation models
└── AndrewDemo.AgentRateLimit.Core/
    └── SubscriptionCredit/    # SqliteSubscriptionCreditService（Microsoft.Data.Sqlite）
```

實作決策見 [docs/decisions/2026-07-03-subscription-credit-v1-sqlite-implementation.md](/Users/andrew/code-work/AndrewDemo.AgentRateLimit/docs/decisions/2026-07-03-subscription-credit-v1-sqlite-implementation.md)。

## Boundary Rules

- `Abstract` 只放穩定 contract，不依賴 provider SDK 或資料庫套件。
- `Core` 放 deterministic orchestration 與 persistence，不讀取 console input；時間一律透過 `TimeProvider` 注入。
- 未來 harness 專案（`Simulation`、`Cli`）依原規劃加入。

若新增 project，先確認它是否真的代表新的 ownership boundary；不要只因檔案多就拆 project。
