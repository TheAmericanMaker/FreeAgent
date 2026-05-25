# Reverse-Engineering Bundle — OpenMonoAgent.ai

<!-- Phase: porting | Pipeline: workflow/pipeline.yaml | Closes: prot-CF1, prot-CF2 -->

---

## System Summary

OpenMonoAgent.ai is a self-hosted, local-first AI coding agent for software developers who want full control over inference cost and data privacy. It is written in .NET 10 C# and ships as a pair of Docker containers: one running a llama.cpp inference server on port 7474, and one running the agent binary that connects to it. The agent implements a complete agentic loop — it streams tokens from the LLM, dispatches tool calls through a 12-step permission-and-execution pipeline, detects and aborts runaway iteration cycles, manages context-window pressure through LLM-generated checkpoints and message compaction, and persists full session state as JSONL files. It targets developers on Linux workstations with NVIDIA GPUs as the primary platform. *observed fact — README.md, docs/ARCHITECTURE.md, architecture-map.md §System Intent.*

The agent's design composes four independently portable concerns: a **provider adapter** layer that normalizes two different LLM SSE wire formats (OpenAI-compatible and Anthropic native) into a single `StreamChunk` stream; a **session and tool runtime** that owns the agentic loop, doom-loop detection, tool execution pipeline, and permission enforcement; a **persistence layer** that writes sessions as JSONL with checkpoint sidecars and a session index; and a **rendering layer** that presents streaming output as either a full-screen ANSI TUI (Spectre.Console) or a classic scrolling REPL. The boundaries between these concerns are clean and can be ported independently. *strong inference — dependency graph in architecture-map.md §Layer Map; provider normalization confirmed in protocols-and-state.md §B1, §B2.*

Extensibility is built into the product via three mechanisms: **MCP servers** (external tool providers via JSON-RPC 2.0 over stdio), **Playbooks** (YAML-defined multi-step workflows with parameter templates, gates, and per-step scripts), and **sub-agents** (inline nested agentic loops with filtered tool sets and their own turn budgets). All three route through the same 12-step tool execution pipeline as built-in tools, which means the trust model is uniform. A reimplementation that reproduces the session runtime, provider adapters, and tool pipeline — and gets the permission model and context-pressure management right — will be functionally equivalent on the critical path. *strong inference — ConversationLoop.cs structure, AgentTool.cs, McpToolAdapter.cs.*

---

## Layer Map With Ownership

| Layer / Module | Role | Owns |
|---|---|---|
| **Product Shell** (`openmono` bash + Docker) | Host-side orchestrator and deployment wrapper | Docker lifecycle (`start`/`stop`/`restart`), `openmono agent` launch, `openmono config`, tunnel management, install scripts |
| **Agent Runtime** (`Session/ConversationLoop`, `Session/SessionState`) | Core agentic loop; highest fan-in component | Turn lifecycle, doom-loop detection, context-pressure triggers, in-flight parallelism, iteration cap (1000), per-turn streaming coordinator |
| **Session Persistence** (`Session/SessionManager`, `Session/Checkpointer`, `Session/Compactor`) | Durable session state and context pressure management | JSONL read/write, checkpoint sidecar, session index, `/resume`, 65%/80% pressure thresholds |
| **LLM Provider Adapters** (`Llm/OpenAiCompatClient`, `Llm/AnthropicClient`, `Llm/ProviderRegistry`) | Wire-format normalization; provider-agnostic stream abstraction | SSE stream parsing per provider, `StreamChunk` normalization, 3-attempt retry before stream, Qwen XML tool-call fallback |
| **Tool Layer** (`Tools/ToolRegistry`, `Tools/ToolDispatcher`, 20 built-in tools, `Tools/SchemaValidator`, `Tools/SanityCheck`) | Tool definitions, validation, and dispatch | JSON schema validation, path-escape checks, tool contract enforcement, plan-mode guard |
| **Permission Layer** (`Permissions/PermissionEngine`, `Permissions/Capability`, `Permissions/PathGuard`) | Fine-grained authorization with interactive prompting | Capability-based check path, legacy glob-match path, session-scoped allow/deny state, hardcoded binary/path blocks |
| **Cache and Artifact Layer** (`Session/ToolResultCache`, `Session/ArtifactStore`) | Read-only result caching and large-output storage | Cache key construction (SHA-256 of normalized JSON), mtime+size invalidation, 50,000-char artifact threshold |
| **MCP Integration** (`Mcp/McpClient`, `Mcp/McpServerManager`, `Mcp/McpToolAdapter`) | External tool server connections | stdio JSON-RPC 2.0 subprocess lifecycle, initialize handshake, tool registration, serialized request queue (`SemaphoreSlim(1,1)`) |
| **LSP Integration** (`Lsp/LspServerManager`, `Lsp/LspClient`, `Lsp/LspTool`) | Language-server intelligence tools | JSON-RPC over stdio for language servers; lazy-start on first use |
| **Hook Runner** (`Hooks/HookRunner`) | Pre/post-tool extensibility via shell scripts | 30 s timeout per hook, non-blocking failure, `{{tool_name}}` / `{{tool_input}}` / `{{tool_output}}` substitution |
| **Playbook Automation** (`Playbooks/PlaybookExecutor`, `PlaybookLoader`, `PlaybookRegistry`, `TemplateEngine`) | YAML-defined multi-step workflow execution | Step ordering (topological sort), gate prompts, template resolution (9 substitution tokens), per-step script validation, playbook state persistence and resume |
| **Sub-agent Feature** (`Agents/AgentTool`, `Agents/AgentDefinition`, `Agents/BuiltInAgents`) | Inline nested agentic loops | 5 named agent types, turn budgets, tool set filtering, parent PermissionEngine reuse, ephemeral SessionState |
| **UI / Rendering** (`Rendering/AnsiTuiRenderer`, `Rendering/TerminalRenderer`) | Terminal output surfaces | Full-screen ANSI TUI (Spectre.Console) and scrolling classic mode; renderer chosen at startup by I/O redirection detection |
| **Configuration** (`Config/AppConfig`, `Config/ConfigLoader`) | Multi-source config merge | 7-layer precedence (defaults → user JSON → project JSON → explicit file → env vars → server probe → CLI flags), additive merge for permission/hook lists |
| **Slash Commands** (`Commands/` — 14 commands) | User-accessible session control | `/undo`, `/checkpoint`, `/compact`, `/resume`, `/plan`, `/export`, `/model`, `/retry`, `/status`, `/stats`, `/init`, `/debug`, `/think`, `/clear` |
| **Infrastructure** (`Utils/ProcessRunner`, `Utils/GitHelper`, `Utils/Log`, `Utils/PathUtils`, `Utils/SecretScanner`, `Utils/ProcessWatchdog`) | Cross-cutting utilities | Process execution, git queries, logging, path normalization, watchdog for hard-kill |
| **Memory Store** (`Memory/MemoryStore`, `Memory/MemorySaveTool`) | Cross-session agent memory | YAML frontmatter files; loaded into system prompt at session start |
| **File History** (`History/FileHistory`, `History/FileSnapshot`) | In-session undo snapshots | In-memory only; lost on session exit; not recoverable via `/resume` |

