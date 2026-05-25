# Protocols and State — OpenMonoAgent.ai

<!-- Phase: protocols | Pipeline: workflow/pipeline.yaml | Closes: contr-CF1, contr-CF2, contr-CF3 -->

## Boundaries Identified

| # | Boundary | Direction | Transport |
|---|---|---|---|
| B1 | Core → LLM Provider (OpenAI-compat) | bidirectional per request | HTTP SSE (`POST /v1/chat/completions`) |
| B2 | Core → LLM Provider (Anthropic) | bidirectional per request | HTTP SSE (`POST /v1/messages`) |
| B3 | Core → MCP server subprocess | bidirectional | stdio JSON-RPC 2.0 (line-delimited) |
| B4 | Core → LSP server subprocess | bidirectional | stdio JSON-RPC (line-delimited) |
| B5 | Runtime → session persistence | write on save/resume | JSONL files + JSON sidecars on host filesystem |
| B6 | Runtime → turn journal | append-only write | JSONL on host filesystem |
| B7 | Runtime → playbook state | write after each step | JSON file on host filesystem |
| B8 | Runtime → artifact store | write on large output | plaintext files on host filesystem |
| B9 | Runtime → config | read-only at session start | JSON files + env vars on host filesystem |
| B10 | UI → Core | in-process | `IRenderer` / `ILiveFeedback` interfaces (method calls) |
| B11 | Docker agent container → llama-server container | request/response | HTTP port 7474 |
| B12 | Host shell → Docker | process spawn | `docker compose run` |

---

## Event Catalog

### B1: OpenAI-Compatible SSE Stream

| Field | Value |
|---|---|
| **Producer** | llama-server (or any OpenAI-compat LLM provider) |
| **Consumer** | `OpenAiCompatClient.StreamChatAsync` |
| **Transport or carrier** | HTTP 1.1 chunked transfer; `Content-Type: text/event-stream`; each event is a `data: <json>` line; stream terminated by `data: [DONE]` |
| **Ordering guarantees** | In-order delivery guaranteed within a single HTTP connection; no out-of-order events. Tool call arguments are accumulated across multiple chunks by `index`. |
| **Required fields** | `choices[0].delta` object in each chunk |
| **Optional fields** | `choices[0].delta.content` (text token), `choices[0].delta.tool_calls[].function.{name,arguments}` (tool call delta), `choices[0].delta.reasoning_content` (thinking token), `choices[0].finish_reason`, `usage.{prompt_tokens,completion_tokens}` (final chunk only, requires `stream_options.include_usage=true`) |
| **Identifiers and timestamps** | Tool call `id` field identifies a specific call across chunks; no per-event sequence number |
| **Error cases** | `data: {"error":{"message":"..."}}` inline error object in stream; HTTP 4xx/5xx before stream begins; malformed JSON in chunk (tolerated up to 50 before abort) |
| **Restart or resume behavior** | No native resume; `OpenAiCompatClient` retries the entire request up to 3 times (delays: 1 s, 4 s, 16 s) on HTTP 429/500/502/503/504 or `HttpRequestException` before the stream starts; mid-stream failures are not retried |

*observed fact — `Llm/OpenAiCompatClient.cs:82-310`.*

**Qwen XML tool call fallback:** If `delta.content` contains a `<function=name>` tag, the entire text stream is suppressed and `QwenFunctionRegex` / `QwenParamRegex` extract tool calls at `[DONE]` time. Normalized to same `ToolCallDelta` shape. *observed fact — `OpenAiCompatClient.cs:173-194`, `Regex` patterns lines 24-29.*

**Request body shape** sent by `OpenAiCompatClient`:
```json
{
  "model": "<model>",
  "messages": [
    { "role": "system", "content": "..." },
    { "role": "user", "content": "..." },
    { "role": "assistant", "content": "...", "tool_calls": [
        { "id": "...", "type": "function", "function": { "name": "...", "arguments": "..." } }
    ]},
    { "role": "tool", "tool_call_id": "...", "content": "..." }
  ],
  "temperature": 0.2, "max_tokens": 4096, "top_p": 0.8, "top_k": 20,
  "presence_penalty": 1.5, "min_p": 0.0, "repetition_penalty": 1.0,
  "stream": true,
  "stream_options": { "include_usage": true },
  "tools": [...],          // omitted if empty
  "tool_choice": "auto",   // only with tools
  "chat_template_kwargs": { "enable_thinking": true }  // optional, thinking mode
}
```
*observed fact — `OpenAiCompatClient.BuildRequestBody:321-378`.*

---

### B2: Anthropic Native SSE Stream

