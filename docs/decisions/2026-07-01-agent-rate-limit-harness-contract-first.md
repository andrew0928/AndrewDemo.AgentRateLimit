# 決策：AgentRateLimit 採 contract-first harness 與可控時間模型

- 決策時間：2026-07-01
- 狀態：accepted
- 範圍：`AndrewDemo.AgentRateLimit` repo 初始化

## Context

這個 repo 要設計的是 agent rate-limit harness。核心風險不是 provider SDK 整合，而是限流策略在 burst、quota exhaustion、retry-after、priority reservation、queue fairness 下是否仍可證明。

參考專案提供兩條可沿用的原則：

- `mud-agents`：agent harness/workspace 主導思考，host code 只提供 structured bridge、validation、permission、feedback、transcript。
- `andrewshop.apidemo`：用 `Abstract -> Core -> host composition -> extension/runtime surface` 固定邊界，並用 `spec/testcases` 作為驗收源頭。

## Decision

本 repo 第一階段只初始化規範與驗收：

- 先建立 architecture design、reference principles、roadmap、testcases。
- 未來 code 採 `Abstract` contracts 與 `Core` deterministic runner 分離。
- 限流相關時間一律透過 controllable clock；測試不得依賴 realtime sleep。
- admission outcome 必須是 structured `AdmissionDecision`。
- provider 429 是 failure signal 或 retry input，不是主要控制策略。
- scenario manifest 負責描述 workload、quota、policy、assertion；core runner 不依 provider 或 workload 名稱寫死分支。

## Consequences

正面影響：

- 後續可以用小型 POC 先驗證 policy 與 metrics。
- provider adapter 可以晚點加入，不會污染核心 harness。
- 所有行為能被 Given/When/Then 與 deterministic replay 驗證。

代價：

- 第一階段不會立刻有 production integration。
- policy expressiveness 會先受限於 fixed-window、bounded queue、reservation 三個基本模型。
- 真實 provider token accounting 需要在第二階段才補齊。

## Validation

第一階段驗收以 `spec/testcases/agent-rate-limit-harness.md` 為準。當 `src` 實作開始後，這些 test case 應映射成 unit/scenario tests 與 CLI smoke run。
