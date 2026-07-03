# Tests

測試直接對齊 `spec/testcases`。

## Current Layout

```text
tests/
└── AndrewDemo.AgentRateLimit.Core.Tests/
    ├── TestSupport/                      # ManualTimeProvider（forward-only）、SQLite temp-db fixture
    ├── SubscriptionCreditRateLimitV1/    # 1:1 對齊 spec/testcases 的 TC-* 驗收測試 + edge cases
    └── SmokeTests.cs                     # 端到端 smoke
```

## Test Rules

- 不使用 realtime sleep；時間一律透過 controllable clock（`ManualTimeProvider`）推進。
- 不用 console log 作為 assertion source。
- 每個 policy change 至少補一個 Given/When/Then testcase。
- Restart 行為以同一 SQLite 檔案重建 service instance 驗證（`SubscriptionCreditServiceFixture.Restart`）。
- 每個 test 一個獨立 fixture 與獨立 temp database，測試之間無共享狀態。
