# Tests

此目錄目前尚未建立測試專案。實作開始後，測試應直接對齊 `spec/testcases`。

## Planned Test Types

- Contract tests：驗證 manifest validation、decision model、metric model。
- Scenario tests：驗證 burst、quota exhaustion、priority reservation、fairness、retry。
- Replay tests：同一 scenario 連跑兩次，normalized timeline 必須一致。
- CLI smoke tests：執行 sample scenario，檢查 JSON/CSV summary。

## Test Rules

- 不使用 realtime sleep。
- 時間一律透過 controllable clock 推進。
- 不用 console log 作為 assertion source。
- 每個 policy change 至少補一個 Given/When/Then testcase。
