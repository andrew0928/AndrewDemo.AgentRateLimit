# Subscription Credit Local Seed Data

> 狀態：active
> 日期：2026-07-02
> 範圍：`AndrewDemo.AgentRateLimit.DatabaseInit` 與 docker compose local environment 會建立的本地測試資料。

## Primary Manual Test Subscription

| Purpose | Token | Subscription | User | 5h remaining | 7d remaining | Extra pool |
|---|---|---|---|---:|---:|---:|
| 前述手動測試案例 | `0123456789ABCDEFFEDCBA9876543210` | `sub-a` | `user-a` | 100 | 1000 | 1000 |

## Additional Test Combinations

| # | Purpose | Token | Subscription | 5h remaining | 7d remaining | Extra pool | Status |
|---:|---|---|---|---:|---:|---:|---|
| 1 | API-AUTH-005 token scope mapping | `11111111111111111111111111111111` | `sub-api-auth-scope` | 7 | 900 | 0 | active |
| 2 | insufficient 5h without extra pool | `22222222222222222222222222222222` | `sub-no-extra-5h-empty` | 0 | 1000 | 0 | active |
| 3 | extra pool authorization required | `33333333333333333333333333333333` | `sub-extra-authorization-required` | 0 | 0 | 1000 | active |
| 4 | authorized extra pool covers 5h shortage | `44444444444444444444444444444444` | `sub-extra-authorized-5h` | 0 | 1000 | 1000 | active |
| 5 | authorized extra pool covers 7d shortage | `55555555555555555555555555555555` | `sub-extra-authorized-7d` | 100 | 0 | 1000 | active |
| 6 | unknown actual exceeds 5h without authorization | `66666666666666666666666666666666` | `sub-overrun-5h` | 100 | 1000 | 1000 | active |
| 7 | unknown actual exceeds 7d without authorization | `77777777777777777777777777777777` | `sub-overrun-7d` | 100 | 10 | 1000 | active |
| 8 | expired 5h lazy renew sample | `88888888888888888888888888888888` | `sub-expired-5h` | 0 | 1000 | 0 | active |
| 9 | expired 7d lazy renew sample | `99999999999999999999999999999999` | `sub-expired-7d` | 100 | 0 | 0 | active |
| 10 | disabled subscription rejection sample | `AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA` | `sub-disabled` | 100 | 1000 | 0 | disabled |

All tokens are 32 uppercase hexadecimal characters without hyphen and are stored in `subscription_access_token`.

## Console Output

`AndrewDemo.AgentRateLimit.DatabaseInit` 會在 seed 完成後把 local test access tokens 印到 console，方便用 `docker compose logs database-init` 或 `docker compose up` 直接複製 `Authorization: Bearer ...`。

這些 token 是本機測試資料，不是 production token provisioning workflow。若未來 database-init 被改成 production bootstrap，不能把真實 credential 印到 log。