*observed fact — architecture-map.md §Layer Map, Program.cs DI wiring.*

---

## Feature Contract Table

| Feature | Surface | Priority | Key Contracts | Notes |
|---|---|---|---|---|
| **Agentic loop** (turn lifecycle, iteration cap) | Session runtime | core | Runs up to 1000 LLM iterations per turn; ends on text-only response; resets doom-loop counter each turn | README says "25 iterations" — code cap is 1000; code is authoritative (arch-OQ5) |
| **SSE streaming — OpenAI-compat** | Provider adapter | core | HTTP SSE `data: <json>` lines terminated by `data: [DONE]`; tool call deltas accumulated by `index`; 3-attempt retry before stream; Qwen XML `<function=...>` fallback | All llama.cpp + OpenAI + Ollama inference routes through this adapter |
| **SSE streaming — Anthropic native** | Provider adapter | core | HTTP SSE `data: <json>` lines terminated by `message_stop` event type; tool use via `content_block_start`/`content_block_stop` envelope; system message extracted to top-level `system` field | Wire format differs significantly from OpenAI; port needs dedicated adapter |
| **`StreamChunk` normalization** | Provider adapter | core | Both providers produce identical `StreamChunk` values (`ThinkingDelta`, `TextDelta`, `ToolCallDelta`, `IsComplete`, `Usage`); consumer is provider-agnostic | The abstraction boundary is `ILlmClient.StreamChatAsync`; porting must preserve this contract |
| **12-step tool execution pipeline** | Session runtime | core | Steps: parse → schema-validate → sanity-check → plan-mode-guard → capability-check → cache-lookup → pre-hook → execute → post-hook → artifact-store → cache-write → invalidate | Every tool call, regardless of source, traverses all 12 steps in order |
| **Permission engine — capability model** | Permission layer | core | 7 capability subtypes; 9-step evaluation order; session-scoped allow/deny; hardcoded binary and path blocks; interactive prompting for uncovered caps | Primary auth model; capability model > legacy model |
| **Permission engine — legacy glob model** | Permission layer | core | Used when `RequiredCapabilities.Count == 0`; 7-step evaluation order; glob matching (anchored `^...$`, case-insensitive) | Fallback for tools not yet migrated to capability model |
| **Session JSONL persistence** | Session persistence | core | Full rewrite on every `SaveAsync` (not append-only); line 0 = header with `session_id`; subsequent lines = `Message` records with `role`/`content`/`toolCalls`/`toolCallId`/`toolName`/`timestamp` | Concurrent save can corrupt; no atomic rename; ports should use write-to-temp + rename |
| **Session index** | Session persistence | core | `index.json` full rewrite on every update; `SessionSummary` with `id`, `startedAt`, `turnCount`, `totalTokens`, `workingDirectory`, `firstMessage` | Used by `/resume` picker (most recent 10) |
| **Multi-source config merge** | Configuration | core | 7-layer precedence; `llm.*` last-non-zero wins; `permissions.tools` and `hooks.*` are additive | Missing file is silent; malformed JSON warns and is skipped; no field-level validation |
| **Doom-loop detection** | Session runtime | important | 3 identical consecutive tool-call batches → inject break message as assistant turn; counter resets per turn | Safety invariant; without it agents can spin indefinitely |
| **Context checkpointing** (65% threshold) | Session persistence | important | LLM-generated summary stored to `.checkpoints.json` sidecar; future context window = checkpoint summary + messages from `cutoffMessageIndex` onward | Auto-triggered at ≥ 65% of `context_size` tokens (from `TokenTracker`) |
| **Context compaction** (80% fallback) | Session persistence | important | Summarize all but last 4 turns; mutate `SessionState.Messages`; full JSONL overwrite | Falls back if checkpoint unavailable; irreversible in-memory mutation |
| **Read-only tool parallelism** | Session runtime | important | `IsConcurrencySafe` tools start as `inFlightTasks` during LLM stream; remaining read-only tools batch via `Task.WhenAll`; sibling-abort if any task crashes (`ResultClass.Crash`) | Two parallelism windows: during stream and after stream; sibling-abort via `siblingAbortCts` |
| **MCP tool integration** | Integration adapter | important | JSON-RPC 2.0 over stdio; serialized (one request at a time); `initialize` → `notifications/initialized` → `tools/list` handshake; tool registration as `mcp__{server}__{name}`; always `PermissionLevel.Ask` | No reconnection; session must restart on connection failure |
| **Full-screen TUI** (`AnsiTuiRenderer`) | UI / Rendering | important | Spectre.Console full-screen; streams thinking panel + text panel + tool events + status bar in real time | Primary differentiator UX; Spectre.Console is .NET-specific |
| **Playbook automation** | Playbook layer | important | YAML frontmatter in `PLAYBOOK.md`; topological step ordering; 9 template tokens; 4 gate types; per-step 10-iteration tool loop cap; state persisted and resumable | `{{shell:<cmd>}}` is an injection surface; ports must sanitize or drop |
| **Provider hot-swap** (`/model`) | LLM provider | important | Replace active `ILlmClient` mid-session; subsequent turns use new provider; no session state persisted | Requires ProviderRegistry to instantiate clients by name at runtime |
| **Sub-agent execution** | Agent layer | optional | 5 named types with distinct turn budgets and tool filters; parent PermissionEngine reused; no session file; no doom-loop detection; no ArtifactStore; self-spawn prevented | `AgentTool.IsConcurrencySafe = true` — sub-agent can start as in-flight task |
| **Roslyn semantic analysis** (`RoslynTool`) | Tool layer | optional | 8 actions using `AdhocWorkspace`; C#-specific; non-portable to non-.NET runtimes | Omit in ports targeting other languages; replace with language-native AST tools |
| **LSP integration** | Integration adapter | optional | Lazy-start language servers; JSON-RPC over stdio; independently portable | LSP 3.17 wire format not fully read; not a porting blocker |
| **Cross-session memory** (`MemoryStore`) | Persistence | optional | YAML frontmatter files in `~/.openmono/memory/`; index injected into system prompt at session start | Replaceable with any K/V store or file-based system |
| **Bash hooks** (`HookRunner`) | Integration adapter | optional | Pre/post-tool shell hooks; 30 s timeout; non-fatal; receives `{{tool_name}}`, `{{tool_input}}`, `{{tool_output}}` | Bash-specific; ports can substitute OS-level process runner |
| **Turn journal** (`TurnJournal`) | Observability | optional | Append-only JSONL; 10 event types; SHA-256 args hash; incomplete-call detection | Audit trail; not required for agent function |
| **File undo** (`/undo`) | Session control | optional | In-memory `FileSnapshot` per write; lost on exit; not recoverable via `/resume` | No persistence; simple stack of pre-write file contents |
| **Classic mode** (`TerminalRenderer`) | UI / Rendering | incidental | Scrolling REPL for redirected I/O; no full-screen | Trivial adapter; auto-selected when stdin or stdout is redirected |
| **`openmono` bash launcher** | Product shell | incidental | Docker orchestration and install scripts | Platform/deployment wrapper; not the agent itself |
| **llama-server Docker container** | Deployment artifact | incidental | Any OpenAI-compat endpoint substitutes | Model serving; not agent logic |

