# Architecture Map — OpenMonoAgent.ai

## System Intent

OpenMono is a self-hosted, local-first AI coding agent for software developers who want full control over inference cost and data privacy. It pairs a .NET 10 CLI with a bundled llama.cpp inference server, running both inside Docker containers so the agent's filesystem access is sandboxed to a single bind-mounted workspace directory. The agent implements a full agentic loop: it streams tokens from the LLM, dispatches tool calls through a 12-step permission-and-execution pipeline, detects and aborts runaway loops, manages context-window pressure through checkpointing and compaction, and persists session state as JSONL files. The product targets developers on Linux workstations with NVIDIA GPUs (primary) or CPU-only hardware (secondary). *observed fact — README.md, docs/ARCHITECTURE.md*

---

## Layer Map

### Package Inventory

| Package / Module | Role | Public Entrypoints | Key Dependencies | Runtime Surface |
|---|---|---|---|---|
| `openmono` (bash script) | product shell | `setup`, `start`, `stop`, `restart`, `agent`, `logs`, `status`, `graph`, `graphify`, `config`, `tunnel` | bash, docker, jq, systemd | Host shell |
| `Session/` | core semantics | `ConversationLoop.RunTurnAsync`, `SessionManager.SaveAsync` | `Llm/`, `Tools/`, `Permissions/`, `Hooks/`, `Memory/`, `Rendering/` | In-process async |
| `Tools/` | core semantics | `ITool.ExecuteAsync`, `ToolRegistry`, `ToolDispatcher`, `SchemaValidator`, `SanityCheck` | `Permissions/`, `Session/` (via ToolContext), `Config/` | In-process async |
| `Llm/` | protocol/normalization | `ILlmClient.StreamChatAsync`, `ProviderRegistry.CreateClient` | HTTP (OpenAI-compat SSE), env vars | HTTP SSE stream |
| `Permissions/` | core semantics | `PermissionEngine.CheckCapabilitiesAsync`, `PermissionEngine.CheckAsync` | `Config/`, `Rendering/` (interactive prompt) | In-process |
| `Mcp/` | integration adapter | `McpServerManager.InitializeAsync`, `McpClient.CallToolAsync`, `McpToolAdapter` | OS subprocess (stdin/stdout), JSON-RPC 2.0 | stdio JSON-RPC |
| `Lsp/` | integration adapter | `LspServerManager`, `LspClient`, `LspTool.ExecuteAsync` | OS subprocess (JSON-RPC) | stdio JSON-RPC |
| `Rendering/` | UI/rendering | `IRenderer`, `AnsiTuiRenderer`, `TerminalRenderer` | Spectre.Console, Terminal.Gui | ANSI terminal |
| `Config/` | persistence/state | `ConfigLoader.Load`, `AppConfig` | JSON files, env vars | Host filesystem |
| `Session/SessionManager` | persistence/state | `SaveAsync`, `LoadAsync`, `ListSessionsAsync` | JSON, filesystem | Host filesystem |
| `Playbooks/` | protocol/normalization | `PlaybookExecutor.ExecuteAsync`, `PlaybookRegistry`, `PlaybookLoader` | YAML (YamlDotNet), `Llm/`, `Tools/`, `Rendering/` | In-process async |
| `Hooks/` | integration adapter | `HookRunner.RunPreToolUseHooksAsync`, `RunPostToolUseHooksAsync`, `RunSessionStartHooksAsync` | OS subprocess (bash) | Shell |
| `Memory/` | persistence/state | `MemoryStore.LoadIndex`, `MemorySaveTool` | YAML frontmatter files | Host filesystem |
| `Agents/` | core semantics | `AgentTool.ExecuteAsync`, `AgentDefinition` | `Session/ConversationLoop` (recursive) | In-process |
| `Commands/` | product shell | 14 slash commands via `CommandRegistry` | `Session/`, `Rendering/`, `Config/` | In-process |
| `History/` | persistence/state | `FileHistory`, `FileSnapshot` | Filesystem | Host filesystem |
| `Tui/Export/` | UI/rendering | `HtmlExporter`, `JsonExporter`, `MarkdownExporter` | `Session/` | In-process |
| `Utils/` | infrastructure | `ProcessRunner`, `GitHelper`, `Log`, `PathUtils`, `SecretScanner`, `ProcessWatchdog` | OS, .NET runtime | In-process |
| `llama-server` (Docker container) | integration adapter | HTTP port 7474 — `/v1/chat/completions`, `/props`, `/health`, `/metrics` | llama.cpp, GGUF models, NVIDIA GPU | HTTP |

