# Changelog

All notable changes to FreeAgent are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/), and the project aims to follow
[Semantic Versioning](https://semver.org/).

## [Unreleased]

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

### Notes

- Whole solution builds clean with warnings-as-errors; 141 kernel tests pass.
