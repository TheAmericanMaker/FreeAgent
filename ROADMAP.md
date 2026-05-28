# Roadmap

Backlog for FreeAgent. Nothing here is committed or scheduled — it's the shape of
where the kernel can grow. Items are grouped by rough horizon, not by priority within
a horizon. Several are seeded from the [OpenMonoAgent.ai](https://github.com/StartupHakk/OpenMonoAgent.ai)
feature set (FreeAgent's lineage) and translated into FreeAgent's kernel terms.

The kernel was built to anticipate most of this. The tool pipeline has labelled no-op
*seams* (`cache-lookup`, `pre-hook`, `post-hook`, `artifact-store`, `cache-write`,
`invalidate`) and the permission engine already models capabilities
(`NetworkEgressCap`, `VcsMutationCap`, `AgentSpawnCap`, `MemoryCap`) that no tool
exercises yet. So many items below are "fill a seam / add an adapter," not "change
the core."

## Near-term — wire up what the kernel already models

Mostly small, because the design already has the hook. Generally more pressing than
the larger ported features further down. (The first daily-driver batch is now done —
see below.)

- [x] **Interactive permission approval** — today an uncovered capability is denied with
  no way to approve it live, so the only approval channel is hand-writing
  `.freeagent/config.json` (and the model tends to hallucinate an approval UI that isn't
  there). Intercept the engine's "needs approval" denial in the host and prompt
  `[allow once / session / always→write a rule / deny]`; carry session grants in
  `SessionState`; tighten the denial text. The kernel already returns a clean,
  distinct denial for exactly this. **Highest-impact near-term item — it currently blocks
  real edits.**
- [x] **Minimal system prompt (user-editable)** — FreeAgent injects *no* system prompt today, so
  the model is ungrounded (it narrates, and invents an approval UI that doesn't exist). Add a
  built-in default telling the model what it is, the working directory, the tools available, and how
  the host actually behaves (denied = denied; be concise), loaded from a **user-editable file**
  (`~/.config/freeagent/system.md`, with an optional project-level `.freeagent/system.md` override)
  so it can be customized. The fuller **System-prompt assembly** below (project file + git status +
  memory) layers on top later.
- [ ] **Local-server providers** — Ollama already works via
  `OPENAI_BASE_URL=http://localhost:11434/v1`; consider a native Ollama provider for its
  non-OpenAI features (a recipe is in `docs/usage.md`).
- [x] **More slash commands** — `/status`, `/model`, `/help` alongside the existing `/plan`
  (the host command dispatch is in place). Feature-specific commands (`/compact`, `/undo`,
  `/commit`, …) arrive with their backing features below, and a `ctrl+p` command palette
  eventually supersedes slash-commands in the TUI (see On the horizon).

## Coming next — larger features

- [ ] **More providers + provider-model scaffolding** — *Anthropic, Azure OpenAI done (see
  Done).* Remaining: Bedrock, Vertex, Groq (Groq is pure OpenAI-compat — just set
  `OPENAI_BASE_URL=https://api.groq.com/openai/v1`); plus the scaffolding the single
  `StreamChatAsync` seam still lacks (pi-mono pattern): a first-class **`Model` metadata
  record** (id / wire-API / baseUrl / context window / max tokens / cost / reasoning) on
  `ProviderRequest`; **per-model compat flags** to absorb OpenAI-compatible variants without
  forking the adapter; typed **request options** + a provider-agnostic **`StopReason`**; and a
  formal provider **registry keyed by wire-API** rather than by vendor, with a separate model
  registry.
- [x] **Context-window management** — per-turn input-token tracking in `SessionState`, configurable
  `ContextWindow` (env `FREE_CONTEXT_TOKENS`), and pre-turn **turn-aware compaction** that drops
  older `User → Assistant → Tool` blocks (preserving `tool_use` / `tool_result` pairings and the
  user-first alternation). The dropped portion is **summarised by the model itself** via
  `Compactor.CompactWithSummaryAsync` and the summary is prepended to the first kept user message;
  on any provider error or blank summary, falls back to a non-LLM notice.
- [x] **Result cache + artifact store** — all four `cache-lookup` / `cache-write` / `invalidate` /
  `artifact-store` seams are now filled. **Cache:** `IToolResultCache` + `InMemoryToolResultCache`
  serve read-only `Success` from cache, skip `execute` on hits, never cache errors/`Empty`, and a
  successful mutating tool invalidates the cache. **Artifact store:** `IArtifactStore` +
  `InMemoryArtifactStore`; `Success` content above a configurable threshold (default 10k chars) is
  moved to the store and replaced with a preview + opaque ref, retrievable via the new `ReadArtifact`
  tool. Wired in the host by default.
- [x] **Hooks (pre/post-tool + SessionStart)** — `HookSpec` / `HookCondition` (tool name +
  inputContains) + `HooksConfig` (`preToolUse` / `postToolUse` / `sessionStart`) in
  `.freeagent/config.json`; `HookRunner` consults them at the existing `pre-hook` / `post-hook`
  pipeline seams and once at session start; `BashShellExecutor` runs `bash -c` with a 30s timeout
  and streams hook stdout/stderr to the user's console. Substitutions: `{{tool_name}}`,
  `{{tool_input}}` (truncated), `{{session_id}}`, `{{working_directory}}`. Failures are non-fatal.
- [x] **Sub-agents** — `AgentDefinition` / `AgentRegistry` + `SubAgentRunner` build an isolated
  sub-session with a filtered tool registry, the role's system-prompt suffix, no-op persistence,
  and silent events; `SpawnAgentTool` exposes it to the model with `AgentSpawnCap` (not auto-allowed
  — each spawn requires approval or an allow rule). Four default roles registered in the host:
  **Explore**, **Plan**, **Coder**, **Verify**.
- [x] **Richer editing tools** — `EditFile` (literal string-replace, unique-match safety,
  `replace_all` opt-in), `MultiEditFile` (atomic batch of edits per file), and `ApplyPatch`
  (unified-diff application — atomic per-file, unique-match for each hunk) all done. Remaining: a
  **colored diff view** for writes (host renderer; sits with the TUI work).
- [x] **System-prompt assembly** — done: base instructions (overridable file) + working directory +
  git branch (read directly from `.git/HEAD`, no subprocess) + a project context file
  (`CLAUDE.md` / `AGENTS.md` / `FREEAGENT.md`, first found, content appended). Cross-session
  memory is exposed as **tools** (`ReadMemoryTool` / `WriteMemoryTool`) so the model loads
  memory deliberately rather than every entry being auto-injected. *Remaining (later): git status
  summary in addition to branch; memory-key inventory in the prompt if useful.*
- [x] **Cross-session memory** — `ReadMemoryTool` (read-only, auto-allowed via `MemoryCap` read) +
  `WriteMemoryTool` (writable, requires approval), backed by markdown files under
  `~/.config/freeagent/memory/` (XDG-aware). Keys restricted to `[A-Za-z0-9._-]+`. Registered in
  the host.
- [x] **File history, `/undo`, and `/revert`** — per-write snapshots and `/undo` done
  (`SessionState.History`, recorded by `WriteFileTool` / `EditFileTool` / `MultiEditFileTool` /
  `ApplyPatchTool`, popped by the host's `/undo`). `/revert [N]` drops the last N user turns from
  the transcript (leading System messages preserved). Files and conversation revert independently
  — combine `/undo` and `/revert` for a full rollback.

## Architecture direction — decided (see [ADR 0005](docs/decisions/0005-headless-core-protocol.md))

**Target: headless core + protocol, with pluggable frontends.** The C# kernel exposes a server
(an HTTP API described by an OpenAPI spec + an SSE event stream); the TUI, a web frontend, editors
(via ACP), and remote access are all **clients of that one protocol** — the opencode pattern (a
client can `attach` to a local *or* remote server and holds zero agent logic). This is what makes
the opencode-grade Bun/opentui TUI reachable from a C# core.

Phasing (the kernel is *already* effectively headless — `SessionRuntime` + `IEventSink`):

- [ ] Keep building near-term/coming-next features **in-process** for now; just keep
  `SessionRuntime` / `IEventSink` / input frontend-agnostic so the seam stays clean.
- [ ] **Protocol server** — add a server project hosting `SessionRuntime`, bridging `IEventSink`
  and input to HTTP + SSE, emitting an OpenAPI spec (additive — not a kernel rewrite).
- [ ] First protocol **frontend** — a Bun/opentui TUI client (opencode-style). The existing
  console host remains as the minimal built-in/fallback client.

## On the horizon — integrations & ecosystem

- [x] **MCP client** — `IMcpTransport` seam, `JsonRpcClient` (JSON-RPC 2.0 over line-delimited
  JSON), `McpClient` (`initialize` + `notifications/initialized` + `tools/list` + `tools/call`),
  `StdioMcpTransport` for child-process servers, `McpServerManager` that spawns each configured
  server at host startup and registers its remote tools as `mcp__{server}__{tool}` via
  `McpToolAdapter` (the adapter requires a `ProcessExecCap("mcp:{server}", ...)` so a whole
  server can be allow- or deny-ruled). Configured via `mcp.servers[]` in `.freeagent/config.json`.
  Integration smoke test is `[Skip]`'d due to a runner interaction with background-loop disposal
  across test classes; passes in isolation. End-to-end with real MCP servers untested.
- [ ] **LSP client** — language-server-backed `hover` / `definition` / `references` /
  `diagnostics`.
- [ ] **Roslyn tool** — C# semantic analysis (overview, find-references, callers,
  blast-radius), relevant since FreeAgent itself is C#.
- [ ] **TUI (protocol client, opencode-style)** — per ADR 0005, the full-screen TUI is a
  **frontend client over the protocol**, not embedded in the host: a Bun/SolidJS app using
  **opentui** (the stack opencode uses) attached to the headless core. opentui is Zig + C-ABI +
  TypeScript and is *not* a .NET drop-in, which is exactly why the frontend is a separate Bun
  process talking the protocol rather than embedded. (A native .NET TUI — `Spectre.Console` /
  `Terminal.Gui` — was the in-process alternative; kept only as a possible minimal fallback
  renderer.)
- [ ] **Command palette** — a `ctrl+p` fuzzy command palette backed by a command registry, the
  opencode model: named, dispatchable commands with metadata that feed both keybindings and the
  palette. Supersedes ad-hoc slash-commands (the host already has a command-dispatch seam).
- [ ] **Status line repositioning** — move the `Session | Model | working dir` line from the top
  to a persistent bottom status bar, with rule lines above and below the input box. *Presentation
  only; lands as part of the TUI client (above) — the current console host keeps the top line.*
- [ ] **Local model runner (orchestrate, don't embed)** — since every local engine already speaks
  OpenAI-compatible HTTP (llama.cpp's `llama-server`, Ollama, LocalAI, vLLM, exo, LM Studio),
  FreeAgent should **download a model + launch/health-check a local server + point its existing
  provider at it** — not embed a C++/LLamaSharp engine in-process. Default to the light single-binary
  engines (Ollama or `llama-server`, which can fetch GGUF itself); architecture-neutral, stays pure
  .NET. What FreeAgent builds: server lifecycle (spawn/health-check/shutdown, port mgmt), a model
  download/catalog UX, and config mapping. *Pointing at an already-running server is config-only
  today (see `docs/usage.md`); this item is about owning the download + launch.* exo (distributed,
  Apple-Silicon/MLX) stays **docs-only** — point at it if you run it.
- [ ] **Multimodal — far future.** Image gen / speech-to-text / text-to-speech are *not* a near-term
  goal and stay text/coding-focused for now. When wanted, reach them the same way as LLMs: via a
  multimodal local server (e.g. **LocalAI**, which already exposes image/STT/TTS behind one
  OpenAI-compatible API) behind the existing provider — **not** by embedding native engines
  (whisper.cpp / piper / SD). Lone in-process exception worth noting: `whisper.net` (MIT, mature .NET
  binding) if voice input ever becomes a real ask.
- [x] **Playbooks** — templated, parameterized prompt shortcuts. Markdown files in
  `.freeagent/playbooks/` (project) or `~/.config/freeagent/playbooks/` (user); positional
  `{{arg1}}…{{argN}}` substitution. Invoked at the prompt with `/run <name> [args]`; bare `/run`
  lists what's available.
- [ ] **Editor & remote** — VS Code extension, ACP (Zed), desktop wrapper, web frontend,
  Slack / GitHub apps. Per ADR 0005 these are all just additional **clients of the one protocol**,
  not separate integrations.
- [ ] **Misc** — **session tagging** (`/tag` / `/untag` + visible in `/status`), **whole-session
  iteration limit** (env `FREE_SESSION_ITERATIONS`, in addition to the per-turn `MaxIterations=90`),
  and **opt-in OpenTelemetry tracing** (the kernel exposes `SessionRuntime.ActivitySource` and
  `ToolPipeline.ActivitySource`; attach any `ActivityListener` / OTel SDK to consume) done.
  Remaining: **session forking** (clone state to a new id), **file watching** during a session,
  **extended-thinking + token-budget controls**.

## Deliberately deferred

The README's "non-goals for now" list — intentionally out of the first cut, kept above
only to show the trajectory. Note: a *whole-conversation* iteration limit (distinct
from the current per-turn `MaxIterations`) would be a separate counter if ever added.

## Done

- [x] Capability-based permission engine (auto-allow / hard-block / session rules)
- [x] 12-step tool-execution pipeline with observable seams
- [x] Read-only + concurrency-safe parallel / serial execution contract (sibling-abort)
- [x] Doom-loop detection with bounded recovery (3 re-prompts, then halt)
- [x] Crash-safe atomic JSONL session persistence
- [x] OpenAI-compatible streaming provider (SSE, tool calls, reasoning deltas, usage)
- [x] Real tool adapters — `ReadFile`, `WriteFile`, `ProcessExec`
- [x] Plan-mode guard in the pipeline
- [x] Interactive host CLI (REPL, env config, per-turn Ctrl+C)

### Daily-driver usability milestone

- [x] Tool `Description` field wired through `ITool` / `ToolDefinition` / the OpenAI request
- [x] `Glob` and `Grep` read-only search tools (managed, workspace-scoped, capped)
- [x] Plan-mode toggle — `EnterPlanMode` / `ExitPlanMode` tools and a `/plan` host command
- [x] Config-driven permission rules (`PermissionConfig`, `.freeagent/config.json`)
- [x] Session resume — host `--resume [id]`

### Agent UX wave

- [x] Interactive permission approval (engine `Prompt` outcome, `IPermissionApprover` seam, session grants, console approver, denial-text fix)
- [x] User-editable system prompt injected on new sessions (`~/.config/freeagent/system.md` + project override)
- [x] `/help`, `/status`, `/model` slash commands (in addition to `/plan`)
- [x] Native Anthropic Messages-API streaming provider (text / thinking / tool-use, cache-aware normalized `Usage`) + `FREEPROVIDER` selection with per-provider config sections
- [x] Context-window management — token tracking + pre-turn turn-aware compaction with LLM-generated summary (fallback notice on failure)
- [x] `EditFile` tool — safe in-place string-replace editing (unique-match by default)
- [x] Result cache + artifact store — all four cache/artifact pipeline seams filled; `ReadArtifact` tool retrieves offloaded content
- [x] File history + `/undo` — per-write snapshots in `SessionState.History`, restored or deleted by `HostCommands.Undo`
- [x] Cross-session memory — `ReadMemoryTool` / `WriteMemoryTool` (filesystem-backed, XDG-aware)
- [x] System-prompt assembly — base + working dir + git branch (from `.git/HEAD`) + project context file (`CLAUDE.md` / `AGENTS.md` / `FREEAGENT.md`)
- [x] Pre/post-tool hooks — `HooksConfig` in `.freeagent/config.json`, `HookRunner` at the pre-hook / post-hook seams, `BashShellExecutor` host-side
- [x] Sub-agents — `AgentRegistry` + `SubAgentRunner` + `SpawnAgentTool`; default roles `Explore` / `Plan` / `Coder` / `Verify`
