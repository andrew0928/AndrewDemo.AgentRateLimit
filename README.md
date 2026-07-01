# AndrewDemo.AgentRateLimit

`AndrewDemo.AgentRateLimit` 是用來設計與驗證 agent rate-limit / quota / admission-control 行為的 harness repo。

目前狀態是 repo 初始化、第一版外部行為規格設計，以及 .NET 10 + SQLite 的第一版可執行實作。

## 設計目標

這個 harness 要回答一個問題：

> 給定一組 agent 工作流、provider quota、workload priority 與可控時間，系統是否能用 deterministic 的方式證明限流策略保護 provider、維持高價值工作 SLO，並輸出可診斷的 metrics？

## 參考專案原則

- `mud-agents`：agent harness/workspace 負責思考與策略；host code 負責 structured operation、validation、permission、feedback、session lifecycle 與 transcript。
- `andrewshop.apidemo`：穩定 contract 放在 `Abstract`，核心流程放在 `Core`，host 只做 composition，變體透過 manifest/extension 註冊，驗收以 `spec/testcases` 先行。

詳細萃取見 [reference-project-principles.md](/Users/andrew/code-work/AndrewDemo.AgentRateLimit/docs/architecture/reference-project-principles.md)。

## 預期 Repo Layout

```text
.
├── AGENTS.md
├── README.md
├── docs/
│   ├── architecture/
│   ├── decisions/
│   └── project-roadmap.md
├── spec/
│   └── testcases/
├── src/
└── tests/
```

目前 code layout：

- `AndrewDemo.AgentRateLimit.Abstract`：usage request、decision、status、audit、reconciliation contract。
- `AndrewDemo.AgentRateLimit.Core`：SQLite-backed subscription usage service。
- `AndrewDemo.AgentRateLimit.Cli`：本機 smoke run。
- `AndrewDemo.AgentRateLimit.Core.Tests`：對齊 V1 規格的 xUnit 驗收測試。

## 驗收梯

1. `spec/testcases` 描述 Given/When/Then。
2. unit tests 驗證 contract 與 deterministic behavior。
3. scenario tests 驗證 burst、quota exhaustion、priority reservation、fairness、retry/degrade。
4. CLI smoke run 輸出 metrics summary，供後續 dashboard 或文章使用。

目前要 review/freeze 的第一版規格在 [subscription-credit-rate-limit-v1.md](/Users/andrew/code-work/AndrewDemo.AgentRateLimit/spec/subscription-credit-rate-limit-v1.md)，驗收案例在 [subscription-credit-rate-limit-v1.md](/Users/andrew/code-work/AndrewDemo.AgentRateLimit/spec/testcases/subscription-credit-rate-limit-v1.md)。

## Build And Test

```bash
dotnet restore AndrewDemo.AgentRateLimit.slnx
dotnet build AndrewDemo.AgentRateLimit.slnx --no-restore -m:1
dotnet test AndrewDemo.AgentRateLimit.slnx --no-build -m:1
dotnet run --project src/AndrewDemo.AgentRateLimit.Cli/AndrewDemo.AgentRateLimit.Cli.csproj --no-build
```

## Implemented V1 Behavior

- 正整數 credit validation。
- 5h 與 7d rolling window。
- window allowance 不足時使用 extra pool。
- insufficient credits / subscription missing / disabled / user mismatch rejection。
- idempotency replay 與 payload mismatch conflict。
- preview usage 不改變帳務狀態。
- 多 user 與多 subscription 隔離。
- 同 subscription concurrent consume 不超扣。
- restart 後 audit/status 可回溯。
- extra pool top-up、manual correction 與 reconciliation report。
