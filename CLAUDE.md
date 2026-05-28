# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

Requires the **.NET 10 SDK** (pinned to `10.0.100` with `rollForward: latestMinor` in `global.json`).

```bash
dotnet build FreeAgent.slnx          # build everything (warnings are errors — a clean build is meaningful)
dotnet test  FreeAgent.slnx          # run the suite (xUnit + FluentAssertions)
dotnet run --project src/FreeAgent.Host -- --verbose   # run the CLI (flags after --)

# A single test / subset (xUnit filters):
dotnet test FreeAgent.slnx --filter "FullyQualifiedName~OpenAIProviderTests"
dotnet test FreeAgent.slnx --filter "FullyQualifiedName~DoomLoop"   # matches by method name
```

Running the CLI: provider is selected by **`FREEPROVIDER`** — `openai` (default), `anthropic`,
`azure`, `ollama`, `bedrock`, or `vertex`. The active provider's key / base-url / model come from
env (`OPENAI_API_KEY` / `ANTHROPIC_API_KEY` / `AZURE_OPENAI_*` / `OLLAMA_HOST` / AWS or GCP
credential chain, `FREEMODEL`) or `$XDG_CONFIG_HOME/freeagent/config.json`. Ollama / Bedrock /
Vertex skip the API-key bootstrap check (Ollama is unauthenticated; Bedrock + Vertex use the
ambient cloud credential chain). The process's **current directory is the agent's sandbox** and
where `session.jsonl` is written — launch from the directory you want it to operate on.

Running the protocol server (`FreeAgent.Server` — HTTP + SSE surface per ADR 0005):

```bash
dotnet run --project src/FreeAgent.Server                     # http://localhost:5000
FREEAGENT_SERVER_API_KEY=secret dotnet run --project src/FreeAgent.Server   # require Bearer auth
```

Endpoints: `POST /sessions`, `GET /sessions`, `GET /sessions/{id}`, `POST /sessions/{id}/turns`
(SSE-streamed), `DELETE /sessions/{id}`, plus `GET /openapi/v1.json`.

## Two project-wide conventions that will trip you up

- **All kernel types live in the single flat `FreeAgent.Kernel` namespace**, regardless of their
  folder (`Sessions/`, `Tools/Adapters/`, `Providers/Adapters/`, `Agents/`, …). One
  `using FreeAgent.Kernel;` imports everything; do **not** invent sub-namespaces from folder names.
- **`Directory.Build.props` (repo root) supplies `TargetFramework=net10.0`, `LangVersion=preview`,
  `Nullable`, `ImplicitUsings`, and `TreatWarningsAsErrors` to every project.** Individual `.csproj`
  files are intentionally bare — do not re-add these per project.

## Architecture

FreeAgent is **kernel-first**: `FreeAgent.Kernel` is the product (a deterministic, fully-tested
agent core), `FreeAgent.Host` is a thin CLI shell over it, `FreeAgent.Server` is the HTTP + SSE
protocol surface (ADR 0005), and `FreeAgent.Kernel.Tests` exercises everything via xUnit. The
kernel holds **no global/static mutable state** and reaches the outside world only through
interfaces — that is what makes it testable against fakes with no network, model, or real
filesystem.

