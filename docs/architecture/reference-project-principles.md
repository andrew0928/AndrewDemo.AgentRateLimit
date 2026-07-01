# 參考專案設計原則

> 狀態：初始化萃取  
> 日期：2026-07-01  
> 範圍：只保留 `mud-agents` 與 `andrewshop.apidemo` 對本 repo 有用的架構原則，不複製兩個專案的 domain model。

## mud-agents 萃取

### Harness 與 host code 的責任切分

`mud-agents` 的 `PlayerClient` 原則是：harness/workspace 主導 agent 思考，host code 主導橋接與結構化控制。

套用到本 repo：

- rate-limit harness 不決定 agent 的任務策略或 plan 優先順序。
- harness code 只處理 work item envelope、admission decision、queue state、provider execution bridge、metric emission。
- workload priority、scenario traffic、quota model、degrade/retry policy 應由 scenario manifest 或 policy config 表達。

### Structured operation 是唯一正式輸出

`mud-agents` 將 agent decision 收斂到 canonical operation，再由 runtime 執行。

套用到本 repo：

- 限流決策必須是 structured `AdmissionDecision`，不得用自由文字判斷是否 accepted/queued/rejected。
- provider call 必須透過 `ExecutionBridge` 或 provider stub；scenario 不可直接繞過 harness 改 metrics。
- transcript 與 metric event 是正式 evidence，而不是 debug log。

### Trigger 是外部時機，不是 host-side 思考流程

`mud-agents` 的 startup、notification、sense-result、idle 都代表外部時機；是否行動由 agent/harness context 決定。

套用到本 repo：

- traffic arrival、quota refill、retry-after、abandonment、provider completion 都是 harness event。
- scheduler 只排序事件與保證 deterministic replay，不把 business strategy 寫死在 loop 裡。

## andrewshop.apidemo 萃取

### Abstract -> Core -> Host -> Extension

`andrewshop.apidemo` 的穩定線是：

```text
Abstract contracts -> Core orchestration -> host composition root -> extension/runtime surface
```

套用到本 repo：

- `Abstract`：scenario、quota、policy、decision、metric 的穩定 contract。
- `Core`：runner、admission controller、quota ledger、queue scheduler、metric recorder。
- `Host/Cli`：讀取 scenario manifest、組裝 policy/provider/clock、輸出 summary。
- `Provider Extension`：不同 provider 或 quota source 的 adapter，不改核心 harness flow。

### Manifest 表達變體，不把變體塞進主流程

`andrewshop.apidemo` 用 manifest 解析 shop runtime 與 enabled rules。

套用到本 repo：

- scenario manifest 應表達 provider quota、workload class、reservation、queue policy、traffic pattern、assertion。
- core runner 不應因特定 provider、特定 agent、特定 workload 名稱而出現 if/else 分支。

### Spec/testcases 是驗收源頭

`andrewshop.apidemo` 用 `spec/testcases` 定義行為驗收，再讓 test suite 與 smoke path 對齊。

套用到本 repo：

- 每個 policy 行為都要先能寫成 Given/When/Then。
- metrics acceptance 也是 spec 的一部分，例如 rejected count、queue wait、fairness violation。
- 若 scenario 無法被 deterministic replay，就不算 harness 行為已定義完成。

### TimeProvider 類設計是必要基礎

限流、quota window、retry-after、queue wait 都依賴時間。參考 `andrewshop.apidemo` 的 time provider 思路，本 repo 必須把時間變成可替換 dependency。

套用到本 repo：

- production clock 與 simulation clock 分離。
- POC 和測試使用 forward-only controllable time。
- fast-forward 跨過 quota refill 或 scheduled retry 時，必須觸發應發生的事件。
