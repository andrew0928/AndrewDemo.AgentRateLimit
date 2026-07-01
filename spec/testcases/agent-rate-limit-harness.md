# Agent Rate Limit Harness 測試案例

## 狀態

- phase: 0
- status: draft-for-review
- 日期：2026-07-01

## Scenario Manifest

### TC-MANIFEST-001 載入最小 scenario

- Given: scenario manifest 內含 `scenarioId`、`clock.startUtc`、`providerQuota`、`workloadClasses`、`traffic`
- When: harness 載入 manifest
- Then: 建立 `ScenarioRun`
- And: `runId` 必須唯一
- And: `HarnessClock` 從 `clock.startUtc` 開始

### TC-MANIFEST-002 未定義 workload class 應失敗

- Given: traffic item 指定 `workloadClass=missing`
- When: harness 載入 manifest
- Then: validation 失敗
- And: 不可進入 partial run

### TC-MANIFEST-003 quota unit 必須明確

- Given: provider quota 未指定 service amount unit
- When: harness 載入 manifest
- Then: validation 失敗
- And: error message 必須指出缺少 quota unit

## Controllable Time

### TC-TIME-001 fast-forward 觸發 quota refill

- Given: fixed window quota 每 60 ticks refill
- And: 目前 clock 在 tick 0
- When: clock fast-forward 到 tick 60
- Then: `QuotaLedger` 產生 refill event
- And: queued work 可重新被 scheduler 評估

### TC-TIME-002 測試不得依賴 realtime sleep

- Given: scenario 需要等待 retry-after 30 ticks
- When: test 執行 retry path
- Then: 使用 controllable clock fast-forward
- And: test 不應等待 30 秒 realtime

### TC-TIME-003 occurrence time 與 decision time 分離

- Given: work item occurrence tick 為 10
- And: harness 在 tick 15 才收到該 item
- When: admission controller 做決策
- Then: metric 必須保留 occurrence tick 與 decision tick
- And: queue wait 不可只用 receive delay 混算

## Admission Control

### TC-ADM-001 quota 足夠時立即 accepted

- Given: provider quota 尚可執行 5 個 request
- And: queue 為空
- When: interactive work item arrival
- Then: admission decision 為 `accepted`
- And: quota usage 增加 1
- And: metric `work.accepted.count` 增加 1

### TC-ADM-002 quota 不足但 queue 有容量時 queued

- Given: provider quota 已耗盡
- And: bounded queue 尚有容量
- When: background work item arrival
- Then: admission decision 為 `queued`
- And: 回傳 queue position
- And: metric `work.queued.count` 增加 1

### TC-ADM-003 quota 不足且 queue 滿時 early rejected

- Given: provider quota 已耗盡
- And: bounded queue 已滿
- When: bulk work item arrival
- Then: admission decision 為 `rejected`
- And: rejection reason 為 `queue-full`
- And: metric `work.rejected.count` 增加 1

### TC-ADM-004 over-limit work 不可直接打到 provider

- Given: provider quota 已耗盡
- When: work item arrival
- Then: harness 不呼叫 provider stub
- And: `provider.429.count` 不應增加

### TC-ADM-005 duplicate idempotency key 不重複佔位

- Given: queue 內已有 `idempotencyKey=task-1`
- When: 同 tenant 送入相同 `idempotencyKey=task-1`
- Then: admission decision 指向既有 queue ticket
- And: queue length 不增加

## Priority And Fairness

### TC-QOS-001 high-priority 保留容量

- Given: provider quota 每 window 可執行 10 個 request
- And: policy 保留 4 個給 `interactive`
- When: background burst 已消耗 6 個 request
- And: interactive work item arrival
- Then: interactive decision 為 `accepted`
- And: background 不得使用 interactive reserved capacity

### TC-QOS-002 low-priority 不可永久 starvation

- Given: interactive reservation 在某個 window 沒有使用完
- And: low-priority queue 有等待項目
- When: policy 允許借用 idle reservation
- Then: low-priority queued item 可被 dispatch
- And: metric 不產生 starvation violation

### TC-QOS-003 單一 tenant 不可吃掉所有 deferred capacity

- Given: tenant A 送入 100 個 background work item
- And: tenant B 送入 1 個 background work item
- When: queue scheduler dispatch queued work
- Then: tenant B 的 work item 不可永遠排在 tenant A 之後
- And: fairness metric 必須能反映 per-tenant dispatch 分布

## Execution And Retry

### TC-EXEC-001 accepted work 透過 execution bridge 執行

- Given: work item admission decision 為 `accepted`
- When: scheduler dispatch work
- Then: provider stub 收到 structured execution request
- And: metric `work.executed.count` 在 provider accepted 後增加

### TC-EXEC-002 provider retry-after 轉為 retryScheduled

- Given: provider stub 回傳 retry-after 30 ticks
- When: execution bridge 處理 response
- Then: harness 產生 `retryScheduled` event
- And: retry 不可立刻 busy loop
- And: retry budget 增加 1

### TC-EXEC-003 retry budget 用完後 rejected

- Given: work item 已達最大 retry 次數
- When: provider 再次要求 retry-after
- Then: admission outcome 轉為 `rejected`
- And: reason 為 `retry-budget-exhausted`

## Metrics And Assertions

### TC-METRIC-001 summary 必須區分 accepted queued rejected executed

- Given: scenario 同時產生 accepted、queued、rejected、executed work
- When: run 完成
- Then: summary 分別輸出四種 count
- And: 不可以只輸出 total request

### TC-METRIC-002 queue wait percentile 可計算

- Given: 至少 100 個 queued work item 完成 execution
- When: metric recorder 產生 summary
- Then: summary 包含 `queue.wait.ticks.p50`
- And: summary 包含 `queue.wait.ticks.p95`
- And: summary 包含 `queue.wait.ticks.p99`

### TC-METRIC-003 provider 429 預設應為 0

- Given: policy 目標是保護 provider quota
- When: scenario run 完成
- Then: `provider.429.count = 0`
- And: 若大於 0，assertion engine 應標示 scenario failed

### TC-METRIC-004 replay deterministic

- Given: 相同 scenario manifest
- When: harness 連續執行兩次
- Then: normalized event timeline 相同
- And: summary metrics 相同

## Host Boundary

### TC-HOST-001 provider adapter 不可決定 admission

- Given: provider adapter 實作存在
- When: work item arrival
- Then: admission decision 由 core admission controller 產生
- And: provider adapter 只接收已 dispatch 的 execution request

### TC-HOST-002 scenario policy 不寫死在 runner 主流程

- Given: 新增一個 workload class
- When: scenario manifest 註冊該 class 與 policy config
- Then: `HarnessRunner` 主流程不需要新增 class-name if/else
- And: 差異應由 policy implementation 或 manifest config 表達