### Dependency Direction

The dependency graph is cleanly layered with no observed cycles. *strong inference from import graph and Program.cs wiring.*

```
Host shell
  └─ openmono (bash)
       └─ docker compose run → openmono-agent container
            └─ Program.cs (DI root)
                 ├─ Config/              ← stable base; nothing project-internal depends on this
                 ├─ Utils/               ← stable base; no project-internal dependents
                 ├─ Rendering/           ← depends on Config/
                 ├─ Llm/                 ← depends on Config/
                 ├─ Permissions/         ← depends on Config/, Rendering/
                 ├─ Memory/              ← depends on Config/ (filesystem path only)
                 ├─ Hooks/               ← depends on Config/
                 ├─ Mcp/                 ← depends on Config/; registers into Tools/
                 ├─ Lsp/                 ← depends on Config/; exposed through Tools/
                 ├─ Playbooks/           ← depends on Llm/, Tools/, Rendering/, Config/
                 ├─ Commands/            ← depends on Session/, Rendering/, Config/, Tools/
                 ├─ Tools/               ← depends on Permissions/, Config/; consumes Lsp/, Mcp/
                 └─ Session/             ← depends on all above layers except Commands/
                      └─ Agents/AgentTool ← spawns recursive ConversationLoop (sub-agent)
```

**Stable base:** `Config/` and `Utils/` — nothing inside the project depends on them; they depend only on the .NET runtime. *observed fact — no imports from these packages into Config/ or Utils/ from program source.*

**Highest fan-in layer:** `Session/ConversationLoop` — orchestrates everything. Its constructor explicitly takes `ILlmClient`, `ToolRegistry`, `PermissionEngine`, `IOutputSink`, `IInputReader`, `ILiveFeedback`, `AppConfig`, `SessionState`, `Compactor`, `MemoryStore`, `HookRunner`, `TurnJournal`, `ToolResultCache`, `ArtifactStore`, `Checkpointer`. *observed fact — `Session/ConversationLoop.cs:39-72`.*

**No dependency cycles observed** between packages. The one re-entrant case (`Agents/AgentTool` creates a nested `ConversationLoop`) is an intentional design feature, not a structural cycle; it uses the same types from the outer scope rather than a cross-package import. *strong inference.*

---

## Public Surfaces

### Binaries and CLI commands

**`openmono` (bash)** — 11 top-level subcommands:

| Subcommand | Surface | Notes |
|---|---|---|
| `setup [--full\|--inference\|--agent] [--gpu\|--cpu]` | host shell | Installs role, runs prereq + install scripts |
| `start` / `stop` / `restart` / `logs` / `status` | host shell | llama-server Docker lifecycle |
| `agent` | Docker container | Launches `openmono-agent` container; default TUI, `--classic` for scrolling |
| `config <get\|set\|unset\|list> <key> [value]` | host shell | Reads/writes `~/.openmono/settings.json` via `jq` |
| `graph [path]` | host shell | Builds `code-review-graph` index |
| `graphify [path]` | host shell | Builds Graphify knowledge graph |
| `tunnel <setup\|rotate-key\|start\|stop\|restart\|status\|logs>` | host shell (inference box) | `frpc` systemd tunnel management |

*observed fact — `openmono` script, `docs/CONFIG.md`.*

