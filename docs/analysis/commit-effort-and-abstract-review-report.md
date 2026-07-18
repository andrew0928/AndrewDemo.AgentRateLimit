# 三個 Branch 的 Commit 投入與 `.Abstract` Review Surface 統計

## 結論摘要

以下統計把每個 commit 對齊到實際 Codex / Claude Code session 的工作回合，目的是分開觀察：

- 人工投入：human prompt 數、會改變或凍結需求的人工決策/修正數。
- Agent 投入：完成工作回合數、tool call 數、token 使用量。
- 產出規模：final production/`.Abstract` LOC，以及每個 commit 的 production、test、`.Abstract` churn。

投入欄位排除三個 branch 共用的 baseline `e60674f`；LOC 欄位則直接量測各 branch final commit 的完整 source tree：

| Branch | Human prompts | 人工決策/修正 | Agent work rounds | Tool calls | Tokens | Production C# final LOC | `.Abstract` final LOC | `.Abstract` / production final LOC |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| `goal-mode` | 2 | 0 | 2 | 66 | 6,316,045 | 1,448 | 97 | 6.7% |
| `architect-mode` | 33 | 15 | 33 completed + 1 aborted | 670 | 41,596,755 | 2,631 | 158 | 6.0% |
| `fable5` | 2 | 0 | 2 | 120 | 13,308,383 | 2,025 | 552 | 27.3% |

這組數字顯示：

- `goal-mode` 的人工往返最少，單一 implementation prompt 後幾乎全由 agent 決定；`.Abstract` 也最小。
- `architect-mode` 的主要成本不是多寫 `.Abstract` code，而是用 15 次人工決策/修正，把 contract、schema、window、source of truth、acceptance scope 與 API boundary 逐步凍結。最後 `.Abstract` C# 是 158 行。
- `fable5` 幾乎沒有產品語意上的人工修正，但 agent 自行展開了 552 行 `.Abstract`，是三者最大的人工作業 review surface；其 120 次 implementation tool calls 還不包含 subagent 內部的每一個操作。

## 結論摘要欄位定義

| 欄位 | 定義 | 計算方式 / 解讀 |
|---|---|---|
| `Branch` | 本次實驗比較的 final branch。 | LOC 取該 branch final commit；投入只計該 branch 的實驗工作回合。 |
| `Human prompts` | 人實際送出的工作 prompt 數。 | 排除 environment/AGENTS 注入、自動 goal continuation、共用 baseline，以及已指定排除的 `f6920cf`。 |
| `人工決策/修正` | 會改變或凍結 contract、schema、acceptance、deployment semantics 的 user input 數。 | `commit`、`continue`、切 branch 等純操作不計；這是人工設計介入量，不是所有對話數。 |
| `Agent work rounds` | Agent 接到一個 human prompt 或自動 continuation 後完成的一輪工作。 | Codex 以 task lifecycle 計；Claude Code 以 implementation turn 計。Aborted round 另列。 |
| `Tool calls` | Main transcript 中 agent 發出的工具操作數。 | Codex 計 `function_call + custom_tool_call`；Claude Code 計唯一 `tool_use.id`。不包含 Claude subagent 內部未展開的所有操作。 |
| `Tokens` | 該 branch 實驗工作回合的模型 token 使用量。 | Codex 與 Claude Code 的 cache/tokenizer accounting 不完全相同，只適合看相對規模，不等同精確成本。 |
| `Production C# final LOC` | Final commit 中 `src/**/*.cs` 的實體總行數。 | 包含空白行與註解；排除 tests、`.csproj`、`.slnx`、docs、Docker/config。這是 final code size，不是 churn。 |
| `.Abstract final LOC` | Final commit 中 `src/*Abstract*/**/*.cs` 的實體總行數。 | 是 `Production C# final LOC` 的子集合，代表公開 contract/type surface 的大小。 |
| `.Abstract / production final LOC` | `.Abstract` 在 final production C# 中的行數占比。 | `.Abstract final LOC / Production C# final LOC * 100%`。這是 contract review 面積的粗略 proxy，不代表每行複雜度或實際 review 品質相同。 |

## 計算口徑

### Session 與 commit 對齊

