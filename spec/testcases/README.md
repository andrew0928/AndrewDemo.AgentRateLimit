# Testcases

## 概觀

- [Subscription Credit Rate Limit V1](./subscription-credit-rate-limit-v1.md)
- [Agent Rate Limit Harness](./agent-rate-limit-harness.md)

## 格式

每個 test case 使用：

- Given：外部條件、scenario、quota、workload、clock state。
- When：arrival、admission、dispatch、fast-forward、provider response。
- Then：decision、queue state、metric、assertion。
- And：補充不可省略的 side effect 或 invariant。
