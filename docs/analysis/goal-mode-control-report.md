# Goal-mode 對照組 Session/Commit Report

## 來源與口徑

- Branch: `goal-mode`
- Commit range: `e60674f` -> `018fd9e`
- 主要 transcript: `/Users/andrew/.codex/sessions/2026/07/01/rollout-2026-07-01T14-30-51-019f1c5f-8b91-73c2-9679-04186c4f8ea4.jsonl`
- 變更量來源: `git show --stat` / `git show --numstat`
- 問/答字數: 可見 user/assistant 文字去除空白後估算，CJK 與英數皆以字元數計。
- 時間: Codex `task_complete.duration_ms`，包含 agent 推論、工具呼叫、build/test 等等待。
- Token: Codex transcript 的 `event_msg.token_count.last_token_usage`；`Reasoning` 另列 `reasoning_output_tokens`。
- `Turns`、token、churn、final LOC 等統一欄位定義見 [commit-effort-and-abstract-review-report.md](commit-effort-and-abstract-review-report.md#結論摘要欄位定義)。

## Commit-step 總表

| Step | Commit range | Commit time | Turns | 對話/作業時間 | 問字數 | 答字數 | Tokens | Reasoning |
|---|---|---:|---:|---:|---:|---:|---:|---:|
| G0 baseline spec | empty repo -> `e60674f` | 2026-07-01 20:45:59 +08:00 | 3 | 47m 01s | 531 | 5,592 | 2,556,145 | 11,147 |
| G1 one-shot implementation | `e60674f` -> `018fd9e` | 2026-07-01 21:09:17 +08:00 | 2 | 13m 02s | 33 | 4,577 | 6,316,045 | 9,995 |

## G0: 建立 baseline spec

### 對話摘錄

- User: 「替我初始化這個 repo... 使用我自己 (andrew) 的架構設計 skill... 參考 mud-agents / andrewshop.apidemo」
- User: 「替我準備第一版規格... 只描述外在能觀測的行為跟結果, 不要描述任何系統內部的實作跟設計」
- Agent: 先套用 `andrew-architecture-core`，再讀參考 repo；判斷 harness 難點在可控時間、可重播情境、可量測指標。

### Docs changes

- 14 files, +1,478/-0
- 建立 repo README、architecture design、reference project principles、roadmap。
- 建立 subscription credit v1 外部行為 spec、decision table、testcases。
- 建立 decisions，固定「observable behavior first」與 harness contract-first 方向。

### Code changes

- 1 file, +22
- 僅 `src/README.md`，還沒有 production implementation。

### Test changes

- 1 file, +17
- 僅 `tests/README.md`，測試仍是 spec/testcases 層。

### Other changes

- 1 file, +22
- `.gitignore`

### 步驟摘要

這一步把 repo 從空目錄整理成可被後續 agent 執行的規格場地。重點不是實作，而是把第一版 subscription credit rate limit 的外部行為、驗收案例、architecture guardrails 寫清楚，作為 goal-mode 一次 prompt 的固定輸入。

## G1: 一次 prompt 實作

### 對話摘錄

- User: 「use dotnet10 + sqlite to implement this」
- Agent: 依據現有 spec 建立 .NET solution、SQLite-backed core service、tests，並執行 restore/build/test/run smoke。

### Docs changes

- 3 files, +71/-24
- 補上 implementation 後的 README/roadmap/spec 對應資訊。

### Code changes

- 12 files, +1,498/-7
- 建立 .NET 10 solution。
- 建立 `Abstract` contract 與 SQLite-backed `Core` implementation。
- 包含 subscription decision/consume flow、storage、clock/testability support。

### Test changes

- 3 files, +480/-6
- 建立 xUnit tests，覆蓋主要 subscription credit rate limit 行為。

### Other changes

- 1 file, +4/-4
- `.gitignore` 調整。

### Verification

- `dotnet restore`
- `dotnet build`
- `dotnet test`
- CLI smoke/run check

### Commit 記錄補充

- Commit body 原先記錄 requested scope: file changes 18, line changes +2041/-39, time spend 11m56s。
- 實際 git stat: 19 files, +2053/-41。

### 步驟摘要

這是典型 goal-mode 對照組：在 baseline spec 已經存在的情況下，只給單一實作 prompt，讓 agent 自行展開 .NET/SQLite implementation 與測試。優點是快；風險是 interface DX、schema source-of-truth、decision table 是否完整，主要由 agent 自行推斷，沒有中間人工 review gate。

## 排除項目

`018fd9e` 後另有 commit message/cost-info 討論與截圖補充，屬於 session 成本資訊整理，不屬於 `e60674f` -> `018fd9e` 的產品變更步驟，因此未納入 G1 變更統計。