| Field | Value |
|---|---|
| **Producer** | Anthropic API (`api.anthropic.com`) |
| **Consumer** | `AnthropicClient.StreamChatAsync` |
| **Transport or carrier** | HTTP 1.1 SSE; `data: <json>` lines; no `[DONE]` sentinel — stream ends with `message_stop` event type |
| **Ordering guarantees** | In-order; tool call arguments accumulated across `input_json_delta` events within one `content_block_start`/`content_block_stop` envelope |
| **Required fields** | `type` discriminator in each event object |
| **Optional fields** | `content_block.{type,id,name}` (on `content_block_start`); `delta.{type,text,partial_json}` (on `content_block_delta`); `usage.output_tokens` (on `message_delta`) |
| **Identifiers and timestamps** | Tool use block `id` from `content_block_start`; no sequence numbers |
| **Error cases** | `type: "error"` event with `error.message`; HTTP 429/500/502/503/529 before stream |
| **Restart or resume behavior** | Same 3-retry pattern as B1 before stream starts; no mid-stream retry |

*observed fact — `Llm/AnthropicClient.cs:95-193`.*

**Normalization to `StreamChunk`:** Both `AnthropicClient` and `OpenAiCompatClient` produce identical `StreamChunk` values. The consumer (`ConversationLoop`) is provider-agnostic. *observed fact — `Llm/ILlmClient.cs`.*

**Request body shape** sent by `AnthropicClient`:
```json
{
  "model": "<model>",
  "system": "<system message content>",
  "messages": [
    { "role": "user", "content": "..." },
    { "role": "assistant", "content": [
        { "type": "text", "text": "..." },
        { "type": "tool_use", "id": "...", "name": "...", "input": { ... } }
    ]},
    { "role": "user", "content": [
        { "type": "tool_result", "tool_use_id": "...", "content": "..." }
    ]}
  ],
  "max_tokens": 4096,
  "stream": true,
  "tools": [
    { "name": "...", "description": "...", "input_schema": { "type": "object", ... } }
  ]
}
```

**Key difference from OpenAI:** System message extracted to top-level `system` field. Tool results are wrapped as `user` messages with `tool_result` content blocks, not a `tool` role. Tools list omits `type/function` wrapper; uses `input_schema` instead of `parameters`. *observed fact — `AnthropicClient.BuildRequestBody:196-266`.*

---

### B3: MCP JSON-RPC 2.0 over stdio (contr-CF3 closure)

| Field | Value |
|---|---|
| **Producer** | `McpClient` (OpenMono, client side) |
| **Consumer** | External MCP server subprocess |
| **Transport or carrier** | OS subprocess stdin/stdout; one JSON object per line (newline-delimited); no framing headers |
| **Ordering guarantees** | Serial — one request in flight at a time enforced by `SemaphoreSlim(1,1)` |
| **Required fields** | `jsonrpc: "2.0"`, `id` (integer, requests only), `method` (string) |
| **Optional fields** | `params` object |
| **Identifiers and timestamps** | `id` is a monotonically incrementing integer per `McpClient` instance; no per-message timestamps |
| **Error cases** | Response with `error` property → `InvalidOperationException($"MCP error: {message}")`; server stdout closed → `InvalidOperationException("MCP server closed connection")`; `_process.HasExited` check before tool calls |
| **Restart or resume behavior** | No reconnection logic; failed connections logged as warnings by `McpServerManager`; server must be restarted by restarting the agent session |

*observed fact — `Mcp/McpClient.cs`.*

**Initialize handshake sequence:**

```
Client → Server:
{"jsonrpc":"2.0","id":1,"method":"initialize","params":{
  "protocolVersion":"2024-11-05",
  "capabilities":{},
  "clientInfo":{"name":"OpenMono.ai","version":"0.1.0"}
}}

Server → Client:
{"jsonrpc":"2.0","id":1,"result":{...}}   // server capabilities

Client → Server (notification, no id):
{"jsonrpc":"2.0","method":"notifications/initialized","params":{}}
```

*observed fact — `McpClient.ConnectAsync:50-58`.*

**tools/list request/response:**

```
Request:  {"jsonrpc":"2.0","id":N,"method":"tools/list","params":{}}
Response: {"jsonrpc":"2.0","id":N,"result":{
  "tools":[{
    "name":"<tool-name>",
    "description":"<description>",
    "inputSchema":{"type":"object","properties":{...},"required":[...]}
  }]
}}
```

*observed fact — `McpClient.ListToolsAsync:62-64`, `McpServerManager.InitializeAsync:38-44`.*

**tools/call request/response:**

```
Request:  {"jsonrpc":"2.0","id":N,"method":"tools/call","params":{"name":"<tool-name>","arguments":{...}}}
Response: {"jsonrpc":"2.0","id":N,"result":{
  "content":[{"type":"text","text":"<output>"}],
  "isError":false
}}
```