- `goal-mode`：Codex session `019f1c5f-8b91-73c2-9679-04186c4f8ea4`。
- `architect-mode`：Codex session `019f1dfc-48a4-7521-88b7-39d1286a178e`。
- `fable5`：Claude Code session `234834cf-84fd-442d-9c19-7732194a39bb`。
- 以「產生該 commit 的完整工作回合」歸屬 token 與 tool calls，不把 commit command 前後數秒硬切成兩段。
- `architect-mode` 在 2026-07-04 rebase 到 `e60674f`，所以目前 hash 與 transcript 當時 hash 不同；內容與 author time 未變。下表使用目前 branch hash。
- `e60674f` 是三個 branch 共用的初始規格 commit，其 session 投入只發生一次；各 branch 表會重列，但 branch 比較總計不重複加總。
- `f6920cf` 是 Fable5 implementation 前的 branch/build-output cleanup，與產品實驗無關，依 user 指示從所有 Fable5 投入與產出統計排除。

### 對談、決策與執行

- `U/A`：human-authored prompt 數 / agent work round 數。自動 goal continuation 算 agent round，不算新的 human prompt。
- 人工決策/修正：只計會改變或凍結 contract、schema、驗收範圍、部署語意的 user input；單純 `commit`、`continue`、切 branch 不計。
- Tool calls：Codex 的 `function_call` + `custom_tool_call`；Claude Code 則計 main transcript 內唯一 `tool_use.id`。
- Aborted work 仍是實際投入，因此 A3 的 token 與 tool calls包含一次 aborted round。

### Line of code

- Summary 的 `final LOC`：branch final commit 內現存的 physical lines；包含空白與註解。
- Commit table 的 `All diff`：該 commit 所有文字檔的 additions/deletions；`churn = additions + deletions`。
- Commit table 的 `Production C#`：該 commit 對 `src/**/*.cs` 造成的 `+A/-D (churn)`，不是 final code 總行數。
- Commit table 的 `Test C#`：該 commit 對 `tests/**/*.cs` 造成的 `+A/-D (churn)`。
- Commit table 的 `.Abstract C#`：該 commit 對 `src/*Abstract*/**/*.cs` 造成的 `+A/-D (churn)`，代表該 commit 需要重新 review 的 contract code 變更面積。
- `+100/-20 (120)` 表示新增 100 行、刪除 20 行、churn 120；final LOC 的淨變化只有 `+80`。
- Docs-only interface review 的 `.Abstract C#` 是 0；這不代表不需 review，而是 review material 當時仍在 architecture docs，尚未成為 code。

### Commit table 其他欄位

- `Commit`：目前 branch 上的 commit short hash；architect-mode 另列 rebase 前 session-time hash。
- `目的`：該 commit 對實驗階段的主要交付目標。
- `U/A`：human-authored prompts / agent work rounds。
- `人工決策/修正`：該 commit 前、實際反映到 commit 內容的人工語意修正數。
- `Agent rounds / tool calls`：工作回合數 / main transcript tool invocation 數。
- `Tokens (reasoning)`：總 token；括號內為 Codex `reasoning_output_tokens`。Claude transcript 沒有可直接對齊的 reasoning 欄位。

### Token

- Codex：加總 `token_count.last_token_usage.total_tokens`；`reasoning_output_tokens` 另列，且已包含在 output/total 的 provider accounting 中。
- Claude Code：依唯一 assistant `message.id` 加總 `input_tokens + cache_creation_input_tokens + cache_read_input_tokens + output_tokens`；transcript 沒有可對齊 Codex 的 reasoning token 欄位。
- 不同模型/tokenizer/cache accounting 並非完全同口徑；適合比較同一實驗內的相對規模，不應解讀成精確成本換算。

Token breakdown 欄位：

- `Messages`：該區間內具有唯一 message id 的 assistant messages；不是 human/assistant 對話回合數。
- `Input`：送入模型計費/記帳的 input tokens。Codex 的 cached input 已包含在此值內。
- `Cached input`：Codex `Input` 中由 cache 命中的 subset，不可再次加到 `Total`。
- `Cache creation`：Claude 建立 prompt cache 所記錄的 input tokens。
- `Cache read`：Claude 從 prompt cache 讀取的 input tokens。
- `Output`：模型產生的 output tokens；Codex reasoning tokens 是其中的 subset。
- `Reasoning`：Codex 回報的 reasoning output tokens；Claude transcript 沒有同口徑欄位。
- `Synthetic`：Claude session limit 等系統產生的非模型訊息；token 為 0，不算 human prompt。
- `Total`：provider transcript 回報的該區間總 token。遇到 compaction total-only event 時，以 `Total` 為權威值。