*observed fact / strong inference — architecture-map.md §Porting Priorities, behavioral-contracts.md §Feature Contracts.*

---

## Protocol and State Notes

### Normalized Stream Abstraction

The core session runtime consumes a single abstract stream type, `IAsyncEnumerable<StreamChunk>`, regardless of which LLM provider is active. `StreamChunk` carries five discriminated payload types: `ThinkingDelta` (reasoning panel), `TextDelta` (output panel), `ToolCallDelta` (queued tool invocation), `Usage` (token counts, final chunk only), and `IsComplete` (stream end signal). This normalization is the primary portability lever: porting the two wire-format parsers is isolated work that does not touch the session runtime. *observed fact — `Llm/ILlmClient.cs`, protocols-and-state.md §B1, §B2.*

**OpenAI-compat wire format (B1):** HTTP 1.1 chunked SSE; events are `data: <json>` lines; stream ends with `data: [DONE]`. Tool call arguments accumulate across chunks correlated by `choices[0].delta.tool_calls[].index`. Usage appears in the final chunk only when `stream_options.include_usage=true` is set in the request. A Qwen XML fallback exists: if text contains `<function=name>`, the text stream is suppressed and tool calls are extracted by regex at `[DONE]` time. *observed fact — `OpenAiCompatClient.cs:82-310`.*

**Anthropic native wire format (B2):** HTTP 1.1 SSE; events carry a `type` discriminator field; stream ends with `type: "message_stop"` (no `[DONE]` sentinel). Tool use appears inside `content_block_start` (with `type: "tool_use"`) / `input_json_delta` / `content_block_stop` envelopes. Request body differs from OpenAI: system message is a top-level `system` field; tool results are `user` messages with `tool_result` content blocks; tools use `input_schema` not `parameters`. *observed fact — `AnthropicClient.cs:95-193`.*

**Both adapters retry the full request** up to 3 times (delays: 1 s, 4 s, 16 s) on HTTP 429/500/502/503/504 or network error before the stream starts. Mid-stream failures are not retried. *observed fact — protocols-and-state.md §B1.*

### prot-CF1 Closure — Provider Adapter Design for Porting

The C# implementation uses `ILlmClient` as the abstraction boundary (interface with a single `StreamChatAsync` method returning `IAsyncEnumerable<StreamChunk>`). The discriminated union character of `StreamChunk` is achieved by a single class with nullable fields, not a C#-native union type. A target language that lacks sum types should replicate the same pattern: a single normalized event struct/record, with optional fields for each payload kind, produced by per-provider adapter modules.

**Recommended adapter pattern for common target languages:**

| Target Language | Async Stream Type | `StreamChunk` Shape | Sibling Provider Coexistence |
|---|---|---|---|
| **Go** | `chan StreamChunk` fed by goroutine | `struct StreamChunk { ThinkingDelta *string; TextDelta *string; ToolCallDelta *ToolCallDelta; Usage *Usage; IsComplete bool }` with pointer fields for optionality | Two structs implementing a `LlmProvider` interface with a `StreamChat(ctx, messages, tools) (<-chan StreamChunk, error)` method |
| **Python (asyncio)** | `async def stream_chat(...) -> AsyncIterator[StreamChunk]` | `@dataclass StreamChunk` with `Optional` fields, or a `TypedDict` | Two classes implementing an `LlmProvider` protocol with `async def stream_chat(...)` |
| **Rust (Tokio)** | `impl Stream<Item = Result<StreamChunk, LlmError>>` via `tokio_stream` | `struct StreamChunk` with `Option<T>` fields | Two structs implementing a `LlmProvider` trait |
| **TypeScript/Node.js** | `AsyncIterable<StreamChunk>` using an async generator | Interface with optional fields: `thinkingDelta?: string; textDelta?: string; toolCallDelta?: ToolCallDelta; usage?: Usage; isComplete?: boolean` | Two classes implementing an `LlmProvider` interface |

**Critical invariants that must survive the adapter boundary:**
1. Both adapters must produce identical `StreamChunk` values for equivalent LLM responses. The session runtime must not branch on provider identity.
2. The Qwen XML fallback must be inside the OpenAI-compat adapter, transparent to the consumer.
3. Tool call delta accumulation by index (OpenAI-compat) and by `content_block_start`/`content_block_stop` envelope (Anthropic) must both produce a complete `ToolCallDelta` with `id`, `name`, and `arguments` before the consumer processes it.
4. The `IsComplete` signal must only fire after all tool call deltas for that stream are fully accumulated — the consumer starts dispatching tools only after receiving this signal.

*strong inference — `ConversationLoop.cs:200-290`, `OpenAiCompatClient.cs`, `AnthropicClient.cs`.*

