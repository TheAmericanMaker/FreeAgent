# FreeAgent architecture

A tour of how the implemented kernel actually works — the contracts, the data that
flows between them, and the invariants each one guarantees. For the product-level
overview and how to run it, start with the [README](../README.md); for the original
behavioral specification the kernel implements, see
[`codecarto/reimplementation-spec.md`](codecarto/reimplementation-spec.md); for the
phasing decision that made the kernel headless and added the protocol server, see
[ADR 0005](decisions/0005-headless-core-protocol.md).

## The shape of the system

Everything the kernel touches at runtime is behind an interface, and the kernel
holds no global or static mutable state. That is what makes it deterministic and
fully testable against fakes (528 passing tests today).

```
                        ┌─────────────────────────────────────────────────┐
                        │                  SessionRuntime                  │
                        │   the agentic turn loop + event emission         │
                        └────────┬────────────────┬────────────────┬──────┘
                                 │                │                │
                       IProvider │   TurnExecutor │ IEventSink     │ IPersistenceStore
                                 │   + ToolPipeline                │
        ┌────────────────────────▼┐  ┌────────────▼─────────────┐  ┌▼────────────────────┐
        │  OpenAIProvider          │  │  IToolRegistry           │  │  JsonlSessionStore   │
        │  AzureOpenAIProvider     │  │   ├ file tools           │  │     ▼                │
        │  AnthropicProvider       │  │   ├ search tools         │  │  IAtomicFileSystem   │
        │  OllamaProvider          │  │   ├ CSharpAnalysis       │  │  (LinuxAtomicFS)     │
        │  BedrockProvider (SDK)   │  │   ├ memory + artifact    │  │                      │
        │  VertexProvider (ADC)    │  │   ├ sub-agent spawn      │  │  NoOpPersistenceStore│
        └──────────────────────────┘  │   ├ mcp__* (per server)  │  │  (sub-agents)        │
                                      │   └ lsp__* (per server)  │  └──────────────────────┘
                                      │  IPermissionEngine       │
                                      │  └ PermissionEngine      │
                                      │  IToolResultCache        │
                                      │  IArtifactStore          │
                                      │  IHookRunner             │
                                      └──────────────────────────┘
```

The host (`FreeAgent.Host`) and the protocol server (`FreeAgent.Server`) are two
**frontends** of the same kernel — they wire the seams to either a console + REPL
or to ASP.NET Core minimal-API endpoints with SSE.

