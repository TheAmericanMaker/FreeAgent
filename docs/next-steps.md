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

### 2. Harden the protocol server defaults (`FreeAgent.Server`)
Today: open unless `FREEAGENT_SERVER_API_KEY` is set, not pinned to loopback,
non-constant-time key compare, no session cap (`Server/Program.cs`).
- [ ] Bind to `127.0.0.1` by default; require an explicit flag/env to expose publicly.
- [ ] Require auth by default (or refuse to bind non-loopback without a key).
- [ ] Constant-time key comparison (`CryptographicOperations.FixedTimeEquals`).
- [ ] Cap concurrent sessions + idle eviction; serialize turns per session.
- [ ] Add the missing SSE turn tests (framing, flush, cancellation, disconnect).

### 3. Symlink workspace-boundary canonicalization
Lexical `Path.GetFullPath` only (`Tools/Adapters/WorkspacePath.cs`,
`PermissionEngine.IsInsideWorkingDirectory`) → a symlink inside the workspace escapes
reads/writes.
- [ ] Resolve real paths at the pipeline `sanity-check` seam via a filesystem
      abstraction (keep `PermissionEngine` pure per ADR 0004).
- [ ] Reject a resolved target outside the workspace / under a protected prefix.
- [ ] Add a symlink-fixture regression test.

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

- [ ] **Ship the dev/CI sandbox with the .NET 10 SDK.** The Claude-Code web sandbox has no
      `dotnet` and blocks the SDK install mirrors, so changes can't be built or tested locally —
      they only get a real compile/test signal from GitHub Actions, which is slow and lossy. Add a
      `.devcontainer` (mcr.microsoft.com/dotnet/sdk:10.0) and/or a SessionStart hook / setup script
      that provisions the SDK, so the agent can run `dotnet build` / `dotnet test` before pushing.
- [ ] **Server robustness:** `POST /sessions` eagerly constructs the provider, which previously
      500'd when no API key was configured (fixed with a placeholder key in `ProviderFactory`).
      Consider deferring provider creation to the first turn, or validating provider config at
      startup and returning a clear error, rather than relying on the placeholder.