### prot-CF2 Closure — Concurrency Model Translation

The C# implementation uses two parallelism windows per turn. Understanding both is required for a faithful port.

**Window 1 — During LLM stream:** When `StreamChatAsync` yields a `ToolCallDelta` for a tool where `IsConcurrencySafe == true`, a task is immediately created (`Task.Run` + `CancellationToken`) and added to `inFlightTasks`. The stream continues; the tool executes in parallel with ongoing token delivery.

**Window 2 — After stream ends (`IsComplete` received):** All `inFlightTasks` are awaited via `Task.WhenAll`. Then remaining read-only tools (those not launched in Window 1) are batched with `Task.WhenAll`. Finally, writable tools are executed serially.

**Sibling-abort pattern:** A secondary `CancellationTokenSource` (`siblingAbortCts`) is created at the start of `ExecuteToolCallsWithInflightAsync`. Every parallel task receives this CTS's token. If any task returns `ResultClass.Crash`, `siblingAbortCts.Cancel()` is called, propagating cancellation to all remaining sibling tasks, which return `ToolResult.Cancelled("sibling abort")`.

**Language mappings for this pattern:**

**Go:**
```
// siblingAbortCts equivalent: context.WithCancel
ctx, cancelSiblings := context.WithCancel(parentCtx)
defer cancelSiblings()

var wg sync.WaitGroup
results := make([]ToolResult, len(tasks))
for i, task := range inFlightTasks {
    wg.Add(1)
    go func(i int, t Task) {
        defer wg.Done()
        result := t.Execute(ctx)
        if result.Class == Crash {
            cancelSiblings() // sibling abort
        }
        results[i] = result
    }(i, task)
}
wg.Wait()
```

**Python (asyncio):**
```python
# asyncio.gather does NOT auto-cancel siblings on exception
# Must use asyncio.create_task and explicit cancellation
sibling_abort_event = asyncio.Event()
async def run_with_abort(task, abort_event):
    async def _inner():
        try:
            return await task.execute()
        except Exception as e:
            abort_event.set()  # sibling abort
            raise
    done, pending = await asyncio.wait(
        [asyncio.create_task(_inner()),
         asyncio.create_task(abort_event.wait())],
        return_when=asyncio.FIRST_COMPLETED)
    # cancel pending on abort
    for p in pending:
        p.cancel()

tasks_coros = [run_with_abort(t, sibling_abort_event) for t in inflight_tasks]
results = await asyncio.gather(*tasks_coros, return_exceptions=True)
```

**Rust (Tokio):**
```rust
// Use tokio_util::sync::CancellationToken for sibling abort
let cancel_token = CancellationToken::new();
let handles: Vec<_> = inflight_tasks.iter().map(|task| {
    let ct = cancel_token.clone();
    tokio::spawn(async move {
        let result = task.execute(ct.clone()).await;
        if result.class == ResultClass::Crash {
            ct.cancel(); // sibling abort
        }
        result
    })
}).collect();
let results = futures::future::join_all(handles).await;
```

**TypeScript/Node.js:**
```typescript
const controller = new AbortController();
const promises = inflightTasks.map(task =>
    task.execute(controller.signal).then(result => {
        if (result.class === ResultClass.Crash) {
            controller.abort(); // sibling abort
        }
        return result;
    })
);
const results = await Promise.allSettled(promises);
```

**Critical invariants for the concurrency model:**
1. Window 1 (during-stream) and Window 2 (post-stream) are distinct: Window 1 tasks start before the stream ends; they must not block token delivery.
2. Writable tools must never run in parallel with each other or with read-only tools in Window 2.
3. The sibling-abort token must be separate from the user-triggered `CancellationToken` (Ctrl+C) — aborting siblings is not the same as cancelling the whole turn.
4. A tool receiving a sibling-abort cancellation must produce `ToolResult.Cancelled`, not `ToolResult.Crash`, so it does not trigger further sibling aborts.

*observed fact — `ConversationLoop.cs:233-239`, `ConversationLoop.cs:489-607`.*

### State Machines Summary

Four state machines govern runtime behavior. Full transition tables are in protocols-and-state.md.

- **SM1 — ConversationLoop Turn Lifecycle:** `Idle → ContextPressureCheck → Streaming ↔ ToolExecution → TurnComplete`. Key transition: `IsComplete` with tool calls pending → `DoomLoopCheck` (3-strike guard) → `ToolExecution`. Hard cap at 1000 iterations.
- **SM2 — 12-Step Tool Execution Pipeline:** Linear `ParseArguments → SchemaValidate → SanityCheck → PlanModeGuard → CapabilityCheck → CacheLookup → PreHook → Execute → PostHook → ArtifactStore → CacheWrite → Invalidate`. Any step can terminate the pipeline with a `ToolResult.Error` or `.Cancelled`.
- **SM3 — MCP Connection Lifecycle:** `Disconnected → Spawning → Initializing → SendingNotification → Ready → Registered`. No reconnection; connection failure is terminal for the server (agent session must restart to recover).
- **SM4 — Playbook Step Execution:** `Pending → StepLoop → GetContent → GatePrompt? → RunStep → ScriptCheck → CompleteStep → Done`. Topological step ordering; 10-iteration per-step tool loop cap; state file persisted after each step for resume.

### Persistence Schemas Summary

Full schemas are in protocols-and-state.md §Persistent Schema Notes. Key porting-relevant points:

- **Session JSONL:** Full rewrite on every save (not append-only); header identified by `"session_id"` substring (fragile — any message body containing this string corrupts the header detection); no atomic write.
- **Checkpoint sidecar:** JSON array of checkpoint records; loaded on `/resume`; messages before `cutoffMessageIndex` are replaced by summary in context window.
- **Session index:** `index.json` full rewrite; filtered by `workingDirectory` in `ListSessionsAsync`.
- **Turn journal:** Append-only JSONL with `AutoFlush=true`; opened lazily; 10 event types with `type` discriminator; SHA-256 args hash for privacy.
- **Playbook state:** Full rewrite after each step; resume by passing loaded state as `resumeFrom` argument.
- **Artifact store:** Written when tool output exceeds 50,000 chars; first 20 + last 10 lines in model preview with omission marker.

---

## Portability Hazards

Consolidated from all three prior phases. Hazards inherited from the protocols phase are marked [prot]; from architecture are marked [arch].

