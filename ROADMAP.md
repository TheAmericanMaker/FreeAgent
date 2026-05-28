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

- [ ] **Interactive permission approval** — today an uncovered capability is denied with
  no way to approve it live, so the only approval channel is hand-writing
  `.freeagent/config.json` (and the model tends to hallucinate an approval UI that isn't
  there). Intercept the engine's "needs approval" denial in the host and prompt
  `[allow once / session / always→write a rule / deny]`; carry session grants in
  `SessionState`; tighten the denial text. The kernel already returns a clean,
  distinct denial for exactly this. **Highest-impact near-term item — it currently blocks
  real edits.**
- [ ] **Local-server providers** — Ollama already works via
  `OPENAI_BASE_URL=http://localhost:11434/v1`; consider a native Ollama provider for its
  non-OpenAI features (a recipe is in `docs/usage.md`).
- [ ] **More slash commands** — `/status`, `/model`, `/help` alongside the existing `/plan`
  (the host command dispatch is in place). Feature-specific commands (`/compact`, `/undo`,
  `/commit`, …) arrive with their backing features below, and a `ctrl+p` command palette
  eventually supersedes slash-commands in the TUI (see On the horizon).

## Coming next — larger features

- [ ] **More providers + provider-model hardening** — native Anthropic (Messages API), plus
  Azure OpenAI, Bedrock, Vertex, and Groq behind the existing `IProvider` seam. The single
  `StreamChatAsync` seam is the right shape (pi-mono uses essentially one streaming method too),
  but to make "add any provider anytime, no core change" real, add the scaffolding around it that
  pi-mono has: a first-class **`Model` metadata record** (id / wire-API / baseUrl / context window /
  max tokens / cost / reasoning) on `ProviderRequest`; **normalized `Usage`** (cache read/write,
  total, cost) not just raw tokens; **per-model compat flags** to absorb OpenAI-compatible variants
  without forking the adapter; typed **request options** + a provider-agnostic **`StopReason`**; and a
  provider **registry keyed by wire-API** (`openai-completions`, `anthropic-messages`, …) rather than
  by vendor, with a separate model registry.
- [ ] **Context-window management** — token tracking, checkpointing, and compaction so
  long sessions don't overrun the window (FreeAgent has none today).
- [ ] **Result cache + artifact store** — fill the `cache-lookup` / `cache-write` /
  `invalidate` and `artifact-store` pipeline seams (offload large tool outputs and
  return a reference to the model).
- [ ] **Hooks** — `SessionStart` / `PreToolUse` / `PostToolUse` scripts at the existing
  `pre-hook` / `post-hook` seams, with tool-name / input-substring conditions.
- [ ] **Sub-agents** — spawn isolated sessions with restricted tool sets
  (`AgentSpawnCap` is already modeled); start with `Explore` / `Plan` / `Coder` /
  `Verify` roles.
- [ ] **Richer editing tools** — MultiEdit, ApplyPatch, and a colored diff view for writes.
- [ ] **System-prompt assembly** — base instructions + a project file (e.g. `CLAUDE.md`)
  + git branch/status + cross-session memory.
- [ ] **Cross-session memory** — `MemoryCap` is modeled; add a memory store and a
  read/write tool.
- [ ] **File history & undo** — per-write snapshots, a `/undo`, and session revert to a
  prior turn.

## Architecture decisions to make

- [ ] **Headless core + protocol, with pluggable frontends** (decide before betting on a TUI).
  opencode (the current Bun/TypeScript version) shows a clean pattern: a headless agent **server**
  — an HTTP API described by an **OpenAPI spec** plus an SSE `/event` stream — with the TUI as a
  pure **client** that holds zero agent logic and can `attach` to a local *or remote* server.
  Adopting this for FreeAgent (the C# kernel exposes the server; clients are generated from the
  spec) would make the **TUI, a web frontend, editor integrations, and ACP all clients of one
  protocol** rather than separate builds — and would let a JS/opentui frontend pair with the C#
  core without embedding either language in the other. The alternative is keeping a single
  in-process host with a native .NET TUI embedded directly. **This decision gates the TUI options
  below.**

## On the horizon — integrations & ecosystem

- [ ] **MCP client** — discover, add, and configure MCP servers; register their tools as
  `mcp__server__tool`.
- [ ] **LSP client** — language-server-backed `hover` / `definition` / `references` /
  `diagnostics`.
- [ ] **Roslyn tool** — C# semantic analysis (overview, find-references, callers,
  blast-radius), relevant since FreeAgent itself is C#.
- [ ] **TUI** — a full-screen renderer beyond the current console `IEventSink`. Two routes,
  pending the architecture decision above:
  - **(a) Native .NET TUI** embedded in the host — `Spectre.Console` or `Terminal.Gui`. Single
    language and process; simplest; no protocol needed.
  - **(b) Separate frontend over the protocol** — e.g. a Bun/SolidJS app using **opentui** (what
    opencode uses) attached to the headless core. Note: opentui is **Zig + C-ABI + TypeScript and
    is not a drop-in for .NET** — P/Invoking its C ABI would mean reimplementing its entire TS
    framework layer (layout/render-loop/components), so the realistic opentui route is a separate
    Bun frontend (a polyglot stack). Buys reuse of opentui's components and a shared path with the
    web/editor frontends, at the cost of a second toolchain.
- [ ] **Command palette** — a `ctrl+p` fuzzy command palette backed by a command registry, the
  opencode model: named, dispatchable commands with metadata that feed both keybindings and the
  palette. Supersedes ad-hoc slash-commands (the host already has a command-dispatch seam).
- [ ] **Status line repositioning** — move the `Session | Model | working dir` line from the top
  to a persistent bottom status bar, with rule lines above and below the input box. *Presentation
  only; the actual design waits on the TUI route chosen above.*
- [ ] **Local inference orchestration** — an optional Docker wrapper that launches and
  manages a llama.cpp (or similar) server, mirroring OpenMono's bundled-server model.
- [ ] **Vision / multimodal input** — image inputs within a turn.
- [ ] **Playbooks** — templated, parameterized workflows.
- [ ] **Editor & remote** — VS Code extension, ACP (Zed), desktop wrapper, web frontend,
  Slack / GitHub apps. If the headless-core + protocol decision lands, these are all just
  additional clients of that one protocol rather than separate integrations.
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