| Seam (`I…`)              | Default impl                                                                       | Test fake                            |
| ------------------------ | ---------------------------------------------------------------------------------- | ------------------------------------ |
| `IProvider`              | `OpenAIProvider`, `AnthropicProvider`, `AzureOpenAIProvider`, `OllamaProvider`, `BedrockProvider`, `VertexProvider` | `FakeProvider` + `StreamScript`      |
| `ITool`                  | many adapters (see below)                                                          | `FakeTool`                           |
| `IPermissionEngine`      | `PermissionEngine`                                                                 | `RecordingPermissionEngine`          |
| `IPermissionApprover`    | `ConsoleApprover` (host)                                                           | `FakeApprover`                       |
| `IPersistenceStore`      | `JsonlSessionStore` / `NoOpPersistenceStore`                                       | `InMemorySessionStore`               |
| `IAtomicFileSystem`      | `LinuxAtomicFileSystem`                                                            | `RecordingAtomicFileSystem`          |
| `IEventSink`             | `ConsoleEventSink` / `NullEventSink` / `HttpSseEventSink` (server)                 | `RecordingEventSink`                 |
| `IToolResultCache`       | `InMemoryToolResultCache`                                                          | (real impl, in-memory)               |
| `IArtifactStore`         | `InMemoryArtifactStore`                                                            | (real impl, in-memory)               |
| `IHookRunner`            | `HookRunner`                                                                       | (real + `FakeShell`)                 |
| `IShellExecutor`         | `BashShellExecutor` (host)                                                         | `FakeShell`                          |
| `IJsonRpcTransport`      | base shape; framing is the impl's job                                              | per-protocol fakes                   |
| `IMcpTransport`          | `StdioMcpTransport` (host, newline-delimited)                                      | in-memory `FakeTransport`            |
| `ILspTransport`          | `StdioLspTransport` (host, `Content-Length` framing)                               | in-memory `FakeLspTransport`         |

### The turn loop

`SessionRuntime.RunTurnAsync` runs the agentic loop (bounded at 90 iterations):

1. **Compact** if the previous turn's input tokens exceeded the threshold —
   `Compactor.CompactWithSummaryAsync` asks the active provider to summarise dropped turns and
   prepends the summary to the first kept user message; falls back to a non-LLM notice on any
   provider error.
2. Append the new user message; stream from the provider; emit chunks to `IEventSink`; track
   `Usage.InputTokens` for the next turn's compaction decision.
3. **Tool calls reassemble by id** — providers split one call across many `ToolCallDelta` chunks;
   the runtime buffers argument fragments per id and emits one complete `ToolCall` when the stream
   ends.
4. **`DoomLoopDetector`** trips when the identical tool-call batch (names + canonicalized JSON
   args) repeats 3× in a row; `SessionRuntime` suppresses the repeat and re-prompts up to
   `DoomRecoveryBudget` (3) times before halting the turn (`TurnResult.DoomLoopDetected`).

### Tool execution: the 12-step pipeline (all steps now do real work)

`TurnExecutor` schedules a batch, then `ToolPipeline` runs each call. Contracts:

- **Concurrency (`TurnExecutor`):** calls whose tool is both `IsReadOnly` and `IsConcurrencySafe`
  run together in one parallel window; everything else runs serially; **results are returned in
  original call order**. A parallel `Crash` cancels its siblings ("sibling abort"), distinct from
  user cancellation.
- **Pipeline (`ToolPipeline.ExecuteAsync`):** fixed sequence
  `parse → schema-validate → sanity-check → plan-mode-guard → permission → cache-lookup →
  pre-hook → execute → post-hook → artifact-store → cache-write → invalidate`. **No exception
  escapes** — every failure is mapped to a `ToolResult`. Failures short-circuit before any
  side-effecting step. Every step appends to `StepLog` so the traversal order is observable and
  tested. What each step *does*:
  - **permission** consults `IPermissionEngine`; on `PermissionOutcome.Prompt` it asks an optional
    `IPermissionApprover` (with session-grant memory in `SessionState.SessionApprovals`).
  - **cache-lookup** / **cache-write** / **invalidate** use `IToolResultCache` — read-only tools'
    `Success` results are cached; a successful mutating tool invalidates everything.
  - **pre-hook** / **post-hook** call `IHookRunner` against matching `HookSpec`s from
    `.freeagent/config.json`.
  - **artifact-store** moves `Success` content larger than `DefaultArtifactThreshold` (10k chars)
    into an `IArtifactStore` and replaces the content with a preview + opaque ref. The model fetches
    the full text via the `ReadArtifact` tool.

### Permission model