Error response: `"isError":true` with `content` containing error text; OR `error` property at root level. *observed fact — `McpClient.CallToolAsync:67-69`, `McpToolAdapter.ExecuteAsync:46-53`.*

**resources/list and resources/read:**

```
resources/list:  {"jsonrpc":"2.0","id":N,"method":"resources/list","params":{}}
resources/read:  {"jsonrpc":"2.0","id":N,"method":"resources/read","params":{"uri":"<uri>"}}
```

*observed fact — `McpClient.ListResourcesAsync`, `ReadResourceAsync:73-81`.*

**Tool registration:** MCP tools registered in `ToolRegistry` as `mcp__{serverName}__{mcpToolName}`. All MCP tools have `RequiredPermission = PermissionLevel.Ask` (always prompt). *observed fact — `McpToolAdapter.FromMcpTool:32-34`, `RequiredPermission`.*

---

### B4: ConversationLoop Inner Event Stream (in-process)

| Field | Value |
|---|---|
| **Producer** | `ConversationLoop.RunTurnAsync` |
| **Consumer** | `IRenderer` (TUI or terminal), `ILiveFeedback`, `TokenTracker` |
| **Transport or carrier** | Direct C# method calls; `IAsyncEnumerable<StreamChunk>` piped to renderer callbacks |
| **Ordering guarantees** | Strict sequential within a single `await foreach` iteration |
| **Required fields** | `StreamChunk` with one of: `ThinkingDelta`, `TextDelta`, `ToolCallDelta`, `IsComplete`, `Usage` |
| **Optional fields** | Any combination of fields may be set; `Usage` may appear on a `TextDelta` chunk |
| **Identifiers and timestamps** | None at event level; `ToolCall.Id` links a `ToolCallDelta` to its later `ToolResult` |
| **Error cases** | `OperationCanceledException` → propagated as cancellation; other exceptions bubble to caller |
| **Restart or resume behavior** | No mid-stream resume; cancelled turn not replayed |

*observed fact — `Session/ConversationLoop.cs:200-290`.*

---

## State Machine

### SM1: ConversationLoop Turn Lifecycle

```
[Idle]
  │  user input received (or slash command detected)
  ↓
[CommandDispatch]  ──────(slash command)──────────────────────→ [Idle]
  │  (regular input)
  ↓
[ContextPressureCheck]
  │  < 65%  ─────────────────────────────────────────────────→ [Streaming]
  │  ≥ 65%  →  [Checkpointing]  ──(checkpoint created)───────→ [Streaming]
  │  ≥ 80% + no checkpoint in progress  →  [Compacting]  ────→ [Streaming]
  ↓
[Streaming]  ← ─────────────────────────────────────────────────┐
  │  LLM returns text only (no tool calls)                      │
  ↓                                                             │
[TurnComplete]  →  SaveAsync  →  [Idle]                        │
  │  LLM returns tool calls                                     │
  ↓                                                             │
[DoomLoopCheck]                                                  │
  │  3 identical consecutive batches  →  inject break msg ─────┘
  │  < 3 repeats                                               │
  ↓                                                             │
[ToolExecution]  →  ExecuteToolCallsWithInflightAsync           │
  │  all tool results collected                                 │
  ↓                                                             │
[AppendToolResults]  →  add to SessionState.Messages  ──────────┘
  │  iteration count ≥ 1000  →  [TurnComplete] (hard cap)
```

*observed fact — `ConversationLoop.cs:130-302`, guards documented.*

| Current State | Event / Trigger | Guard | Next State | Side Effects |
|---|---|---|---|---|
| `Idle` | User input | starts with `/` | `CommandDispatch` | `CommandRegistry.Execute()` |
| `Idle` | User input | normal text | `ContextPressureCheck` | resolve `@file` refs, add User msg to session |
| `ContextPressureCheck` | Threshold check | tokens ≥ 65% | `Checkpointing` | `Checkpointer.CreateCheckpointAsync` |
| `ContextPressureCheck` | Threshold check | tokens ≥ 80%, fallback | `Compacting` | `Compactor.CompactAsync`; JSONL overwritten |
| `ContextPressureCheck` | Threshold check | tokens < 65% | `Streaming` | build tool defs, start LLM request |
| `Streaming` | `ThinkingDelta` | — | `Streaming` | render to thinking panel |
| `Streaming` | `TextDelta` | — | `Streaming` | render to output; append to fullText |
| `Streaming` | `ToolCallDelta` | tool is `IsConcurrencySafe` | `Streaming` | start `inFlightTask` |
| `Streaming` | `Usage` | — | `Streaming` | `TokenTracker.Update` |
| `Streaming` | `IsComplete`, no tool calls | — | `TurnComplete` | `EndAssistantResponse`; save session |
| `Streaming` | `IsComplete`, tool calls present | — | `DoomLoopCheck` | add Assistant msg to session |
| `DoomLoopCheck` | 3 identical consecutive batches | — | `Streaming` (next iter) | inject break message as Assistant msg |
| `DoomLoopCheck` | < 3 repeats | — | `ToolExecution` | wait for in-flight read-only tasks |
| `ToolExecution` | all tool results | — | `AppendToolResults` | complete `inFlightTasks`; run serial writable tools |
| `AppendToolResults` | — | iter < 1000 | `Streaming` (next iter) | add Tool msgs to session |
| `AppendToolResults` | — | iter ≥ 1000 | `TurnComplete` | hard cap; session saved |