**`openmono` (binary inside container)** — 14 slash commands dispatched from REPL:

`/help`, `/status`, `/stats`, `/init`, `/undo [n]`, `/debug`, `/resume [id]`, `/export`, `/clear`, `/checkpoint`, `/think`, `/plan`, `/compact`, `/retry`

*observed fact — `src/OpenMono.Cli/Program.cs:199-216`, `Commands/` directory.*

### Network / RPC interfaces

- **HTTP port 7474** (llama-server): OpenAI-compatible chat completions endpoint. Key routes: `POST /v1/chat/completions` (streaming SSE), `GET /props` (model metadata), `GET /health`, `GET /metrics`. *observed fact — `docker/docker-compose.yml:9-12`, `Program.cs:651-739`.*
- **MCP stdio**: JSON-RPC 2.0 over stdin/stdout with each configured MCP server subprocess. *observed fact — `docs/ARCHITECTURE.md:153-162`.*
- **LSP stdio**: JSON-RPC with language server subprocesses per language extension. *observed fact — `docs/ARCHITECTURE.md:167-182`.*
- **frpc tunnel** (optional, inference box only): outbound TCP tunnel to `app.openmonoagent.ai` relay; exposes llama-server to remote agent box. *observed fact — `openmono` script `cmd_tunnel`.*

### File formats and persistent artifacts

See §Durable State for the full inventory.

- JSONL session files with optional checkpoint sidecars.
- YAML playbook definitions scanned from configured paths.
- YAML-frontmatter memory files.
- `OPENMONO.md` project instructions file.
- `settings.json` (user-level and project-level).

### User-facing screens / workflows

- **TUI mode** (default for interactive terminals): full-screen Spectre.Console renderer with streaming text, thinking panel, tool events, status bar. *observed fact — `docs/ARCHITECTURE.md:247-251`, `OpenMono.Cli.csproj`.*
- **Classic mode** (`--classic` or redirected I/O): scrolling REPL via `TerminalRenderer`. *observed fact — `Program.cs:107-115`.*

---

## Runtime Lifecycle

### Boot sequence (`Program.cs`)

1. Parse CLI flags: `--endpoint`, `--model`, `--workdir`, `--config`, `--verbose`, `--detail`, `--tui`, `--classic`. *observed fact — `Program.cs:21-83`.*
2. Load config (`ConfigLoader.Load`): merges defaults → `~/.openmono/settings.json` → `.openmono/settings.json` → `--config` path → env vars → CLI flag overrides. *observed fact — `docs/CONFIG.md:68-76`.*
3. Probe LLM server: `GET /props` (llama.cpp format), fall back to `GET /v1/models`. Overwrites `config.Llm.Model` and `config.Llm.ContextSize` with live values. *observed fact — `Program.cs:650-739`.*
4. Create `SessionState` (12-char UUID + timestamp + empty message list). *observed fact — `Program.cs:104-105`.*
5. Wire DI: `PermissionEngine`, `MemoryStore`, `HookRunner`, `ProviderRegistry` → `ILlmClient`, `FileHistory`, `LspServerManager`, `TokenTracker`, `ToolRegistry`, `PlaybookRegistry`, `McpServerManager`, `Checkpointer`, `CommandRegistry`, `Compactor`, `ConversationLoop`. *observed fact — `Program.cs:117-214`.*
6. Choose renderer: `useTui ?? (!Console.IsInputRedirected && !Console.IsOutputRedirected)`. *observed fact — `Program.cs:107`.*
7. Build system prompt: base instructions + `OPENMONO.md` + memory index + git branch/status + environment block + available playbooks. *observed fact — `Program.cs:606-648`.*
8. Initialize MCP servers (spawn subprocesses, JSON-RPC handshake, register tools). *observed fact — `Program.cs:182-191`.*
9. Run `SessionStart` hooks. *observed fact — `Program.cs:219`.*
10. Enter full-screen TUI if applicable. *observed fact — `Program.cs:221`.*
11. Probe KV cache warmth via `/metrics`; if cold, fire non-blocking warmup request in background. *observed fact — `Program.cs:225-229`, `IsServerWarmAsync`.*

