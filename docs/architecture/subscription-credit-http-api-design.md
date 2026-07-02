# Subscription Credit HTTP API Design

> 狀態：draft-for-review  
> 日期：2026-07-02  
> 範圍：把已凍結的 `ISubscriptionCreditUsageService` 正式暴露成 HTTP REST API。本文只定義 API contract、access token authentication、subscription scoping、response/error semantics；不定義 deployment、gateway、token provisioning UI 或 production key rotation workflow。

## 1. Problem Analysis

這個 API 要保護的是 money-like service capability：subscription credit、extra pool、consume record 與後續 infra cost reconciliation。

主要風險：

- 未授權 caller 直接消費 subscription credit。
- caller 偽造或切換 `subscriptionId`，跨 subscription 使用額度。
- token 被偷後可被 replay，直到 token 被刪除或輪替。
- API handler 忘記套 subscription scope，讓 body 中的 subscription id 汙染 decision。
- rejected / invalid / conflict 被 HTTP status 混淆，導致 client 無法依 `.Abstract` decision result 做一致處理。

V1 security quality requirement：

> Every HTTP usage request must authenticate an access token first, resolve exactly one subscription id from storage, and run `DecideAsync` / `ConsumeAsync` only inside that subscription scope.

## 2. Trust Boundary And Threat Model

Trust boundary：

- trusted：API host、Core service、SQLite database。
- untrusted：HTTP client、request body、headers except after validation。
- scoped credential：access token 只代表一個 subscription scope，不代表 user login session、admin role 或 billing identity。

Threat model：

| Threat | V1 control |
|---|---|
| missing token | reject before service call |
| malformed token | reject before service call |
| token not found | reject before service call |
| caller sends another subscription id in body | body must not contain subscription id; host ignores/rejects any subscription-scope override |
| token replay | V1 accepts replay while token exists; revoke by deleting token row |
| token leakage | blast radius is the mapped subscription; rotate by inserting a new UUID token and deleting the old row |

## 3. Database Schema Delta

新增一張 access token management table。

```text
subscription_access_token
```

| Column | Type | Responsibility |
|---|---|---|
| `token` | UUID | access token primary identifier |
| `subscription_id` | subscription id | token maps to exactly one subscription |

Rules：

- `token` 必須唯一。
- `subscription_id` 必須對應到一個 subscription scope。
- V1 table 只允許這兩個欄位，不加入 `created_at`、`expires_at`、`status`、`actor`、`memo`。
- revoke / rotation 的最小操作是 delete old token row and insert new token row。
- API runtime 必須只用 token table resolve subscription scope；request body 不可指定或覆蓋 subscription id。

SQLite implementation note：

- SQLite 沒有 native UUID type 時，implementation 可用 canonical text 儲存 UUID，但 API/schema contract 仍把它視為 UUID。
- header credential 使用 32 uppercase hex UUID without hyphen；storage layer 可 normalize 成 UUID canonical value 後查詢。

## 4. Token Format

HTTP Authorization header：

```http
Authorization: Bearer 0123456789ABCDEFFEDCBA9876543210
```

Credential rules：

- scheme：`Bearer`
- token credential：UUID encoded as 32 uppercase hexadecimal characters
- token credential 不包含 hyphen
- token credential 不包含 prefix、suffix、空白或 quote
- token credential 長度必須是 32
- allowed characters：`0-9`、`A-F`

Invalid examples：

```http
Authorization: Bearer 01234567-89AB-CDEF-FEDC-BA9876543210
Authorization: Bearer 0123456789abcdeffedcba9876543210
Authorization: Token 0123456789ABCDEFFEDCBA9876543210
Authorization: Bearer 0123456789ABCDEFFEDCBA9876543210 extra
```

V1 不使用 signed token 或 JWT，因為 token 本身不攜帶 claims。它只是 database lookup key；授權 scope 由 server-side table 決定。

## 5. Authentication And Authorization Flow

```text
HTTP request
  -> parse Authorization header
  -> validate Bearer token format
  -> lookup subscription_access_token by token
  -> resolve subscription_id
  -> load subscription_account for subscription user/status/state
  -> build UsageCreditRequest from body + resolved subscription scope
  -> call ISubscriptionCreditUsageService
  -> return UsageCreditDecision JSON
```

Authorization model：

- subject：access token。
- resource scope：resolved `subscription_id`。
- allowed actions：`decide` and `consume` for that subscription.
- denied by default：no token, invalid token format, token not found, or subscription scope override attempt.

The API host must not trust `userId` or `subscriptionId` from HTTP body. If an implementation keeps these fields in DTOs for compatibility, the handler must reject non-null values or ignore them before calling Core.

## 6. API Surface

