# Changelog

All notable changes to FreeAgent are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/), and the project aims to follow
[Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added — C# analysis

- **`CSharpAnalysis` tool** — Roslyn-backed syntactic analysis of `.cs` files in the workspace.
  Three actions: `list-types` (every class / interface / struct / record / record struct / enum /
  delegate declaration, qualified by enclosing namespaces + types), `list-members` (methods,
  ctors, properties, fields, events, indexers per type with parameter-type signatures), and
  `diagnostics` (syntax-level parse errors only). Read-only, concurrency-safe; required capability
  is a `FileReadCap` on the resolved root. Optional `glob` filter and a 500-line output cap keep
  results bounded. Registered in the host and added to the `Explore` / `Plan` sub-agent
  whitelists. Pulls in `Microsoft.CodeAnalysis.CSharp` 5.3 (parse-only — no `Compilation` or
  metadata references). Semantic features (find-references / callers / blast-radius) remain a
  follow-up.

### Added — sub-agents

- **Sub-agents** — `AgentDefinition` / `AgentRegistry` + `SubAgentRunner` build an isolated
  sub-session with a tool registry filtered to the role's allow-list, the role's system-prompt
  suffix, no-op persistence, and silent events (so sub-agent activity doesn't leak into the
  parent's console). `SpawnAgentTool` exposes spawning to the model via an `AgentSpawnCap` — not
  auto-allowed, so every spawn requires explicit approval. Four default roles registered in the
  host: **Explore** (read-only investigation), **Plan** (planning, no writes), **Coder**
  (implementation), **Verify** (tests/lints).
- Supporting kernel additions: `NoOpPersistenceStore` and `NullEventSink`.

### Added — hooks

- **SessionStart hooks** — `HooksConfig.SessionStart` runs once per session (after state creation,
  before the first turn) with `{{session_id}}` / `{{working_directory}}` substitutions.
- **`/doctor`** slash command — one-shot diagnostic snapshot: active provider, model, base URL,
  config path, working dir, tool inventory, sub-agent roles, plan mode, session approvals, undo
  stack depth.
- **Pre/post-tool hooks** — fills the pipeline's existing `pre-hook` / `post-hook` seams.
  `HooksConfig` (now part of `.freeagent/config.json` alongside permission rules) declares hooks
  with optional conditions (`tool`, `inputContains`); `HookRunner` matches and dispatches via an
  `IShellExecutor` seam. Host's `BashShellExecutor` runs `bash -c` with a 30s timeout and streams
  hook output to the user's console (not the model's transcript). Substitutions: `{{tool_name}}`,
  `{{tool_input}}`. Hook errors are non-fatal — they never block the agent.

### Added — grounding

- **System-prompt assembly** — the system prompt now layers (in order): base text (default or
  user-editable override) → working directory → git branch (read straight from `.git/HEAD`, no
  subprocess, silently skipped if absent) → project context file content (first existing of
  `CLAUDE.md` / `AGENTS.md` / `FREEAGENT.md` in the working dir). Memory is exposed as tools so
  the model loads it deliberately rather than auto-injecting every entry.

### Added — cross-session memory

- **`ReadMemoryTool` / `WriteMemoryTool`** — markdown-file-backed memory under
  `~/.config/freeagent/memory/` (XDG-aware). Read is auto-allowed via the existing `MemoryCap` read
  rule; write requires approval (interactive or an allow rule). Keys are restricted to
  `[A-Za-z0-9._-]+` so they cannot escape the root.

### Added — editing & undo

- **File history + `/undo`** — `WriteFileTool` and `EditFileTool` snapshot the pre-write content
  to `SessionState.History` after a successful write. The new `/undo` host command pops the most
  recent snapshot and restores it (or deletes the file if it didn't exist before). LIFO ordering
  across multiple writes.

### Added — editing

- **`EditFile` tool** — in-place file edit by literal string-replace, with a unique-match safety
  by default (precise edits) and an opt-in `replace_all`. Use this rather than `WriteFile` for
  changes to existing files: it preserves untouched content and is far cheaper in tokens. Required
  capability: `FileWriteCap` on the resolved path.
- **`MultiEditFile` tool** — atomic batch of literal-string edits on one file. Each edit obeys the
  same unique-match safety as `EditFile`; any failure aborts the batch *without writing*. Sequential
  edits see each other's intermediate state (so chains compose). One snapshot is taken for `/undo`.
- **`ApplyPatch` tool** — apply a unified diff to a single file. Each hunk's removed + context lines
  must match the file uniquely; if any hunk doesn't match, the patch aborts atomically with no
  changes written. `ApplyPatchTool.ParseHunks` is public and unit-tested.

### Added — robustness

- **Artifact store** — fills the pipeline's `artifact-store` seam. `Success` content above a
  configurable threshold (default 10k chars) is moved into `IArtifactStore` and the result content
  is replaced with `[Large output (N chars) saved as artifact \`ref\`. Preview: ...]`; the model
  retrieves the full text via the new `ReadArtifact` tool. Default `InMemoryArtifactStore`; wired
  in the host by default.
- **Result cache** — fills the pipeline's `cache-lookup` / `cache-write` / `invalidate` seams.
  Read-only tool `Success` results are cached by `(toolName, canonicalised-arguments)`; a hit
  short-circuits before `execute`; a successful mutating tool drops every cached entry (conservative
  invalidation). `IToolResultCache` / `InMemoryToolResultCache` are public seams; the host wires the
  in-memory cache by default.
- **LLM-summary compaction** — `Compactor.CompactWithSummaryAsync` asks the active provider to
  summarise the dropped portion in one short paragraph; the summary replaces the previous notice in
  the first kept user message. `SessionRuntime` uses it by default; on any provider error or blank
  summary it falls back to the non-LLM notice (compaction never blocks the turn).
- **Context-window compaction** — `SessionState` tracks `LastInputTokens` (from `Usage`) and a
  configurable `ContextWindow` (env `FREE_CONTEXT_TOKENS`); when the previous turn pushed input
  tokens past 80% of the window, `SessionRuntime` calls `Compactor.Compact` before the next turn —
  a pure, turn-aware drop that preserves leading System messages, the last 4 turns, and every
  `tool_use` ↔ `tool_result` pairing, with a notice prepended to the first kept user message.
  LLM-based summarization of the dropped turns is the next step.

### Added — providers

- **Native Anthropic provider** — `AnthropicProvider` (Messages API; streaming text, thinking, and
  tool-use blocks; consecutive `Tool` results merged into one user message with `tool_result` blocks
  per Anthropic's role-alternation rule). 10 tests cover SSE parsing and request-body shape.
- **Provider selection** — `FREEPROVIDER` env / `provider` config field selects `openai` (default)
  or `anthropic`; per-provider config sections (`openai`, `anthropic`); `ANTHROPIC_API_KEY` /
  `ANTHROPIC_BASE_URL` / `ANTHROPIC_MODEL` envs. Legacy flat fields preserved for OpenAI.
- **Normalized `Usage`** — additive `CacheReadTokens` / `CacheWriteTokens` (populated by Anthropic).

### Added — agent UX

- **Slash commands** — `/help`, `/status`, `/model` added alongside `/plan`, in a testable
  `HostCommands` dispatcher (extracted from `Program`).
- **System prompt (user-editable)** — new sessions now start with a grounding system message
  (agent identity, tool-first behavior, "denied is final — don't invent an approval dialog", be
  concise) + the working directory. Overridable via `.freeagent/system.md` (project) or
  `~/.config/freeagent/system.md` (user); previously no system prompt was sent at all.
- **Interactive permission approval** — the engine now distinguishes a hard deny from an
  approvable `Prompt` (uncovered capability); the pipeline consults an optional `IPermissionApprover`
  on `Prompt`, with "allow for session" grants tracked in `SessionState.SessionApprovals`. The host's
  `ConsoleApprover` prompts `[once / session / always / deny]`, where "always" persists a rule to
  `.freeagent/config.json`. With no approver the kernel stays deterministic (prompt = deny).

### Added — packaging & distribution

- **Global tool** — `FreeAgent.Host` packs as a .NET tool (`dotnet tool install -g FreeAgent`),
  exposing a single `freeagent` command that runs in the current directory. `scripts/install.sh`
  builds and installs/updates it from a local checkout.
- **`--help` / `--version`** flags.
- **User-level provider config** — `ProviderConfig` reads `~/.config/freeagent/config.json`
  (XDG-aware) for `baseUrl` / `model` / `apiKey`, with precedence env > file > default, so the bare
  command works without exporting env vars.
- **Release workflow** — `.github/workflows/release.yml` builds/tests on `v*` tags, packs the tool,
  publishes to NuGet when `NUGET_API_KEY` is set, and attaches self-contained binaries
  (linux-x64 / osx-arm64 / win-x64) to the GitHub Release.

### Added — daily-driver usability milestone

- **Tool descriptions** — `ITool.Description`, threaded through `ToolDefinition` and sent
  to the provider as the function description for reliable tool selection.
- **`Glob` and `Grep` tools** — read-only, concurrency-safe, managed (no `rg` dependency),
  workspace-scoped via `FileReadCap`, with deterministic noise-dir-skipping walks and capped
  output.
- **Plan-mode toggle** — `EnterPlanMode` / `ExitPlanMode` tools (read-only) plus a `/plan
  [on|off]` host command.
- **Config-driven permissions** — `PermissionConfig` loads allow/deny rules from
  `$FREEAGENT_CONFIG` or `.freeagent/config.json` and applies them to the engine (missing is
  fine, malformed is a non-fatal warning).
- **Session resume** — `--resume [id]` rehydrates `session.jsonl` and continues that session;
  falls back to a fresh session on any problem.

### Added

- **`FreeAgent.Host` interactive CLI** — env-configured (`OPENAI_API_KEY`,
  `OPENAI_BASE_URL`, `FREEMODEL`) REPL that wires the kernel to real tools, streams
  responses, cancels the current turn on Ctrl+C, and saves the session on exit.
  `--verbose` surfaces reasoning and per-turn token usage.
- **`OpenAIProvider`** — streaming `IProvider` for any OpenAI-compatible
  `/chat/completions` endpoint: SSE parsing, tool-call delta reassembly, usage
  extraction (OpenAI and Anthropic field names), and status-coded HTTP errors.
- **Real tool adapters** — `ReadFileTool`, `WriteFileTool`, and `ProcessExecTool`,
  each declaring its capability and concurrency profile.
- **Documentation** — rewritten [README](README.md) plus
  [`docs/architecture.md`](docs/architecture.md) and [`docs/usage.md`](docs/usage.md).

### Fixed

- `FreeAgent.Host` did not compile: added the missing `using FreeAgent.Kernel;`,
  corrected an illegal `var?` declaration, and fixed a `ReadFileTool` constructor
  mismatch. The Ctrl+C handler is now registered once instead of accumulating one
  per turn.
- Added `FreeAgent.Host` to `FreeAgent.slnx` so the whole solution builds together.

#### From multi-agent review

- `OpenAIProvider` buffered the whole response (`PostAsync` → `ResponseContentRead`),
  collapsing the SSE stream into one burst — now uses `SendAsync` with
  `ResponseHeadersRead`, and the owned `HttpClient` no longer caps long streamed
  completions at the default 100s timeout.
- `OpenAIProvider` now parses reasoning deltas (`reasoning_content`/`reasoning`) into
  `ThinkingDelta`, so `--verbose` reasoning actually works against reasoning models.
- SSE parsing now accepts `data:{...}` (the space after `data:` is optional per spec).
- An empty `tools` array is now omitted from the request body (OpenAI rejects `tools: []`).
- Malformed/empty accumulated tool-call arguments no longer crash the turn in the
  doom-loop signature step — they fall through to the pipeline's `InvalidInput` path.
- Doom-loop guard now suppresses **every** repeat after detection (`>=` threshold),
  not just the third batch; docs and messages corrected to "suppress + re-prompt".
- Host `Ctrl+C` handler no longer races the per-turn cleanup into an
  `ObjectDisposedException` (atomic clear-then-dispose + guarded `Cancel`).

### Changed

- Doom-loop handling is now a bounded recovery: after the guard trips, the model is
  re-prompted (with the repeat suppressed) up to `DoomRecoveryBudget` (3) times, then
  the turn halts instead of re-prompting toward the iteration ceiling.
- Per-turn `MaxIterations` lowered from 1000 to **90** (matching the Hermes Agent
  default) as the hard ceiling on a stuck turn.

### Notes

- Whole solution builds clean with warnings-as-errors; 142 kernel tests pass.