| Seam                     | Default implementation                                                                | Test fake                          |
| ------------------------ | ------------------------------------------------------------------------------------- | ---------------------------------- |
| `IProvider`              | `OpenAIProvider`, `AzureOpenAIProvider`, `AnthropicProvider`, `OllamaProvider`, `BedrockProvider`, `VertexProvider` | `FakeProvider` + `StreamScript`    |
| `ITool`                  | many adapters (see [Built-in tools](../README.md#built-in-tools))                     | `FakeTool`                         |
| `IPermissionEngine`      | `PermissionEngine`                                                                    | `RecordingPermissionEngine`        |
| `IPermissionApprover`    | `ConsoleApprover` (host) / unused in server                                           | `FakeApprover`                     |
| `IPersistenceStore`      | `JsonlSessionStore` / `NoOpPersistenceStore` (sub-agents)                             | `InMemorySessionStore`             |
| `IAtomicFileSystem`      | `LinuxAtomicFileSystem`                                                               | `RecordingAtomicFileSystem`        |
| `IEventSink`             | `ConsoleEventSink` (host) / `HttpSseEventSink` (server) / `NullEventSink` (sub-agents) | `RecordingEventSink`               |
| `IToolResultCache`       | `InMemoryToolResultCache`                                                             | (real impl, in-memory)             |
| `IArtifactStore`         | `InMemoryArtifactStore`                                                               | (real impl, in-memory)             |
| `IHookRunner`            | `HookRunner`                                                                          | (real + `FakeShell`)               |
| `IShellExecutor`         | `BashShellExecutor` (host)                                                            | `FakeShell`                        |
| `IJsonRpcTransport`      | base shape — framing is the impl's job                                                | per-protocol fakes                 |
| `IMcpTransport`          | `StdioMcpTransport` (host, newline-delimited)                                         | in-memory `FakeTransport`          |
| `ILspTransport`          | `StdioLspTransport` (host, `Content-Length`-framed)                                   | in-memory `FakeLspTransport`       |

## The turn loop (`SessionRuntime`)

`RunTurnAsync(userText, ct)`:

1. **Compact** if `SessionState.LastInputTokens` > the compaction threshold for the active
   context window. `Compactor.CompactWithSummaryAsync` asks the active provider to summarise the
   turns it's about to drop and prepends the summary to the first kept user message; provider
   failures fall back to a non-LLM "previous turns elided" notice so a network blip can't kill the
   session.
2. Append the user message; build a `ProviderRequest` from the **full** message history and the
   registry's current `ToolDefinition`s.
3. Stream chunks from `IProvider.StreamChatAsync`, handling each `StreamChunk`:
   - `ThinkingDelta` → `IEventSink.OnThinking`.
   - `TextDelta` → accumulate **and** `IEventSink.OnText`.
   - `ToolCallDelta` → buffer into a `PartialToolCall` keyed by id (providers split args
     across multiple chunks; the runtime joins them by id).
   - `Usage` → `IEventSink.OnUsage`; the `InputTokens` count is remembered for the next turn's
     compaction decision.
4. Emit one complete `ToolCall(id, name, argumentsJson)` per buffered id.
5. **Terminate** if there are no tool calls: append the assistant text, persist the session via
   `IPersistenceStore.SaveAsync`, and return `TurnResult(finalText, doomDetected)`.
6. **Doom-loop guard** — `DoomLoopDetector` watches for the *identical* tool-call batch (names +
   canonicalized JSON args) three times in a row. On a trip, `SessionRuntime` records a notice
   and re-prompts the model (it does **not** execute the repeat). It tolerates up to
   `DoomRecoveryBudget` (3) such re-prompts before halting the turn with
   `TurnResult.DoomLoopDetected`. The 90-iteration `MaxIterations` cap is the final backstop;
   `SessionState.SessionIterationLimit` (env `FREE_SESSION_ITERATIONS`) caps iterations across
   the whole session.
7. Otherwise: append the assistant message (text + tool calls), execute the batch via
   `TurnExecutor`, append one `Tool` message per result (preserving call order), and loop.

`SessionRuntime.SwapEventSink(IEventSink)` is an atomic sink swap that lets the protocol server
route each HTTP turn into its own SSE stream without recreating the runtime — every other consumer
just passes a permanent sink.

### Streaming tool-call reassembly

OpenAI-style streaming delivers a tool call across many chunks: the first carries the
index / id / name, later chunks carry argument fragments. `PartialToolCall` accumulates
`ArgumentsJson` per id until the stream ends. The provider adapters map their wire-specific id
shape to a stable string id so fragments join correctly even when the id only appears once
(OpenAI maps by `index`; Anthropic uses `content_block_start`'s `id`; Ollama synthesises
`call_0`, `call_1`, …).

### Doom-loop detection (`DoomLoopDetector`)

The detector builds a signature per batch: `name:canonicalJson` for each call, joined with `|`.
Arguments are canonicalized through `JsonOptions` (falling back to the raw text if they're
malformed) so semantically identical args with different whitespace or key order still match. A
counter increments while the signature is unchanged and trips once the count reaches the
threshold (3) — and stays tripped for every subsequent identical batch. The detector resets at
the start of every turn.

## Tool execution

### Scheduling (`TurnExecutor`)

A completed batch is split by capability:

- A call whose tool is **`IsReadOnly && IsConcurrencySafe`** joins a single parallel
  window run with `Task.WhenAll`.
- All other calls run **serially**, in order, after the parallel window.
- Results are always returned in the **original call order** regardless of how they were
  scheduled.

Failure handling inside the parallel window uses a **linked sibling-abort token**:

- If a parallel call returns `Crash` (and the user has not cancelled), the executor cancels the
  sibling token, stopping the rest of the window early.
- A call cancelled *because* of that sibling abort is reported as `Cancelled` with a
  "sibling abort" message — distinct from user cancellation, which propagates the user's intent.
- Any slot that somehow has no result is backfilled with a `Crash` result, so the array is always
  complete and aligned with the calls.

### The 12-step pipeline (`ToolPipeline`)

Every tool call traverses the same fixed sequence. **No exception escapes** — every failure is
mapped to a `ToolResult` class. Every step appends to `StepLog` under a lock so the traversal
order is observable and tested. **All steps do real work** (none are no-ops anymore).

```
parse → schema-validate → sanity-check → plan-mode-guard → permission →
cache-lookup → pre-hook → execute → post-hook → artifact-store → cache-write → invalidate
```

- **parse / schema-validate** — `ArgumentsJson` is parsed; unknown tools and schema mismatches
  become `InvalidInput` without ever throwing.
- **sanity-check** — path-escape and workspace-boundary checks.
- **plan-mode-guard** — if `SessionState.PlanMode` is on, a non-read-only tool is
  `PlanModeBlocked` here, **before** capability gathering, so a model can never sneak a write
  past plan mode by hiding behind allowed capabilities. `ExitPlanMode` is declared read-only so
  it can always be called while plan mode is active.
- **permission** — `IPermissionEngine.Decide(tool, capabilities, workingDir)` returns
  `Allow` / `Deny` / `Prompt`. On `Prompt` the pipeline asks the optional `IPermissionApprover`;
  granted "session" approvals are remembered in `SessionState.SessionApprovals` so the same
  capability type isn't re-prompted later in the session.
- **cache-lookup / cache-write / invalidate** — `IToolResultCache` (in-memory by default). A hit
  on a read-only tool returns the cached `Success` without re-executing. A successful mutating
  tool invalidates the entire read-only cache (no per-key invalidation today — keeps the model
  simple and correct).
- **pre-hook / post-hook** — `IHookRunner` matches `HookSpec`s from `.freeagent/config.json`
  against the call (by tool name and optional `inputContains` substring) and dispatches via
  `IShellExecutor`. Hook failures are non-fatal — they're logged but never block the agent.
- **execute** — run the tool body. `OperationCanceledException` becomes `Cancelled`; any other
  exception becomes `Crash` with a retry hint.
- **artifact-store** — `Success` content longer than `DefaultArtifactThreshold` (10k chars) is
  offloaded to `IArtifactStore` and the result content is replaced with a preview + opaque ref
  the model fetches via the `ReadArtifact` tool. Keeps token usage bounded on big outputs.

A tool that succeeds but returns blank content is normalized to `Empty`, so the model gets a
distinct signal rather than an ambiguous empty success.

### Tools (`ITool`)

A tool declares `Name`, a model-facing `Description` (serialized as the provider's
`function.description`, so it shapes tool selection), an `InputSchema` (`JsonDocument`),
`IsReadOnly`, `IsConcurrencySafe`, the `RequiredCapabilities(args, context)` for a given
call, and `ExecuteAsync`. `RequiredCapabilities` is what couples a tool to the permission engine:
the tool says *what authorization this specific call needs*, derived from its arguments, and the
engine decides. `WorkspacePath.Resolve` is shared between capability derivation and execution so
the path checked equals the path acted on.

The read-only search tools `Glob` / `Grep` / `CSharpAnalysis` share `WorkspaceSearch` for
deterministic, noise-dir-skipping, capped file walking and glob-to-regex matching; all three are
`IsReadOnly` + `IsConcurrencySafe`, so they run in the parallel window. `CSharpAnalysis`'s
**semantic** actions (find-references / find-definition / semantic-diagnostics) build a real
`CSharpCompilation` over the workspace's `.cs` files via `RoslynSemanticHelpers`, with metadata
references taken from `TRUSTED_PLATFORM_ASSEMBLIES` and cached after the first build.

### Sub-agents (`Agents/`)

`AgentDefinition` (Type + AllowedTools + SystemPromptSuffix), `AgentRegistry`, and
`SubAgentRunner` build an isolated sub-session against a tool registry **filtered to the role's
allow-list**, with a fresh `ToolPipeline` (reusing the parent's engine/approver/cache/hooks),
the role's system-prompt suffix, `NoOpPersistenceStore`, and `NullEventSink`. `SpawnAgentTool`
exposes spawning to the model with an `AgentSpawnCap` that is never auto-allowed, so every spawn
goes through the permission engine. The host registers four default roles — `Explore`, `Plan`,
`Coder`, `Verify`.

## Permission engine (`PermissionEngine`)

Pure and deterministic: `Decide(tool, capabilities, workingDirectory)` →
`PermissionDecision { Allow | Deny | Prompt }`. It depends only on its inputs and the configured
rules — no clock, no I/O, no prompts. Session rules are configured by method
(`AllowTool`, `DenyTool`, `AllowCapabilityType<T>`, `DenyCapabilityType<T>`,
`AllowCapabilityRule<T>(pattern)`, `DenyCapabilityRule<T>(pattern)`).

`PermissionConfig` loads the same rules declaratively from `.freeagent/config.json` at startup
(validating capability names against the reflected `Capability` catalog). The same JSON file
also holds the `hooks` block (`preToolUse` / `postToolUse` / `sessionStart`), the `mcp.servers[]`
block for MCP integration, and the `lsp.servers[]` block for LSP integration — one project-level
config covers permission, hooks, and external tool servers.

The key invariants: **denies and hardcoded security blocks always beat allows**; an uncovered
capability returns `Prompt` (not `Deny`) so a configured `IPermissionApprover` can ask the user
inline rather than the model just bouncing off `PermissionDenied`. Full precedence and the
hardcoded allow/block sets are in the [README permission section](../README.md#permission-model).

## Provider adapters

Six native providers, all implementing `IProvider.StreamChatAsync`. They differ in wire format
but converge on the same `StreamChunk` shape the runtime understands, and every provider maps its
wire-specific finish reason to the normalized `StopReason` enum
(`EndTurn` / `ToolUse` / `MaxTokens` / `StopSequence` / `Refusal` / `Unknown`).

- **`OpenAIProvider`** — any OpenAI-compatible `/chat/completions` endpoint over SSE. Two
  constructors: one that owns its `HttpClient` (`baseUrl, apiKey, model`) and one that accepts an
  injected client for tests. Request body is built directly with `Utf8JsonWriter`.
- **`AzureOpenAIProvider`** — Azure URL pattern (`{endpoint}/openai/deployments/{deployment}/chat/completions?api-version=…`)
  + `api-key` header instead of Bearer. Reuses `OpenAICompatStreaming` for the body and SSE
  parser.
- **`OllamaProvider`** — native Ollama `/api/chat` newline-delimited JSON. Optional `num_ctx` /
  `temperature` via ctor params (env `FREE_NUM_CTX` / `FREE_TEMPERATURE`). Synthesises tool-call
  ids (Ollama doesn't emit them) so the runtime's per-id accumulation still works.
- **`AnthropicProvider`** — native Messages API; `x-api-key` + `anthropic-version` headers.
  Handles `content_block_start` → `input_json_delta` for tool-use streaming, merges consecutive
  `Tool` messages into a single user message with multiple `tool_result` blocks (Anthropic
  requires strict role alternation), and supports extended thinking with a `thinkingBudgetTokens`
  ctor param (env `FREE_THINKING_BUDGET`) that auto-bumps `max_tokens` headroom.
- **`BedrockProvider`** — AWSSDK.BedrockRuntime wrapper for Anthropic-on-Bedrock. SigV4 signing,
  region routing, retries, and AWS event-stream (`vnd.amazon.eventstream`) parsing are all the
  SDK's responsibility. Body shape is Anthropic-Messages minus the top-level `model` (which moves
  into the SDK request envelope) plus required `anthropic_version: bedrock-2023-05-31`. Auth via
  the default AWS credential chain (env vars / shared profile / IMDS / SSO).
- **`VertexProvider`** — native HTTP client for Anthropic-on-Vertex. Auth via
  `Google.Apis.Auth`'s Application Default Credentials (env / gcloud / GCE metadata). URL:
  `https://{location}-aiplatform.googleapis.com/v1/projects/{project}/locations/{location}/publishers/anthropic/models/{modelId}:streamRawPredict`.
  Body is Anthropic-Messages with `anthropic_version: vertex-2023-10-16`. Token source is
  exposed via `VertexProvider.ITokenSource` so tests don't need real ADC.

`Usage` understands cache-aware fields when the provider exposes them
(`cache_read_input_tokens`, `cache_creation_input_tokens`).

The `Model` record and `ModelCatalog` (keyed by `wire-api/id`) give the runtime an optional
lookup of context window + default max-output + feature flags (supports tools / vision /
thinking). The catalog is additive — unknown models still work, they just don't get specialized
handling.

## Persistence (`JsonlSessionStore` + `IAtomicFileSystem`)

`SerializeAsync` emits a header line (`session_id`, `started_at`, `working_directory`) followed
by one JSON line per message; `DeserializeAsync` reverses it with precise, line-numbered error
messages for a malformed header or record. `SaveAsync` performs the durable write through the
filesystem seam:

```
CreateTempPath → WriteTemp → FsyncTemp → Rename(temp → target) → FsyncDirectory
```

`LinuxAtomicFileSystem` implements this with real POSIX `fsync` on both the file and its
containing directory (`PosixFileSystemPrimitives`), which is what makes the rename durable. On
failure it best-effort deletes the temp file and rethrows. The atomic `rename` guarantees a
reader sees either the previous complete file or the new complete file — never a partial write.

`NoOpPersistenceStore` is used by sub-agents and the protocol server where on-disk durability is
unwanted (sub-agents are ephemeral; the protocol server's `SessionRegistry` holds the live state
in memory while the underlying `JsonlSessionStore` still writes per-turn).

## MCP and LSP (JSON-RPC over stdio)

Both protocols speak JSON-RPC 2.0 but differ in framing — MCP uses newline-delimited JSON, LSP
uses `Content-Length`-headered framing. The shared seam `IJsonRpcTransport` lets `JsonRpcClient`
(background read loop + id-keyed completion dispatch) drive either; `IMcpTransport` and
`ILspTransport` are namesake interfaces extending it so the layer names read as the protocol
they're carrying.

- **MCP** — `McpClient` does `initialize` + `notifications/initialized` + `tools/list` +
  `tools/call`; `McpToolAdapter` wraps each remote tool as `mcp__{server}__{tool}`; required
  capability per call is `ProcessExecCap("mcp:{server}", ...)`. `McpServerManager` spawns each
  `mcp.servers[]` entry at host startup.
- **LSP** — `LspClient` does `initialize` + `initialized` notification + `textDocument/didOpen`
  + `hover` / `definition` / `references`; `LspToolAdapter` registers four tools per server
  (`lsp__{server}__{hover|definition|references|open}`); required capability is
  `ProcessExecCap("lsp:{server}", ...)`. `LspServerManager` mirrors `McpServerManager`'s shape.
  `StdioLspTransport` handles the Content-Length framing.

Both layers have an end-to-end smoke test in the `JsonRpcCollection` (`DisableParallelization =
true`). The original "hangs the runner" issue was a race against the zero-latency in-memory test
transport — the read loop could consume a pre-queued response before `CallAsync` registered its
TCS. `JsonRpcClient` now buffers responses for unknown ids (`_earlyResponses` / `_earlyErrors`)
and `CallAsync` drains the matching entry under the same gate that registers the TCS, so even
pre-queued responses resolve correctly. Production paths never hit the buffer (TCS registration
runs before the transport write).

## Protocol server (`FreeAgent.Server`)

Per [ADR 0005](decisions/0005-headless-core-protocol.md), the kernel is headless and the protocol
server is an additive frontend. It hosts the kernel as an ASP.NET Core minimal-API service:

| Method   | Path                          | Notes                                                                  |
| -------- | ----------------------------- | ---------------------------------------------------------------------- |
| `POST`   | `/sessions`                   | Create a session; returns id + working directory.                      |
| `GET`    | `/sessions`                   | List active session ids.                                               |
| `GET`    | `/sessions/{id}`              | State summary (messages, plan mode, tags, iterations).                 |
| `POST`   | `/sessions/{id}/turns`        | Submit user input; SSE response streams text/thinking/tool_call/tool_result/usage events, then a `done` event with the assembled reply. |
| `DELETE` | `/sessions/{id}`              | Remove from the in-memory `SessionRegistry`.                           |
| `GET`    | `/openapi/v1.json`            | OpenAPI document — TUI/editor frontends regenerate types from this.    |

`HttpSseEventSink` is the `IEventSink` impl that turns runtime callbacks into one SSE event per
callback (lock-serialized so interleaved events can't tear a line). `SessionRuntime.SwapEventSink`
lets each per-turn HTTP request swap in a fresh sink without recreating the runtime.
`ProviderFactory` resolves providers from the same `ProviderConfig` matrix the CLI uses. Optional
bearer-token gate via `FREEAGENT_SERVER_API_KEY` env var; unset means the server is open and
intended for loopback bind only.

## State (`SessionState`)

Holds `SessionId`, `WorkingDirectory`, `StartedAt`, and the `Messages` list. Several fields are
**in-memory only** and never persisted:

- `PlanMode` — when set, the pipeline's plan-mode guard blocks every non-read-only tool with
  `PlanModeBlocked` before the permission step.
- `SessionApprovals` — capability types the user granted "for this session" via the approver.
- `LastInputTokens` / `ContextWindow` — drive the pre-turn compactor's threshold.
- `History` — a LIFO `FileHistory` of file snapshots used by `/undo`.
- `TotalIterations` / `SessionIterationLimit` — session-wide iteration tracking and optional cap
  (env `FREE_SESSION_ITERATIONS`).
- `Tags` — session tags managed by `/tag` and `/untag`, visible in `/status` and `/doctor`.

## Observability

The runtime exposes two `System.Diagnostics.ActivitySource`s — `SessionRuntime.ActivitySource`
("Session.RunTurn") and `ToolPipeline.ActivitySource` ("Tool.Pipeline.Execute") — so consumers
can attach any `ActivityListener` or OpenTelemetry SDK to capture turn and per-tool spans without
adding a hard OTel dependency to the kernel.

## Host commands (`FreeAgent.Host/HostCommands.cs`)

Slash commands are dispatched on input that starts with `/`. Each command's metadata is
registered in `HostCommands.BuildDefaultRegistry` (a `CommandRegistry` instance) so `/commands
[query]` and the future TUI palette bind against the same source. New commands should add both
the `case "/foo":` in `Handle` and the matching `CommandDefinition` in the registry.

## Testing model

The suite (xUnit + FluentAssertions) exercises each contract against a fake counterpart:
`FakeProvider` replays a `StreamScript` of chunks; `RecordingPermissionEngine` and
`RecordingEventSink` capture interactions; `InMemorySessionStore` and `RecordingAtomicFileSystem`
stand in for durable I/O; the MCP/LSP tests use in-memory `Channel`-backed transports. The
protocol server is tested via `Microsoft.AspNetCore.Mvc.Testing`'s `WebApplicationFactory` —
in-process HTTP, no network bind. The MCP and LSP end-to-end smoke tests live in
`JsonRpcCollection` (`DisableParallelization = true`) and run every build — see the MCP/LSP
section above for why a buffer in `JsonRpcClient` was needed to make the in-memory transport
sequencing work.