### Branch report 的時間與文字欄位

- `Step`：一個 commit 對應的實驗工作階段代號，例如 A3、F1。
- `Commit range`：前一個 commit 到本次 commit 的 diff 邊界；不代表前一個 commit 的投入會被重複計入。
- `Commit time`：commit 寫入 git history 的時間；architect-mode rebase 後以原始 author/session time 對齊。
- `Turns`：完成該 commit 的 agent work rounds；不是 transcript 中所有 commentary messages 的數量。
- `Wall interval`：從該階段起點到終點的牆鐘時間，包含 user 暫離、session limit 等等待。
- `Active turn time` / `對話/作業時間`：transcript 回報的 agent active duration，包含推論、tool execution、build/test 等待，不包含兩個 turn 之間的 idle time。
- `問字數`：human 可見文字去除空白後的字元數；CJK 與英數都按字元計，不是 token。
- `答字數`：assistant 可見文字去除空白後的字元數；不包含 hidden reasoning 與 tool output 全文。

## Goal-mode

| Commit | 目的 | U/A | 人工決策/修正 | Agent rounds / tool calls | Tokens (reasoning) | All diff | Production C# | Test C# | `.Abstract` C# |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|
| `e60674f` | 共用 baseline：repo、observable V1 spec、testcases | 3 / 3 | 1：把 V1 限定為外部可觀測行為 | 3 / 73 | 2,556,145 (11,147) | +1,539/-0 (1,539) | 0 | 0 | 0 |
| `018fd9e` | 一次 prompt 完成 .NET 10 + SQLite implementation | 2 / 2 | 0 | 2 / 66 | 6,316,045 (9,995) | +2,053/-41 (2,094) | +1,448/-0 (1,448) | +449/-0 (449) | +97/-0 (97) |

`018fd9e` 的兩個 human-directed rounds 是 implementation 與 commit/cost metadata。產品語意沒有人工中途修正，interface、storage、window 與測試展開都由 agent 依 baseline spec 自行推導。

## Architect-mode

目前 hash 與 transcript 原始 hash 對照：

| Current | Session-time original | Commit subject |
|---|---|---|
| `3daf49f` | `5db51d2` | Add subscription credit abstract design draft |
| `b84c06e` | `74258df` | Refine subscription credit abstract surface |
| `bab4a8f` | `6fffdb2` | Add subscription credit abstract DX slice |
| `d2cd72b` | `c00e91a` | Define minimal subscription credit storage |
| `53982de` | `4cc39e2` | Freeze subscription credit run outcome specs |
| `5958425` | `6c492a8` | Implement subscription credit core service |
| `3d9d63a` | `7a8ff96` | Design subscription credit HTTP API token auth |
| `d2e2937` | `6363084` | Add subscription credit API hosting scaffold |
| `e8f83d0` | `a262d76` | Add subscription credit API blackbox tests |

| Commit | 目的 | U/A | 人工決策/修正 | Agent rounds / tool calls | Tokens (reasoning) | All diff | Production C# | Test C# | `.Abstract` C# |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|
| `e60674f` | 共用 baseline | 3 / 3 | 1 | 3 / 73 | 2,556,145 (11,147) | +1,539/-0 (1,539) | 0 | 0 | 0 |
| `3daf49f` | 第一版 `.Abstract` architecture draft | 1 / 1 | 0 | 1 / 38 | 633,865 (5,359) | +439/-0 (439) | 0 | 0 | 0 |
| `b84c06e` | 砍除 reconciliation / adjustment 等過寬 surface | 1 / 1 | 1 | 1 / 29 | 1,054,548 (4,715) | +97/-189 (286) | 0 | 0 | 0 |
| `bab4a8f` | 用實際 `.Abstract` code 與 test sketch 驗證 DX | 4 / 4 | 1：unknown final credits 不用 0/1 偽裝 | 4 / 86 | 6,099,353 (10,446) | +462/-2 (464) | +150/-0 (150) | +185/-0 (185) | +150/-0 (150) |
| `d2cd72b` | 收斂 schema、window 與 accounting truth | 16 / 15 completed + 1 aborted | 6 | 15 completed + 1 aborted / 143 | 10,924,453 (31,273) | +655/-89 (744) | +8/-0 (8) | +1/-0 (1) | +8/-0 (8) |
| `53982de` | 展開並凍結 16 個 E2E run outcomes | 4 / 4 | 3 | 4 / 69 | 3,893,463 (10,256) | +391/-43 (434) | 0 | 0 | 0 |
| `5958425` | 依 frozen contract 實作 Core | 2 / 2 | 0 | 2 / 94 | 6,051,688 (12,544) | +2,291/-83 (2,374) | +1,659/-0 (1,659) | +575/-76 (651) | 0 |
| `3d9d63a` | 先凍結 HTTP token / subscription scope spec | 2 / 2 | 1 | 2 / 32 | 3,054,420 (3,081) | +420/-0 (420) | 0 | 0 | 0 |
| `d2e2937` | Minimal API、Docker Compose、database-init | 1 / 1 | 1 | 1 / 55 | 2,180,873 (9,203) | +935/-2 (937) | +782/-2 (784) | 0 | 0 |
| `e8f83d0` | API blackbox tests、Docker SQLite fix、token visibility | 2 / 3 | 2 | 3 / 124 | 7,704,092 (12,949) | +940/-2 (942) | +34/-0 (34) | +859/-0 (859) | 0 |