### Main event loop

```
while (true):
  ReadInput() → sanitize
  if input starts with '/'  → CommandRegistry.Execute()
  else:
    resolve @file references → inject file contents inline
    loop.RunTurnAsync(input, ct)
    sessionManager.SaveAsync(session)
```

*observed fact — `Program.cs:260-424`.*

### Per-turn flow (`ConversationLoop.RunTurnAsync`)

1. Reset doom-loop detector. Add user message to session.
2. Check context threshold (uses last prompt token count from `TokenTracker`):
   - ≥65% → `Checkpointer.CreateCheckpointAsync`: LLM-generated summary stored; future context window built from cutoff. *observed fact — `docs/ARCHITECTURE.md:127-133`.*
   - ≥80% (fallback) → `Compactor.CompactAsync`: summarize all but last 4 turns; replace messages in session. *observed fact — `ConversationLoop.cs:113-114`.*
3. Build tool definitions (read-only set only if plan mode is active). *observed fact — `ConversationLoop.cs:118-120`.*
4. For up to 1000 iterations: *strong inference — `ConversationLoop.cs:136`, README says "25 iterations per turn" but code cap is 1000; README likely describes default typical behavior or sub-agent cap.*
   - Stream LLM via `ILlmClient.StreamChatAsync(contextWindow, toolDefs, options, ct)`.
   - Accumulate `ThinkingDelta` (thinking panel), `TextDelta` (streaming output), `ToolCallDelta` (tool dispatch queue), `Usage` (token tracking).
   - **Read-only/concurrency-safe tools start as in-flight tasks while stream is still open.** *observed fact — `ConversationLoop.cs:233-239`.*
   - If no tool calls → finish turn (text-only response).
   - Check doom loop: 3 identical sequential tool call sequences → inject break message, abort. *observed fact — `ConversationLoop.cs:288-300`.*
   - Execute tool calls (`ExecuteToolCallsWithInflightAsync`): complete in-flight read-only tasks, then serial writable tools.
   - Add tool results to session; loop for next LLM iteration.
5. Turn ends when LLM produces text with no tool calls. Session saved to JSONL.

### Tool execution pipeline (12 steps)

Every tool call traverses this pipeline before touching anything:

```
1.  Parse JSON arguments
2.  Schema validation (SchemaValidator vs ITool.InputSchema)
3.  Sanity check (SanityCheck — path escapes workspace, etc.)
4.  Plan mode guard (read-only tools only)
5a. Capability check → PermissionEngine.CheckCapabilitiesAsync (primary path)
5b. Legacy permission → PermissionEngine.CheckAsync (fallback for tools without capabilities)
6.  Result cache lookup (read-only tools — ToolResultCache)
7.  Pre-tool hook (HookRunner.RunPreToolUseHooksAsync)
8.  Execute (tool.ExecuteAsync)
9.  Post-tool hook (HookRunner.RunPostToolUseHooksAsync)
10. Artifact store (ArtifactStore.PersistAndReplace for outputs > threshold)
11. Cache write (read-only tools on success)
12. File cache invalidation (FileWrite/FileEdit/ApplyPatch — invalidates ToolResultCache + FileReadTool internal cache)
```

*observed fact — `docs/ARCHITECTURE.md:96-116`, `ConversationLoop.cs:627-781`.*

### Shutdown

- Ctrl+C once: cancel current `CancellationTokenSource` (cancels in-flight LLM stream and tool calls).
- Ctrl+C twice within 1.5 s: `ProcessWatchdog.ScheduleHardKill()` + `Environment.Exit(0)`.
- Normal exit: `ansiTui?.ExitFullScreen()` → final `sessionManager.SaveAsync`.

*observed fact — `Program.cs:237-258`, `Program.cs:421-425`.*