| Hazard | Source Phase | Impact | Mitigation |
|---|---|---|---|
| `.NET async/await` + `Task.WhenAll` + `CancellationToken` — no direct equivalent in most other runtimes | architecture [arch], protocols [prot] | High — affects the entire session runtime's concurrency model | Map to goroutines+context (Go), asyncio.gather+explicit cancel (Python), Tokio streams (Rust), async/await + AbortController (TypeScript); see prot-CF2 closure above |
| `IAsyncEnumerable<StreamChunk>` — lazy pull-based async stream not available in all languages | protocols [prot] | High — entire token delivery and tool dispatch loop depends on this | Map to Go channel, Python async generator, Rust Stream trait, JS AsyncIterable; see prot-CF1 closure above |
| OpenAI vs Anthropic request body shapes are fundamentally different (system field, tool role, `input_schema`, content blocks) | protocols [prot] | High — any port must maintain per-provider adapters; sharing request construction is not possible | Two separate `BuildRequestBody` implementations; normalize output to shared `StreamChunk` |
| Qwen XML `<function=...>` tool call format — non-standard; suppresses text stream and parses at `[DONE]` | protocols [prot] | Medium — silently drops tool calls if Qwen models are used and fallback is omitted | Include Qwen XML detection and regex-based extraction in the OpenAI-compat adapter |
| Session JSONL header identified by `"session_id"` substring check — breaks if any message content contains this literal | protocols [prot] | Medium — corrupts session load; `SessionManager.LoadAsync:87` | Replace with proper discriminated deserialization (e.g., try-parse as header type; fall back to message type) |
| Session JSONL full rewrite on save — concurrent saves can produce partial files; no atomic rename | protocols [prot] | Medium — data loss on SIGTERM mid-save | Ports should write to a temp file and atomically rename into place |
| `SemaphoreSlim(1,1)` serialization of all MCP requests — high-latency servers stall the agent loop | protocols [prot] | Medium — performance degradation with slow MCP servers | Ports targeting higher throughput may want async pipelining with response correlation by `id` |
| `Spectre.Console` full-screen ANSI TUI — .NET-specific library; alternate screen buffer, cursor positioning | architecture [arch], protocols [prot] | Medium — primary UX surface is not portable | Replace with target-language TUI library (e.g., `bubbletea` Go, `textual` Python, `ratatui` Rust, `ink` Node.js) or a web UI; the session runtime is TUI-independent via `IRenderer` |
| `process.Kill(entireProcessTree: true)` — .NET 5+ API on Linux/macOS | protocols [prot] | Medium — subprocess cleanup fails to kill grandchild processes without this API | Use `os.killpg` (Python/Go via syscall), `libc::killpg` (Rust), or process group management in the target runtime |
| Playbook `{{shell:<cmd>}}` token executes arbitrary shell commands embedded in YAML — no sanitization | protocols [prot] | Medium — security risk in multi-user or untrusted-playbook scenarios | Ports should either remove shell substitution or scope it behind an explicit user-trust boundary |
| `TiktokenSharp` for token counting — .NET-specific tokenizer package | architecture [arch] | Medium — context-pressure thresholds (65%, 80%) depend on accurate counts | Replace with target-language tokenizer for the active model family (e.g., `tiktoken` Python, `tiktoken-go`, `tokenizers` Rust) |
| YamlDotNet `HyphenatedNamingConvention` — all playbook YAML keys use hyphens | protocols [prot] | Low — camelCase or snake_case parsers will fail to bind fields | YAML parser in the port must use hyphenated key mapping |
| 12-char hex session IDs (`Guid.NewGuid().ToString("N")[..12]`) — first 12 chars of a v4 GUID; not cryptographically uniform | protocols [prot] | Low — adequate for file naming; not for security | Use `crypto/rand` (Go), `secrets.token_hex(6)` (Python), or `rand::random::<u64>()` (Rust) for equivalent or stronger uniqueness |
| `global.json` SDK version pin not read — exact .NET 10 preview or stable SDK unknown | architecture [arch] | Low — reproducibility of build toolchain | Read `global.json` before attempting a .NET build; confirm SDK version matches |
| `Terminal.Gui` package present in `.csproj` but not confirmed used at runtime | architecture [arch] | Low — dead dependency or latent feature | Grep for `Terminal.Gui` usage before porting; if unused, omit |
| `Microsoft.CodeAnalysis.CSharp.Workspaces` (Roslyn) — `RoslynTool` is C#-specific | architecture [arch] | Low for non-C# ports — `RoslynTool` cannot be ported to non-.NET runtimes | Omit or replace with language-native AST tooling |
| `SecretScanner` integration point not confirmed | contracts | Low — unknown whether outputs are scanned for secrets before returning to LLM | Grep for `SecretScanner` call sites; confirm or implement equivalent |
| `Ask` permission list in `ToolPermissionRules` loaded but not evaluated | contracts | Low — config field described in docs as "always prompt" but is dead code | Ports should either implement the described behavior or remove the field; do not replicate dead config |

---

## Defect Synthesis

The `full-with-deep-audit` pipeline produced two defect reports: `findings/defect-scan-mechanical/mechanical-defects.md` (14 findings) and `findings/defect-scan-semantic/semantic-defects.md` (13 findings). For porting, the important lesson is not to clone the current implementation literally: preserve the behavioral contracts, but deliberately fix crash-safety, protocol pairing, hook execution, and background-process lifecycle.