`d2cd72b` 的 6 次主要人工修正是：每次 consume 為獨立操作、unknown-cost probe 語意、lazy quota lease、consume record 作為唯一 accounting truth、extra pool 必須保留、未授權 overrun 由 system absorbed。這是整個 architect-mode 人工投入最高的 commit，也是後續 code correctness 的主要規格來源。

`e8f83d0` 的 U/A 不相等，是因為 blackbox test work 由前一個 goal continuation 自動續跑，之後才有 user 的 Docker error/token visibility 回報與 commit 指令。

## Fable5

| Commit | 目的 | U/A | 人工決策/修正 | Agent rounds / tool calls | Tokens | All diff | Production C# | Test C# | `.Abstract` C# |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|
| `e60674f` | 共用 baseline | 3 / 3 | 1 | 3 / 73 | 2,556,145 | +1,539/-0 (1,539) | 0 | 0 | 0 |
| `21f6f66` | 一次 prompt + continue 完成 Fable5 implementation | 2 / 2 | 0 | 2 / 120 | 13,308,383 | +4,514/-17 (4,531) | +2,025/-0 (2,025) | +2,298/-0 (2,298) | +552/-0 (552) |

排除項目：`f6920cf Fix gitignore to exclude nested bin/obj build output`。該 commit 與對應 session 回合只處理 branch hygiene，不屬於 Fable5 產品實作實驗。

`21f6f66` 沒有產品 contract 的人工中途修正。大量修正來自 agent 自己的 test fan-out 與 adversarial audit，例如 write-lock 後重新取得 decision time、clock regression clamp、idempotency ownership 與 information disclosure guard。這些是 agent 自主 hardening，不應記成人工決策次數。

Claude Code main transcript 的 120 次 tool calls 只代表 orchestration surface。該回合另啟動 10 個 test-authoring agents 與 50-agent audit；subagent 內部每一個 read/edit/test 並未完整展開成 main transcript 的 tool call，因此不能把 120 解讀成全部執行動作。

## Token 明細

### Codex commits

| Commit | Input | Cached input | Output | Reasoning | Total |
|---|---:|---:|---:|---:|---:|
| `e60674f` | 2,518,807 | 2,243,072 | 37,338 | 11,147 | 2,556,145 |
| `018fd9e` | 6,279,249 | 6,156,800 | 36,796 | 9,995 | 6,316,045 |
| `3daf49f` | 620,568 | 539,520 | 13,297 | 5,359 | 633,865 |
| `b84c06e` | 1,043,212 | 978,688 | 11,336 | 4,715 | 1,054,548 |
| `bab4a8f` | 6,072,280 | 5,840,512 | 27,073 | 10,446 | 6,099,353 |
| `d2cd72b` | 10,819,708 | 10,308,224 | 82,086 | 31,273 | 10,924,453 |
| `53982de` | 3,843,124 | 3,561,728 | 26,575 | 10,256 | 3,893,463 |
| `5958425` | 6,005,828 | 5,784,448 | 45,860 | 12,544 | 6,051,688 |
| `3d9d63a` | 3,043,167 | 2,860,928 | 11,253 | 3,081 | 3,054,420 |
| `d2e2937` | 2,133,824 | 2,026,880 | 22,920 | 9,203 | 2,180,873 |
| `e8f83d0` | 7,667,701 | 7,172,096 | 36,391 | 12,949 | 7,704,092 |