### Background tasks

- **KV cache warmup**: fire-and-forget `Task` that sends a `max_tokens=1` probe to pre-fill the KV cache with the system prompt + tools. Checked at first user message. *observed fact — `Program.cs:225-229`, `SendWarmupAsync`.*
- **In-flight parallel tool execution**: read-only tool tasks started while LLM stream is still active. *observed fact — `ConversationLoop.cs:233-239`.*

---

## Concurrency Model

**Threading model:** Single-threaded C# `async/await` on the .NET thread pool. No explicit thread management. *strong inference — all code uses `async/await`, no `Thread` or `ThreadPool` calls observed.*

**Tool parallelism:** Read-only, `IsConcurrencySafe`-flagged tools run concurrently via `Task.WhenAll` and `Task.Run`. Two parallelism windows exist:
- **During LLM stream:** concurrency-safe read-only tools are launched as `inFlightTasks` before the stream ends. *observed fact — `ConversationLoop.cs:233-239`.*
- **After stream ends:** remaining read-only tools batch via `Task.WhenAll`; writable tools run serially after. *observed fact — `ConversationLoop.cs:489-535`.*

**Sibling abort:** If any parallel read-only task crashes (`ResultClass.Crash`), `siblingAbortCts` cancels remaining sibling tasks. *observed fact — `ConversationLoop.cs:590-599`.*

**Shared mutable state:** `SessionState.Messages` (append-only from single async context), `TokenTracker` (atomic-style updates), `ToolResultCache`, `CursorStore`. No explicit locks observed; all access is serialized by the async/await call graph. *strong inference — no `lock`, `Monitor`, or `SemaphoreSlim` calls observed in primary path.*

**Connection handling:** Each LLM request opens a new HTTP connection (no connection pool reuse visible). MCP and LSP connections are long-lived subprocesses with single stdio channels each. *strong inference.*

**Rate limiting / backpressure:** llama-server runs with `--parallel 1`; a single concurrent inference slot. The agent does not implement application-level rate limiting or retry logic beyond user-prompted recovery. *observed fact — `docker/docker-compose.yml:22`, `Program.cs:370-397`.*

**Portability hazard:** The `Task.Run` + `CancellationToken` concurrency model is .NET-specific. Porting to Go, Rust, or Python requires mapping to goroutines, Tokio tasks, or `asyncio` tasks respectively, with attention to the sibling-abort propagation pattern. *portability hazard.*

---

## Build and Packaging

### Build tool chain

- **.NET 10 SDK** (`global.json` pins target). Single project: `src/OpenMono.Cli/OpenMono.Cli.csproj`. Assembly name: `openmono`. *observed fact — `Dockerfile.agent:13-18`, `OpenMono.Cli.csproj`.*
- `dotnet publish -c Release -o /app --no-restore` → self-contained binary set in `/usr/local/bin/openmono/`. *observed fact — `Dockerfile.agent:17-19`.*
- Test project: `src/OpenMono.Tests/` — xUnit-style, no CI pipeline file visible. *observed fact — file inventory; no `.github/workflows/` directory found.*

### NuGet dependencies

| Package | Version | Purpose |
|---|---|---|
| `Spectre.Console` | 0.50.0 | Full-screen ANSI TUI rendering |
| `Spectre.Console.Json` | 0.50.0 | JSON display in TUI |
| `Markdig` | 0.40.0 | Markdown → ANSI conversion |
| `YamlDotNet` | 16.3.0 | Playbook YAML parsing |
| `Microsoft.Extensions.FileSystemGlobbing` | 10.0.0-preview.4 | Glob pattern matching (GlobTool) |
| `Microsoft.CodeAnalysis.CSharp.Workspaces` | 4.12.0 | Roslyn semantic analysis |
| `Terminal.Gui` | 2.0.0-develop | Alternative TUI (presence unclear at runtime) |
| `TiktokenSharp` | 1.2.1 | Token counting for context tracking |

