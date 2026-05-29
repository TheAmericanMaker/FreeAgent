# FreeAgent — Project Review & Recommendations (against `dev`)

_Review date: 2026-05-29. Branch under review: the up-to-date `dev` line (this branch
was re-cut from `origin/dev`). Static review only — no .NET SDK was available, so the
build and the ~500-test suite were not run here; findings are from reading the code.
Subsystems were reviewed in parallel; the highest-severity items below were then
verified firsthand._

## Overall assessment

`dev` has grown enormously beyond the kernel I first saw — four projects now
(`Kernel`, `Host`, `Tests`, **`Server`**), plus MCP/LSP/Roslyn clients, a provider
matrix (OpenAI, Azure, Anthropic, Bedrock, Vertex, Ollama, Groq), sub-agents,
hooks, compaction, an artifact store/result cache, a command palette, playbooks, a
local model runner, and an HTTP+SSE protocol server. The original core
(permission precedence, the 12-step pipeline, doom-loop, atomic persistence,
streaming reassembly) remains clean and well-tested.

The dominant theme of this review: **the new integration surface has outrun the
"safe by construction" discipline of the original kernel.** The kernel was careful
about every tool call; several of the new features execute code or expose the agent
*outside* that gate. The top recommendations are about restoring safe-by-default to
the new surface, plus a couple of real concurrency bugs on the parallel execution
path.

## Critical / High — security (the new surface area)

1. **Opening a cloned repo can execute attacker-controlled code before any prompt.**
   `SessionStart` hooks run `bash -c <command>` taken straight from project
   `.freeagent/config.json` (`Host/Program.cs:98,181` → `BashShellExecutor`), and
   MCP/LSP servers are launched from the same config (`Host/McpServerManager.cs`,
   `Host/LspServerManager.cs`) — all at startup, with no trust gate and bypassing the
   per-call permission engine. A checked-in config is remote code execution on
   `freeagent` launch. **Fix:** gate project-level executable config (hooks, MCP/LSP
   commands) behind an explicit, remembered "trust this directory?" prompt.

2. **The protocol server is open and not loopback-pinned by default.**
   `Server/Program.cs`: the API-key gate exists only when `FREEAGENT_SERVER_API_KEY`
   is set (default = open, line 17); nothing pins the bind to localhost (no Kestrel
   `UseUrls`, so `ASPNETCORE_URLS` can make it `0.0.0.0`, line 37); the key compare is
   not constant-time (line 25); there is no CORS deny and no session cap. The comment
   admits it relies on "a loopback bind" that the code never enforces. Once tools are
   registered on the server this is RCE-by-design. **Fix:** bind loopback by default,
   require auth unless explicitly opted out, constant-time compare, cap sessions.

3. **Symlink workspace escape.** `Tools/Adapters/WorkspacePath.cs:12` and
   `PermissionEngine.IsInsideWorkingDirectory` normalize only lexically
   (`Path.GetFullPath`) and never resolve symlinks. A symlink *inside* the workspace
   pointing at `/etc/shadow` is auto-allowed for read, and a write goes *through* the
   link past the protected-prefix block — the capability is declared on the textual
   path while the OS acts on the link target. The pipeline's step-3 `sanity-check`
   seam is the designated home for real-path canonicalization.

4. **`find` is auto-allowed with destructive arguments.**
   `PermissionEngine.cs:35` lists `find` among unconditionally safe binaries with no
   argument inspection, so `find . -delete` / `find . -exec rm {} \;` runs without
   approval. Restrict it the way `git` is restricted to `status|diff|log`.

## High — correctness (concurrency on the parallel path)

5. **Result cache and artifact store are not thread-safe.**
   `InMemoryToolResultCache` and `InMemoryArtifactStore` wrap a plain `Dictionary`,
   but `TurnExecutor` runs all read-only/concurrency-safe calls through the one shared
   `ToolPipeline` in a parallel window — concurrent `TryGet`/`Set`/`Store` on a
   `Dictionary` is a data race that can corrupt state or throw, on the exact path the
   kernel is designed around. Use `ConcurrentDictionary` (or lock).

6. **Provider HTTP responses are not disposed.** The OpenAI/Azure/Anthropic/Ollama/
   Vertex adapters never `using`-dispose the `HttpResponseMessage` (and the Bedrock
   event-stream response is undisposed), leaking sockets/connections under load.

## Medium

7. **SSE parse errors abort the turn.** The OpenAI-compatible/Anthropic/Vertex
   adapters call `JsonDocument.Parse` on each `data:` line unguarded; one malformed
   line throws out of the async iterator. Ollama's adapter try/catches and skips —
   and its comment claims parity the SSE adapters don't have.
8. **MCP `tools/call` ignores `isError`** (`Mcp/McpClient.cs`): a failing MCP tool is
   reported to the model as `ToolResult.Success`.
9. **Server: concurrent turns on one session race** on the shared runtime/event-sink
   (events bleed between responses), and SSE writes are synchronous so a slow client
   blocks the agent loop / pins a thread.
10. **Compaction can re-fire every turn** — it doesn't reset `LastInputTokens`, so if
    the next response reports no usage the stale over-threshold value re-triggers
    summarization (`Sessions/SessionRuntime.cs` compaction guard).
11. **`ProcessExecCap` matches only the binary, never args** (`Capability.cs:32`): an
    allow-rule for a binary authorizes every argument form.
12. **Grep regex has no match-timeout** (model-supplied pattern) → ReDoS can hang a
    turn between cancellation checks.
13. **Non-`https` URLs accepted** by providers (cleartext credentials / SSRF) and by
    `/serve` model download (downloaded GGUF then handed to a process).

## Low

- Write tools (`WriteFile`, `ApplyPatch`, `MultiEdit`) are not crash-atomic on disk
  despite `IAtomicFileSystem` existing for exactly that ("atomic" in their docstrings
  refers only to all-or-nothing matching).
- Doom-loop budget is off-by-one vs its "(attempt N of 3)" message; `JsonlSessionStore`
  in-memory fallback can mask a deleted file on resume; LSP reads headers a byte at a
  time; a cancelled JSON-RPC call leaves a stale `_pending` entry; `ModelServerLauncher`
  PID-reuse race in `IsAlive`/`Stop`; OpenAI `cached_tokens` usage is advertised but
  never parsed; a duplicate stream-complete sentinel is emitted.

## Gaps (not bugs)

- **No CI workflow** — only `release.yml` (tag-triggered). A push/PR build+test
  workflow is the cheapest high-value addition; I add `.github/workflows/ci.yml` in
  this change.
- **The server registers zero tools and has no SSE turn tests** — the protocol surface
  is functionally incomplete and its event framing/cancellation/disconnect paths are
  untested.

## Suggested sequencing

1. **Trust gate for project-executable config** (item 1) and **server hardening**
   (item 2) — these are the two that turn "open a repo" / "run the server" into code
   execution.
2. **Concurrency fixes** (item 5) and **response disposal** (item 6) — small, contained
   correctness wins on the hot path.
3. **Symlink canonicalization at the `sanity-check` seam** + **`find` arg restriction**
   (items 3–4).
4. **CI workflow** (added here), then the Medium correctness items.
