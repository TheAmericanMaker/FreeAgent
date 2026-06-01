# Next steps — review follow-ups

Actionable backlog distilled from `docs/review-recommendations.md`. Ordered by priority.
Checked items are already done on this branch; unchecked are the work to pick up next.

## Already fixed (this branch)
- [x] Thread-safe `InMemoryToolResultCache` + `InMemoryArtifactStore` (`ConcurrentDictionary`)
- [x] Dispose provider `HttpResponseMessage` (+ Bedrock event-stream) across all 6 adapters
- [x] `find` auto-allowed only without a destructive action (`-delete`/`-exec`/…) + tests
- [x] MCP `tools/call` honors `isError` (maps to an error result) + test
- [x] Grep regex 2s match-timeout (ReDoS guard)
- [x] CI workflow (`.github/workflows/ci.yml`) — build + test on push/PR

## High priority — security (need a small design, then implement)

### 1. Trust gate for project-executable config  ✅ done
A checked-in `.freeagent/config.json` ran code on launch: `SessionStart` hooks via
`bash -c` and MCP/LSP servers auto-spawned, all before any prompt.
- [x] Trust remembered per absolute dir in `$XDG_CONFIG_HOME/freeagent/trusted.json` (`ProjectTrust`).
- [x] On startup, if the config declares hooks/MCP/LSP/allow-grants and the dir is
      untrusted, prompt once (`[y]es once / [a]lways / [N]o`, default No).
- [x] When untrusted: skip hook/MCP/LSP launch **and** skip allow-grants
      (`PermissionConfig.ApplyTo(includeGrants:false)`); deny-rules still apply.
- [x] Escape hatches: `freeagent trust` subcommand, `--trust` flag, `FREEAGENT_TRUST=1`;
      non-TTY fails closed. Tests cover parsing, `DescribeRequests`, the trust round-trip,
      and grant-skipping.
- [ ] Follow-up: re-prompt when a trusted directory's config *changes* (hash-pin), rather
      than trusting the path forever.

### 2. Harden the protocol server defaults (`FreeAgent.Server`)  ✅ mostly done
- [x] Bind to `127.0.0.1` by default (`FREEAGENT_SERVER_URLS` / `ASPNETCORE_URLS` to override).
- [x] Refuse to bind a non-loopback address without `FREEAGENT_SERVER_API_KEY` (`ServerSecurity.BindsBeyondLoopback`).
- [x] Constant-time key comparison (`ServerSecurity.TokenMatches` → `CryptographicOperations.FixedTimeEquals`).
- [x] Cap concurrent sessions (`FREEAGENT_SERVER_MAX_SESSIONS`, default 256 → `429`) and serialize
      turns per session (per-`SessionEntry` `SemaphoreSlim`, second concurrent turn → `409`).
- [ ] Idle session eviction (TTL) — sessions still live until explicit `DELETE`.
- [ ] Add the missing SSE turn tests (framing, flush, cancellation, disconnect) — needs a fake
      provider injected into the test host; `ServerSecurity` + the session-cap path are covered.
- [ ] Make SSE writes async (the runtime's `IEventSink` callbacks are sync, so a slow client can
      still block the agent loop — needs an `IEventSink` shape change).

### 3. Symlink workspace-boundary canonicalization  ✅ done
- [x] `IRealPathResolver` (+ `RealPathResolver`) follows symlinks over the longest existing prefix
      and appends any non-existing remainder lexically.
- [x] `ToolPipeline` canonicalizes the working directory and `FileReadCap`/`FileWriteCap` paths
      before the engine decides, so a symlink inside the workspace pointing outside it (or through a
      protected prefix) is denied. `PermissionEngine` stays pure (ADR 0004); wired in host + sub-agents.
- [x] Tests: real symlink-fixture resolver test + pipeline escape/allow tests with a fake resolver.
- [ ] Follow-up: TOCTOU — the tool still acts on the lexical path after the gate passes; a same-turn
      symlink swap is out of scope (would need open-by-handle / re-check at use).

## Medium priority — correctness
- [ ] SSE adapters: guard `JsonDocument.Parse` per `data:` line so one malformed line
      doesn't abort the turn (match Ollama's try/skip).
- [ ] Compaction: reset `LastInputTokens` after compacting so it can't re-fire every
      turn when the next response reports no usage.
- [ ] Server: serialize concurrent turns per session; make SSE writes async so a slow
      client can't block the agent loop.
- [ ] `ProcessExecCap`: consider matching args, not just the binary, in allow-rules.
- [ ] Providers/`/serve`: require `https://` (or explicit opt-in) for base URLs and
      model-download sources.

## Low priority — polish
- [ ] Crash-atomic writes for `WriteFile` / `ApplyPatch` / `MultiEdit` via
      `IAtomicFileSystem` (temp-write → rename).
- [ ] Doom-loop budget off-by-one vs its "(attempt N of 3)" message.
- [ ] `JsonlSessionStore` in-memory fallback can mask a deleted file on resume.
- [ ] LSP header read is byte-at-a-time; add a write gate like the MCP transport.
- [ ] Cancelled JSON-RPC call leaves a stale `_pending` entry.
- [ ] `ModelServerLauncher` PID-reuse race in `IsAlive`/`Stop`.
- [ ] Parse OpenAI `cached_tokens` usage (advertised in `Usage` but never read).
- [ ] Remove the duplicate stream-complete sentinel in the OpenAI-compat parser.

## Tooling / environment

- [x] **Provision the .NET 10 SDK in the sandbox.** The web sandbox has no `dotnet` and blocks the
      public SDK installers, but the Ubuntu 24.04 archive ships `dotnet-sdk-10.0` (10.0.10x, which
      satisfies `global.json`). Added **`scripts/setup-sdk.sh`** (idempotent apt install) + README/
      CLAUDE.md notes, so a session can `./scripts/setup-sdk.sh` then build/test locally instead of
      pushing blind. _Considered but declined (per maintainer): an auto-run SessionStart hook and a
      `.devcontainer`. Revisit a devcontainer/hook if hands-free provisioning is wanted later._
- [ ] **Server robustness:** `POST /sessions` eagerly constructs the provider, which previously
      500'd when no API key was configured (fixed with a placeholder key in `ProviderFactory`).
      Consider deferring provider creation to the first turn, or validating provider config at
      startup and returning a clear error, rather than relying on the placeholder.

