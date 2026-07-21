# FreeAgent

A Linux-native, modular **agent kernel** for tool-using LLMs, with an interactive
CLI, an HTTP + SSE protocol server, and a full-screen terminal UI. FreeAgent has
native adapters for six provider APIs and also talks to **OpenAI-compatible**
chat-completions endpoints. It streams responses, lets the model call real tools
(read files, write files, run processes), and enforces a deterministic
capability-based permission model around every one of those calls.

The kernel is the product: a small, well-tested core (`FreeAgent.Kernel`) that owns
the turn loop, the tool-execution pipeline, the permission engine, and crash-safe
session persistence. The CLI (`FreeAgent.Host`) and server (`FreeAgent.Server`) are
thin shells over it; `clients/tui` consumes the server protocol.

Licensed under [Apache 2.0](LICENSE). See [NOTICE](NOTICE) for acknowledgments.

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
- [Security](#security)
- [License and provenance](#license-and-provenance)

---

## Why FreeAgent

- **Provider-agnostic.** Native adapters for OpenAI, Anthropic, Azure OpenAI, Ollama,
  AWS Bedrock, and Google Vertex AI — any OpenAI-compatible `/chat/completions`
  endpoint (Groq, gateways, local servers) also works through the OpenAI path. That
  freedom is where the name comes from.
- **Safe by construction.** Tools never act before the permission engine approves
  the specific *capabilities* a call needs. Some binaries (`sudo`, `chmod`, …) and
  write paths (`/etc`, `/usr`, …) are blocked unconditionally and cannot be
  re-enabled by an allow rule.
- **Deterministic and testable.** The kernel has no global state and no hidden I/O.
  Providers, tools, the clock-free permission engine, and the filesystem are all
  interfaces, so the 550-test suite runs entirely against fakes — no network, no model,
  no real filesystem.
- **Crash-safe.** Sessions persist to JSONL through an atomic write-temp → fsync →
  rename → fsync-dir sequence, so a crash mid-write never corrupts the transcript.
- **Frontend-agnostic.** Per ADR 0005, the kernel is headless; `FreeAgent.Host`
  is one frontend (interactive CLI) and `FreeAgent.Server` is another (HTTP + SSE
  protocol with an OpenAPI spec at `/openapi/v1.json`).

## Install

### Quick install (one line)

```bash
curl -fsSL https://raw.githubusercontent.com/TheAmericanMaker/FreeAgent/main/scripts/get.sh | bash
```

This detects your OS, installs the .NET 10 SDK if needed, builds and installs the
`freeagent` global tool, then runs the interactive setup wizard to configure a provider.
Works on Fedora, Ubuntu/Debian, macOS, and any Linux distro with `curl`.

After install, from any project directory:

```bash
freeagent        # start a session
```

### Full-screen TUI

```bash
curl -fsSL https://raw.githubusercontent.com/TheAmericanMaker/FreeAgent/main/scripts/get.sh | bash -s -- --tui
```

Or from a clone:

```bash
git clone https://github.com/TheAmericanMaker/FreeAgent.git
cd FreeAgent
scripts/install-tui.sh          # Windows: powershell -ExecutionPolicy Bypass -File scripts\install-tui.ps1
scripts/freeagent-ui            # Windows: scripts\freeagent-ui.ps1
```

`install-tui` installs Bun if needed, restores the UI's dependencies, and publishes
`FreeAgent.Server` as a **self-contained binary** — so after install the app launches with no .NET
SDK at run time. (Publishing needs the .NET 10 SDK once; pass `--skip-publish` to use a `dotnet run`
dev server instead.) On first launch the app walks you through provider setup inside the UI. The
TUI lives in [`clients/tui/`](clients/tui/).

### CLI tool from source

```bash
git clone https://github.com/TheAmericanMaker/FreeAgent.git
cd FreeAgent
./scripts/install.sh
```

The installer runs a few pre-flight checks (`.NET 10` SDK present, `~/.dotnet/tools` on `PATH`),
builds + packs + installs the `freeagent` global tool, then hands off to **`freeagent setup`** —
a wizard that asks which provider you want, prompts for credentials (API key input is masked),
and writes `~/.config/freeagent/config.json` with mode `600`. Re-run `freeagent setup` any time
you want to switch providers or add another one alongside the current default.

Other modes if you'd rather not be prompted:

```bash
./scripts/install.sh --non-interactive   # build + install only; no prompts, no wizard
./scripts/install.sh --skip-setup        # interactive install but don't run the wizard
dotnet tool install --global FreeAgent   # once a release lands on NuGet
```

To remove it later:

```bash
./scripts/uninstall.sh           # guided — asks before touching state
./scripts/uninstall.sh --purge   # remove the tool AND ~/.config/freeagent + ~/.cache/freeagent
```

Verify the install and explore:

```bash
freeagent --version
freeagent --help            # subcommands + flags
freeagent                   # start a session in the current directory
```

If `freeagent` isn't found after install, the installer probably appended
`export PATH="$PATH:$HOME/.dotnet/tools"` to your shell profile but the running shell hasn't
picked it up yet — restart your shell or `source ~/.zshrc` / `~/.bashrc` / `~/.profile`.

Then, from **any project directory**, just run:

```bash
# If you skipped 'freeagent setup', you can still configure providers with env vars:
export OPENAI_API_KEY=sk-...   # or put it in the config file (see Configuration)
cd ~/code/my-project
freeagent
```

The directory you launch from is the agent's sandbox.

## Quick start (from source, without installing)

```bash
dotnet build FreeAgent.slnx     # build everything (warnings are errors)
dotnet test  FreeAgent.slnx     # 550 pass + 0 skip
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

The host is configured through environment variables and a few flags.

#### Provider selection

| Variable          | Default     | Purpose                                                                                    |
| ----------------- | ----------- | ------------------------------------------------------------------------------------------ |
| `FREEPROVIDER`    | `openai`    | Active provider — `openai`, `anthropic`, `azure`, `ollama`, `bedrock`, or `vertex`.        |
| `FREEMODEL`       | per-provider| Model name (also `OPENAI_MODEL` / `ANTHROPIC_MODEL` / `AZURE_OPENAI_DEPLOYMENT` / `OLLAMA_MODEL`). |

OpenAI / OpenAI-compat:

| Variable          | Required | Default                     | Purpose                                              |
| ----------------- | :------: | --------------------------- | ---------------------------------------------------- |
| `OPENAI_API_KEY`  |   yes    | —                           | Bearer token sent to the provider.                   |
| `OPENAI_BASE_URL` |    no    | `https://api.openai.com/v1` | Endpoint base; `/chat/completions` is appended.      |

Anthropic (`FREEPROVIDER=anthropic`):

| Variable             | Required | Default                      | Purpose                                                            |
| -------------------- | :------: | ---------------------------- | ------------------------------------------------------------------ |
| `ANTHROPIC_API_KEY`  |   yes    | —                            | Sent as `x-api-key`.                                              |
| `ANTHROPIC_BASE_URL` |    no    | `https://api.anthropic.com`  | Endpoint base; `/v1/messages` is appended.                         |
| `FREE_MAX_TOKENS`    |    no    | `4096`                       | Per-request `max_tokens` ceiling on the visible reply.            |
| `FREE_THINKING_BUDGET` |   no   | `0` (off)                    | Enables extended thinking with the given token budget for reasoning. |

Azure OpenAI (`FREEPROVIDER=azure`):

| Variable                       | Required | Default               | Purpose                                              |
| ------------------------------ | :------: | --------------------- | ---------------------------------------------------- |
| `AZURE_OPENAI_API_KEY`         |   yes    | —                     | Sent as `api-key`.                                  |
| `AZURE_OPENAI_ENDPOINT`        |   yes    | —                     | e.g. `https://my-resource.openai.azure.com`.        |
| `AZURE_OPENAI_DEPLOYMENT`      |   yes    | —                     | Deployment name (used in place of model).           |
| `AZURE_OPENAI_API_VERSION`     |    no    | `2024-08-01-preview`  | Azure API version.                                  |

Ollama (`FREEPROVIDER=ollama`, no API key needed):

| Variable          | Default                  | Purpose                                              |
| ----------------- | ------------------------ | ---------------------------------------------------- |
| `OLLAMA_HOST`     | `http://localhost:11434` | Endpoint base.                                       |
| `OLLAMA_MODEL`    | `qwen2.5-coder`          | Model name.                                          |
| `FREE_NUM_CTX`    | (off)                    | Per-request `num_ctx` in Ollama options.            |
| `FREE_TEMPERATURE`| (off)                    | Per-request `temperature` in Ollama options.        |

AWS Bedrock (`FREEPROVIDER=bedrock`, auth from default AWS credential chain — env, profile, IMDS, SSO):

| Variable          | Default                                              | Purpose                                |
| ----------------- | ---------------------------------------------------- | -------------------------------------- |
| `AWS_REGION`      | `us-east-1`                                          | Region the SDK targets.               |
| `BEDROCK_MODEL`   | `anthropic.claude-3-7-sonnet-20250219-v1:0`          | Bedrock model id.                     |

Google Vertex AI (`FREEPROVIDER=vertex`, auth from GCP Application Default Credentials):

| Variable          | Default                          | Purpose                                              |
| ----------------- | -------------------------------- | ---------------------------------------------------- |
| `VERTEX_PROJECT`  | —                                | GCP project id (required).                          |
| `VERTEX_LOCATION` | `us-central1`                    | GCP region for the Vertex endpoint.                 |
| `VERTEX_MODEL`    | `claude-3-7-sonnet@20250219`     | Vertex model id (Anthropic-on-Vertex naming).       |

#### Other host knobs

| Variable                  | Purpose                                                                              |
| ------------------------- | ------------------------------------------------------------------------------------ |
| `FREEAGENT_CONFIG`        | Path to the permission-rules config (defaults to `.freeagent/config.json`).         |
| `FREE_CONTEXT_TOKENS`     | Override the model's context window for the pre-turn compactor.                     |
| `FREE_SESSION_ITERATIONS` | Cap iterations across a whole session (in addition to the per-turn `MaxIterations`).|
| `FREE_WATCH_FILES=1`      | Enable the workspace file watcher (off by default; inotify can be heavy on big repos).|

| Flag              | Purpose                                                          |
| ----------------- | --------------------------------------------------------------- |
| `--help`, `-h`    | Show usage and exit.                                             |
| `--version`       | Show the version and exit.                                      |
| `--verbose`, `-v` | Print streamed reasoning (dimmed) and a `[Tokens: in → out]` line. |
| `--resume [id]`   | Resume the session in `session.jsonl` (optionally requiring its id). |

At the prompt, slash commands handle host-side concerns (not sent to the model):
`/help`, `/status`, `/model`, `/plan [on|off]`, `/undo`, `/revert [N]`, `/tag <n>`, `/untag <n>`,
`/run <playbook>`, `/doctor`, `/serve {start|stop|status}`, `/fork`, `/commands [query]`.

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
never escapes the pipeline — it is mapped to a result class. **All twelve steps now
do real work** (the table below documents what each step does today; nothing in the
pipeline is a no-op).

| #  | Step             | Behavior                                                                                          |
| -- | ---------------- | ------------------------------------------------------------------------------------------------- |
| 1  | parse            | Parse `ArgumentsJson`; bad JSON → `InvalidInput` (never throws).                                  |
| 2  | schema-validate  | Resolve the tool; unknown tool → `InvalidInput`; validate args vs the tool's JSON Schema.         |
| 3  | sanity-check     | Path-escape / workspace-boundary checks.                                                          |
| 4  | plan-mode-guard  | If `PlanMode` is on, a non-read-only tool is `PlanModeBlocked` here, before any capability check. |
| 5  | permission       | `IPermissionEngine.Decide`; `Prompt` outcomes ask `IPermissionApprover` with session-grant memory.|
| 6  | cache-lookup     | `IToolResultCache`: a hit on a read-only tool returns the cached `Success` without re-executing.  |
| 7  | pre-hook         | `IHookRunner` fires matching `PreToolUse` hooks from `.freeagent/config.json`.                    |
| 8  | execute          | Run the tool; cancellation → `Cancelled`, exception → `Crash`.                                    |
| 9  | post-hook        | Matching `PostToolUse` hooks; non-fatal.                                                          |
| 10 | artifact-store   | `Success` content larger than the artifact threshold (10k chars) is offloaded to `IArtifactStore`; the result is replaced with a preview + opaque ref the model fetches via the `ReadArtifact` tool. |
| 11 | cache-write      | Read-only `Success` results land in `IToolResultCache`.                                           |
| 12 | invalidate       | A successful **mutating** tool invalidates the read-only cache.                                   |

A tool that succeeds but returns blank content is reported as `Empty`, so the model
gets a distinct signal rather than an ambiguous empty success. Every step appends to
the pipeline's `StepLog` under a lock, so traversal order is observable and tested.

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
7. **Any uncovered capability** → ask the interactive approver if one is configured (the host
   prompts `[once / session / always→config / deny]`); otherwise deny.

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

| Tool             | Args                                                                  | Read-only | Capability         | Notes                                                                                                                              |
| ---------------- | --------------------------------------------------------------------- | :-------: | ------------------ | ---------------------------------------------------------------------------------------------------------------------------------- |
| `ReadFile`       | `path`                                                                |    yes    | `FileReadCap`      | UTF-8 read; auto-allowed inside the workspace.                                                                                     |
| `WriteFile`      | `path`, `content`                                                     |    no     | `FileWriteCap`     | Creates parent dirs; never auto-allowed; protected prefixes blocked.                                                               |
| `EditFile`       | `path`, `old_string`, `new_string`, `replace_all?`                    |    no     | `FileWriteCap`     | In-place edit by literal substring; unique-match required (opt-in `replace_all`). Snapshots for `/undo`.                            |
| `MultiEditFile`  | `path`, `edits[]`                                                     |    no     | `FileWriteCap`     | Atomic batch of `EditFile` operations on the same file; rolls back if any edit fails.                                              |
| `ApplyPatch`     | `path`, `patch`                                                       |    no     | `FileWriteCap`     | Apply a unified diff; `ParseHunks` is public for tests.                                                                            |
| `ProcessExec`    | `command`, `args?`                                                    |    no     | `ProcessExecCap`   | Runs in the workspace; 30s timeout kills the process tree; returns exit code + stdout/stderr.                                      |
| `Glob`           | `pattern`, `path?`                                                    |    yes    | `FileReadCap`      | Find files by glob (`**/*.cs`); managed (no `rg`); skips noise dirs; capped.                                                       |
| `Grep`           | `pattern`, `path?`, `glob?`, `ignore_case?`                           |    yes    | `FileReadCap`      | Regex content search → `path:line:text`; skips binary files; capped.                                                               |
| `CSharpAnalysis` | `action`, `symbol?`, `path?`, `glob?`                                 |    yes    | `FileReadCap`      | Roslyn — syntactic (`list-types` / `list-members` / `diagnostics`) + semantic (`find-references` / `find-definition` / `semantic-diagnostics`). |
| `EnterPlanMode`  | —                                                                     |    yes    | none               | Turns plan mode on.                                                                                                                |
| `ExitPlanMode`   | —                                                                     |    yes    | none               | Turns plan mode off; read-only so it's always callable while plan mode is active.                                                  |
| `ReadMemory`     | `key`                                                                 |    yes    | `MemoryCap` read   | Read a cross-session memory entry (XDG memory dir). Auto-allowed.                                                                  |
| `WriteMemory`    | `key`, `content`                                                      |    no     | `MemoryCap` write  | Save/overwrite a memory entry. Not auto-allowed.                                                                                   |
| `ReadArtifact`   | `ref`                                                                 |    yes    | `FileReadCap`      | Fetch full text of an artifact offloaded by step 10.                                                                               |
| `SpawnAgent`     | `type`, `prompt`                                                      |    no     | `AgentSpawnCap`    | Run a sub-agent against a filtered tool registry; never auto-allowed.                                                              |

Optional, registered only when configured in `.freeagent/config.json`:

- **`mcp__{server}__{tool}`** per MCP tool from each entry under `mcp.servers[]` — capability is
  `ProcessExecCap("mcp:{server}", ...)`, so a whole server can be allow- or deny-ruled as a unit.
- **`lsp__{server}__{hover|definition|references|open}`** per LSP server under `lsp.servers[]` —
  capability is `ProcessExecCap("lsp:{server}", ...)`. Path-extension filter refuses files outside
  the server's declared extensions before sending the request.

`Glob`/`Grep`/`CSharpAnalysis` are read-only **and** concurrency-safe, so they run in the parallel
window. Paths are resolved against the working directory by the same rule the permission engine
uses, so the capability checked at step 5 and the path acted on at step 8 always agree.

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
FreeAgent.slnx                     Solution (Kernel, Kernel.Tests, Host, Server)
Directory.Build.props              Shared: net10.0, nullable, implicit usings, warnings-as-errors
global.json                        Pins the .NET 10 SDK

src/FreeAgent.Kernel/              The kernel library — all types live in one flat
                                   `FreeAgent.Kernel` namespace regardless of folder
  Messaging/      Message, MessageRole, ToolCall, ToolResult, ToolResultKind
  Providers/      IProvider, ProviderRequest, StreamChunk, ToolCallDelta, Usage,
                  StopReason, Model, ModelCatalog
    Adapters/     OpenAIProvider, AzureOpenAIProvider, AnthropicProvider, OllamaProvider,
                  BedrockProvider (AWSSDK), VertexProvider (Google ADC),
                  OpenAICompatStreaming (shared body/SSE for OpenAI-shape providers)
  Tools/          ITool, IToolRegistry, ToolRegistry, ToolPipeline, ToolDefinition,
                  ToolContext, Hooks, IToolResultCache + InMemoryToolResultCache,
                  IArtifactStore + InMemoryArtifactStore
    Adapters/     ReadFileTool, WriteFileTool, EditFileTool, MultiEditFileTool, ApplyPatchTool,
                  ProcessExecTool, GlobTool, GrepTool, CSharpAnalysisTool (Roslyn),
                  PlanModeTools, MemoryTools, ReadArtifactTool, SpawnAgentTool,
                  McpToolAdapter, WorkspacePath, WorkspaceSearch, RoslynSemanticHelpers
  Permissions/    IPermissionEngine, PermissionEngine, PermissionConfig, Capability,
                  PermissionDecision, IPermissionApprover
  Persistence/    IPersistenceStore, JsonlSessionStore, NoOpPersistenceStore,
                  IAtomicFileSystem, LinuxAtomicFileSystem
  Sessions/       SessionRuntime, SessionState, TurnExecutor, TurnResult, Compactor, FileHistory
  Runtime/        IEventSink, NullEventSink, DoomLoopDetector
  Agents/         AgentDefinition, AgentRegistry, SubAgentRunner
  Mcp/            IJsonRpcTransport, IMcpTransport, ILspTransport, JsonRpcClient, McpClient
  Lsp/            LspClient, LspToolAdapter
  Commands/       CommandRegistry, CommandDefinition
  Validation/     ToolInputSchemaValidator
  Serialization/  JsonOptions

src/FreeAgent.Host/                The interactive CLI
  Program.cs              Provider/tool/sub-agent wiring; REPL loop; resume
  HostOptions.cs          --verbose, --resume [id], --help, --version
  HostCommands.cs         All /foo slash commands + CommandRegistry default population
  ConsoleEventSink.cs     stdout streaming; reasoning + usage behind --verbose
  ConsoleApprover.cs      IPermissionApprover for the [once/session/always/deny] prompt
  ProviderConfig.cs       Per-provider env-var + JSON-config resolution
  Playbooks.cs            Markdown playbook loading + {{argN}} substitution
  SystemPrompt.cs         Base + working dir + git branch + project file layering
  BashShellExecutor.cs    IShellExecutor for hooks (bash -c, 30s timeout)
  StdioMcpTransport.cs    Newline-delimited stdio for MCP servers
  StdioLspTransport.cs    Content-Length-framed stdio for LSP servers
  McpServerManager.cs     Spawns mcp.servers[] at startup
  LspServerManager.cs     Spawns lsp.servers[] at startup
  ModelServerLauncher.cs  /serve start|stop|status for llama-server-style local servers
  WorkspaceFileWatcher.cs Opt-in (FREE_WATCH_FILES=1) external-change watcher

src/FreeAgent.Server/              HTTP + SSE protocol surface (ADR 0005)
  Program.cs              ASP.NET Core minimal API entrypoint + optional bearer auth
  SessionEndpoints.cs     POST /sessions, GET /sessions[/id], POST /sessions/{id}/turns (SSE), DELETE
                          — sessions get the full built-in tool set + system prompt + trust-aware perms
  ConfigEndpoints.cs      GET /providers, /models, /config; PUT /config/provider; POST
                          /config/provider/test; GET/PUT /config/permissions; GET/POST /config/trust
  ProviderProbe.cs        Best-effort live key/reachability check behind /config/provider/test
  SessionRegistry.cs      In-memory session map
  ProviderFactory.cs      Picks IProvider from the same ProviderConfig the CLI uses (reloadable)
  HttpSseEventSink.cs     IEventSink that streams events into an SSE response

clients/tui/                       Full-screen TUI (Bun + React + opentui) — the in-app UI + setup
  src/tui.tsx             Entry: boots/finds the server, then renders
  src/ui/Chat.tsx         Streaming chat, tool activity, status bar
  src/ui/Setup.tsx        In-app setup wizard (provider/key/model/working-dir) — no terminal config

src/FreeAgent.Kernel.Tests/        xUnit + FluentAssertions; fakes for every seam
                                   (550 pass)

docs/                              Architecture notes, ADRs, and the reimplementation spec
```

## Development

```bash
dotnet build FreeAgent.slnx        # warnings are errors
dotnet test  FreeAgent.slnx        # 550 pass + 0 skip
dotnet run --project src/FreeAgent.Host -- --verbose      # interactive CLI
dotnet run --project src/FreeAgent.Server                 # HTTP + SSE protocol on :5000
```

No `dotnet` on PATH (e.g. a fresh Claude Code web sandbox, where the public SDK installers are
network-blocked)? Run `./scripts/setup-sdk.sh` once to install the .NET 10 SDK from the Ubuntu
archive, then build/test as above.

The build treats warnings as errors and enforces code style, so a clean build is a
real signal. Tests are written against fakes (`FakeProvider`, `FakeTool`,
`InMemorySessionStore`, `RecordingPermissionEngine`, …) and need no network, model,
or real filesystem; the protocol server is tested with `WebApplicationFactory` in
process. See `docs/architecture.md` for a deeper tour and `docs/usage.md` for host
details and recipes.

**Releasing.** A strict `vMAJOR.MINOR.PATCH` tag triggers `.github/workflows/release.yml`. The
workflow requires the tag, project version, and a dated changelog section to agree; runs the .NET
and TUI gates; packs and smoke-installs the tool; publishes self-contained binaries; and attaches
SHA-256 checksums. If `NUGET_API_KEY` is configured it also publishes to NuGet; otherwise that step
is explicitly skipped. Preparing or merging a candidate does not publish anything. (NuGet IDs are
global; if `FreeAgent` is taken, change `PackageId` in
`src/FreeAgent.Host/FreeAgent.Host.csproj`.)

## Design decisions

The reasoning behind the shape of the project lives in `docs/decisions/`:

- [0001 — Product direction](docs/decisions/0001-product-direction.md)
- [0002 — Kernel-first implementation strategy](docs/decisions/0002-kernel-first.md)
- [0003 — Linux-native first](docs/decisions/0003-linux-native-first.md)
- [0004 — Extension-first capabilities](docs/decisions/0004-extension-first-capabilities.md)
- [0005 — Headless core + protocol, with pluggable frontends](docs/decisions/0005-headless-core-protocol.md)

The full architecture tour is in [`docs/architecture.md`](docs/architecture.md).

## Roadmap & non-goals

See [`ROADMAP.md`](ROADMAP.md) for the full backlog and what's shipped, and
[`CHANGELOG.md`](CHANGELOG.md) for a feature-by-feature log. The "near-term" and
"larger features" sections of the roadmap are now essentially complete; the
remaining unchecked items are primarily deeper editor and remote integrations
(ACP, web, Slack/GitHub) that are clients of the protocol surface rather than
additions to the kernel.

**Now shipped (since the original "out of scope" list above was written):**
sub-agents, playbooks, **MCP client**, **LSP client** (both smoke tests now
run cleanly — `JsonRpcClient` buffers responses for ids whose `CallAsync`
hasn't finished registering yet), native **Anthropic / Azure OpenAI /
Ollama / AWS Bedrock / Google Vertex** providers, **Roslyn syntactic +
semantic** analysis (`CSharpAnalysis` tool — `find-references` /
`find-definition` / `semantic-diagnostics` over a real
`CSharpCompilation`, with **`.csproj`-aware references** pulled from each
project's `obj/project.assets.json` after `dotnet restore`), **local
model server lifecycle** (`/serve start|stop|status`) plus
**GGUF download + catalog** (`/serve download hf:owner/repo/path.gguf` /
`/serve models`), opt-in workspace file watching, session forking
(`/fork`), Anthropic **extended thinking** (`FREE_THINKING_BUDGET`),
**`StopReason` + `Model` + `ModelCatalog`** provider-model scaffolding,
the **`FreeAgent.Server` HTTP + SSE protocol surface** (per ADR 0005,
with an OpenAPI spec served at `/openapi/v1.json` and an optional
bearer-token gate), a **`CommandRegistry` + `/commands`** palette layer, and the
full-screen **Bun/React/opentui TUI** with in-app setup, streaming chat, settings,
and command handling.

Still **deliberately deferred:** production-grade editor and remote integrations
(the VS Code client remains a scaffold; ACP, web, Slack, and GitHub apps are not
implemented), first-class multimodal tools, and a Windows-shaped backend for
`/serve`'s pid-file model.

## Security

Please report suspected vulnerabilities privately as described in [SECURITY.md](SECURITY.md).
FreeAgent executes user-authorized tools, hooks, and protocol integrations, so reports should
identify behavior outside the documented permission or trust boundary.

## License and acknowledgments

FreeAgent is licensed under the [Apache License, Version 2.0](LICENSE). See
[NOTICE](NOTICE) for acknowledgments. Contributions are welcome under the same
terms; see [CONTRIBUTING.md](CONTRIBUTING.md).
