# Fable5 + Claude Code 對照組 Session/Commit Report

## 來源與口徑

- Branch: `fable5`
- Commit range: `e60674f` -> `f6920cf` -> `21f6f66`
- 主要 transcript: `/Users/andrew/.claude/projects/-Users-andrew-code-work-AndrewDemo-AgentRateLimit/234834cf-84fd-442d-9c19-7732194a39bb.jsonl`
- 前置 transcript: `/Users/andrew/.claude/projects/-Users-andrew-code-work-AndrewDemo-AgentRateLimit/28a65a4a-a574-44d1-b1dd-32529b5b8620.jsonl`
- 變更量來源: `git show --stat` / `git show --numstat`
- 問/答字數: 可見 human/assistant 文字去除空白後估算，CJK 與英數皆以字元數計。
- Claude token: 依 timestamp 區間加總唯一 assistant `message.id` 的 `message.usage`，包含 `input_tokens`、`cache_creation_input_tokens`、`cache_read_input_tokens`、`output_tokens`。
- Claude transcript 未提供可與 Codex `reasoning_output_tokens` 對齊的 reasoning token 欄位，因此本報告不列 reasoning。
- F1 的 commit timestamp 與最後回答之間跨越等待時間；因此同時列 wall interval 與 transcript `turn_duration` active time。

## 前置 session

前置 session `28a65a4a-a574-44d1-b1dd-32529b5b8620` 只做 Claude Code 模型切換與 repo 可見性確認，沒有 commit。

- Visible user chars: 0
- Visible assistant chars: 106
- Active time: 7.173s
- Tokens: 23,306
- Model: `claude-fable-5`
- 摘錄: 「Set model to Fable 5」後，assistant 回覆已看到 repo 與 `architect-mode` branch。

## Commit-step 總表

| Step | Commit range | Commit time | Turns | Wall interval | Active turn time | 問字數 | 答字數 | Tokens |
|---|---|---:|---:|---:|---:|---:|---:|---:|
| F0 branch/setup cleanup | `e60674f` -> `f6920cf` | 2026-07-03 01:28:28 +08:00 | 3 | 6m 31s | about 3m 00s | 145 | 2,773 | 1,219,258 |
| F1 one-shot implementation | `f6920cf` -> `21f6f66` | 2026-07-03 09:36:47 +08:00 | 2 | 8h 08m 44s | 52m 41s | 47 | 3,912 | 13,308,383 |

## F0: 建 branch 與 cleanup

### 對話摘錄

- User: 「based on main branch (first commit), create new branch: fable5」
- User: 「我不清楚這些檔案怎麼會跑進來... 我要乾淨的 main branch... 替我檢驗」
- User: 「switch to fable5 branch, and commit」
- Agent: 檢查 main/branch 狀態後，判定 untracked 檔案主要來自 nested `bin/obj` build outputs，修正 `.gitignore`。

### Token breakdown

| Model | Messages | Input | Cache creation | Cache read | Output | Total |
|---|---:|---:|---:|---:|---:|---:|
| `claude-fable-5` | 3 | 7,463 | 11,883 | 71,673 | 508 | 91,527 |
| `claude-sonnet-5` | 20 | 610 | 36,385 | 1,083,550 | 7,186 | 1,127,731 |
| Total | 23 | 8,073 | 48,268 | 1,155,223 | 7,694 | 1,219,258 |

### Changes

- `.gitignore`: 1 file, +2/-2

### Step summary

這一步不是產品實作，而是第三組對照組的 branch hygiene。Claude Code 起初從 `main` 建 `fable5`，但因 build artifacts 沒有被 main 的 `.gitignore` 完整排除，切 branch 後出現 untracked nested output。這個 commit 修正 ignore 規則，讓後續 implementation 不把 build output 混進 branch。

## F1: Fable5 goal-mode implementation

### 對話摘錄

- User: 「use dotnet10 and sqlite to implement this spec」
- User: 「continue」
- Agent: 最終回覆表示已在 `fable5` commit `21f6f66`，.NET 10 + SQLite 實作完成，`56/56 tests passing`，且連續 3 次測試、zero warnings。

### Token breakdown

| Model | Messages | Input | Cache creation | Cache read | Output | Total |
|---|---:|---:|---:|---:|---:|---:|
| `claude-fable-5` | 64 | 13,734 | 563,637 | 12,504,496 | 162,222 | 13,244,089 |
| `claude-sonnet-5` | 1 | 2 | 425 | 63,840 | 27 | 64,294 |
| Synthetic | 1 | 0 | 0 | 0 | 0 | 0 |
| Total | 66 | 13,736 | 564,062 | 12,568,336 | 162,249 | 13,308,383 |

### Docs changes

- 3 files, +130/-17
- `docs/decisions/2026-07-03-subscription-credit-v1-sqlite-implementation.md`: +108
- `src/README.md`: +10/-9
- `tests/README.md`: +12/-8

### Code changes

- 24 files, +2,061/-0
- `AndrewDemo.AgentRateLimit.slnx`
- `src/AndrewDemo.AgentRateLimit.Abstract/*`
- `src/AndrewDemo.AgentRateLimit.Core/*`
- `SqliteSubscriptionCreditService` split across admin/query/usage/core/schema files.

### Test changes

- 15 files, +2,323/-0
- `tests/AndrewDemo.AgentRateLimit.Core.Tests/*`
- Includes smoke tests, audit reconciliation/regression, consistency/persistence, credit validation, edge cases, extra pool, idempotency, isolation, preview, status output, window usage, test support manual clock/fixture.

### Verification reported by agent

- `56/56 tests passing`
- 3 consecutive test runs
- zero warnings
- All 36 `spec/testcases` `TC-*` mapped 1:1, plus edge and regression tests.

### Agent-reported audit process

- Test authoring fanned out to 10 parallel agents.
- 50-agent adversarial audit after passing suite.
- Audit found a real correctness bug: decision time was captured before obtaining the write lock, so a blocked writer could decide with stale time and miss competitor usage.
- Fix: read time inside the transaction and clamp to subscription newest ledger record.
- Additional fixes included ownership-gated idempotency binding, non-owner information disclosure guard, remaining-balance fields on owner-visible rejection, overflow-proof credit bound, and caller-as-actor on usage audit records.
- Verify phase hit session usage limit partway: 38 verifier agents errored; agent adjudicated remaining findings manually.

### Step summary

這是第三組對照：同樣基於 single implementation prompt，但改用 Claude Code + Fable5，不走 architect-mode 的人工 review gates。結果比 goal-mode 對照組更大，直接產出 Abstract/Core/Tests/ADR，並透過大量 agent fan-out 與 adversarial audit 補強 correctness。不過它的中間規格決策仍主要由 agent 自行做成 ADR，而不是像 architect-mode 一樣在每個 gate 先由 user review 後 commit。