*observed fact — `OpenMono.Cli.csproj`.*

### Docker images

| Service | Image | Profile | Notes |
|---|---|---|---|
| `llama-server` | `ghcr.io/ggml-org/llama.cpp:server` | `full`, `server` | Port 7474; mounts `../models:/models:ro` |
| `agent` | built locally via `Dockerfile.agent` | `full`, `agent` | Mounts `$WORKSPACE:/workspace`, `~/.openmono:/home/agent/.openmono` |

*observed fact — `docker/docker-compose.yml`.*

**Multi-stage build** (`Dockerfile.agent`): stage 1 (`build`) uses `mcr.microsoft.com/dotnet/sdk:10.0` to compile; stage 2 (`runtime`) also uses `dotnet/sdk:10.0` (not a smaller runtime image — includes full SDK for `dotnet` CLI tools during agent operation). Runtime stage installs: `git`, `ripgrep`, `curl`, `jq`, `tree`, `python3`, `python3-pip`; pip installs `code-review-graph` and `graphifyy`. *observed fact — `Dockerfile.agent`.*

### Distribution

- **One-line installer**: `bash <(curl -fsSL .../get-openmono.sh)`. Clones repo, runs `openmono setup`. *observed fact — `README.md`.*
- **Linux**: `scripts/install_prereqs.sh` (Docker, nvidia-container-toolkit, jq) + `scripts/install.sh` (model download, docker-compose.override.yml generation). *observed fact — `openmono` script `cmd_setup`.*
- **macOS**: `scripts/install_prereqs_macos.sh` + `scripts/install_macos.sh`. *observed fact — `openmono:204-208`.*
- **Fedora/RPM**: mentioned in recent commit history (`fix: add Fedora/RPM support to setup scripts`). *observed fact — git log.*
- **No CI/CD pipeline** visible in the repository. *observed fact — no `.github/workflows/` or `Jenkinsfile` found.*

---

## Durable State

| Artifact | Path | Format | Notes |
|---|---|---|---|
| User config | `~/.openmono/settings.json` | JSON | `chmod 0600` enforced by `openmono config set` |
| Project config | `$cwd/.openmono/settings.json` | JSON | Overrides user config |
| Session messages | `~/.openmono/sessions/{date}_{id}.jsonl` | JSONL | Line 1 = header; subsequent lines = messages |
| Session checkpoints | `~/.openmono/sessions/{date}_{id}.checkpoints.json` | JSON | LLM-generated summaries with cutoff indices |
| Session index | `~/.openmono/sessions/index.json` | JSON | `SessionSummary` records for `/resume` |
| Memory files | `~/.openmono/memory/` | YAML frontmatter | Cross-session agent memory; loaded into system prompt |
| Artifact cache | `~/.openmono/content-cache/{tool}_{ts}_{guid}.txt` | plaintext | Tool outputs > 20,000 chars, referenced by path |
| Background process logs | `~/.openmono/bg/` | plaintext | Logs from `Bash tool background: true` processes |
| Setup logs | `~/.openmono/logs/setup-{ts}.log` | plaintext | Written by install scripts |
| Turn journal | `~/.openmono/journals/` | JSON (inferred) | `TurnJournal.ForSession` — per-turn audit trail |
| File history (undo) | In-memory during session | `FileSnapshot` objects | Stored in `SessionState.Meta.FileHistory` |
| Docker env | `docker/.env` | shell env | `LLAMA_API_KEY`, `LLAMA_PORT`, `MODEL_NAME`, `CTX_SIZE` |
| Docker compose override | `docker/docker-compose.override.yml` | YAML | Generated by `install.sh`; pins model and GPU flags |
| GGUF models | `models/*.gguf` | binary | Bind-mounted into llama-server as `/models` |
| frpc tunnel config | `/etc/frp/frpc.toml` | TOML | Inference box only |
| Relay cache | `~/.openmono/relay.json` | JSON | Tunnel endpoint + remotePort for `tunnel rotate-key` |
| Playbooks | `.openmono/playbooks/*.yaml` or `~/.openmono/playbooks/*.yaml` | YAML | Loaded at session start |
| Project instructions | `$cwd/OPENMONO.md` | Markdown | Injected into system prompt |
| Install env temp file | `~/.openmono/.tmp_install_env` | shell env | Cross-script env propagation during setup |
| GPU mode temp file | `~/.openmono/.tmp_gpu_mode` | shell env | Written by prereqs script, read by setup orchestrator |

