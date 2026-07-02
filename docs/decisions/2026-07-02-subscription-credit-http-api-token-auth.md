# subscription credit HTTP API 採 Bearer access token 綁定 subscription scope

> 狀態：proposed  
> 日期：2026-07-02  
> 範圍：subscription credit rate limit 的 HTTP API hosting 與基本安全機制。

## Context

`ISubscriptionCreditUsageService` 已有 `.Abstract` contract 與 `.Core` implementation。下一步要把它暴露成正式 HTTP REST API，並且在 API boundary 加上基本安全機制。

使用者指定：

- database 需要 access token 管理表。
- table 只有兩個欄位：`token`，type `UUID`；`subscription_id`。
- 所有 HTTP API 都必須在 Authorization header 帶 token。
- token 使用 Bearer header，credential 為全大寫、不包含減號的 UUID。
- 後續 API 都按照 token 對應的 subscription id 操作。

## Decision

採用 server-side lookup token model：

```text
Authorization: Bearer {UUID32UPPER}
```

API host 先用 token 查 `subscription_access_token`，取得唯一 `subscription_id`，再用該 scope 建立 Core request。HTTP body 不可指定或覆蓋 `subscriptionId` / `userId`。

API V1 只暴露：

- `POST /v1/subscription-credit/decide`
- `POST /v1/subscription-credit/consume`

Token management 不在 V1 HTTP surface 內；create/delete/rotation 是 admin/storage operation。

## Consequences

- subscription scoping 集中在 API auth boundary，不依賴每個 caller 自行傳入正確 subscription id。
- token 本身不攜帶 claims，因此不需要 JWT/signature validation；授權狀態完全由 database row 決定。
- token 一旦外洩，在刪除該 row 前可被 replay；V1 接受這個風險，因為目前只要求基本安全機制。
- 未來若需要 expiry、status、actor、audit 或 role/scope，需要新增欄位或額外 token management/audit table，屆時應重新開 decision。

## References

- [Subscription Credit HTTP API Design](../architecture/subscription-credit-http-api-design.md)
