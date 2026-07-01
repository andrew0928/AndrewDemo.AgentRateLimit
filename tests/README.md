# Tests

此目錄保存對齊 `spec/testcases` 的 xUnit 驗收測試。

## Current Test Project

- `AndrewDemo.AgentRateLimit.Core.Tests`

## Covered Test Types

- Contract tests：驗證 request validation、decision result、reason、status output。
- Scenario tests：驗證 5h/7d rolling window、extra pool、idempotency、multi-user isolation。
- Persistence tests：驗證 restart 後 status 與 audit trail 仍可查詢。
- Concurrency tests：驗證同 subscription 同時 consume 不超扣。
- Reconciliation tests：驗證 audit trail 可重建期間 credit 變化。

## Test Rules

- 不使用 realtime sleep。
- 時間一律透過 controllable clock 推進。
- 不用 console log 作為 assertion source。
- 每個 policy change 至少補一個 Given/When/Then testcase。