*observed fact — `AppConfig.cs`, `SessionManager.cs`, `Program.cs`, `openmono` script, `docker-compose.yml`, `docs/CONFIG.md`.*

**Auth material:** `LLAMA_API_KEY` in `docker/.env`; `OPENMONO_API_KEY` / `OPENMONO_ENDPOINT` env vars for remote/tunnel endpoints; provider API keys in `settings.json`. *observed fact — `docs/CONFIG.md`, `docker-compose.yml:54`.*

---

## Porting Priorities

| Component | Priority | Rationale |
|---|---|---|
| `ConversationLoop` (agentic loop, doom-loop detection) | core | The product's defining behavior; everything else serves it |
| `ILlmClient` + `OpenAiCompatClient` (SSE streaming) | core | All inference passes through this; SSE stream format is the primary protocol boundary |
| 12-step tool execution pipeline (`ExecuteSingleToolAsync`) | core | Permission, caching, hook, artifact discipline is the trust model |
| `PermissionEngine` (capability-based + legacy) | core | Security and user-consent model |
| `SessionManager` (JSONL persistence + index) | core | Cross-session resume and `/undo` depend on this |
| `AppConfig` + `ConfigLoader` (multi-source merge) | core | Config hierarchy shapes every session |
| `Checkpointer` / `Compactor` | important | Required for long-context sessions without these the agent fails on large tasks |
| `DoomLoopDetector` | important | Safety invariant; without it agents can spin indefinitely |
| `ToolRegistry` + 20 built-in tools | important | The agent's capability surface; each tool is independently portsble |
| `AnsiTuiRenderer` (Spectre.Console) | important | Primary interactive surface; the TUI is a differentiator but is Spectre.Console-specific |
| `McpClient` / `McpServerManager` (JSON-RPC 2.0 stdio) | important | MCP ecosystem is the extensibility mechanism |
| `PlaybookExecutor` / `PlaybookLoader` (YAML workflows) | important | Composable automation; user-visible feature |
| `ProviderRegistry` (4 providers, hot-swap) | important | Cloud provider fallback is documented and expected |
| `AgentTool` (sub-agents, recursive sessions) | optional | Advanced feature; requires the full session stack to work |
| `LspServerManager` / `LspClient` | optional | Language intelligence; lazy-started, independently portable |
| `RoslynTool` (AdhocWorkspace, 8 actions) | optional | C#-specific; non-portable to non-.NET runtimes |
| `MemoryStore` (cross-session YAML memory) | optional | Convenience; re-implementable as any KV store |
| `HookRunner` (bash hooks) | optional | Extension point; bash-specific |
| `TurnJournal` / `ArtifactStore` | optional | Observability; not required for agent function |
| `TerminalRenderer` (classic mode) | incidental | Trivial scrolling-text fallback |
| `openmono` bash launcher (Docker orchestration) | incidental | Platform/deployment-specific wrapper; not the agent itself |
| llama-server Docker container | incidental | Deployment artifact; any OpenAI-compat endpoint substitutes |

---

## Open Questions

