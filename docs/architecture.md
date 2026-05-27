# FreeAgent architecture

This is a tour of how the implemented kernel actually works — the contracts, the
data that flows between them, and the invariants each one guarantees. For the
product-level overview and how to run it, start with the [README](../README.md);
for the original behavioral specification the kernel implements, see
[`codecarto/reimplementation-spec.md`](codecarto/reimplementation-spec.md).

## The shape of the system

Everything the kernel touches at runtime is behind an interface, and the kernel
holds no global or static mutable state. That is what makes it deterministic and
fully testable against fakes.

```
                 ┌──────────────────────────────────────────────┐
                 │                 SessionRuntime                 │
                 │   the agentic turn loop + event emission       │
                 └───────┬───────────────┬───────────────┬───────┘
                         │               │               │
              IProvider  │   TurnExecutor│   IEventSink   │ IPersistenceStore
                         │      + ToolPipeline            │
            ┌────────────▼──┐  ┌──────────▼─────────┐  ┌──▼───────────────┐
            │ OpenAIProvider│  │  IToolRegistry     │  │ JsonlSessionStore│
            │  (SSE stream) │  │  ├ ReadFileTool    │  │   ▼              │
            └───────────────┘  │  ├ WriteFileTool   │  │ IAtomicFileSystem│
                               │  └ ProcessExecTool │  │ (LinuxAtomicFS)  │
                               │  IPermissionEngine │  └──────────────────┘
                               │  └ PermissionEngine│
                               └────────────────────┘
```

| Seam                | Contract            | Default implementation | Test fake                  |
| ------------------- | ------------------- | ---------------------- | -------------------------- |
| Model provider      | `IProvider`         | `OpenAIProvider`       | `FakeProvider` + `StreamScript` |
| Tool                | `ITool`             | three adapters         | `FakeTool`                 |
| Tool registry       | `IToolRegistry`     | `ToolRegistry`         | (real)                     |
| Permissions         | `IPermissionEngine` | `PermissionEngine`     | `RecordingPermissionEngine`|
| Persistence         | `IPersistenceStore` | `JsonlSessionStore`    | `InMemorySessionStore`     |
| Filesystem          | `IAtomicFileSystem` | `LinuxAtomicFileSystem`| `RecordingAtomicFileSystem`|
| Output/observability| `IEventSink`        | `ConsoleEventSink` (host) | `RecordingEventSink`    |

## The turn loop (`SessionRuntime`)

`RunTurnAsync(userText, ct)` appends the user message to `SessionState.Messages`,
then loops up to `MaxIterations` (1000) times:

1. Build a `ProviderRequest` from the **full** message history and the registry's
   current `ToolDefinition`s.
2. Stream chunks from `IProvider.StreamChatAsync`, handling each `StreamChunk`:
   - `ThinkingDelta` → `IEventSink.OnThinking`
   - `TextDelta` → accumulate **and** `IEventSink.OnText`
   - `ToolCallDelta` → accumulate into a `PartialToolCall` keyed by id
   - `Usage` → `IEventSink.OnUsage`
3. Collect the buffered tool calls into complete `ToolCall`s.
4. **Terminate** if there are no tool calls: append the assistant text, persist the
   session, and return `TurnResult(finalText, doomDetected)`.
5. **Doom-loop guard**: feed the batch to `DoomLoopDetector.Observe`. On the third
   identical batch in a row it returns true; the runtime appends a notice message,
   sets `doomDetected`, and `continue`s (re-prompting the model) rather than
   executing the repeat.
6. Otherwise append the assistant message (text + tool calls), execute the batch via
   `TurnExecutor`, append one `Tool` message per result (preserving call order), and
   loop.

### Streaming tool-call reassembly

OpenAI-style streaming delivers a tool call across many chunks: the first carries
the index/id/name, later chunks carry argument fragments. `PartialToolCall`
accumulates `ArgumentsJson` per id until the stream ends, then the runtime emits one
`ToolCall(id, name, argumentsJson)` per collected id. The provider adapter maps the
provider's `index` to a stable `id` so fragments join correctly even when the id only
appears on the first chunk.

### Doom-loop detection (`DoomLoopDetector`)

The detector builds a signature per batch: `name:canonicalJson` for each call,
joined with `|`. Arguments are canonicalized (re-serialized through `JsonOptions`)
so semantically identical args with different whitespace/key order still match. A
counter increments while the signature is unchanged and fires at threshold 3. The
detector is reset at the start of every turn.

## Tool execution

### Scheduling (`TurnExecutor`)

A completed batch is split by capability:

- A call whose tool is **`IsReadOnly && IsConcurrencySafe`** joins a single parallel
  window run with `Task.WhenAll`.
- All other calls run **serially**, in order, after the parallel window.
- The result array is indexed by original position, so ordering is preserved
  regardless of how calls were scheduled.

Failure handling inside the parallel window uses a **linked sibling-abort token**:

- If a parallel call returns `Crash` (and the user has not cancelled), the executor
  cancels the sibling token, stopping the rest of the window early.
