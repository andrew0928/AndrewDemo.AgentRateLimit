# AndrewDemo.AgentRateLimit

`AndrewDemo.AgentRateLimit` 是用來設計與驗證 agent rate-limit / quota / admission-control 行為的 harness repo。

目前狀態是 repo 初始化、第一版外部行為規格設計，以及 subscription credit rate limit 的第一個 `.Core` implementation slice。這一版先固定 subscription credit rate limit 的可觀測行為與驗收案例，並用 SQLite-backed Core 與 xUnit 覆蓋 End-to-End run outcome。

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

目前 code layout 採：

- `AndrewDemo.AgentRateLimit.Abstract`：scenario、policy、decision、metric contract。
- `AndrewDemo.AgentRateLimit.Core`：subscription credit usage service、DI builder、SQLite-backed admission / consume state。
- `AndrewDemo.AgentRateLimit.Api`：subscription credit HTTP REST API，使用 bearer token resolve subscription scope。
- `AndrewDemo.AgentRateLimit.DatabaseInit`：本機/docker compose seed database initializer。
- `AndrewDemo.AgentRateLimit.Simulation`：traffic profile、可控時間、provider stub、golden scenario。
- `AndrewDemo.AgentRateLimit.Cli`：本機執行 scenario 與輸出 CSV/JSON summary。
- `AndrewDemo.AgentRateLimit.Core.Tests`：subscription credit End-to-End run outcome xUnit tests。
- `AndrewDemo.AgentRateLimit.Api.Tests`：subscription credit HTTP API blackbox xUnit tests。

## 驗收梯

1. `spec/testcases` 描述 Given/When/Then。
2. unit tests 驗證 contract 與 deterministic behavior。
3. scenario tests 驗證 burst、quota exhaustion、priority reservation、fairness、retry/degrade。
4. CLI smoke run 輸出 metrics summary，供後續 dashboard 或文章使用。

目前要 review/freeze 的第一版規格在 [subscription-credit-rate-limit-v1.md](/Users/andrew/code-work/AndrewDemo.AgentRateLimit/spec/subscription-credit-rate-limit-v1.md)，驗收案例在 [subscription-credit-rate-limit-v1.md](/Users/andrew/code-work/AndrewDemo.AgentRateLimit/spec/testcases/subscription-credit-rate-limit-v1.md)。

## 下一個實作切片

第一個 code slice 應只做最小可執行模型：

1. 定義 `ScenarioManifest`、`AgentWorkItem`、`AdmissionDecision`、`MetricSnapshot`。
2. 建立 forward-only controllable clock。
3. 實作 fixed-window quota + bounded queue policy。
4. 用一個 burst scenario 證明 accepted / queued / rejected / executed / wait ticks 都可重播。