| Defect ID | Source Report | One-line Description | Severity | Porting Recommendation |
|-----------|---------------|----------------------|----------|------------------------|
| M1.1 | Mechanical Pass 1 | `SessionManager.LoadAsync` skips any JSONL line containing `"session_id"`, dropping legitimate messages. | High | fix before porting — parse header by position/schema, not substring. |
| M1.2 / S5.2 | Mechanical Pass 1 + Semantic Pass 5 | LSP response reader ignores expected JSON-RPC id and can return the wrong response. | High | port differently — implement id-aware JSON-RPC dispatcher with notification handling. |
| M2.1 / S5.5 | Mechanical Pass 2 + Semantic Pass 5 | Session/index/checkpoint writes are non-atomic and can corrupt durable recovery state. | High | fix before porting — temp-file + atomic rename for all durable writes. |
| M2.2 | Mechanical Pass 2 | `ProcessRunner` reads stdout then stderr sequentially, risking pipe deadlock. | High | fix before porting — concurrent bounded stream drains and process-tree kill on timeout. |
| S3.1 | Semantic Pass 3 | MCP/LSP subprocess stderr is redirected but never drained. | High | port differently — every subprocess adapter needs stdout/stderr pump ownership. |
| S3.2 / S5.1 | Semantic Pass 3 + Pass 5 | Hook timeout warns but does not kill the hook process; side effects can continue after pipeline advances. | High | fix before porting — hard timeout must kill the hook process tree before continuing. |
| S4.1 | Semantic Pass 4 | Hook variables are interpolated directly into `/bin/bash -c`, creating shell injection risk. | High | port differently — pass hook data through env/stdin/argv, not shell templates. |
| M1.4 | Mechanical Pass 1 | Numeric config merge cannot intentionally set valid zero values such as deterministic temperature. | Medium | fix before porting — use nullable overlay config or explicit presence markers. |
| M6.1 / M6.2 | Mechanical Pass 6 | Env/file config lacks central validation and silently ignores malformed env overrides. | Medium | fix before porting — validate after all layers merge, fail/warn explicitly. |
| S3.3 | Semantic Pass 3 | Background Bash lacks process registry, cleanup, readiness, and recovery model. | Medium | port differently — build a background-process manager or leave feature out of MVP. |
| S4.2 | Semantic Pass 4 | `FileRead from_cursor` bypasses per-file capability checks by returning no capabilities. | Medium | fix before porting — authorize resolved cursor contents or preserve capability provenance. |
| S5.3 | Semantic Pass 5 | `Ask` permission rules are loaded but unused. | Medium | port differently — either implement `ask` precedence or remove/deprecate it. |
| S5.4 | Semantic Pass 5 | `StreamChunk` consumer drops mixed fields when `ThinkingDelta` is present. | Medium | fix before porting — process all optional fields or constrain provider adapters to one-field chunks. |

**Porting policy derived from defects:**

1. **Preserve behavior as tests, not source structure.** The loop semantics, permission prompts, stream normalization, and session persistence should be black-box acceptance tests before any rewrite.
2. **Fix crash-safety and process lifecycle in the new design.** Non-atomic writes, untracked background processes, undrained subprocess stderr, and non-killing hook timeouts are implementation defects, not compatibility requirements.
3. **Make trust boundaries explicit.** Hooks, Bash, MCP servers, LSP servers, and provider streams are all external-input boundaries. Reimplementation should pass data as data, authorize after resolution, and keep subprocess output pumps under one owner.
4. **Narrow the MVP.** The safest first port should include the agent loop, OpenAI-compatible provider, tool pipeline, permissions, atomic session store, and basic file/bash tools. Defer TUI, MCP, LSP, hooks, sub-agents, playbooks, and background process management until the kernel is stable.

---

## Observed Facts vs. Inferred Structure

### Observed Facts

*Direct statements from code, docs, tests, and schemas.*

- `ConversationLoop.cs:136` sets `maxIterations = 1000` — the main loop iteration cap is 1000, not 25 as README states. *observed fact.*
- `ArtifactStore.DefaultLargeOutputThreshold = 50_000` chars — artifact threshold is 50,000, not 20,000 as architecture docs stated. *observed fact — ArtifactStore.cs:10.*
- `OpenAiCompatClient` retries 3 times with delays 1 s, 4 s, 16 s on HTTP 429/500/502/503/504 before the stream starts. *observed fact — OpenAiCompatClient.cs:82-310.*
- `AnthropicClient` normalizes to the same `StreamChunk` type as `OpenAiCompatClient`. *observed fact — `Llm/ILlmClient.cs`, AnthropicClient.cs:95-193.*
- `McpClient` serializes all requests with `SemaphoreSlim(1,1)` — one request in flight at a time. *observed fact — McpClient.cs.*
- `siblingAbortCts.Cancel()` is called when any parallel task returns `ResultClass.Crash`. *observed fact — ConversationLoop.cs:590-607.*
- `HookRunner` applies a 30 s timeout per hook; timeout is non-fatal; tool execution proceeds. *observed fact — HookRunner.cs:100.*
- `PermissionEngine.CheckCapabilitiesAsync` has a 9-step evaluation order; steps 4–8 cover deny rules before allow rules before auto-allow before prompting. *observed fact — PermissionEngine.cs:44-93.*
- `ConfigLoader` precedence order: defaults → `~/.openmono/settings.json` → `.openmono/settings.json` → `--config` path → env vars → server probe → CLI flags. *observed fact — docs/CONFIG.md:68-76, ConfigLoader.cs.*
- `SessionManager.SaveAsync` uses `append: false` on `StreamWriter` — full rewrite. *observed fact — SessionManager.cs:25.*
- MCP tool registration format: `mcp__{serverName}__{mcpToolName}`; always `RequiredPermission = PermissionLevel.Ask`. *observed fact — McpToolAdapter.cs:32-34.*
- `AgentTool.IsConcurrencySafe = true` — sub-agents can run as in-flight tasks. *observed fact — AgentTool.cs.*
- `AgentTool` explicitly excludes itself from sub-agent tool registries (`if (tool.Name == "Agent") continue`). *observed fact — AgentTool.cs:43.*
- `PlaybookExecutor.ResolveStepOrder` does a DFS over `step.Requires` for topological sort; cycles are not detected. *observed fact — PlaybookExecutor.cs:282-303.*
- `PlaybookExecutor.RunStepAsync` has a 10-iteration per-step tool loop cap. *observed fact — PlaybookExecutor.cs:215.*
- Cache key format: `{toolName}:{first_16_hex_chars_of_SHA256(NormalizeJson(input))}`. *observed fact — ToolResultCache.BuildCacheKey.*
- Denial tracking: 3 consecutive or 20 total denials triggers escalation path. *observed fact — PermissionEngine.TrackDenial:322-335.*
- `ToolResult.IsError` is `true` for all non-`Success` classes; LLM always receives `result.Content` regardless of class. *observed fact — ToolResult.cs.*

### Inferred Structure

*Architectural conclusions drawn from multiple facts.*

