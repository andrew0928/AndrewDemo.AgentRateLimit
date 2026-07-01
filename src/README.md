# Source Layout

此目錄目前尚未建立 production code。第一個 implementation phase 應採以下分層。

## Planned Projects

```text
src/
├── AndrewDemo.AgentRateLimit.Abstract/
├── AndrewDemo.AgentRateLimit.Core/
├── AndrewDemo.AgentRateLimit.Simulation/
└── AndrewDemo.AgentRateLimit.Cli/
```

## Boundary Rules

- `Abstract` 只放穩定 contract，不依賴 provider SDK。
- `Core` 放 deterministic orchestration，不讀取 console input，不做 provider-specific protocol。
- `Simulation` 放 controllable clock、traffic generator、provider stub。
- `Cli` 是 composition root，負責載入 scenario、組裝 runner、輸出 summary。

若新增 project，先確認它是否真的代表新的 ownership boundary；不要只因檔案多就拆 project。
