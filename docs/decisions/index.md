# Decisions Index

> 狀態：active index  
> 日期：2026-07-01  
> 範圍：整理 `docs/decisions/` 的閱讀順序、決策狀態與 supersede 關係。本文不取代 decision record 本身；若內容衝突，以各 decision record 內文與狀態為準。

## 2026-07-01 Decision Order

| Order | Decision | Status | Role | Relationship |
|---:|---|---|---|---|
| 1 | [AgentRateLimit 採 contract-first harness 與可控時間模型](./2026-07-01-agent-rate-limit-harness-contract-first.md) | accepted | repo 初始化與工作方式基線 | 後續 subscription credit 設計都應遵守 contract-first、controllable time、spec/testcases-first 的原則 |
| 2 | [V1 規格只凍結外部可觀測的 subscription credit rate limit 行為](./2026-07-01-v1-spec-observable-subscription-credit-rate-limit.md) | superseded | subscription credit V1 第一版外部行為邊界 | 外部可觀測行為邊界仍有效；其中 rolling-window 語意已由第 3 筆取代 |
| 3 | [subscription credit 採 lazy quota window lease，不採 per-usage rolling window](./2026-07-01-subscription-credit-lazy-quota-window-lease.md) | accepted for behavior; storage details partially superseded | subscription credit V1 window semantics 修正 | supersedes 第 2 筆的 5h / 7d rolling-window 假設；storage source-of-truth 由第 4 筆取代 |
| 4 | [subscription credit 以 consume record 作為唯一帳務 source of truth](./2026-07-01-subscription-credit-consume-record-source-of-truth.md) | accepted | minimal storage source of truth | supersedes 先前 schema draft 中為了重算 window state 拆出的額外 source-fact tables |
| 5 | [extra pool 必須 explicit authorization 後才可消耗](./2026-07-01-subscription-credit-extra-pool-explicit-authorization.md) | accepted | extra pool usage semantics | 修正 extra pool 不得在 settlement overrun 時被靜默消耗 |
| 6 | [subscription credit HTTP API 採 Bearer access token 綁定 subscription scope](./2026-07-02-subscription-credit-http-api-token-auth.md) | proposed | HTTP API hosting 與基本安全機制 | 新增 `subscription_access_token` table 與 token-resolved subscription scope；等待 review 後進入 implementation |

## Current Effective Decisions

- Repo 工作模型：採 contract-first harness、可控時間、`spec/testcases` 驗收先行。
- Subscription credit V1 邊界：仍先凍結外部可觀測行為，不在 V1 spec 中凍結 API route、database schema、transaction/locking strategy。
- Subscription credit window semantics：採 lazy quota window lease；長時間 idle 不發生背景 reset，下一次 admission / consume 才在過期時開新 5h / 7d lease。
- Subscription credit storage source of truth：append-only `subscription_consume_record` 是 consume source of truth；append-only `subscription_extra_pool_record` 是 extra pool supply/adjustment source of truth；time window state 是 `subscription_account` 內的 mutable admission control state。
- Extra pool usage：只有本次 operation 已 explicit authorization 時才可消耗 extra pool；未授權 settlement overrun 必須 system absorbed。
- Proposed HTTP API security baseline：所有 subscription credit HTTP API 先以 Bearer access token resolve subscription scope；request body 不可指定或覆蓋 subscription id。

## Superseded Notes

`2026-07-01-v1-spec-observable-subscription-credit-rate-limit.md` 的整體 spec-first 邊界仍可參考，但其 rolling-window 內容不再是有效 window semantics。後續文件、testcase、schema 與 implementation 應以 `2026-07-01-subscription-credit-lazy-quota-window-lease.md` 為準。

`2026-07-01-subscription-credit-lazy-quota-window-lease.md` 的 window 行為仍有效，但其早期 schema/rebuild 想法不再是有效 storage baseline。storage source-of-truth 應以 `2026-07-01-subscription-credit-consume-record-source-of-truth.md` 與 minimal schema design 為準。