- The system is cleanly layered with no dependency cycles. The one re-entrant case (`AgentTool` creates a nested `ConversationLoop`) is an intentional design feature that reuses parent-layer types without creating cross-package imports. *strong inference.*
- The `StreamChunk` normalization layer is the cleanest porting seam in the system: porting both LLM adapters is bounded, well-defined work with no leakage into the session runtime. *strong inference.*
- Plan mode is implemented as a tool-list filter at the `ConversationLoop` level and as an early-exit guard in the tool execution pipeline — not as a capability. This means plan mode bypass cannot be configured via the permission system. *strong inference — ConversationLoop.cs:118-120, ConversationLoop.cs:665-673.*
- `FileHistory` is not serialized to JSONL (not present in `Message` record fields or `SessionHeader`). `/undo` functionality is irrecoverably lost on session exit. *strong inference — SessionState.cs, Message.cs field inventory.*
- `SessionMetadata.PlanMode`, `ThinkingEnabled`, and `LastPlan` are in-memory fields not persisted to JSONL — session resume does not restore plan mode or thinking state. *strong inference — prot-OQ2.*
- The `Ask` permission list is dead config: it is defined in `AppConfig`, loaded by `ConfigLoader`, described in docs, but is not referenced in `PermissionEngine.CheckAsync` or `CheckCapabilitiesAsync`. *strong inference — PermissionEngine.cs grep for `rules.Ask` returns no matches.*
- `Terminal.Gui` is a NuGet dependency in `.csproj` but is not the primary TUI library used at runtime; `AnsiTuiRenderer` uses Spectre.Console. Whether `Terminal.Gui` is actively used or a dead dependency is unconfirmed. *strong inference — arch-OQ4, still open.*

---

## Domain Glossary

| Term | Definition | Where Used |
|---|---|---|
| **Turn** | One user input → one or more LLM iterations → final text-only LLM response cycle. A turn ends only when the LLM produces text with no tool calls. | `ConversationLoop.RunTurnAsync`, JSONL session files |
| **Session** | A persistent conversation with its own JSONL file, `SessionState` ID, and optional checkpoint sidecar. Resumable via `/resume`. | `Session/`, `~/.openmono/sessions/` |
| **Agentic loop** | The iterative cycle within a turn: stream LLM → dispatch tools → inject results → re-stream LLM. Bounded by `maxIterations = 1000`. | `ConversationLoop.cs:130-302` |
| **Tool call** | An LLM-initiated invocation of a named tool with a JSON arguments object. Always traverses the 12-step pipeline. | `ToolCall` record, `Session/ConversationLoop` |
| **Doom loop** | Condition where the LLM emits the same tool-call batch 3 consecutive times within one turn. Detected and broken by injecting an assistant message. | `DoomLoopDetector`, `ConversationLoop.cs:288-300` |
| **Checkpoint** | An LLM-generated summary of session history written to a `.checkpoints.json` sidecar. Future turns build context from `cutoffMessageIndex` onward; earlier messages are represented by the summary. | `Session/Checkpointer`, `.checkpoints.json` |
| **Compaction** | In-memory summarization of all but the last 4 turns; mutates `SessionState.Messages` and rewrites the JSONL. Less sophisticated than checkpointing; used as fallback at ≥ 80% context utilization. | `Session/Compactor` |
| **Context pressure** | The ratio of token usage (from `TokenTracker`) to `config.Llm.ContextSize`. Drives automatic checkpointing (≥ 65%) and compaction (≥ 80%). | `ConversationLoop.cs:113-114` |
| **StreamChunk** | Normalized LLM stream event emitted by both provider adapters. Carries optional fields: `ThinkingDelta`, `TextDelta`, `ToolCallDelta`, `Usage`, `IsComplete`. | `Llm/ILlmClient.cs`, `ConversationLoop.cs` |
| **Provider** | Named LLM backend (llama-server, OpenAI, Anthropic, Ollama). Hot-swappable via `/model`. Each provider has an `ILlmClient` implementation that normalizes to `StreamChunk`. | `Llm/ProviderRegistry`, `Llm/ILlmClient.cs` |
| **Capability** | A fine-grained authorization unit naming a specific resource (file path, binary, network host) and operation. 7 subtypes: `FileReadCap`, `FileWriteCap`, `ProcessExecCap`, `NetworkEgressCap`, `VcsMutationCap`, `MemoryCap`, `AgentSpawnCap`. | `Permissions/Capability.cs` |
| **Concurrency-safe tool** | A tool with `IsConcurrencySafe = true` and `IsReadOnly = true`. Can run in parallel with other concurrency-safe tools during the LLM stream (Window 1) or after it (Window 2). | `ITool.IsConcurrencySafe`, `ConversationLoop.cs:233` |
| **In-flight task** | A `Task` for a concurrency-safe tool started during LLM streaming (Window 1), before the stream ends. Awaited in `ExecuteToolCallsWithInflightAsync`. | `ConversationLoop.cs:233-239` |
| **Sibling abort** | Cancellation propagated to all remaining in-flight parallel tool tasks when any one of them returns `ResultClass.Crash`. | `ConversationLoop.cs:590-607`, `siblingAbortCts` |
| **Plan mode** | A session-scoped toggle restricting the tool list to read-only tools only. Activated via `/plan` command or LLM `EnterPlanMode`/`ExitPlanMode` tools. Not persisted to JSONL. | `Commands/PlanCommand`, `Tools/PlanModeTool` |
| **WorkingDirectory** | The sandbox root for all file operations. All file path checks in `SanityCheck` and `PathGuard` are relative to this directory. Set by `--workdir` or `OPENMONO_WORKSPACE`. | `Config/AppConfig`, `Tools/SanityCheck` |
| **OPENMONO.md** | Per-project instructions file injected into the system prompt at session start. Equivalent to a project-level context injection point. | `Program.cs:606-648`, project root |
| **MCP server** | External tool provider connected via JSON-RPC 2.0 over stdio. Tools registered as `mcp__{server}__{name}`; always require user permission. | `Mcp/McpClient`, `Mcp/McpServerManager` |
| **Sub-agent** | A named inline agentic loop for delegated subtasks. Reuses parent `PermissionEngine`; ephemeral `SessionState`; no doom-loop detection, ArtifactStore, or TurnJournal. | `Agents/AgentTool`, `Agents/AgentDefinition` |
| **Playbook** | A YAML-defined multi-step workflow stored in a `PLAYBOOK.md` file with YAML frontmatter. Loadable by name; supports parameters, gates, template substitutions, and step-level scripts. | `Playbooks/`, `~/.openmono/playbooks/` |
| **TUI mode** | Full-screen ANSI terminal mode using Spectre.Console. Active when stdin and stdout are both a terminal and `--classic` is not passed. | `Rendering/AnsiTuiRenderer` |
| **Classic mode** | Scrolling REPL mode. Active when stdin or stdout is redirected, or `--classic` is passed. | `Rendering/TerminalRenderer` |
| **Artifact** | A full tool output stored to disk when `ModelPreview.Length > 50,000` chars. The LLM receives a truncated preview with an artifact reference. | `Session/ArtifactStore`, `~/.openmono/artifacts/` |
| **KV cache warmup** | A background fire-and-forget `max_tokens=1` probe sent to llama-server to pre-fill its KV cache with the system prompt and tool definitions. Checked at first user message. | `Program.cs:225-229`, `SendWarmupAsync` |