Base path：

```text
/v1/subscription-credit
```

Endpoints：

| Method | Path | Service method | Auth required | Purpose |
|---|---|---|---|---|
| `POST` | `/v1/subscription-credit/decide` | `DecideAsync` | yes | admission / preview decision without consume evidence |
| `POST` | `/v1/subscription-credit/consume` | `ConsumeAsync` | yes | actual settlement; may append consume record |

No public token management endpoints in V1. Token creation, deletion, and rotation are admin/storage operations outside this HTTP API spec.

## 7. Request Contract

### Decide Request

```json
{
  "requestedCredits": "1",
  "creditAmountMode": "minimum-available-balance",
  "extraPoolAuthorization": "not-authorized",
  "idempotencyKey": "agent-run-2026-07-02-001",
  "correlationId": "corr-agent-run-2026-07-02-001",
  "source": "agent-runtime"
}
```

### Consume Request

```json
{
  "requestedCredits": "120",
  "creditAmountMode": "exact-credits",
  "extraPoolAuthorization": "not-authorized",
  "idempotencyKey": "agent-run-2026-07-02-001",
  "correlationId": "corr-agent-run-2026-07-02-001-settlement",
  "source": "agent-runtime"
}
```

Field rules：

| Field | Required | Meaning |
|---|---:|---|
| `requestedCredits` | yes | raw string input; preserves fractional / zero / negative invalid cases for `.Abstract` decision |
| `creditAmountMode` | yes | `exact-credits` or `minimum-available-balance` |
| `extraPoolAuthorization` | yes | `not-authorized` or `authorized` |
| `idempotencyKey` | yes | caller-provided operation key; required by Core consume idempotency |
| `correlationId` | yes | request correlation for logs and consume evidence |
| `source` | yes | caller/source label, for example `agent-runtime`, `cli`, `scenario-runner` |

Forbidden body fields：

- `subscriptionId`
- `userId`
- `accessToken`

If any forbidden field is present, API should fail before calling Core with `subscription-scope-overridden`.

## 8. Response Contract

Both endpoints return `UsageCreditDecision` shaped JSON.

Example accepted consume response：

```json
{
  "mode": "consume",
  "creditAmountMode": "exact-credits",
  "result": "accepted",
  "requestedCredits": 120,
  "creditsCoveredBySubscriptionAllowance": 100,
  "creditsCoveredByExtraPool": 0,
  "creditsAbsorbedBySystem": 20,
  "fiveHourWindowAfterDecision": {
    "kind": "five-hours",
    "limit": 100,
    "used": 120,
    "remaining": 0,
    "nextResetTimeUtc": "2026-07-02T04:01:23Z"
  },
  "sevenDayWindowAfterDecision": {
    "kind": "seven-days",
    "limit": 1000,
    "used": 120,
    "remaining": 880,
    "nextResetTimeUtc": "2026-07-08T23:01:23Z"
  },
  "extraPoolRemainingAfterDecision": 1000,
  "rejectionReason": null,
  "invalidReason": null,
  "conflictReason": null,
  "auditReference": "consume-...",
  "decisionTimeUtc": "2026-07-02T00:00:00Z"
}
```

Enum values use the lower-kebab names already documented in `.Abstract` design.

## 9. HTTP Status Semantics

HTTP status is for transport/auth/protocol outcome. `UsageCreditDecision.result` is for subscription-credit business outcome.

| Case | HTTP status | Body |
|---|---:|---|
| valid token, service returns accepted | `200` | `UsageCreditDecision` |
| valid token, service returns rejected | `200` | `UsageCreditDecision` |
| valid token, service returns invalid | `200` | `UsageCreditDecision` |
| valid token, service returns conflict | `200` | `UsageCreditDecision` |
| missing Authorization header | `401` | API error |
| malformed Authorization header | `401` | API error |
| token format invalid | `401` | API error |
| token not found | `401` | API error |
| malformed JSON | `400` | API error |
| forbidden scope field in body | `400` | API error |
| unsupported media type | `415` | API error |

API error shape：

```json
{
  "error": "invalid-access-token",
  "message": "Authorization token is missing, malformed, or not recognized.",
  "correlationId": "corr-..."
}
```

Required API error codes：

| Error | Meaning |
|---|---|
| `missing-authorization` | header missing |
| `invalid-authorization-scheme` | scheme is not `Bearer` |
| `invalid-access-token-format` | token is not 32 uppercase hex without hyphen |
| `invalid-access-token` | token not found |
| `malformed-json` | request body cannot be parsed |
| `subscription-scope-overridden` | body attempted to provide `subscriptionId`, `userId`, or token |
| `unsupported-media-type` | request is not JSON |

