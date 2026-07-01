# Agent Rate Limit Harness Design

> 狀態：initial architecture spec  
> 日期：2026-07-01  
> 範圍：`AndrewDemo.AgentRateLimit` 的 harness 規範，不含 production provider adapter 實作。

## 1. Problem Analysis

這個 repo 的困難點不是「能不能呼叫 agent provider」，而是要用可重播的方式證明 rate-limit policy 在壓力下仍然可解釋、可量測、可操作。

主要壓力來源：

- provider quota 有 RPM、TPM、concurrent session、daily budget、retry-after 等不同單位。
- agent workload 有不同價值與時效，例如 foreground request、background deep think、batch summarization。
- burst 期間若只依賴 provider 429，代表 admission control 已經太晚。
- queue wait、retry、degrade、reject 都會影響使用者可見服務品質。
- 時間 window 會影響 correctness，因此測試不能靠 realtime sleep。

Dominant operations：

- 載入 scenario manifest。
- 產生 traffic arrivals。
- 對每個 `AgentWorkItem` 做 admission decision。
- 維護 quota ledger 與 queue。
- 在可控時間中 dispatch execution。
- 記錄 decision/event/metric timeline。
- 對 scenario assertions 做 deterministic verification。

## 2. Modeling

穩定 contract 句子：

> Given scenario traffic and controllable time, the harness produces a deterministic admission/execution/rejection timeline and metrics that prove whether a rate-limit policy protects provider quotas and workload SLOs.

核心部件：

- `ScenarioManifest`：描述 provider quota、workload classes、traffic profile、policy config、assertions。
- `HarnessClock`：forward-only controllable clock，測試時可 fast-forward。
- `TrafficGenerator`：依 scenario 產生 `AgentWorkItem` arrival event。
- `AdmissionController`：把 work item 與目前 quota/queue 狀態轉成 `AdmissionDecision`。
- `QuotaLedger`：維護 provider capacity、reservation、window refill、retry-after。
- `QueueScheduler`：維護 bounded queue、ordering、fairness、abandonment。
- `ExecutionBridge`：執行 accepted work。POC 先用 provider stub，未來再接實際 provider。
- `MetricRecorder`：輸出 timeline event、summary metrics、assertion evidence。
- `AssertionEngine`：驗證 scenario 是否符合 SLO、fairness、quota protection。

責任切分：

- scenario/workspace 定義策略與 workload 意義。
- core harness 負責 deterministic control、validation、metrics。
- provider adapter 只負責外部 provider protocol，不改 admission semantics。

## 3. Service Objective Model

SLA 通常不是這個 harness 的第一版範圍；第一版先定義內部 SLO 與 SLI。

候選 SLO：

- high-priority workload 在 burst 下 p95 queue wait 不超過指定 ticks。
- provider 429 count 必須為 0；若不是 0，代表 admission control 沒有保護 bottleneck。
- low-priority workload 可被 queue/degrade/reject，但不能造成 high-priority rejected。
- bounded queue 滿時必須早期 reject，並提供 retry-after 或 reject reason。
- replay 同一 scenario 必須產生相同 timeline 與 summary。

SLI observation points：

- arrival time
- admission decision time
- queue enter time
- execution start time
- provider accepted/completed time
- retry/degrade/reject time
- abandonment time

## 4. Bottleneck And Workload Model

受限資源要以明確單位建模，不預設只有 request count：

- requests per minute
- tokens per minute
- concurrent executions
- per-agent turn budget
- daily cost budget
- retry budget
- reserved capacity per workload class

Workload class 至少包含：

- `interactive`：使用者正在等待的前景工作，最嚴格 SLO。
- `background`：可延後的規劃、整理、deep think。
- `bulk`：批次匯入、摘要、回填，可被強制節流。

當 backlog 與 wait time 同時上升，表示 arrival rate 超過 bottleneck capacity；harness 應在 admission 階段保護 provider，而不是讓 provider 429 變成主要節流機制。

## 5. Capacity Control And Admission

`AdmissionDecision` 必須明確表達 outcome：

- `accepted`：可立即執行。
- `queued`：進入 bounded queue，附 queue position 與 estimated wait。
- `rejected`：不進 queue，附 reason 與 retry-after。
- `degraded`：改用較低成本或較低能力路徑。
- `retryScheduled`：依 provider 或 policy 指定時間重試。

Policy 第一版建議只做三種：

- fixed window quota
- bounded priority queue
- reserved capacity for high-priority workload

更複雜的 adaptive policy 必須等上述三種 policy 有共同 test harness 後再加。

## 6. Queue And Lineup Design

Queue semantics：

- ordering rule：同 priority 以 arrival order；不同 priority 依 reservation 與 fairness policy。
- max queue length：超過後 early reject。
- abandonment timeout：超時的 queued work 必須轉 abandonment event。
- duplicate rule：同 `idempotencyKey` 的 pending work 不可重複佔位。
- status query：queue position、estimated wait、decision reason 必須可由 cheap state 查出。

Fairness 的第一版定義：

- high-priority capacity 受保護。
- low-priority 不能永久 starvation；若 reservation 用不完，可依 policy 借給其他 class。
- per-tenant queue 不能讓單一 tenant 用 burst 吃掉所有 deferred capacity。

## 7. Data And Storage Design

POC 階段先用 in-memory timeline；contract 要保留未來 persistence 可能性。

核心 identity：