---

## Open Questions

*Items genuinely unknown — require a runtime test, maintainer decision, or spec ruling. Not deferred to a later phase.*

| ID | Kind | Description | Deferred Reason |
|---|---|---|---|
| arch-OQ2 | needs-spec-ruling | `global.json` SDK version pin not read; exact .NET 10 preview or stable SDK required is unknown. | Affects reproducibility of any port's build toolchain. |
| arch-OQ4 | needs-runtime-test | `Terminal.Gui` package is in `.csproj` but code primarily references `AnsiTuiRenderer` (Spectre.Console). Whether `Terminal.Gui` is actually used at runtime, or is a dead dependency, is unclear. | Grep for `Terminal.Gui` usage in source; if unused, omit from dependency inventory. |
| arch-OQ5 | needs-maintainer-decision | README says "up to 25 iterations per turn" but `ConversationLoop.cs:136` shows `maxIterations = 1000`. Which is the authoritative limit for documentation and external contracts? | Sub-agent definitions use per-agent turn budgets (15–300); the main loop limit is separate. Code value (1000) is authoritative until maintainer confirms. |
| contr-OQ1 | needs-runtime-test | `Utils/SecretScanner.cs` is present but its integration point — whether it scans tool inputs, outputs, or session content — was not confirmed from source reading. | Trace all call sites of `SecretScanner` in the agent loop. |
| contr-OQ2 | needs-runtime-test | The `Ask` permission list is loaded and described in docs but not evaluated in `PermissionEngine.CheckAsync` or `CheckCapabilitiesAsync`. Appears to be dead config. Needs integration test or maintainer confirmation. | Decision affects whether ports should implement the described "always prompt" behavior or omit the field. |
| prot-OQ1 | needs-runtime-test | LSP client wire format and serialization pattern not read in depth. Whether `LspServerManager` uses the same `SemaphoreSlim` serialization as `McpClient` is unknown. | LSP is optional; not a porting blocker. |
| prot-OQ2 | needs-runtime-test | Whether `SessionState.Meta.PlanMode` is persisted to JSONL on save is not confirmed. Current inference is no (field not in `Message` record). | Affects resume behavior — whether plan mode persists across session restarts. |
| prot-OQ3 | needs-maintainer-decision | `StepDefinition.Playbook` (nested playbook) and `StepDefinition.Output` fields exist in the model but are not consumed by `PlaybookExecutor.RunStepAsync`. Unclear if intentional no-op or unfinished feature. | Affects whether ports should implement nested playbook support. |

---

## Carry-Forward

*Items deferred to the reimplementation-spec phase.*

| ID | Target Phase | Description | Deferred Reason |
|---|---|---|---|
| port-CF1 | reimplementation-spec | Tokenizer selection: the 65%/80% context-pressure thresholds require accurate token counts for the target model family. `TiktokenSharp` (.NET) must be replaced with a target-language equivalent; the spec should name the specific tokenizer and confirm it produces equivalent counts for the models supported. | Tokenizer selection is a target-stack decision that belongs in the reimplementation spec, not in the porting bundle. |
| port-CF2 | reimplementation-spec | Config merge semantics for `permissions.tools` and `hooks.*` (additive concatenation across user + project layers) require explicit specification of concatenation order and deduplication rules for the reimplementation. The C# implementation's `List.AddRange` behavior (no dedup) may or may not be the intended semantic. | Rule-pinning belongs in the reimplementation spec; the porting bundle documents the observed behavior. |
| port-CF3 | reimplementation-spec | Atomic session save design: the JSONL full-rewrite-without-atomic-rename is a known portability hazard. The reimplementation spec should define the correct write-to-temp + rename pattern and specify whether the checkpoint sidecar should be written atomically with the main file. | Design decision for the new implementation; beyond the scope of the reverse-engineering bundle. |

---

## Validation

| # | Criterion | Result | Evidence |
|---|-----------|--------|----------|
| 1 | The system summary, layer map, contract table, protocol notes, and porting findings are synthesized. | PASS | §System Summary (3 paragraphs); §Layer Map With Ownership (18-row table); §Feature Contract Table (28-row table with all 4 porting priority levels); §Protocol and State Notes (provider normalization, prot-CF1/CF2 closures, state machine summary, persistence schema summary); §Portability Hazards (17-row consolidated table). |
| 2 | Portability hazards and open questions are separated from facts. | PASS | §Portability Hazards clearly separated from §Observed Facts vs. Inferred Structure; open questions enumerated in §Open Questions with `kind` and deferred reason; all facts tagged with evidence level in §Observed Facts. |
| 3 | Feature importance is sorted for porting. | PASS | §Feature Contract Table has Priority column with `core`/`important`/`optional`/`incidental` classifications for all 28 features; matches and refines the §Porting Priorities table from architecture-map.md. |
| 4 | Known defects are referenced in the Defect Synthesis with porting recommendations (fix before porting / port differently / leave behind), or the section explicitly notes that no defect scan ran. | PASS | §Defect Synthesis integrates both split deep-audit reports, references 13 grouped defect IDs, and assigns `fix before porting` / `port differently` recommendations. |
| 5 | Findings are marked with evidence levels. | PASS | Existing observed/inferred/hazard sections retain evidence tags; defect rows cite source reports whose findings are individually marked `Observed fact` or `Strong inference`. Carry-forward items prot-CF1 and prot-CF2 are closed with concrete design guidance; 8 open questions remain explicit. |

**Validated by:** 2026-05-24 (porting phase — implementing session)
**Overall:** PASS
