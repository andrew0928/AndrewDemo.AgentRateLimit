# Agent Instructions

本 repo 的主要目標是建立 `AndrewDemo.AgentRateLimit` 的 agent rate-limit harness。後續 agent 在這個 repo 工作時，請先把這裡視為 contract/spec-first 的練習場，不要直接跳到 production implementation。

## 必讀順序

1. `README.md`
2. `docs/architecture/harness-design.md`
3. `docs/architecture/reference-project-principles.md`
4. `spec/testcases/agent-rate-limit-harness.md`
5. `docs/project-roadmap.md`

## 工作原則

- 以 Andrew 的 architecture design skill 風格工作：先界定困難點、穩定抽象、dominant operations、可量測指標，再決定類別與實作。
- 涉及 rate limit、throttle、admission control、queue fairness、SLO/SLI 時，使用 service reliability 的設計口徑。
- 參考 `mud-agents` 時，只萃取原則：harness/workspace 主導 agent 思考，host code 只做橋接、validation、permission、transcript 與 structured operation。
- 參考 `andrewshop.apidemo` 時，只萃取原則：`Abstract` contract、`Core` orchestration、host composition root、extension/runtime surface、`spec/testcases` 驗收梯。
- 修改行為前先更新或新增 `spec/testcases`；若 test case 與設計文件衝突，先修文件再寫 code。
- 時間影響 correctness 時，必須使用可控時間，不使用 realtime sleep 來證明限流行為。
- 文件預設使用繁體中文；contract name、metric name、code symbol 維持英文。

## 不要做的事

- 不要把 agent 的任務排序、思考策略、plan 使用方式硬寫進 host orchestration。
- 不要把 provider 429 當成正常控制策略；harness 應在 provider 受傷前先做 admission control。
- 不要只量平均值；burst、queue wait、rejection、retry、fairness 都要能被觀察。
- 不要在沒有 test case 的狀態下新增 policy heuristic。