*observed fact — `ConversationLoop.cs`, `DoomLoopDetector.cs`.*

---

### SM2: 12-Step Tool Execution Pipeline

| Step | State Name | Guard | Next State | Side Effect on Failure |
|---|---|---|---|---|
| 1 | `ParseArguments` | JSON parse succeeds | `SchemaValidate` | `ToolResult.Error(invalid_json)`; journal: `RecordSchemaRejected` |
| 2 | `SchemaValidate` | `SchemaValidator.Validate` passes | `SanityCheck` | `ToolResult.Error(schema_error)`; journal: `RecordSchemaRejected` |
| 3 | `SanityCheck` | path-escape / workspace guard passes | `PlanModeGuard` | `ToolResult.Error(sanity_error)`; journal: `RecordSanityRejected` |
| 4 | `PlanModeGuard` | plan mode off, OR tool is read-only | `CapabilityCheck` | `ToolResult.Error("Plan mode is active…")` |
| 5a | `CapabilityCheck` | `RequiredCapabilities.Count > 0` path; all covered | `CacheLookup` | `ToolResult.Error("Permission denied…")`; journal: `RecordPermissionDecided` |
| 5b | `LegacyPermCheck` | `RequiredCapabilities.Count == 0` path; allow | `CacheLookup` | `ToolResult.Error("Permission denied…")`; journal: `RecordPermissionDecided` |
| 6 | `CacheLookup` | read-only tool; cache hit (mtime+size valid) | `Done` (cached result) | (miss is not failure; proceed to step 7) |
| 7 | `PreHook` | hook script exits 0 within 30 s | `Execute` | warning logged; execution **not** blocked |
| 8 | `Execute` | `tool.ExecuteAsync` returns | `PostHook` | `OperationCanceledException` → `ToolResult.Cancelled`; other exception → `ToolResult.Crash`; journal: `RecordToolStarted` before, `RecordToolCompleted` / `RecordToolCrashed` after |
| 9 | `PostHook` | hook script exits 0 within 30 s | `ArtifactStore` | warning logged; result unmodified |
| 10 | `ArtifactStore` | `result.Class == Success` AND `ModelPreview.Length > 50,000` | `CacheWrite` | truncate preview; write full content to artifacts/ |
| 11 | `CacheWrite` | read-only tool; `ResultClass.Success` | `Invalidate` | no-op on failure |
| 12 | `Invalidate` | tool is `FileWrite`/`FileEdit`/`ApplyPatch` | `Done` | evict path from `ToolResultCache` and `FileReadTool` cache |

*observed fact — `ConversationLoop.ExecuteSingleToolAsync:627-781`, `HookRunner.cs`, `ArtifactStore.cs`, `ToolResultCache.cs`, `TurnJournal.cs`.*

---

### SM3: MCP Connection Lifecycle

| Current State | Event / Trigger | Guard | Next State | Side Effects |
|---|---|---|---|---|
| `Disconnected` | `McpServerManager.InitializeAsync` called | `config.Enabled == true` | `Spawning` | `Process.Start(psi)` with redirected stdio |
| `Spawning` | `Process.Start` returns | process not null | `Initializing` | `new McpClient(process, name)` |
| `Spawning` | `Process.Start` returns null | — | `Error` | `warn("Failed to start MCP server")` |
| `Initializing` | `initialize` request sent | — | `AwaitingInitAck` | `SendRequestAsync("initialize", {...})` |
| `AwaitingInitAck` | response received | `result` property present | `SendingNotification` | — |
| `AwaitingInitAck` | response with `error` | — | `Error` | `InvalidOperationException` |
| `SendingNotification` | `notifications/initialized` sent | — | `Ready` | `SendNotificationAsync`; `ListToolsAsync` called |
| `Ready` | `tools/list` result | `result.tools` array present | `Registered` | each tool registered in `ToolRegistry` |
| `Registered` | tool call | `IsRunning == true` | `Registered` | `SendRequestAsync("tools/call", ...)` |
| `Registered` | `IsRunning == false` | — | `Error` | `ToolResult.Error("MCP server not running")` |
| `Error` | `McpServerManager.InitializeAsync` continues | — | (next server) | connection failure logged; server skipped |
| `Ready` or `Error` | `Dispose()` | — | `Terminated` | `process.Kill(entireProcessTree:true)` + 3 s wait |

