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
the larger ported features further down.

- [ ] **Tool `Description` field** — `ToolDefinition` carries no description, so the
  provider sends tools name-only; add descriptions for reliable tool selection.
- [ ] **Plan-mode toggle** — `SessionState.PlanMode` and the pipeline guard exist, but
  nothing flips the flag; add `EnterPlanMode`/`ExitPlanMode` tools and a `/plan` command.
- [ ] **Permission rules from config** — the engine supports allow/deny tool and
  capability rules; the host wires none. Load them from a config file and/or flags so
  writes and extra binaries can be granted without code changes.
- [ ] **Session resume** — `JsonlSessionStore.LoadAsync` already works; add a host
  `--resume <id>` path.
- [ ] **Glob / Grep read-only tools** — round out discovery. Both can be read-only +
  concurrency-safe, so they parallelize for free under the existing execution contract.
- [ ] **Local-server providers** — Ollama already works via
  `OPENAI_BASE_URL=http://localhost:11434/v1`; document recipes for Ollama / llama.cpp /
  vLLM, and consider a native Ollama provider for its non-OpenAI features.

## Coming next — larger features

- [ ] **More providers** — native Anthropic (Messages API), plus Azure OpenAI, Bedrock,
  Vertex, and Groq behind the existing `IProvider` seam.
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
- [ ] **Slash commands** — `/help`, `/status`, `/model`, `/compact`, `/commit`,
  `/review`, `/doctor`, `/undo`.
- [ ] **File history & undo** — per-write snapshots, a `/undo`, and session revert to a
  prior turn.

## On the horizon — integrations & ecosystem

- [ ] **MCP client** — discover, add, and configure MCP servers; register their tools as
  `mcp__server__tool`.
- [ ] **LSP client** — language-server-backed `hover` / `definition` / `references` /
  `diagnostics`.
- [ ] **Roslyn tool** — C# semantic analysis (overview, find-references, callers,
  blast-radius), relevant since FreeAgent itself is C#.
- [ ] **Full-screen TUI** — a richer renderer alongside the current console `IEventSink`.
- [ ] **Local inference orchestration** — an optional Docker wrapper that launches and
  manages a llama.cpp (or similar) server, mirroring OpenMono's bundled-server model.
- [ ] **Vision / multimodal input** — image inputs within a turn.
- [ ] **Playbooks** — templated, parameterized workflows.
- [ ] **Editor & remote** — VS Code extension, ACP (Zed), desktop wrapper, web frontend,
  Slack / GitHub apps.
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