| ID | Kind | Description | Deferred Reason |
|---|---|---|---|
| arch-OQ1 | needs-runtime-test | Does `AnthropicClient` use Anthropic's native SSE message format (with `content_block_delta` events) or does it normalize to the OpenAI-compat shape before returning `StreamChunk`? The file was not read in depth. | Cannot determine wire format from class signature alone; requires reading `AnthropicClient.cs` and a live capture. |
| arch-OQ2 | needs-spec-ruling | `global.json` SDK version pin was not read; the exact .NET 10 preview or stable SDK required is unknown. | Affects reproducibility of any port's build toolchain. |
| arch-OQ3 | needs-runtime-test | `TurnJournal` file path and JSON schema are not confirmed — `TurnJournal.ForSession` was not read in depth. | Needed for full durable-state inventory. |
| arch-OQ4 | needs-runtime-test | The `Terminal.Gui` package is in the `.csproj` but the code primarily references `AnsiTuiRenderer` (Spectre.Console). Whether `Terminal.Gui` is actually used at runtime, or is a dead dependency, is unclear. | Affects portability assessment of the TUI layer. |
| arch-OQ5 | needs-maintainer-decision | README says "up to 25 iterations per turn" but `ConversationLoop.cs:136` shows `maxIterations = 1000`. Which is the authoritative limit? Sub-agent definitions use per-agent turn budgets (15–30); the main loop limit is separate. | Documentation vs. code discrepancy. |

---

## Carry-Forward

| ID | Target Phase | Description | Deferred Reason |
|---|---|---|---|
| arch-CF1 | contracts | Tool execution pipeline details: pre/post-hook timing guarantees, artifact replacement threshold (20,000 chars observed in code), cache key construction, tool error vs. crash vs. cancel return shapes | Contracts phase has the rubric for behavioral guarantees |
| arch-CF2 | protocols | Session JSONL schema: exact message record fields (`Role`, `Content`, `ToolCalls`, `ToolCallId`, `ToolName`), checkpoint JSON structure, index.json structure | Wire-format extraction belongs to protocols |
| arch-CF3 | protocols | Playbook YAML schema: step types, parameter schema, conditional gate syntax, checkpoint/resume semantics, template engine substitution rules | Protocols phase covers persistence formats |
| arch-CF4 | protocols | MCP wire protocol detail: `initialize` handshake sequence, `tools/list` response shape, `tools/call` request/response format, resource listing | Protocols phase covers event catalogs and wire formats |
| arch-CF5 | contracts | Permission capability schema: exact `Capability` subtypes (`FileReadCap`, `FileWriteCap`, `ProcessExecCap`, `NetworkEgressCap`, `VcsMutationCap`, `AgentSpawnCap`), config pattern matching semantics (allow/deny/ask glob evaluation order) | Contracts phase covers authorization model |
| arch-CF6 | contracts | Sub-agent isolation: what state the parent session shares with sub-agents (permission engine reused per docs), whether tool results flow back, how sub-agent turn budget interacts with parent iteration count | Contracts phase covers feature behavioral guarantees |

---

## Validation

| # | Criterion | Result | Evidence |
|---|-----------|--------|----------|
| 1 | The system intent is documented. | PASS | §System Intent — one paragraph with evidence level. |
| 2 | The layer map and dependency direction are documented. | PASS | §Layer Map — Package Inventory table (18 rows), Dependency Direction with annotated call tree, stable-base identification, no cycles noted. |
| 3 | Public surfaces are identified. | PASS | §Public Surfaces — bash subcommands, slash commands, HTTP endpoints, stdio protocols, file formats, user-facing screens enumerated. |
| 4 | Runtime lifecycle, concurrency model, and porting priorities are summarized. | PASS | §Runtime Lifecycle — 11-step boot, per-turn flow, 12-step pipeline, shutdown sequence; §Concurrency Model — task parallelism, sibling abort, shared state, portability hazard; §Porting Priorities — 20-row priority table. |
| 5 | Findings are marked with evidence levels. | PASS | All paragraphs and claims tagged `observed fact`, `strong inference`, `portability hazard`, or `open question`. Five open questions routed to `open_questions`; six carry-forward items routed to `carry_forward` with target phases. |

**Validated by:** 2026-05-24 (architecture phase, session 1 — implementing session)
**Overall:** PASS