*observed fact — `Mcp/McpClient.cs`, `Mcp/McpServerManager.cs`.*

---

### SM4: Playbook Step Execution

| Current State | Event / Trigger | Guard | Next State | Side Effects |
|---|---|---|---|---|
| `Pending` | `ExecuteAsync` called | parameters valid | `StepLoop` | `ParameterValidator.Validate`; topological step ordering |
| `StepLoop` | next step | `IsStepCompleted(stepId)` | `StepLoop` (skip) | log "already completed (resumed)" |
| `StepLoop` | next step | dep not completed | `Aborted` | return error |
| `StepLoop` | next step | ready | `GetContent` | `GetStepContentAsync` → template resolution |
| `GetContent` | content resolved | `step.Gate == None` | `RunStep` | skip gate |
| `GetContent` | content resolved | gate != None | `GatePrompt` | `HandleGateAsync` — user prompted |
| `GatePrompt` | user says N / no | — | `StepLoop` (skip step) | log "skipped by user" |
| `GatePrompt` | user says Y | — | `RunStep` | proceed |
| `RunStep` | LLM + tool loop | `pendingToolCalls.Count == 0` | `ScriptCheck` | up to 10 LLM iterations per step |
| `RunStep` | 10 tool loops | — | `ScriptCheck` | warning: max tool loops |
| `ScriptCheck` | `step.Script` present | script exits non-0 | `Aborted` | playbook aborted |
| `ScriptCheck` | `step.Script` present | script exits 0 | `CompleteStep` | — |
| `ScriptCheck` | no script | — | `CompleteStep` | — |
| `CompleteStep` | — | more steps remain | `StepLoop` | `state.CompleteStep`; `state.SaveAsync` |
| `CompleteStep` | last step | — | `Done` | return final step output |

*observed fact — `Playbooks/PlaybookExecutor.cs`.*

---

## Persistent Schema Notes

### Session JSONL — `~/.openmono/sessions/{yyyy-MM-dd}_{id}.jsonl` (contr-CF1 closure)

**Semantics:** Full rewrite on every `SaveAsync` call (not append-only). `append: false` on `StreamWriter`. *observed fact — `SessionManager.SaveAsync:25`.*

**Line 0 — SessionHeader** (JSON object; identified by `"session_id"` substring on load):
```json
{ "session_id": "<12-char hex>", "started_at": "<ISO8601 UTC>", "working_directory": "<path>" }
```

**Lines 1..N — Message records** (JSON object, camelCase from `JsonOptions.Default`):
```json
{
  "role": "System" | "User" | "Assistant" | "Tool",
  "content": "<string or null>",
  "toolCalls": [                   // non-null only on Assistant messages with tool calls
    { "id": "<string>", "name": "<string>", "arguments": "<JSON string>" }
  ],
  "toolCallId": "<string or null>",  // Tool role messages: links result to call
  "toolName": "<string or null>",    // Tool role messages: informational
  "timestamp": "<ISO8601 UTC>"
}
```

**Role usage:**
- `System` — first message; system prompt (appears once, not re-sent on resume)
- `User` — user turn input
- `Assistant` — LLM response; has `toolCalls` when tools were invoked
- `Tool` — tool execution result; `toolCallId` links it to the `Assistant` call; `toolName` is informational

*observed fact — `Session/Message.cs`, `Session/SessionManager.cs`.*

---

### Checkpoint Sidecar — `…/{date}_{id}.checkpoints.json`

**Semantics:** Written only if `session.Checkpoints.Count > 0`; JSON array (pretty-printed with `JsonOptions.Indented`). *observed fact — `SessionManager.SaveAsync:42-47`.*

```json
[
  {
    "id": "<string>",
    "createdAt": "<ISO8601 UTC>",
    "turnIndex": <int>,
    "cutoffMessageIndex": <int>,
    "summary": "<LLM-generated summary string>",
    "messagesCompressed": <int>
  }
]
```

On resume: `session.CheckpointCutoffIndex = checkpoints[^1].CutoffMessageIndex`. Messages before the cutoff index are replaced by the summary in the context window; messages from `cutoffMessageIndex` onward are included verbatim. *observed fact — `SessionManager.LoadAsync:93-102`, `docs/ARCHITECTURE.md:127-133`.*

---

### Session Index — `~/.openmono/sessions/index.json`

**Semantics:** Full rewrite on every `UpdateIndexAsync` call; JSON array (pretty-printed). Filtered by `WorkingDirectory` in `ListSessionsAsync`. *observed fact — `SessionManager.UpdateIndexAsync`.*

