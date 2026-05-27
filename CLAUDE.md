# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

Requires the **.NET 10 SDK** (pinned to `10.0.100` with `rollForward: latestMinor` in `global.json`).

```bash
dotnet build FreeAgent.slnx          # build everything (warnings are errors — a clean build is meaningful)
dotnet test  FreeAgent.slnx          # run all 135 tests (xUnit + FluentAssertions)
dotnet run --project src/FreeAgent.Host -- --verbose   # run the CLI (flags after --)

# A single test / subset (xUnit filters):
dotnet test FreeAgent.slnx --filter "FullyQualifiedName~OpenAIProviderTests"
dotnet test FreeAgent.slnx --filter "FullyQualifiedName~DoomLoop"   # matches by method name
```

Running the CLI needs `OPENAI_API_KEY` (required); `OPENAI_BASE_URL` (default
`https://api.openai.com/v1`) and `FREEMODEL` (default `gpt-4o-mini`) are optional.
The process's **current directory is the agent's sandbox** and where `session.jsonl`
is written — launch from the directory you want it to operate on.

## Two project-wide conventions that will trip you up

- **All kernel types live in the single flat `FreeAgent.Kernel` namespace**, regardless
  of their folder (`Sessions/`, `Tools/Adapters/`, `Providers/Adapters/`, …). One
  `using FreeAgent.Kernel;` imports everything; do **not** invent sub-namespaces from
  folder names.
- **`Directory.Build.props` (repo root) supplies `TargetFramework=net10.0`,
  `LangVersion=preview`, `Nullable`, `ImplicitUsings`, and `TreatWarningsAsErrors` to
  every project.** Individual `.csproj` files are intentionally bare — do not re-add
  these per project.

## Architecture

FreeAgent is **kernel-first**: `FreeAgent.Kernel` is the product (a deterministic,
fully-tested agent core), `FreeAgent.Host` is a thin CLI shell over it, and
`FreeAgent.Kernel.Tests` exercises it. The kernel holds **no global/static mutable
state** and reaches the outside world only through interfaces — that is what makes it
testable against fakes with no network, model, or real filesystem.

| Seam (`I…`)        | Default impl            | Test fake                          |
| ------------------ | ----------------------- | ---------------------------------- |
| `IProvider`        | `OpenAIProvider`        | `FakeProvider` + `StreamScript`    |
| `ITool`            | `ReadFile/WriteFile/ProcessExecTool` | `FakeTool`            |
| `IPermissionEngine`| `PermissionEngine`      | `RecordingPermissionEngine`        |
| `IPersistenceStore`| `JsonlSessionStore`     | `InMemorySessionStore`             |
| `IAtomicFileSystem`| `LinuxAtomicFileSystem` | `RecordingAtomicFileSystem`        |
| `IEventSink`       | `ConsoleEventSink` (host) | `RecordingEventSink`             |

### The turn loop

`SessionRuntime.RunTurnAsync` runs the agentic loop (bounded at 90 iterations):
stream from the provider → emit chunks to the `IEventSink` → if the model returned no
tool calls, persist and return; otherwise run the tool batch and loop. Two things to
know when touching it:

- **Streaming tool calls are reassembled by id.** A provider splits one tool call
  across many SSE chunks (`ToolCallDelta`); the runtime buffers argument fragments per
  id and emits one complete `ToolCall` when the stream ends.
- **`DoomLoopDetector`** trips when the *identical* tool-call batch (names +
  canonicalized JSON args) repeats 3× in a row; `SessionRuntime` then suppresses the
  repeat and re-prompts the model up to `DoomRecoveryBudget` (3) times before halting
  the turn, surfacing `TurnResult.DoomLoopDetected`.

### Tool execution: two layers, strict contracts

`TurnExecutor` schedules a batch, then `ToolPipeline` runs each call. These contracts
are load-bearing and are asserted by tests — preserve them:

- **Concurrency (`TurnExecutor`):** calls whose tool is **both `IsReadOnly` and
  `IsConcurrencySafe`** run together in one parallel window; everything else runs
  serially; **results are always returned in original call order**. If a parallel call
  `Crash`es, siblings are cancelled and reported as `Cancelled` ("sibling abort"),
  distinct from user cancellation.
- **Pipeline (`ToolPipeline.ExecuteAsync`):** a fixed **12-step** sequence
  (parse → schema-validate → sanity-check → plan-mode-guard → permission → cache-lookup
  → pre-hook → execute → post-hook → artifact-store → cache-write → invalidate).
  **No exception escapes** — bad JSON, unknown tool, schema failure, cancellation, and
  crashes are all mapped to a `ToolResult`. Failures **short-circuit before any
  side-effecting step**. Every step (including the future no-op *seams*) appends to
  `StepLog`, so the traversal order is observable and tested. The seams are the
  designated extension points for caching/hooks/artifacts.

### Permission model

`PermissionEngine.Decide(tool, capabilities, workingDir)` is pure and deterministic
(no clock, no I/O, no prompts). A tool declares the `Capability` objects a *specific
call* needs via `ITool.RequiredCapabilities(args, context)` — that is the only coupling
between tools and authorization. Precedence (first match wins): hardcoded security
blocks → tool-deny → capability-deny → no-caps-allow → tool-allow → per-capability
coverage (allowed type / allow rule / auto-allow) → otherwise **deny**. Hardcoded
blocks (`sudo`, `chmod`, … and writes under `/etc`, `/usr`, …) and any deny **always
beat allows**. Auto-allowed without a rule: reads inside the working dir, a small set
of safe read-only binaries (incl. `git status|diff|log`), and `read` memory ops.

### Result taxonomy

`ToolResult` carries a `ToolResultKind` (`Success, InvalidInput, PermissionDenied,
PlanModeBlocked, StateConflict, Crash, Empty, Cancelled`). Everything but `Success` is
an error and may include a model-facing `RetryHint`. Use the static factories rather
than constructing kinds by hand.

### Persistence

`JsonlSessionStore` writes a JSONL transcript (header line + one line per message)
through `IAtomicFileSystem` as **write-temp → fsync temp → rename → fsync directory**,
so an interrupted save never corrupts the file. `SessionState.PlanMode` is **in-memory
only** (never persisted); when set, the pipeline's plan-mode guard blocks every
non-read-only tool before the permission step.

## Adding things

- **A new tool:** implement `ITool` (declare `IsReadOnly`/`IsConcurrencySafe` honestly —
  they drive parallel scheduling), derive its `Capability` in `RequiredCapabilities`,
  resolve paths with `WorkspacePath.Resolve` (the same rule the permission engine uses,
  so the checked path equals the acted path), let `OperationCanceledException`
  propagate (the pipeline maps it to `Cancelled`), and map other failures to
  `ToolResult` classes rather than throwing. Register it in `src/FreeAgent.Host/Program.cs`.
- **A new provider:** implement `IProvider.StreamChatAsync` yielding `StreamChunk`s;
  mirror `OpenAIProvider` (SSE parsing, per-id tool-call accumulation, usage extraction).

## Reference docs

- `README.md`, `docs/architecture.md`, `docs/usage.md` — overview, deep tour, CLI usage.
- `docs/codecarto/reimplementation-spec.md` — the **canonical behavioral contract** the
  kernel implements; code comments cite its section names ("contracts §…").
- `docs/decisions/` — ADRs (kernel-first, Linux-native-first, extension-first capabilities).
