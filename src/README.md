# Source Layout

此目錄目前已有 `Abstract` 與 subscription credit rate limit 的第一個 `Core` implementation slice。後續 general harness implementation 仍應採以下分層。

## Planned Projects

```text
src/
├── AndrewDemo.AgentRateLimit.Abstract/
├── AndrewDemo.AgentRateLimit.Core/
├── AndrewDemo.AgentRateLimit.Simulation/
└── AndrewDemo.AgentRateLimit.Cli/
```

目前 `AndrewDemo.AgentRateLimit.Core` 已包含：

- `ISubscriptionCreditUsageService` 的 Microsoft DI registration builder。
- SQLite-backed `DecideAsync` / `ConsumeAsync` implementation。
- subscription account、consume record、extra pool record 的最小 schema。

## Boundary Rules

- `Abstract` 只放穩定 contract，不依賴 provider SDK。
- `Core` 放 deterministic orchestration，不讀取 console input，不做 provider-specific protocol。
- `Simulation` 放 controllable clock、traffic generator、provider stub。
- `Cli` 是 composition root，負責載入 scenario、組裝 runner、輸出 summary。

若新增 project，先確認它是否真的代表新的 ownership boundary；不要只因檔案多就拆 project。
