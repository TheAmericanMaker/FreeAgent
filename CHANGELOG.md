# Changelog

All notable changes to FreeAgent are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/), and the project aims to follow
[Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added — agent UX

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
