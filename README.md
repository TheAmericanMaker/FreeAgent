# FreeAgent

A Linux-native, modular **agent kernel** for tool-using LLMs, with an interactive
command-line host. FreeAgent talks to any **OpenAI-compatible** chat-completions
endpoint, streams responses, lets the model call real tools (read files, write
files, run processes), and enforces a deterministic capability-based permission
model around every one of those calls.

The kernel is the product: a small, well-tested core (`FreeAgent.Kernel`) that owns
the turn loop, the tool-execution pipeline, the permission engine, and crash-safe
session persistence. The CLI (`FreeAgent.Host`) is a thin shell over it.

```
You ▸ list the .cs files under src and tell me which is largest

  …model streams reasoning + text…
  ▸ ProcessExec  ls -R src        (auto-allowed: safe read-only binary)
  ▸ ReadFile     src/.../Big.cs   (auto-allowed: inside working dir)

The largest is OpenAIProvider.cs at 307 lines …
```

---

## Contents

- [Why FreeAgent](#why-freeagent)
- [Quick start](#quick-start)
- [Configuration](#configuration)
- [How a turn works](#how-a-turn-works)
- [The tool-execution pipeline](#the-tool-execution-pipeline)
- [Permission model](#permission-model)
- [Built-in tools](#built-in-tools)
- [Session persistence](#session-persistence)
- [Project layout](#project-layout)
- [Development](#development)
- [Design decisions](#design-decisions)
- [Roadmap & non-goals](#roadmap--non-goals)

---

## Why FreeAgent

- **Provider-agnostic.** Any OpenAI-compatible `/chat/completions` server works —
  hosted OpenAI, a gateway, or a local model server. Point `OPENAI_BASE_URL` at it
  and set `FREEMODEL`. That freedom is where the name comes from.
- **Safe by construction.** Tools never act before the permission engine approves
  the specific *capabilities* a call needs. Some binaries (`sudo`, `chmod`, …) and
  write paths (`/etc`, `/usr`, …) are blocked unconditionally and cannot be
  re-enabled by an allow rule.
- **Deterministic and testable.** The kernel has no global state and no hidden I/O.
  Providers, tools, the clock-free permission engine, and the filesystem are all
  interfaces, so the 135-test suite runs entirely against fakes.
- **Crash-safe.** Sessions persist to JSONL through an atomic write-temp → fsync →
  rename → fsync-dir sequence, so a crash mid-write never corrupts the transcript.

## Install

Requires the **.NET 10 SDK** (the repo pins `10.0.100` via `global.json` with
`rollForward: latestMinor`). Install the `freeagent` command as a .NET global tool:

```bash
# From a published release (once available on NuGet):
dotnet tool install --global FreeAgent

# Or from a local checkout:
./scripts/install.sh        # packs + installs/updates the global tool

freeagent --version
freeagent --help
```

If the command isn't found, add the tools directory to your `PATH`:
`export PATH="$PATH:$HOME/.dotnet/tools"`.

Then, from **any project directory**, just run:

```bash
export OPENAI_API_KEY=sk-...   # or put it in the config file (see Configuration)
cd ~/code/my-project
freeagent
```

The directory you launch from is the agent's sandbox.

## Quick start (from source, without installing)

```bash
dotnet build FreeAgent.slnx     # build
dotnet test  FreeAgent.slnx     # 185 tests, all green
OPENAI_API_KEY=sk-... dotnet run --project src/FreeAgent.Host
```

You get a prompt. Type a request; the model streams its reply and may call tools.
The **working directory is wherever you launched the process** — that is the
sandbox tools read and write within.

```
> summarize what this repo does, then write it to NOTES.md
> exit
```

- `exit` or `quit` (or EOF) ends the session and saves it.
- **Ctrl+C** cancels the *current turn* without killing the process.
- Pass `--verbose` (or `-v`) to also print the model's reasoning and per-turn
  token usage.

If `OPENAI_API_KEY` is unset, the host prints a clear error and exits with code 1
before contacting anything.

## Configuration

The host is configured through environment variables and a few flags:

| Variable          | Required | Default                       | Purpose                                              |
| ----------------- | :------: | ----------------------------- | ---------------------------------------------------- |
| `OPENAI_API_KEY`  |   yes    | —                             | Bearer token sent to the provider.                   |
| `OPENAI_BASE_URL` |    no    | `https://api.openai.com/v1`   | Endpoint base; `/chat/completions` is appended.      |
| `FREEMODEL`       |    no    | `gpt-4o-mini`                 | Model name passed in the request body.               |
| `FREEAGENT_CONFIG`|    no    | `.freeagent/config.json`      | Path to the permission-rules config (see below).     |

| Flag              | Purpose                                                          |
| ----------------- | --------------------------------------------------------------- |
| `--help`, `-h`    | Show usage and exit.                                             |
| `--version`       | Show the version and exit.                                      |
| `--verbose`, `-v` | Print streamed reasoning (dimmed) and a `[Tokens: in → out]` line. |
| `--resume [id]`   | Resume the session in `session.jsonl` (optionally requiring its id). |

At the prompt, `/plan [on\|off]` toggles plan mode (read-only tools only).

### Provider settings without env vars

So the bare `freeagent` command works in any shell, the provider settings can also live in a
user config file at `$XDG_CONFIG_HOME/freeagent/config.json` (default
`~/.config/freeagent/config.json`). Precedence is **environment variable > config file > default**:

```jsonc
{
  "baseUrl": "http://localhost:11434/v1",  // e.g. Ollama
  "model": "qwen2.5-coder",
  "apiKey": "…"                            // optional; prefer the env var, or chmod 600 this file
}
```

Because any OpenAI-compatible base URL is accepted, a local server typically just
needs `baseUrl` set to `http://localhost:<port>/v1` (and any non-empty key).

### Granting permissions via config

By default writes and non-safe binaries are denied (see [Permission model](#permission-model)).
To grant them without code, drop a `.freeagent/config.json` in the working directory (or point
`FREEAGENT_CONFIG` at one). A capability rule with no `pattern` (or `"*"`) covers the whole
capability type; otherwise the pattern is a glob matched against the capability's target. Hardcoded
security blocks still cannot be overridden.

```jsonc
{
  // allow writing anywhere under the project, and let the agent run npm + node
  "allow": [
    { "capability": "FileWriteCap", "pattern": "**" },
    { "capability": "ProcessExecCap", "pattern": "npm" },
    { "capability": "ProcessExecCap", "pattern": "node" }
  ],
  // ...but never let it run this one, even if a broader rule would
  "deny": [ { "capability": "ProcessExecCap", "pattern": "rm" } ],
  "allowTools": [],
  "denyTools": []
}
```

A missing config is fine; a malformed one is a non-fatal startup warning.

## How a turn works

One user message drives `SessionRuntime.RunTurnAsync`, which runs an
agentic loop (bounded at 90 iterations) until the model produces a reply with no
tool calls:

```
user text
   │
   ▼
┌────────────────────────────────────────────────────────────┐
│ for each iteration:                                          │
│   stream provider(messages + tool defs)                      │
│     ├─ thinking delta ─▶ IEventSink.OnThinking               │
│     ├─ text delta     ─▶ IEventSink.OnText  (+ accumulate)   │
│     ├─ tool-call delta ─▶ accumulate by id (args may split)  │
│     └─ usage          ─▶ IEventSink.OnUsage                  │
│                                                              │
│   no tool calls?  ─▶ save session, return final text  ✔      │
│                                                              │
│   same tool-call batch 3× in a row? ─▶ suppress + re-prompt  │
│                                                              │
│   else: run the batch through the TurnExecutor,              │
│         append tool results, loop again                      │
└────────────────────────────────────────────────────────────┘
```

**Streaming normalization.** A provider may split one logical tool call across many
SSE chunks; the runtime buffers argument fragments by call id and emits one
complete `ToolCall` per id once the stream ends.

**Doom-loop detection.** If the model emits the *identical* tool-call batch (same
names + canonicalized JSON args) three times running, the runtime stops executing
that batch and re-prompts the model — injecting a notice into the transcript so it
can change course — rather than running the repeat. It allows up to **3 such
recovery re-prompts**; if the model is still looping after that, the turn **halts**.
A genuinely stuck turn that somehow escapes the guard is ultimately bounded by the
90-iteration loop cap. The result carries `DoomLoopDetected = true`.

**Concurrency contract (`TurnExecutor`).** Within a single batch:

- Calls that are **both read-only and concurrency-safe** run together in one
  parallel window.
- Every other call runs **serially**.
- Results are always returned in the **original call order**.
- If a parallel call crashes, its siblings are cancelled and reported as
  `Cancelled` ("sibling abort"); user cancellation is distinguished from it.

## The tool-execution pipeline

Every tool call traverses `ToolPipeline.ExecuteAsync` as a fixed 12-step sequence.
A failure short-circuits *before* any side-effecting step runs, and an exception
never escapes the pipeline — it is mapped to a result class. Steps marked *(seam)*
are deliberate, logged no-ops today: they reserve the place for a future extension.

| #  | Step             | Behavior                                                                 |
| -- | ---------------- | ------------------------------------------------------------------------ |
| 1  | parse            | Parse `ArgumentsJson`; bad JSON → `InvalidInput` (never throws).         |
| 2  | schema-validate  | Resolve the tool; unknown tool → `InvalidInput`; validate args vs schema.|
| 3  | sanity-check     | *(seam)* path-escape / workspace-boundary checks.                        |
| 4  | plan-mode-guard  | If `PlanMode` is on, a non-read-only tool is `PlanModeBlocked` here.      |
| 5  | permission       | Gather capabilities; the engine decides; deny → `PermissionDenied`.      |
| 6  | cache-lookup     | *(seam)* read-only result cache.                                         |
| 7  | pre-hook         | *(seam)* non-fatal pre-execution hook.                                   |
| 8  | execute          | Run the tool; cancellation → `Cancelled`, exception → `Crash`.           |
| 9  | post-hook        | *(seam)* non-fatal post-execution hook.                                  |
| 10 | artifact-store   | *(seam)* offload large success previews.                                 |
| 11 | cache-write      | *(seam)* persist read-only successes.                                    |
| 12 | invalidate       | *(seam)* invalidate caches after mutating tools.                         |

A tool that succeeds but returns blank content is reported as `Empty`, so the model
gets a distinct signal rather than an ambiguous empty success.

**Result taxonomy.** Every result is one of: `Success`, `InvalidInput`,
`PermissionDenied`, `PlanModeBlocked`, `StateConflict`, `Crash`, `Empty`,
`Cancelled`. All but `Success` are errors, and error classes may carry a
model-facing `RetryHint`.

## Permission model

The permission engine is deterministic and non-interactive: given a tool, the
capabilities a call requires, and the working directory, it returns allow/deny with
a reason. Evaluation order (first match wins):

1. **Hardcoded security blocks** — blocked binaries and protected write prefixes.
   Never overridable, even by an allow rule.
2. **Tool-level deny** — beats any allow.
3. **Capability-level deny** (by type or glob rule) — beats any allow.
4. **No capabilities required** → allow.
5. **Tool-level allow** → covers all of the tool's capabilities.
6. **Per-capability coverage** — an allowed capability type, a matching allow rule,
   or an auto-allow rule.
7. **Any uncovered capability** → deny (this is where a UX layer would prompt).

**Capabilities** are fine-grained authorization units a tool declares per call:
`FileReadCap`, `FileWriteCap`, `ProcessExecCap`, `NetworkEgressCap`,
`VcsMutationCap`, `MemoryCap`, `AgentSpawnCap`.

**Auto-allowed without a rule:**

- `FileReadCap` whose path resolves **inside the working directory**.
- `ProcessExecCap` for a safe read-only binary —
  `pwd, ls, cat, head, tail, grep, rg, find`, plus `git status|diff|log`.
- `MemoryCap` with a `read` operation.

**Always blocked (cannot be allowed):**

- Binaries: `sudo, su, doas, pkexec, chmod, chown, chattr, setfacl, icacls,
  takeown, attrib`.
- Writes under: `/etc/, /usr/, /bin/, /sbin/, /System/, /Library/`.

Everything else (writes outside the workspace, network egress, VCS mutation,
arbitrary binaries, sub-agent spawns) requires an explicit allow rule and otherwise
denies — safe by default.

## Built-in tools

The host registers these adapters. Each carries a model-facing description (sent to the provider as
the function description) and declares whether it is read-only and concurrency-safe (which drives the
parallel/serial scheduling above) and which capability it needs.

| Tool            | Args                                   | Read-only | Capability        | Notes                                                                 |
| --------------- | -------------------------------------- | :-------: | ----------------- | --------------------------------------------------------------------- |
| `ReadFile`      | `path`                                 |    yes    | `FileReadCap`     | UTF-8 read; auto-allowed inside the workspace.                        |
| `WriteFile`     | `path`, `content`                      |    no     | `FileWriteCap`    | Creates parent dirs; never auto-allowed; protected prefixes blocked.  |
| `ProcessExec`   | `command`, `args?`                     |    no     | `ProcessExecCap`  | Runs in the workspace; 30s timeout kills the process tree; returns exit code + stdout/stderr. |
| `Glob`          | `pattern`, `path?`                     |    yes    | `FileReadCap`     | Find files by glob (`**/*.cs`); managed (no `rg`); skips noise dirs; capped. |
| `Grep`          | `pattern`, `path?`, `glob?`, `ignore_case?` | yes  | `FileReadCap`     | Regex content search → `path:line:text`; skips binary files; capped.  |
| `EnterPlanMode` | —                                      |    yes    | none              | Turns plan mode on (only read-only tools run until exit).             |
| `ExitPlanMode`  | —                                      |    yes    | none              | Turns plan mode off; read-only so it is callable while plan mode is active. |

`Glob`/`Grep` are read-only **and** concurrency-safe, so they run in the parallel window. Paths are
resolved against the working directory by the same rule the permission engine uses, so the capability
checked at step 5 and the path acted on at step 8 always agree.

## Session persistence

Sessions are stored as **JSONL** (`session.jsonl` in the working directory by
default): line 1 is a header (`session_id`, `started_at`, `working_directory`) and
each subsequent line is one message. The runtime saves after every completed turn
and on exit.

Writes go through `LinuxAtomicFileSystem` as **write-temp → fsync temp → rename →
fsync directory**, so an interrupted save can never leave a half-written or
corrupt transcript — readers see either the old file or the complete new one.

## Project layout

```
FreeAgent.slnx                     Solution (Kernel, Kernel.Tests, Host)
Directory.Build.props              Shared: net10.0, nullable, implicit usings, warnings-as-errors
global.json                        Pins the .NET 10 SDK

src/FreeAgent.Kernel/              The kernel library
  Messaging/      Message, MessageRole, ToolCall, ToolResult
  Providers/      IProvider, ProviderRequest, StreamChunk, ToolCallDelta, Usage
    Adapters/     OpenAIProvider (OpenAI-compatible SSE streaming)
  Tools/          ITool, IToolRegistry, ToolRegistry, ToolPipeline, ToolDefinition, ToolContext
    Adapters/     ReadFileTool, WriteFileTool, ProcessExecTool, GlobTool, GrepTool,
                  PlanModeTools, WorkspaceSearch, WorkspacePath
  Permissions/    IPermissionEngine, PermissionEngine, PermissionConfig, Capability, PermissionDecision
  Persistence/    IPersistenceStore, JsonlSessionStore, IAtomicFileSystem, LinuxAtomicFileSystem
  Sessions/       SessionRuntime, SessionState, TurnExecutor, TurnResult
  Runtime/        IEventSink, DoomLoopDetector
  Validation/     ToolInputSchemaValidator
  Serialization/  JsonOptions

src/FreeAgent.Host/                The interactive CLI
  Program.cs          Env config, permission-config load, tool registration, REPL, /plan, resume
  HostOptions.cs      Command-line option parsing (--verbose, --resume [id])
  ConsoleEventSink.cs Streams text to stdout; reasoning/usage gated behind --verbose

src/FreeAgent.Kernel.Tests/        xUnit + FluentAssertions; fakes for every seam

docs/                              Architecture notes, ADRs, and the reimplementation spec
```

## Development

```bash
dotnet build FreeAgent.slnx        # warnings are errors
dotnet test  FreeAgent.slnx        # 185 tests
dotnet run --project src/FreeAgent.Host -- --verbose
```

The build treats warnings as errors and enforces code style, so a clean build is a
real signal. Tests are written against fakes (`FakeProvider`, `FakeTool`,
`InMemorySessionStore`, `RecordingPermissionEngine`, …) and need no network, model,
or real filesystem. See `docs/architecture.md` for a deeper tour and
`docs/usage.md` for host details and recipes.

**Releasing.** Push a `v*` tag (e.g. `git tag v0.1.0 && git push --tags`) to trigger
`.github/workflows/release.yml`: it tests, packs the tool, attaches self-contained
binaries to the GitHub Release, and—if a `NUGET_API_KEY` secret is set—publishes to
NuGet so others can `dotnet tool install -g FreeAgent`. (NuGet IDs are global; if
`FreeAgent` is taken, change `PackageId` in `src/FreeAgent.Host/FreeAgent.Host.csproj`.)

## Design decisions

The reasoning behind the shape of the project lives in `docs/decisions/`:

- [0001 — Product direction](docs/decisions/0001-product-direction.md)
- [0002 — Kernel-first implementation strategy](docs/decisions/0002-kernel-first.md)
- [0003 — Linux-native first](docs/decisions/0003-linux-native-first.md)
- [0004 — Extension-first capabilities](docs/decisions/0004-extension-first-capabilities.md)
- [0005 — Headless core + protocol, with pluggable frontends](docs/decisions/0005-headless-core-protocol.md)

The full behavioral contract the kernel implements is in
`docs/codecarto/reimplementation-spec.md`.

## Roadmap & non-goals

See [`ROADMAP.md`](ROADMAP.md) for the full backlog (much of it seeded from
FreeAgent's OpenMonoAgent.ai lineage). In brief:

The pipeline's *(seam)* steps mark where the next features attach without reworking
the core: result caching, pre/post hooks, large-artifact offloading, and
cache invalidation. The permission engine already models capabilities
(`NetworkEgressCap`, `VcsMutationCap`, `AgentSpawnCap`, `MemoryCap`) that no tool
exercises yet — networked tools, VCS tooling, sub-agents, and memory are the
natural next adapters.

Deliberately **out of scope for now:** a full-screen TUI, MCP/LSP integration,
background process management, sub-agents, playbooks, a Docker wrapper, Roslyn-based
tooling, and a broad multi-provider matrix beyond the OpenAI-compatible shape.
