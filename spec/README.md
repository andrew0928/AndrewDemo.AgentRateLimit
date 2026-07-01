# Spec

本目錄保存 repo 的行為規範。`spec/testcases` 是第一層驗收來源，implementation、unit tests、CLI smoke run 都應對齊這裡。

## Current V1

- [Subscription Credit Rate Limit V1](./subscription-credit-rate-limit-v1.md)
- [Subscription Credit Rate Limit V1 Coverage Decision Table](./coverage/subscription-credit-rate-limit-v1-decision-table.md)

## Earlier Exploration

- [Agent Rate Limit Harness](./testcases/agent-rate-limit-harness.md) 是 repo 初始化時的 generic harness 探索稿，不是目前要 review/freeze 的 V1 billing behavior spec。

## 規範原則

- 用 Given/When/Then 描述可驗證行為。
- 指標與觀測點也是規格，不只是 debug output。
- 若行為依賴時間，必須明確寫出 occurrence time、decision time、execution time。
- 若行為依賴 quota，必須明確寫出 service amount unit。