`PermissionEngine.Decide(tool, capabilities, workingDir)` is pure (no clock/I/O/prompts). Tools
declare `Capability` objects per call via `ITool.RequiredCapabilities` — the only coupling between
tools and authorization. Three possible outcomes: `Allow`, hard `Deny` (security block, explicit
deny), or `Prompt` (uncovered — the pipeline asks the approver). Precedence: hardcoded blocks →
tool-deny → capability-deny → no-caps allow → tool-allow → per-capability coverage → otherwise
`Prompt`. Auto-allowed without a rule: reads inside the working dir, safe read-only binaries
(`pwd`, `ls`, `cat`, `git status|diff|log`, …), and `read` memory ops. **Capabilities a hard block
can't be overridden by an allow rule.**

`PermissionConfig` (loaded from `.freeagent/config.json`) is the project-level umbrella config —
permission rules plus `hooks` (`preToolUse` / `postToolUse` / `sessionStart`).

### Built-in tools

Registered by the host: `ReadFile`, `WriteFile`, `EditFile` (string-replace, unique-match),
`MultiEditFile` (atomic batch), `ApplyPatch` (unified-diff with public `ParseHunks`),
`ProcessExec` (30s timeout, kills tree on cancel), `Glob` / `Grep` (managed, no `rg`),
`CSharpAnalysis` (Roslyn — syntactic `list-types` / `list-members` / `diagnostics`, plus
semantic `find-references` / `find-definition` / `semantic-diagnostics`), `EnterPlanMode` /
`ExitPlanMode`, `ReadMemory` / `WriteMemory` (XDG memory store), `ReadArtifact`, `SpawnAgent`
(sub-agents). Optional, registered when configured: `mcp__{server}__{tool}` per MCP server
(`.freeagent/config.json#mcp.servers`), `lsp__{server}__{hover|definition|references|open}` per
LSP server (`#lsp.servers`).

### MCP + LSP (JSON-RPC over stdio)

Shared `IJsonRpcTransport` seam wraps the framing — newline-delimited for MCP, `Content-Length`
headered for LSP — so `JsonRpcClient` (background read loop, id-keyed completion dispatch) is
reusable. `McpClient` (`initialize` + `tools/list` + `tools/call`) feeds `McpToolAdapter`;
`LspClient` (`initialize` + `didOpen` + `hover` / `definition` / `references`) feeds
`LspToolAdapter`. `McpServerManager` / `LspServerManager` spawn the configured child processes at
host startup and register the resulting tools. Capability per call is a `ProcessExecCap` scoped to
`mcp:{server}` / `lsp:{server}` so a whole server can be allow- or deny-ruled as a unit. Both have
end-to-end smoke tests living in `JsonRpcCollection` (sequential, non-parallel) — `JsonRpcClient`
buffers responses for ids whose `CallAsync` hasn't finished registering yet, which is what kept
the in-memory tests passing once the artificial race was understood.

### Provider matrix

- **`OpenAIProvider`** + **`AzureOpenAIProvider`** + **`OllamaProvider`** (OpenAI-compat path)
  share `OpenAICompatStreaming` (SSE body builder + parser).
- **`AnthropicProvider`** — native Messages API; emits `thinking_delta` / `text_delta` /
  `input_json_delta`; supports extended thinking with `FREE_THINKING_BUDGET` (auto-bumps
  `max_tokens` headroom).
- **`OllamaProvider`** (native) — `/api/chat` newline-delimited JSON; optional `num_ctx` /
  `temperature` via `FREE_NUM_CTX` / `FREE_TEMPERATURE`.
- **`BedrockProvider`** — `AWSSDK.BedrockRuntime` SDK wrapper for Anthropic-on-Bedrock; SigV4 +
  AWS event-stream parsing are the SDK's job; auth from default AWS credential chain.
- **`VertexProvider`** — Anthropic-on-Vertex; `Google.Apis.Auth` ADC for the bearer token; URL
  pattern `…-aiplatform.googleapis.com/.../publishers/anthropic/models/…:streamRawPredict`.
- **`Model`** record + **`ModelCatalog`** keyed by `wire-api/id` for capability flags (context
  tokens, default max-output, supports tools / vision / thinking).