```json
[
  {
    "id": "<12-char hex>",
    "startedAt": "<ISO8601 UTC>",
    "turnCount": <int>,
    "totalTokens": <int>,
    "workingDirectory": "<path>",
    "firstMessage": "<first 100 chars of first User message>"
  }
]
```

*observed fact — `SessionManager.SessionSummary`, `UpdateIndexAsync:123-153`.*

---

### Turn Journal — `…/{date}_{id}.journal.jsonl`

**Semantics:** Append-only (`StreamWriter(path, append:true)`); `AutoFlush=true`; one event per line; JSON snake_case (via `JournalSerializerContext`); polymorphic type discriminator field `"type"`. Opened lazily on first event. *observed fact — `Session/TurnJournal.cs:138-146`.*

**Event types:**

| `type` value | Required fields (beyond `timestamp`) | Notes |
|---|---|---|
| `turn_started` | `turn_id`, `model` | `parent_message_id` optional |
| `turn_finished` | `turn_id`, `finish_reason` | |
| `tool_call_received` | `turn_id`, `call_id`, `tool_name`, `args_hash` | `args_hash` = first 16 hex chars of SHA-256(args UTF-8) |
| `schema_validated` | `call_id` | |
| `schema_rejected` | `call_id`, `error` | |
| `sanity_checked` | `call_id` | |
| `sanity_rejected` | `call_id`, `reason` | |
| `permission_decided` | `call_id`, `decision` (`"allow"` or `"deny"`) | `reason` optional |
| `tool_started` | `call_id` | |
| `tool_completed` | `call_id`, `result_class`, `artifact_ids` | `result_class` = `ToolResult.ResultClass.ToString()`; `artifact_ids` = `[]` unless artifacts created |
| `tool_crashed` | `call_id`, `exception_class`, `message` | |

**Incomplete call detection:** `FindIncompleteToolCalls` computes `started − (completed ∪ crashed)`. *observed fact — `TurnJournal.cs:174-196`.*

---

### Playbook State — `~/.openmono/playbook-state/{name}_{sessionId}.json`

**Semantics:** Full rewrite after each completed step; JSON pretty-printed. Created in `CompleteStep → state.SaveAsync`. *observed fact — `PlaybookState.cs:26-33`, `PlaybookExecutor.cs:126-129`.*

```json
{
  "playbookName": "<string>",
  "sessionId": "<8-char hex>",
  "startedAt": "<ISO8601 UTC>",
  "parameters": { "<key>": <value> },
  "stepOutputs": { "<stepId>": "<output string>" },
  "completedSteps": ["<stepId>", ...],
  "currentStepId": "<stepId> or null",
  "tokensUsed": <int>
}
```

**Resume semantics:** Pass a loaded `PlaybookState` as `resumeFrom` to `PlaybookExecutor.ExecuteAsync`. Steps in `CompletedSteps` are skipped; `step.Requires` dependencies checked against `CompletedSteps`. *observed fact — `PlaybookExecutor.ExecuteAsync:68-84`.*

---

### Playbook Definition — `PLAYBOOK.md` YAML Frontmatter (contr-CF2 closure)

**File format:** Markdown with YAML frontmatter delimited by `---`. File must live at `<search-path>/<subdir>/PLAYBOOK.md`. Parsed with YamlDotNet; naming convention: `HyphenatedNamingConvention` (all YAML keys use hyphens). *observed fact — `Playbooks/PlaybookLoader.cs:9-11`, `ParsePlaybook`.*

```yaml
---
name: string                        # playbook name; defaults to directory name
version: string                     # defaults to "1.0.0"
description: string                 # summary for LLM/user
trigger: manual | auto | both       # defaults to manual
trigger-patterns: [string]          # patterns for auto-trigger matching
user-invocable: bool                # show in /playbook list; defaults to true
argument-hint: string               # hint for LLM argument
allowed-tools: [string]             # tool name list; defaults to ["*"]
context-mode: selective | full | fork  # defaults to selective
max-context-tokens: int             # defaults to 3000
tags: [string]
parameters:
  <name>:
    type: string | number | boolean | array
    required: bool
    default: <value>
    hint: string
    enum: [string]                  # optional allowed values
    min: number                     # optional
    max: number                     # optional
steps:
  - id: string                      # required; unique within playbook
    file: string                    # path relative to BasePath
    inline-prompt: string           # used if file absent
    script: string                  # path to validation shell script
    agent: string                   # agent type name filter (BuiltInAgents key)
    output: string                  # (present in model but not consumed by executor)
    playbook: string                # nested playbook name
    gate: none | confirm | review | approve  # defaults to none
    requires: [string]              # step IDs that must complete first
    params: { key: string }         # param overrides
constraints:
  file: string                      # path to external constraints file
  inline: [string]                  # inline constraint lines
---
<Role description / system prompt body (Markdown)>
```