- `scenarioId`
- `runId`
- `workItemId`
- `tenantId`
- `agentId`
- `workloadClass`
- `quotaWindowId`
- `decisionId`

核心 state：

- `ScenarioRun`：一次可重播執行。
- `AgentWorkItem`：待處理 agent 工作。
- `AdmissionDecision`：admission outcome 與 reason。
- `QueueTicket`：排隊狀態與 position。
- `ExecutionAttempt`：provider/stub 執行嘗試。
- `QuotaWindowState`：各 quota unit 的消耗與 refill。
- `MetricSnapshot`：指定時間點的 counters/gauges/histograms。

存取路徑：

- by `runId` replay timeline
- by `workItemId` 查完整 lifecycle
- by `workloadClass` 彙總 SLI
- by `tenantId` 檢查 fairness
- by `quotaWindowId` 檢查 capacity protection

## 8. Class And Boundary Design

預期 project 分層：

```text
AndrewDemo.AgentRateLimit.Abstract
  ScenarioManifest
  AgentWorkItem
  AdmissionDecision
  HarnessEvent
  MetricSnapshot

AndrewDemo.AgentRateLimit.Core
  HarnessRunner
  AdmissionController
  QuotaLedger
  QueueScheduler
  MetricRecorder
  AssertionEngine

AndrewDemo.AgentRateLimit.Simulation
  ManualHarnessClock
  TrafficGenerator
  ProviderStub

AndrewDemo.AgentRateLimit.Cli
  Scenario manifest loader
  Local run command
  JSON/CSV output
```

邊界規則：

- `HarnessRunner` 只依賴 abstract contract 與 clock，不依賴特定 provider SDK。
- `AdmissionController` 回傳 structured result，不寫 log-only decision。
- `QueueScheduler` 不呼叫 provider。
- `ExecutionBridge` 不做 admission decision。
- `MetricRecorder` 接收 domain event，不從 console log 反推結果。

## 9. Scenario And Flow Mapping

Baseline flow：

1. CLI 載入 scenario manifest。
2. 建立 manual clock、quota ledger、policy、provider stub。
3. Traffic generator 在 tick 0 產生 10 個 interactive work item。
4. Admission controller 檢查 quota，前 N 個 accepted，其餘 queued。
5. Clock fast-forward 到下一個 quota refill。
6. Queue scheduler dispatch queued work。
7. Metric recorder 輸出 accepted、queued、executed、wait ticks。
8. Assertion engine 驗證 provider 429 為 0、p95 wait 在目標內、replay deterministic。

Stress flow：

1. interactive、background、bulk 同時 burst。
2. interactive 使用 reserved capacity。
3. background 進入 bounded queue。
4. bulk 在 queue 滿或 predicted wait 超標時 early reject。
5. 若 provider stub 回傳 retry-after，harness 轉成 retryScheduled event，且 retry budget 不可無限膨脹。
6. Metrics 必須能看出 high-priority 是否被保護，以及 low-priority 是否 starvation。

## 10. Metrics And Theory Limits

必備 metrics：

- `work.arrived.count`
- `work.accepted.count`
- `work.queued.count`
- `work.rejected.count`
- `work.degraded.count`
- `work.executed.count`
- `work.abandoned.count`
- `provider.429.count`
- `queue.length`
- `queue.wait.ticks.p50/p95/p99`
- `quota.utilization`
- `fairness.violation.count`
- `retry.scheduled.count`

第一版 theory limit：

- 若 arrival rate 長期大於 execution/refill capacity，queue wait 必然無界成長；policy 正確行為是 early reject 或 degrade，不是讓 queue 無限累積。
- 若 high-priority reservation 設太高，low-priority 可能 starvation；harness 必須讓 starvation 成為可見 metric。
- 若 service amount 選錯，例如只算 request 不算 token，harness 可能通過測試但無法保護真實 provider。

## 11. POC

最小 POC 不需要真實 provider。

第一個可執行切片：

- YAML 或 JSON scenario manifest。
- Manual forward-only clock。
- Fixed-window quota ledger。
- Bounded queue。
- Provider stub。
- JSON summary output。
- xUnit scenario tests。

成功條件：

- 同一 scenario 連跑兩次得到相同 timeline。
- burst scenario 能產生 accepted、queued、rejected 三種 outcome。
- provider 429 可被 stub 模擬，但預設 policy 應讓 429 count 維持 0。
- metrics 足以回答「哪個 workload 被保護、哪個 workload 被犧牲、原因是什麼」。

## 12. Evaluation

第一版不追求最聰明的 rate-limit algorithm，而是追求：

- contract 穩定
- 時間可控
- 行為可重播
- overload outcome 明確
- metrics 可診斷
- policy 可替換

等 fixed-window + bounded queue + reservation 能被 spec/testcases 穩定驗證後，再評估 token bucket、sliding window、adaptive concurrency 或 provider-specific policy。

## 13. Risks And Refactor Triggers

需要重看設計的訊號：

- 新 policy 需要改 `HarnessRunner` 主流程。
- metrics 只能從 log parsing 得到。
- provider adapter 開始直接決定 admission outcome。
- scenario 需要 realtime sleep 才能通過。
- high-priority/low-priority 的 SLO 無法從 summary 看出差異。
- `provider.429.count` 被視為正常 throttling 手段。
- `ScenarioManifest` 開始塞入 provider SDK 細節。