`Cached input` 是 `Input` 的 cache subset，不應再加到 `Total`。一般 token event 的 `Total = Input + Output`；但 transcript 有三筆 compaction accounting event 只記 `Total`、沒有 input/output breakdown：`d2cd72b` 22,659、`53982de` 23,764、`d2e2937` 24,129 tokens。因此這三列應以 `Total` 為準，不能從明細欄位反算。

### Claude Code commits

| Commit | Input | Cache creation | Cache read | Output | Total |
|---|---:|---:|---:|---:|---:|
| `21f6f66` | 13,736 | 564,062 | 12,568,336 | 162,249 | 13,308,383 |

## 評估人力投入時的解讀

只看 LOC 會得到錯誤結論：`architect-mode` 的 `.Abstract` C# 只有 158 行，卻有最多對談與 token。這些投入主要是在 code 產生前排除錯誤語意與凍結驗收，而不是反覆改寫大量 contract code。

只看 token 也不夠：`fable5` 的 token 少於 architect-mode，但產生 552 行 `.Abstract`，代表後續人工 contract review 的面積更大；同時它的 subagent fan-out 又讓 main transcript tool-call 數低估實際 agent 執行量。

因此，人力投入比例建議至少同時看三組數字：

1. `Human prompts + 人工決策/修正`：需求釐清與 reviewer 介入量。
2. `Agent rounds + tool calls + tokens`：agent 執行與推論量。
3. `.Abstract final LOC ratio + per-commit production/test/Abstract churn`：最終 contract 占比與每次實際變更的 review 面積。

## Appendix

### A. Branch Comparison

投入統計排除共用 baseline `e60674f`；`Prod LOC` 與 `Contract LOC` 取各 branch final commit 的 physical LOC。

| Branch | Prompts | Decisions | Tool calls | Tokens | Prod LOC | Contract LOC |
|---|---:|---:|---:|---:|---:|---:|
| `goal-mode` | 2 | 0 | 66 | 6,316,045 | 1,448 | 97 |
| `architect-mode` | 33 | 15 | 670 | 41,596,755 | 2,631 | 158 |
| `fable5` | 2 | 0 | 120 | 13,308,383 | 2,025 | 552 |

### B. Architect-mode Commit Breakdown

只列 architect-mode 的實驗 commit，排除共用 baseline `e60674f`。`Human prompts` 取原表 `U/A` 的 `U`；diff 格式為 `+新增/-刪除 (churn)`。

| Commit | Goal | Prompts | Decisions | Tool calls | Tokens | Prod diff | Tests diff | Contract diff |
|---|---|---:|---:|---:|---:|---:|---:|---:|
| `3daf49f` | 第一版 `.Abstract` architecture draft | 1 | 0 | 38 | 633,865 | 0 | 0 | 0 |
| `b84c06e` | 砍除 reconciliation / adjustment 等過寬 surface | 1 | 1 | 29 | 1,054,548 | 0 | 0 | 0 |
| `bab4a8f` | 用實際 `.Abstract` code 與 test sketch 驗證 DX | 4 | 1 | 86 | 6,099,353 | +150/-0 (150) | +185/-0 (185) | +150/-0 (150) |
| `d2cd72b` | 收斂 schema、window 與 accounting truth | 16 | 6 | 143 | 10,924,453 | +8/-0 (8) | +1/-0 (1) | +8/-0 (8) |
| `53982de` | 展開並凍結 16 個 E2E run outcomes | 4 | 3 | 69 | 3,893,463 | 0 | 0 | 0 |
| `5958425` | 依 frozen contract 實作 Core | 2 | 0 | 94 | 6,051,688 | +1,659/-0 (1,659) | +575/-76 (651) | 0 |
| `3d9d63a` | 先凍結 HTTP token / subscription scope spec | 2 | 1 | 32 | 3,054,420 | 0 | 0 | 0 |
| `d2e2937` | Minimal API、Docker Compose、database-init | 1 | 1 | 55 | 2,180,873 | +782/-2 (784) | 0 | 0 |
| `e8f83d0` | API blackbox tests、Docker SQLite fix、token visibility | 2 | 2 | 124 | 7,704,092 | +34/-0 (34) | +859/-0 (859) | 0 |