## 10. Security Enforcement Boundaries

Enforcement must happen at three boundaries：

1. HTTP middleware / filter：parse and validate `Authorization` header.
2. API handler：reject forbidden scope fields, map request DTO to `UsageCreditRequest`.
3. Core call boundary：construct `UserId` / `SubscriptionId` only from token-resolved subscription account, never from client body.

Handler pseudo flow：

```text
principal = authenticate bearer token
account = load subscription_account by principal.subscription_id
request = map body to UsageCreditRequest(
    userId = account.user_id,
    subscriptionId = principal.subscription_id,
    requestedCredits = body.requestedCredits,
    creditAmountMode = body.creditAmountMode,
    extraPoolAuthorization = body.extraPoolAuthorization,
    idempotencyKey = body.idempotencyKey,
    correlationId = body.correlationId,
    source = body.source)
decision = usageService.DecideAsync or ConsumeAsync(request)
return 200 + decision
```

## 11. C4 Boundary

```mermaid
flowchart LR
    Client["HTTP client"] --> Api["Subscription Credit HTTP API"]
    Api --> Auth["Bearer token authentication"]
    Auth --> TokenTable[("subscription_access_token")]
    Auth --> Account[("subscription_account")]
    Api --> Core["ISubscriptionCreditUsageService"]
    Core --> Store[("subscription_account / consume / extra pool records")]
```

## 12. Sequence

```mermaid
sequenceDiagram
    participant Client
    participant API as HTTP API
    participant Auth as Token Auth
    participant DB as SQLite
    participant Core as ISubscriptionCreditUsageService

    Client->>API: POST /v1/subscription-credit/consume
    API->>Auth: parse Authorization header
    Auth->>DB: lookup subscription_access_token(token)
    DB-->>Auth: subscription_id
    API->>DB: load subscription_account(subscription_id)
    DB-->>API: user_id and current state
    API->>Core: ConsumeAsync(mapped request)
    Core->>DB: append consume record and update account
    Core-->>API: UsageCreditDecision
    API-->>Client: 200 UsageCreditDecision
```

## 13. POC / Testcase Plan

First implementation slice should add HTTP-level tests before code is considered complete：

| Testcase | Given | Expected |
|---|---|---|
| API-AUTH-001 | missing Authorization header | `401 missing-authorization` |
| API-AUTH-002 | token contains hyphen | `401 invalid-access-token-format` |
| API-AUTH-003 | token lowercase | `401 invalid-access-token-format` |
| API-AUTH-004 | token not found | `401 invalid-access-token` |
| API-AUTH-005 | valid token maps to `sub-a` | request operates on `sub-a` |
| API-SCOPE-001 | body contains `subscriptionId` | `400 subscription-scope-overridden` |
| API-SCOPE-002 | token maps to `sub-a`, body attempts `sub-b` | `400 subscription-scope-overridden`; no Core call |
| API-USAGE-001 | valid decide request with minimum balance 1 | returns `UsageCreditDecision.result = accepted` |
| API-USAGE-002 | valid consume request over allowance without extra authorization | returns system absorbed credits |
| API-USAGE-003 | valid token but insufficient subscription allowance | HTTP `200`, decision `rejected` |

## 14. Out Of Scope For V1

- token create/list/delete HTTP admin endpoints
- token expiry column
- token status column
- per-token role/permission column
- JWT / signed token
- OAuth/OIDC user login
- request signing or nonce replay protection
- IP allowlist
- API gateway integration
- multi-subscription token
- user-selected subscription switching

## 15. Risks And Refactor Triggers

This design is intentionally minimal. Revisit it when any of these become true：

- token lifecycle needs audit beyond insert/delete rows
- stolen-token replay becomes unacceptable without expiry or nonce
- one caller needs access to multiple subscriptions
- API needs admin/token-management endpoints
- external partners require delegated scopes, roles, or environment restrictions
- token revocation must be observable historically instead of just deleting the row
- public internet exposure requires stronger operational controls such as short-lived credentials, gateway policy, rate limiting, or request signing

## 16. Review Points Before Implementation

請先確認這些 API contract 細節：

1. Authorization header canonical form 是否固定為 `Authorization: Bearer {UUID32UPPER}`，且 token credential 一律要求 uppercase / no hyphen。
2. service-level decision 是否維持 HTTP `200` 回傳 `UsageCreditDecision`，包含 `rejected`、`invalid`、`conflict`；只有 auth / protocol / malformed JSON 使用 HTTP error status。
3. V1 是否不提供 public token management endpoints，token 建立、刪除、輪替都先視為 admin/storage operation。
4. HTTP body 是否明確禁止 `subscriptionId` / `userId`，所有 scope 都由 access token row resolve。
