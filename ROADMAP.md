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

- [ ] **More providers + provider-model scaffolding** — *Anthropic done (see Done).* Remaining:
  additional providers (Azure OpenAI, Bedrock, Vertex, Groq) behind the existing seam, plus the
  scaffolding the single `StreamChatAsync` seam still lacks (pi-mono pattern): a first-class
  **`Model` metadata record** (id / wire-API / baseUrl / context window / max tokens / cost /
  reasoning) on `ProviderRequest`; **per-model compat flags** to absorb OpenAI-compatible variants
  without forking the adapter; typed **request options** + a provider-agnostic **`StopReason`**;
  and a formal provider **registry keyed by wire-API** (`openai-completions`, `anthropic-messages`,
  …) rather than by vendor, with a separate model registry.
- [x] **Context-window management** — done: per-turn input-token tracking in `SessionState`,
  configurable `ContextWindow` (env `FREE_CONTEXT_TOKENS`), and pre-turn **turn-aware** compaction
  that drops older `User → Assistant → Tool` blocks (preserving `tool_use` / `tool_result`
  pairings and the user-first alternation), prepending a notice to the first kept user message.
  Remaining/next: **LLM-based summarization** of the dropped turns (replace the notice with a
  real summary).
- [ ] **Result cache + artifact store** — fill the `cache-lookup` / `cache-write` /
  `invalidate` and `artifact-store` pipeline seams (offload large tool outputs and
  return a reference to the model).
- [ ] **Hooks** — `SessionStart` / `PreToolUse` / `PostToolUse` scripts at the existing
  `pre-hook` / `post-hook` seams, with tool-name / input-substring conditions.
- [ ] **Sub-agents** — spawn isolated sessions with restricted tool sets
  (`AgentSpawnCap` is already modeled); start with `Explore` / `Plan` / `Coder` /
  `Verify` roles.
- [x] **Richer editing tools** — `EditFile` (literal string-replace with unique-match safety;
  `replace_all` opt-in) done. Remaining: **MultiEdit** (atomic batch of edits per file),
  **ApplyPatch** (unified diff), **colored diff view** for writes.
- [ ] **System-prompt assembly** — base instructions + a project file (e.g. `CLAUDE.md`)
  + git branch/status + cross-session memory.
- [ ] **Cross-session memory** — `MemoryCap` is modeled; add a memory store and a
  read/write tool.
- [ ] **File history & undo** — per-write snapshots, a `/undo`, and session revert to a
  prior turn.

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

- [ ] **MCP client** — discover, add, and configure MCP servers; register their tools as
  `mcp__server__tool`.
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
- [ ] **Playbooks** — templated, parameterized workflows.
- [ ] **Editor & remote** — VS Code extension, ACP (Zed), desktop wrapper, web frontend,
  Slack / GitHub apps. Per ADR 0005 these are all just additional **clients of the one protocol**,
  not separate integrations.
- [ ] **Misc** — extended thinking + token budgets, session tagging/forking, file
  watching during a session, opt-in OpenTelemetry tracing.

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
- [x] Context-window safety net — token tracking + pre-turn turn-aware compaction (no LLM summary yet)
- [x] `EditFile` tool — safe in-place string-replace editing (unique-match by default)