*observed fact — `Playbooks/PlaybookDefinition.cs`, `Playbooks/PlaybookLoader.cs`.*

**Template engine substitutions** (resolved at step run time by `TemplateEngine.ResolveAsync`):

| Token | Resolves to |
|---|---|
| `{{params.<key>}}` | Value from `state.Parameters[key]` |
| `{{state.<stepId>}}` | Output string from completed step `stepId` |
| `{{constraints}}` | Rendered constraint set (file contents + inline list) |
| `{{playbook.base-path}}` | Absolute path to playbook directory |
| `{{env.CWD}}` | `config.WorkingDirectory` |
| `{{env.DATE}}` | UTC date `yyyy-MM-dd` |
| `{{env.GIT_BRANCH}}` | Current git branch (or `"unknown"`) |
| `{{file:<path>}}` | File contents from `<path>` (relative to CWD) |
| `{{shell:<cmd>}}` | stdout of `<cmd>` (10 s timeout; on non-zero exit: `(exit N) stderr`) |

*observed fact — `Playbooks/TemplateEngine.cs`.*

**Step ordering:** Topological sort (DFS over `step.Requires` graph) via `ResolveStepOrder`. Cycles not detected — would hang. *observed fact — `PlaybookExecutor.ResolveStepOrder:282-303`.*

**Per-step tool loop cap:** 10 iterations (`maxToolLoops`). *observed fact — `PlaybookExecutor.RunStepAsync:215`.*

---

### Artifact Store — `~/.openmono/artifacts/{sessionId}/{toolName}_{ts}_{hash8}.txt`

**Trigger:** `result.Class == Success` AND `ModelPreview.Length > 50,000 chars`. *observed fact — `Session/ArtifactStore.cs:10`.*

**Truncation preview:** First 20 lines + last 10 lines. Omission marker: `[N lines omitted — full output in artifact {id} ({bytes} bytes)]`. *observed fact — `ArtifactStore.BuildTruncatedPreview`.*

---

## Compatibility Hazards

| Hazard | Where It Appears | Severity | Notes |
|---|---|---|---|
| `.NET async/await + Task.WhenAll + CancellationToken` | `ConversationLoop` (in-flight parallel tasks, sibling-abort CTS) | High | Direct port requires language-equivalent: goroutines (Go), `asyncio.gather` (Python), Tokio select (Rust). Sibling-abort propagation (`siblingAbortCts.Cancel()` on first crash) must be explicitly replicated. *portability hazard* |
| OpenAI vs Anthropic request body shape | `AnthropicClient.BuildRequestBody` | High | System message is top-level in Anthropic; `tool` role becomes nested `user` + `tool_result`; tools use `input_schema` not `parameters`; tool calls use `tool_use` content blocks not `tool_calls` array. Any port must maintain per-provider adapters. *observed fact* |
| Qwen XML `<function=...>` tool call format | `OpenAiCompatClient` (lines 24–29, 173–194) | Medium | Non-standard XML tool call syntax used by Qwen models; suppresses text stream, parses at `[DONE]`. Ports must handle this fallback or it silently drops tool calls. *observed fact* |
| Session JSONL header identification by substring | `SessionManager.LoadAsync:87` | Medium | Header line skipped by checking `line.Contains("\"session_id\"")` — not proper discriminated deserialization. Breaks if any message `content` field contains the literal string `"session_id"`. *portability hazard* |
| Session JSONL full rewrite on save | `SessionManager.SaveAsync:25` (`append: false`) | Medium | Not append-only. Concurrent saves (e.g., SIGTERM during write) can produce partial files. No atomic rename/replace. Ports should use write-to-temp + rename. *portability hazard* |
| MCP no request pipelining | `McpClient._lock = SemaphoreSlim(1,1)` | Medium | All requests serialized. High-latency MCP servers will block the agent loop. A port targeting performance may want async pipelining with response correlation by `id`. *strong inference* |
| ANSI TUI (Spectre.Console full-screen) | `Rendering/AnsiTuiRenderer` | Medium | Spectre.Console is .NET-specific. Full-screen TUI semantics (alternate buffer, cursor positioning, ANSI escape sequences) are terminal-dependent. Ports must reimplement or substitute a TUI library. *portability hazard* |
| `process.Kill(entireProcessTree: true)` | `McpClient.Dispose`, `ProcessRunner` | Medium | `entireProcessTree` flag is .NET 5+ on Linux/macOS. Go `exec.Cmd` / Python `subprocess` require `os.killpg` or `proc.kill()` with process group. *portability hazard* |
| Playbook `{{shell:<cmd>}}` injection surface | `TemplateEngine.ResolveShellCommandsAsync` | Medium | Shell command embedded in YAML frontmatter; executes via `bash` with no sanitization. Malicious or misconfigured PLAYBOOK.md can run arbitrary commands. *portability hazard* |
| YamlDotNet `HyphenatedNamingConvention` | `PlaybookLoader` | Low | All playbook YAML keys are hyphenated (e.g., `max-context-tokens`). Ports must use the same YAML naming convention; camelCase or snake_case parsers will fail to bind fields. *observed fact* |
| `Guid.NewGuid().ToString("N")[..12]` session IDs | `SessionState.Id`, `PlaybookState.SessionId` | Low | 12-char hex prefix of a GUID — not a full UUID, not cryptographically uniform (first 12 chars of a v4 GUID have known bit patterns). Adequate for file naming; not for security purposes. *observed fact* |
| `DateTime.UtcNow` in JSONL field names | `SessionManager` (file name: `{yyyy-MM-dd}_{id}.jsonl`) | Low | File names contain date; `ListSessionsAsync` searches by glob `*_{id}.jsonl`. Ports must preserve this naming or update `LoadAsync` pattern. *observed fact* |
| `TiktokenSharp` for token counting | `Session/TokenTracker` (implied) | Low | .NET-specific tokenizer. Context pressure thresholds (65%, 80%) rely on accurate token counts; porting requires equivalent tokenizer for the target model family. *strong inference* |

