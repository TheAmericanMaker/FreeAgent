# Changelog

All notable changes to FreeAgent are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/), and the project aims to follow
[Semantic Versioning](https://semver.org/).

## [Unreleased]

### Security — protocol server hardening

- **Safe-by-default networking for `FreeAgent.Server`.** The server now binds to `127.0.0.1` by
  default (override with `FREEAGENT_SERVER_URLS` / `ASPNETCORE_URLS`) and **refuses to start** on a
  non-loopback address unless `FREEAGENT_SERVER_API_KEY` is set — an open agent on a routable
  interface is remote code execution. The bearer-token check is now **constant-time**
  (`CryptographicOperations.FixedTimeEquals`). Live sessions are **capped**
  (`FREEAGENT_SERVER_MAX_SESSIONS`, default 256 → `429`), and **turns are serialized per session**
  (a second concurrent `POST /turns` gets `409`, preventing the event-sink/state race). New pure
  helpers live in `ServerSecurity`; also fixed `POST /sessions` 500'ing when no provider key is
  configured (placeholder key, deferring auth failure to the turn).

### Added — workspace trust (project-config security gate)

- **Directory trust for executable project config** — a project's `.freeagent/config.json` can run
  code on launch (SessionStart/pre/post hooks via `bash -c`, MCP/LSP server processes) and grant
  extra privileges (allow rules). These are now honored only for a **trusted** working directory.
  The first time you open an untrusted project whose config declares any of them, FreeAgent prompts
  `[y]es once / [a]lways / [N]o` (default No); until trusted, hooks/MCP/LSP are skipped and
  allow-rules are not applied (deny-rules always apply, and the agent still works via the interactive
  approver). Trust is remembered per absolute path in `$XDG_CONFIG_HOME/freeagent/trusted.json`.
  Escape hatches: a `freeagent trust` subcommand, a `--trust` flag, and `FREEAGENT_TRUST=1`;
  non-interactive (piped) runs fail closed. Closes the "open a cloned repo → code execution" hole.

### Fixed / Security — from the project review

- **Thread-safe cache + artifact store** — `InMemoryToolResultCache` and `InMemoryArtifactStore` now
  use `ConcurrentDictionary`. They are reached from the executor's parallel read-only window through
  a single shared `ToolPipeline`, where the previous plain `Dictionary` was a data race.
- **`find` is no longer auto-allowed with a destructive action** — `find` is auto-allowed only without
  `-delete` / `-exec` / `-execdir` / `-ok` / `-okdir` / `-fprint*` / `-fls`; otherwise it requires
  approval like any other binary (previously `find . -delete` ran unprompted).
- **MCP `tools/call` honors `isError`** — `CallToolAsync` returns the `isError` flag and the adapter
  maps a server-reported failure to an error result instead of a silent `Success`.
- **Grep regex match-timeout** — model-supplied patterns compile with a 2s match timeout; a
  catastrophic-backtracking (ReDoS) pattern now returns `InvalidInput` instead of hanging the turn.
- **Provider HTTP responses are disposed** — OpenAI / Azure / Anthropic / Ollama / Vertex / Bedrock
  adapters now `using`-dispose the response (and Bedrock's event-stream), closing a socket/connection
  leak on both the success and error paths.

### Added — find-callers semantic action

- **`CSharpAnalysis` gains `find-callers`** — walks the call graph outward from the target symbol
  via BFS up to `depth=N` (1–5, default 1). Each result line carries `depth N: file:line:col:
  caller-display calls target-display`, grouped by depth so the model sees the immediate callers
  first, then their callers, etc. — the blast radius of a change. Implemented against the bare
  `Compilation` + per-tree `SemanticModel` (uses `Microsoft.CodeAnalysis.CSharp.Workspaces`'s
  symbol comparer; doesn't depend on `SymbolFinder`). 4 new tests cover direct callers,
  multi-hop BFS, unknown-symbol empty result, and the missing-symbol validation.

### Added — colored diff renderer

- **`ColoredDiff.Render(oldText, newText, …)`** — kernel-side unified-diff renderer. Line-level
  LCS, standard `@@ -a,b +c,d @@` hunk headers, configurable context (default 3), optional ANSI
  styling (red for removals, green for additions, cyan for hunk headers — matches `git diff`).
  Handles CRLF normalization, additions to empty files, deletions, and an arbitrary number of
  context lines. 9 unit tests.
- **`/undo` now shows what was reverted** — when the previous content differs from the current
  on-disk content, the undo response includes a colored diff of the reverted change.

### Added — status-bar (opt-in)

- **`Host/StatusBar.cs`** — pinned bottom status row in the existing console host, enabled with
  `FREE_STATUS_BAR=1`. Uses ANSI DECSTBM (`[1;Hr`) to carve out a fixed scroll region so
  output scrolling above never touches the bottom line; `[s…[u` brackets the repaint so the
  cursor returns to where the user was typing. No-ops when stdout is redirected. Renders
  `provider/model | session | msgs: N | iter: N [| PLAN] [| tags] | cwd: …` and repaints after
  every turn. Disposing restores the scroll region so the host's exit is clean. Stopgap until the
  full TUI status bar lands with the Bun/opentui client.

### Added — protocol-client scaffolds

- **`clients/tui/`** — Bun + TypeScript package with a full protocol client (`FreeAgentClient`:
  `createSession` / `listSessions` / `getSession` / `deleteSession` / SSE-streamed `streamTurn`)
  and a smoke CLI that creates a session, submits one turn from argv, prints the SSE stream. SSE
  parser unit-tested with `bun test`: single event, joined events, split-across-chunks
  reassembly, comment-line / malformed-record handling. The opentui-rendered full-screen UI
  builds on top of this without changes to the wire surface.
- **`clients/vscode/`** — VS Code extension scaffold with "FreeAgent: New Session" + "FreeAgent:
  Ask…" commands, a status-bar item, and an output channel streaming the SSE response inline.
  Settings: `freeagent.baseUrl` + `freeagent.apiKey`. Inlines a minimal SSE parser today; a
  shared `@freeagent/client` package is the obvious refactor when both clients grow more code.
- **`clients/README.md`** — index documenting the per-client status and how each consumes
  `/openapi/v1.json`.

### Added — multimodal recipe

- **`docs/recipes/multimodal.md`** — documents the LocalAI path for reaching image generation,
  speech-to-text, and text-to-speech: point `OPENAI_BASE_URL` at LocalAI (one binary, OpenAI-
  compatible chat + images + audio endpoints) and call the multimodal routes outside the agent.
  Explains why the kernel stays text-first by design (multimodal-as-tools is additive — the
  permission engine + artifact store already handle the shape).

### Fixed — MCP / LSP smoke tests now run cleanly

- **Root cause**: against a zero-latency in-memory test transport, the `JsonRpcClient` read loop
  could pop a queued response *before* `CallAsync` registered its `TaskCompletionSource` (in
  production this can't happen — the TCS is registered before the transport write). The response
  was then dropped, and the test waited forever.
- **Fix**: `JsonRpcClient` now buffers responses for unknown ids in `_earlyResponses` /
  `_earlyErrors`. `CallAsync` drains a matching buffered entry under the same lock that registers
  the TCS, so even pre-queued responses resolve. Purely additive — production code paths are
  unchanged because the buffer stays empty there.
- Both `McpClientTests.EndToEndProtocolFlow` and `LspClientTests.EndToEndProtocolFlow` are now
  un-skipped and live in the new `JsonRpcCollection` (which keeps them sequential; combined with
  the buffer fix they run cleanly together). 424 → 445 pass + 0 skip.

### Added — Roslyn `.csproj`-aware references

- **`RoslynSemanticHelpers.WorkspacePackageReferences`** — walks every `obj/project.assets.json`
  under the working directory (after `dotnet restore`), maps each `targets.*.compile` entry
  through the `packageFolders` to an absolute DLL path, and returns the resolved
  `MetadataReference` set. Cached per working directory.
- **`BuildWorkspaceCompilation` now takes a `workingDirectory`** and merges the runtime references
  with these workspace package references — so `find-references` / `find-definition` /
  `semantic-diagnostics` now bind into NuGet packages the project actually depends on (not just
  the .NET stdlib the host happens to ship).
- 7 new tests cover: stdlib enumeration, `EnumerateAssetsFiles` discovery + noise-dir skipping,
  `ResolveAssetsReferences` for existing/missing/non-DLL entries, and the empty-workspace path.

### Added — GGUF download + catalog for `/serve`

- **`/serve download <url-or-hf:owner/repo/path.gguf> [--name <local-name>]`** — streams a GGUF
  into `$XDG_CACHE_HOME/freeagent/models/`. Writes through a `.part` temp file so an interrupted
  download leaves no half-file at the model's name; rejects anything that doesn't end in `.gguf`
  or that contains a path separator. `HF_TOKEN` (when set) is forwarded as a Bearer header for
  gated HuggingFace repos.
- **`hf:owner/repo/path/to/file.gguf`** shorthand → expands to
  `https://huggingface.co/owner/repo/resolve/main/path/to/file.gguf`. Multi-segment paths inside
  the repo are preserved.
- **`/serve models`** lists every cached GGUF with sizes.
- **`/serve start <name>`** now accepts bare catalog names (with or without the `.gguf` extension)
  in addition to absolute / relative paths — `ModelServerLauncher.ResolveModelName` handles the
  lookup and returns the input unchanged if nothing matches.
- 14 new tests cover the `hf:` parser (valid + invalid specs), `ResolveModelName` (existing path
  passes through, bare name finds catalog entry, unknown name returns input), `ListCatalog`
  empty-state, and the `/serve download` argument parser.

### Added — command-palette registry

- **`CommandRegistry` + `CommandDefinition`** — single source of truth for the host's named
  commands. Each definition carries id, label, optional description, optional shortcut display
  string, and optional category. Designed so the future Bun/opentui TUI's `ctrl+p` palette and any
  editor extension bind their command list to the same registry the host slash dispatcher uses.
- **Subsequence fuzzy matcher** — `CommandRegistry.Search(query)` and the static helper
  `FuzzyScore(query, haystack)` score by `(last - first)` indices of the query characters in the
  haystack, so a tighter cluster beats a wider spread (`fk` over a label containing `fork` beats
  `fk` spread across `fork-long-id`). Case-insensitive; an empty query returns everything in the
  default category+label order.
- **`HostCommands.BuildDefaultRegistry`** — registers every existing slash command (help, status,
  model, plan, undo, revert, tag, untag, doctor, fork, serve, run, commands) so the registry
  is preloaded with everything the user can do today.
- **`/commands [query]` slash command** — renders the registry grouped by category, indented under
  the shortcut display string, with a one-line description. Fuzzy-filters when a query is given.

### Added — Google Vertex AI provider

- **`VertexProvider`** — native client for Anthropic Claude models on Google Vertex AI.
  Auth flows through `Google.Apis.Auth`'s Application Default Credentials chain
  (`GOOGLE_APPLICATION_CREDENTIALS` env var / gcloud auth / GCE metadata / SSO). The token source
  is exposed via `VertexProvider.ITokenSource` so tests can inject a static bearer.
- Endpoint: `https://{location}-aiplatform.googleapis.com/v1/projects/{project}/locations/
  {location}/publishers/anthropic/models/{modelId}:streamRawPredict`. Body shape is identical to
  the direct Anthropic API except `anthropic_version: vertex-2023-10-16` replaces the
  top-level `model` field (which moves into the URL). SSE chunk dispatch reuses the Anthropic
  shape (text / thinking / tool_use / stop_reason).
- **`FREEPROVIDER=vertex`** wiring in `ProviderConfig`. `VERTEX_PROJECT` (required),
  `VERTEX_LOCATION` (default `us-central1`), `VERTEX_MODEL` or `FREEMODEL` (default
  `claude-3-7-sonnet@20250219`). No API key required at the host bootstrap check — auth is ADC.
- 8 new tests cover URL composition, bearer-token plumbing, body shape (Vertex anthropic_version,
  no top-level model, system hoisted), text-delta streaming, non-2xx wrapping, ctor validation,
  and the default model id.

### Added — AWS Bedrock provider

- **`BedrockProvider`** — official `AWSSDK.BedrockRuntime` SDK wrapper. SigV4 signing, region
  routing, retries, and AWS event-stream (`vnd.amazon.eventstream`) parsing are all the SDK's
  responsibility; the adapter translates between FreeAgent's `ProviderRequest` shape and Bedrock's
  anthropic-on-bedrock body (Anthropic-Messages shape minus the `model` field, plus required
  `anthropic_version: bedrock-2023-05-31`). Streaming chunk dispatch is identical to
  `AnthropicProvider`'s — text, thinking, tool-use, stop-reason — because the wire payload is the
  same JSON.
- **`FREEPROVIDER=bedrock`** wiring in `ProviderConfig`. `AWS_REGION` (default `us-east-1`) sets
  the region; `BEDROCK_MODEL` or `FREEMODEL` selects the model id (default
  `anthropic.claude-3-7-sonnet-20250219-v1:0`). No API key required — auth flows through the
  default AWS credential chain (env vars / shared profile / IMDS / SSO).
- Bedrock body shape is exhaustively covered by the existing Anthropic adapter tests (they share
  the same builder logic); Bedrock-specific tests cover ctor validation and the divergences
  (`anthropic_version` carried, no top-level `model`).

### Added — OpenAPI spec emission

- **`GET /openapi/v1.json`** — the protocol server now publishes its OpenAPI document via
  `Microsoft.AspNetCore.OpenApi`. Lets the eventual TUI / VS Code / web frontends regenerate
  contracts instead of mirroring hand-rolled types. Covered by a regression test that asserts the
  document is served and references every session endpoint.

### Added — protocol server (HTTP + SSE)

- **`FreeAgent.Server`** — new ASP.NET Core minimal-API project that hosts the kernel as a
  network service (ADR 0005). Endpoints: `POST /sessions`, `GET /sessions`, `GET /sessions/{id}`,
  `POST /sessions/{id}/turns` (SSE-streamed: `event: text|thinking|tool_call|tool_result|usage`
  per callback, then a final `event: done` with the assembled reply), and `DELETE /sessions/{id}`.
- **`HttpSseEventSink`** — bridges the kernel's `IEventSink` to a per-turn SSE response. Writes are
  serialized via a lock so interleaved events can't tear a line; client-disconnect mid-turn
  is swallowed (the runtime's cancellation token handles the rest).
- **`SessionRuntime.SwapEventSink`** — the runtime now exposes an atomic sink swap so each HTTP
  turn can route into its own SSE stream without recreating the runtime.
- **Auth gate** — optional bearer-token check via `FREEAGENT_SERVER_API_KEY`. When unset, the
  server is open and intended for loopback bind only.
- **`SessionRegistry`** + **`ProviderFactory`** — in-memory session map (concurrent) and a
  reusable wrapper around the host's `ProviderConfig` so the server selects providers from the
  same env-var/config matrix as the CLI.
- Added `Microsoft.AspNetCore.Mvc.Testing` to the test project; nine new tests cover the full
  HTTP surface (create / list / get / get-404 / delete / delete-again-404 / post-turn-404 /
  post-turn-400) plus the auth gate (rejects without header, accepts correct bearer).

### Added — LSP client

- **`LspClient` + `StdioLspTransport` + `LspServerManager` + `LspToolAdapter`** — language-server
  integration for `hover` / `definition` / `references`, plus an `open` action that pushes file
  text into the server before lookups. Shares `JsonRpcClient` with the MCP layer; the transport
  contract was hoisted into a new `IJsonRpcTransport` base that both `IMcpTransport` and
  `ILspTransport` extend. LSP framing (`Content-Length: N\r\n\r\n{body}`) is handled by
  `StdioLspTransport`.
- **`lsp.servers[]` config** — declares each language server with name, language id, file
  extensions, command, and args. `LspServerManager` spawns each at host startup, runs the
  `initialize` + `initialized` handshake against the workspace root URI, and registers four tools
  per server: `lsp__{name}__{hover|definition|references|open}`. Required capability per call is a
  `ProcessExecCap("lsp:{server}", …)` so a whole language server can be allow- or deny-ruled as a
  unit (mirrors `McpToolAdapter`). Per-server failures are isolated — others still come up. The
  adapter rejects paths whose extension isn't in the server's `fileExtensions`. Positions are
  converted between the host's 1-based and LSP's 0-based at the adapter boundary.

### Added — Roslyn semantic actions

- **`CSharpAnalysis` gains `find-references` / `find-definition` / `semantic-diagnostics`** — full
  `CSharpCompilation` over the workspace's `.cs` files, with metadata references pulled from the
  host's `TRUSTED_PLATFORM_ASSEMBLIES` and cached after the first build (helper:
  `RoslynSemanticHelpers`). `find-references` walks every `IdentifierNameSyntax` and emits
  `file:line:col: Kind FullName` for every binding whose final identifier matches the requested
  symbol (and, if the symbol was dotted, whose containing type also appears in the requested path).
  `find-definition` walks `MemberDeclarationSyntax` nodes and reports declaration sites the same
  way. `semantic-diagnostics` returns compiler errors and warnings from the full compilation —
  distinct from the existing `diagnostics` action's parse-only errors. Limitation: metadata refs
  come from the host's assembly graph rather than the workspace's `.csproj`, so a reference into a
  NuGet package the host doesn't ship won't bind. Workspace-local symbol queries are fully
  supported. A `.csproj`-aware reference resolver remains a follow-up.

### Added — provider-model scaffolding

- **`StopReason` enum** — normalized stop reason on every `StreamChunk`
  (`EndTurn`/`ToolUse`/`MaxTokens`/`StopSequence`/`Refusal`/`Unknown`). Every provider now maps
  its wire-specific finish/stop reason to this enum: OpenAI's `finish_reason`
  (`stop`/`length`/`tool_calls`/`function_call`/`content_filter`), Anthropic's `stop_reason`
  emitted on `message_delta` (`end_turn`/`tool_use`/`max_tokens`/`stop_sequence`/`refusal`), and
  Ollama's optional `done_reason` (`stop`/`length`). Lets the runtime distinguish "model finished"
  from "ran out of tokens" without re-parsing provider-specific wire strings.
- **`Model` metadata record** — id, wire API, context tokens, default max-output tokens, and
  `SupportsTools`/`SupportsVision`/`SupportsThinking` capability flags. Optional — the runtime
  works without it; when present, lookups can size budgets and gate features.
- **`ModelCatalog`** — `wire-api/id`-keyed registry with `Defaults()` factory carrying built-in
  records for the major OpenAI + Anthropic models (`gpt-4o`, `gpt-4o-mini`,
  `claude-3-7-sonnet-latest`, `claude-3-5-haiku-latest`). Additive lookup — `TryResolve` returns
  null for unknown models without affecting behavior.

### Added — Groq docs

- **Groq recipe in `docs/usage.md`** — Groq is pure OpenAI-compat; documented the exact env-var
  trio (`OPENAI_BASE_URL=https://api.groq.com/openai/v1`, `OPENAI_API_KEY`, `FREEMODEL`). Same
  section gained native-Ollama, Anthropic, and Azure recipes for parity.

### Added — native Ollama provider

- **`OllamaProvider`** — native client for Ollama's `POST /api/chat` endpoint (newline-delimited
  JSON streaming, not SSE). Maps Ollama's tool-call shape (`message.tool_calls[].function`) to
  `StreamChunk.ToolCallDelta` with synthesized ids (`call_0`, `call_1`, …) since `/api/chat`
  doesn't emit ids itself. Usage extracted from `prompt_eval_count` / `eval_count` on `done:true`.
  Malformed JSON lines are skipped (matches the OpenAI/Anthropic adapters' robustness). Optional
  `numCtx` and `temperature` ctor params emit an `options` block; both can be set from the host via
  `FREE_NUM_CTX` and `FREE_TEMPERATURE` env vars.
- **`FREEPROVIDER=ollama`** — selects Ollama via the existing provider switch. No API key required
  (Ollama is unauthenticated by default). `OLLAMA_HOST` defaults to `http://localhost:11434`;
  `OLLAMA_MODEL` or `FREEMODEL` selects the model. `ProviderConfig` learns an `Ollama` section.

### Added — extended thinking + token budgets

- **Anthropic extended thinking** — `AnthropicProvider` now takes `thinkingBudgetTokens` (default
  0 = disabled). When > 0 the request body emits
  `"thinking":{"type":"enabled","budget_tokens":N}` and auto-bumps `max_tokens` to
  `max(maxTokens, budget + 1024)` so callers don't have to remember the API constraint that the
  visible-reply ceiling must exceed the reasoning budget. The provider already routed
  `thinking_delta` SSE events as `StreamChunk.ThinkingDelta` so consumer UIs see the reasoning
  trace separately from the final reply. Negative budgets are clamped to 0.
- **Per-request budget env vars** — `FREE_MAX_TOKENS` overrides the default 4096 ceiling on the
  visible reply; `FREE_THINKING_BUDGET=N` enables extended thinking with that token budget. Both
  Anthropic-only today (OpenAI / Azure don't require `max_tokens` and their thinking control is
  different).

### Added — session forking

- **`/fork`** — snapshots the current transcript to `session-fork-<id>.jsonl` alongside the live
  `session.jsonl`. The clone gets a fresh 8-character id and is persisted via a dedicated
  `JsonlSessionStore` so the running session is never touched. Resume the fork later by moving
  it back: `mv session-fork-<id>.jsonl session.jsonl && freeagent --resume <id>`. Empty sessions
  (no messages yet) refuse to fork.

### Added — workspace file watching

- **`WorkspaceFileWatcher`** — opt-in (`FREE_WATCH_FILES=1`) watcher that surfaces externally
  changed files between turns. Wraps `FileSystemWatcher` with a deduplicated change set,
  noise-directory filtering (`.git` / `node_modules` / `bin` / `obj` / `.vs` / `.idea`), and an
  enlarged internal buffer (64 KB) so heavy bursts don't get dropped. Between turns the host
  drains the watcher and, if any files changed, prepends a notice
  (`[freeagent] Files changed externally since the last turn: …`) to the user's input — capped at
  10 paths with an "…(N more)" summary. Off by default because inotify usage on a large monorepo
  can hit kernel limits; flip it on per session.

### Added — local model runner

- **`ModelServerLauncher` + `/serve`** — spawn / stop / inspect a local OpenAI-compat inference
  server (default: `llama-server`; configurable with `--bin <path>`). `/serve start <model-path>
  [--port N] [--bin <path>] [-- <extra args>]` spawns the binary, records the pid in
  `$XDG_CACHE_HOME/freeagent/model-server.pid`, drains stdout/stderr to a rolling log, and polls
  `/health` for up to 30s. Pre-flight checks: model file exists, port free, binary launchable.
  `/serve stop` kills the recorded pid (entire process tree) and clears the pid file; `/serve
  status` reports running / stale / not-running. Idempotent: a second start while running just
  reports the existing pid instead of stomping it. On success the launcher prints the exact
  `OPENAI_BASE_URL=…/v1 FREEMODEL=… freeagent` line to point a fresh REPL at the server. Tests
  cover the argument parser exhaustively (port validation, unknown flags, extra args after `--`,
  conflicting positionals) plus the "no pid file" status / stop paths; the spawn path itself is
  intentionally exercised by hand against a real `llama-server` rather than mocked. Model
  download/catalog UX remains a follow-up — bring your own GGUF for now.

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