- A call cancelled *because* of that sibling abort is reported as `Cancelled` with a
  "sibling abort" message — distinct from user cancellation, which propagates the
  user's intent.
- Any slot that somehow has no result is backfilled with a `Crash` result, so the
  array is always complete and aligned with the calls.

### The pipeline (`ToolPipeline`)

Each call runs the 12-step pipeline described in the
[README](../README.md#the-tool-execution-pipeline). The contract that matters:

- **No exception escapes.** Bad JSON, unknown tool, schema failure, cancellation,
  and crashes are all mapped to a `ToolResult` class.
- **Short-circuit before side effects.** parse → schema-validate → plan-mode-guard →
  permission all run before `execute`, so a rejected call never touches the world.
- **Observable order.** Every step (including the future-seam no-ops) appends to
  `StepLog` under a lock, so tests assert the exact traversal order and seam
  placement.
- **Empty success normalization.** A `Success` with blank content becomes `Empty`.

### Tools (`ITool`)

A tool declares `Name`, an `InputSchema` (`JsonDocument`), `IsReadOnly`,
`IsConcurrencySafe`, the `RequiredCapabilities(args, context)` for a given call, and
`ExecuteAsync`. `RequiredCapabilities` is what couples a tool to the permission
engine: the tool says *what authorization this specific call needs*, derived from
its arguments, and the engine decides. `WorkspacePath.Resolve` is shared between
capability derivation and execution so the path checked equals the path acted on.

## Permission engine (`PermissionEngine`)

Pure and deterministic: `Decide(tool, capabilities, workingDirectory)` →
`PermissionDecision`. It depends only on its inputs and the configured rules — no
clock, no I/O, no prompts. Session rules are configured by method
(`AllowTool`, `DenyTool`, `AllowCapabilityType<T>`, `DenyCapabilityType<T>`,
`AllowCapabilityRule<T>(pattern)`, `DenyCapabilityRule<T>(pattern)`).

Rules match a capability's `MatchTarget` (path, binary, host, …) with an anchored,
case-insensitive glob (`*` = any run, `?` = one char). The full precedence and the
hardcoded allow/block sets are in the
[README](../README.md#permission-model). The key invariant: **denies and hardcoded
security blocks always beat allows**, and an uncovered capability denies rather than
defaulting open.

## Persistence (`JsonlSessionStore` + `IAtomicFileSystem`)

`SerializeAsync` emits a header line (`session_id`, `started_at`,
`working_directory`) followed by one JSON line per message; `DeserializeAsync`
reverses it with precise, line-numbered error messages for a malformed header or
record. `SaveAsync` performs the durable write through the filesystem seam:

```
CreateTempPath → WriteTemp → FsyncTemp → Rename(temp → target) → FsyncDirectory
```

`LinuxAtomicFileSystem` implements this with real POSIX `fsync` on both the file and
its containing directory (`PosixFileSystemPrimitives`), which is what makes the
rename durable. On failure it best-effort deletes the temp file and rethrows the
original exception. The atomic `rename` guarantees a reader sees either the previous
complete file or the new complete file — never a partial write.

## Provider adapter (`OpenAIProvider`)

An `IProvider` over any OpenAI-compatible `/chat/completions` endpoint:

- Two constructors — one that owns its `HttpClient` (`baseUrl, apiKey, model`) and
  one that accepts an injected `HttpClient` for tests. The base URL is
  trailing-slash normalized; the request targets `{baseUrl}/chat/completions` with
  `Bearer` auth.
- The request body is written directly with `Utf8JsonWriter` (no serialization
  dependency): messages with roles, `tool_calls`, and `tool_call_id`; tools as
  `type: function` with their JSON-schema parameters.
- The SSE stream is read line by line. `data:` payloads are parsed into
  `StreamChunk`s: text deltas, tool-call deltas (mapping `index` → stable id and
  accumulating `function.arguments`), and usage. `usage` understands both OpenAI
  (`prompt_tokens`/`completion_tokens`) and Anthropic-style
  (`input_tokens`/`output_tokens`) field names. `finish_reason` sets `IsComplete`;
  `[DONE]` ends the stream.
- A non-2xx response throws `HttpRequestException` carrying the status code and body.

## State (`SessionState`)

Holds `SessionId`, `WorkingDirectory`, `StartedAt`, the `Messages` list, and a
`PlanMode` flag. `PlanMode` is **in-memory only** (never persisted); when set, the
pipeline's plan-mode guard blocks every non-read-only tool with `PlanModeBlocked`
before the permission step — a read-only "look but don't touch" mode.

## Testing model

The suite (135 tests, xUnit + FluentAssertions) exercises each contract against a
fake counterpart: `FakeProvider` replays a `StreamScript` of chunks;
`RecordingPermissionEngine` and `RecordingEventSink` capture interactions;
`InMemorySessionStore` and `RecordingAtomicFileSystem` stand in for durable I/O.
Because nothing reaches the network or a real model, the tests are fast,
hermetic, and deterministic — the same properties the kernel design is built to
preserve.