---

## Open Questions

| ID | Kind | Description | Deferred Reason |
|---|---|---|---|
| prot-OQ1 | needs-runtime-test | The LSP client (`Lsp/LspClient.cs`) uses JSON-RPC over stdio but its wire format was not read in depth. LSP 3.17 has specific request/response shapes that may differ from MCP. Whether `LspServerManager` uses the same `SemaphoreSlim` serialization pattern as `McpClient` is unknown. | LSP is optional and not a porting blocker; MCP is the primary external protocol. |
| prot-OQ2 | needs-runtime-test | `SessionMetadata.ThinkingEnabled` and `LastPlan` fields are in-memory only (not serialized to JSONL). Whether `PlanMode` state is persisted to session JSONL was not confirmed. | Affects resume behavior for plan mode. |
| prot-OQ3 | needs-maintainer-decision | `StepDefinition.Playbook` field exists (nested playbook) and `StepDefinition.Output` field exists but neither is consumed by `PlaybookExecutor.RunStepAsync`. These appear to be reserved schema fields not yet implemented. | Unclear if intentional no-op or unfinished feature. |

---

## Carry-Forward

| ID | Target Phase | Description | Deferred Reason |
|---|---|---|---|
| prot-CF1 | porting | Protocol normalization design: how to model the two-provider SSE adapter gap (OpenAI-compat vs Anthropic native) in a target language that may not have C# discriminated unions or `IAsyncEnumerable`. | Porting phase synthesis is the right place to recommend concrete adapter patterns. |
| prot-CF2 | porting | Concurrency model translation: the sibling-abort `CancellationTokenSource` + `Task.WhenAll` parallel read-only tool pattern requires explicit mapping to target language concurrency primitives. | Porting phase rubric covers portability synthesis. |

---

## Validation

| # | Criterion | Result | Evidence |
|---|-----------|--------|----------|
| 1 | An event catalog is documented. | PASS | §Event Catalog covers 4 protocol streams: OpenAI-compat SSE (B1), Anthropic SSE (B2), MCP JSON-RPC stdio (B3), and ConversationLoop inner stream (B4). Request/response shapes documented with concrete JSON examples. contr-CF3 closed. |
| 2 | A state machine is documented. | PASS | §State Machine covers 4 machines: ConversationLoop turn lifecycle (SM1) with full state/transition table, 12-step tool execution pipeline (SM2), MCP connection lifecycle (SM3), and Playbook step execution (SM4). Guards and side effects included. |
| 3 | Persistent schema notes are documented. | PASS | §Persistent Schema Notes covers Session JSONL, checkpoint sidecar, index.json, turn journal JSONL, playbook state JSON, playbook YAML frontmatter with full field inventories. contr-CF1 and contr-CF2 closed. arch-OQ3 (TurnJournal schema) resolved. |
| 4 | Compatibility hazards are documented. | PASS | §Compatibility Hazards — 13 entries covering async model, provider API differences, Qwen XML fallback, session JSONL header detection, file rewrite atomicity, MCP serialization, TUI layer, process kill semantics, template injection, YAML naming, and token counting. |
| 5 | Findings are marked with evidence levels. | PASS | All claims tagged `observed fact`, `strong inference`, or `portability hazard`. 3 open questions routed to `open_questions`; 2 carry-forward items routed to `porting` phase. |

**Validated by:** 2026-05-24 (protocols phase — implementing session)
**Overall:** PASS
