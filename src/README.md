# Source Layout

此目錄保存 .NET 10 implementation projects。

## Projects

```text
src/
├── AndrewDemo.AgentRateLimit.Abstract/
├── AndrewDemo.AgentRateLimit.Core/
└── AndrewDemo.AgentRateLimit.Cli/
```

## Boundary Rules

- `Abstract` 只放外部 contract，不依賴 SQLite 或 provider SDK。
- `Core` 實作 subscription usage service 與 SQLite persistence。
- `Cli` 是本機 smoke run composition root。

若新增 project，先確認它是否真的代表新的 ownership boundary；不要只因檔案多就拆 project。