- **`StopReason`** enum on every `StreamChunk` (`EndTurn` / `ToolUse` / `MaxTokens` /
  `StopSequence` / `Refusal` / `Unknown`) — every provider maps its wire-specific finish reason.

### Sub-agents

`Agents/AgentDefinition` (Type + AllowedTools + SystemPromptSuffix), `AgentRegistry`, and
`SubAgentRunner` build an isolated sub-session against a tool registry **filtered to the role's
allow-list**, with a fresh `ToolPipeline` (reuses parent's engine/approver/cache/hooks), the role's
system-prompt suffix, `NoOpPersistenceStore`, and `NullEventSink`. The `SpawnAgentTool` exposes it
to the model with an `AgentSpawnCap` (never auto-allowed). Host registers four default roles:
`Explore`, `Plan`, `Coder`, `Verify`.

### Result taxonomy

`ToolResult` carries a `ToolResultKind` (`Success, InvalidInput, PermissionDenied, PlanModeBlocked,
StateConflict, Crash, Empty, Cancelled`). Everything but `Success` is an error and may include a
model-facing `RetryHint`. Use the static factories.

### Persistence + session state

`JsonlSessionStore` writes a JSONL transcript through `IAtomicFileSystem` as
**write-temp → fsync temp → rename → fsync directory**, so an interrupted save never corrupts the
file. `NoOpPersistenceStore` is used by sub-agents (no disk). `SessionState` carries (in-memory
only, *not* persisted): `PlanMode`, `SessionApprovals` (granted capability types),
`LastInputTokens`, `ContextWindow`, `History` (file snapshots for `/undo`).

## Adding things

- **A new tool:** implement `ITool` (write a real `Description` — it's sent to the model; declare
  `IsReadOnly`/`IsConcurrencySafe` honestly — they drive parallel scheduling), derive its
  `Capability` in `RequiredCapabilities`, resolve paths with `WorkspacePath.Resolve`, let
  `OperationCanceledException` propagate (the pipeline maps it to `Cancelled`), and map other
  failures to `ToolResult` classes rather than throwing. Register it in
  `src/FreeAgent.Host/Program.cs`. For file-walking/glob, reuse `WorkspaceSearch` (see
  `GlobTool`/`GrepTool`); for in-place edits prefer `EditFileTool`'s unique-match pattern.
- **A new provider:** implement `IProvider.StreamChatAsync` yielding `StreamChunk`s; mirror one of
  the six existing adapters under `Providers/Adapters/` — `OpenAIProvider` /
  `AzureOpenAIProvider` / `OllamaProvider` are the OpenAI-compat shape (share
  `OpenAICompatStreaming`), `AnthropicProvider` is native Messages-API, `BedrockProvider` wraps
  the AWS SDK, `VertexProvider` uses Google ADC. Map your wire-specific finish reason to
  `StopReason`. Add the provider key to `ProviderConfig.ResolveProvider` / `SettingsFor`, and
  have both `src/FreeAgent.Host/Program.cs` (CLI bootstrap) **and**
  `src/FreeAgent.Server/ProviderFactory.cs` (protocol server) instantiate it.
- **A new sub-agent role:** `AgentRegistry.Register(new AgentDefinition(Type, AllowedTools, Prompt))`
  in the host. Tools you list must already be registered in the parent registry.
- **A new host command:** add a `case "/foo":` in `HostCommands.Handle`, implement the body as a
  static method (testable), and register the metadata in `HostCommands.BuildDefaultRegistry` so
  `/commands` and the future TUI palette pick it up.

## Reference docs

- `README.md`, `docs/architecture.md`, `docs/usage.md` — overview, deep tour, CLI + server usage.
- `docs/codecarto/reimplementation-spec.md` — the **canonical behavioral contract** the kernel
  implements; code comments cite its section names ("contracts §…").
- `docs/decisions/` — ADRs (kernel-first, Linux-native-first, extension-first capabilities,
  **headless-core + protocol with pluggable frontends** — ADR 0005).
- `ROADMAP.md` — what's done and what remains.
