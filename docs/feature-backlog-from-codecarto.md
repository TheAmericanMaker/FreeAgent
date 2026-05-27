# Feature Backlog — Derived from the PicoClaw CodeCartographer Findings

This document mines the PicoClaw reverse-engineering bundle (architecture map, behavioral
contracts, protocols, mechanical + semantic defect scans, and the reimplementation spec)
and turns it into a concrete, prioritized feature backlog **for FreeAgent**, with an
implementation sketch in FreeAgent's own kernel terms for every item.

**Sources** (all under `docs/codecarto/` here, mirrored from the PicoClaw `.codecarto/`):
- `architecture-map.md` — package inventory, public surfaces, runtime lifecycle, porting priorities.
- `reverse-engineering-bundle.md` — Feature Contract Table + the 21-item **Defect Synthesis**.
- `reimplementation-spec.md` — the canonical behavioral contract the kernel already implements.
- `behavioral-contracts.md`, `protocols-and-state.md`, `mechanical-defects.md`, `semantic-defects.md`.

**Method.** PicoClaw is a mature (Go) personal-assistant daemon spanning 35+ packages, 19+
chat channels, and 30+ providers. FreeAgent is the kernel-first C# line: a small,
deterministic core with deliberate extension *seams* and a capability model that already
*names* features no tool exercises yet. So most items below are **"fill a seam / add an
adapter," not "change the core."** Each entry cites the finding it comes from and is tagged:

- **Effort** — S (≤1 day), M (a few days), L (1–2 weeks), XL (multi-week / architectural).
- **Seam** — the existing FreeAgent extension point it attaches to, if any.

> Note on overlap with `ROADMAP.md`: the roadmap is FreeAgent's own running backlog. This
> document is the *findings-grounded* view — it traces each feature back to PicoClaw evidence,
> adds the **Tier A hardening items the roadmap omits** (lessons from PicoClaw's defects), and
> gives a concrete C# implementation sketch per item. Where they overlap, treat this as the
> "why + how"; the roadmap remains the "what's queued."

---

## 0. Where FreeAgent already stands (snapshot)

Confirmed by reading the source, not just the docs. Some README/ROADMAP items are **already
done in code**:

| Capability | PicoClaw equivalent | FreeAgent status |
|---|---|---|
| Turn loop + iteration cap | `pkg/agent` AgentLoop, max-iter 50 | **Done** — `SessionRuntime.RunTurnAsync`, cap 90 |
| Doom-loop / repeat-batch guard | (not in PicoClaw) | **Done** — `DoomLoopDetector`, bounded recovery |
| 12-step tool pipeline w/ seams | `pkg/tools` registry + recovery | **Done** — `ToolPipeline`, 6 labelled no-op seams |
| Parallel/serial batch contract | per-session workers | **Done** — `TurnExecutor` (read-only+safe ⇒ parallel) |
| Capability permission engine | channel-trust + exec deny/allow regex | **Done & stronger** — `PermissionEngine` (7 caps) |
| Atomic crash-safe persistence | JSONL + atomic meta write | **Done** — `JsonlSessionStore` + `LinuxAtomicFileSystem` |
| OpenAI-compatible streaming | provider factory | **Done** — `OpenAIProvider` (SSE, tool calls, usage) |
| Read/Write/Exec tools | fs/shell tools | **Done** — `ReadFileTool`, `WriteFileTool`, `ProcessExecTool` |
| Glob / Grep discovery | fs tools | **Done** — `GlobTool`, `GrepTool` (read-only, parallel) |
| Plan mode | (Claude-style) | **Done** — guard + `EnterPlanMode`/`ExitPlanMode` + `/plan` |
| Permission rules from config | `config.json` | **Done** — `PermissionConfig` (`.freeagent/config.json`) |
| Tool `Description` to provider | tool schema | **Done** — `ITool.Description` / `ToolDefinition.Description` |
| Session resume | session reload | **Partial** — `JsonlSessionStore.LoadAsync` exists; host has no `--resume` |
| **System-prompt assembly** | agent system prompts + memory | **Missing** — kernel sends raw messages, no system message |
| **Context/token management** | `pkg/seahorse` + session compaction | **Missing** |
| **Steering / queued input** | steering messages, subturns | **Missing** (single-shot turns) |
| **Provider retry / fallback** | `FallbackChain` + backoff | **Missing** (provider called directly) |
| **Memory / sub-agents / network / VCS tools** | tools + skills | **Missing** — capabilities modeled, no adapters |
| **Channels / bus / gateway daemon** | `pkg/bus`, `pkg/gateway`, `pkg/channels` | **Missing** (interactive CLI only) |

The gaps in **bold** are where the highest-value work lives.

---

## Tier A — Hardening baked in from PicoClaw's Defect Synthesis

The single most valuable thing the carto work gives a reimplementation is a list of bugs the
original shipped. PicoClaw's 21-item Defect Synthesis splits into *fix before porting* / *port
differently* / *leave behind*. Several are **already structurally prevented** by FreeAgent's
design — those should be guarded by a test so they never regress. The rest are small,
concrete hardening tasks worth doing **before** the surface area grows.

### A1. Bound all tool-execution output buffers — **S** · *fix (D1.1)*
PicoClaw's synchronous shell exec used an unbounded `bytes.Buffer` (background sessions were
capped but the sync path was not), a memory-exhaustion vector.
**How:** in `ProcessExecTool`, cap captured stdout/stderr at a configurable `MaxOutputBytes`
(e.g. 1 MiB each), stop reading past it, and append a truncation marker
(`…[output truncated, N bytes dropped]`). Add a test feeding a high-volume command. The same
ceiling should apply to any future tool that buffers child output.

### A2. Provider retry: exponential backoff + jitter, drain-before-close — **M** · *fix (D1.2, D3.2)*
PicoClaw's retry was linear and (in the semantic scan) closed retryable HTTP bodies without
draining, risking transport/goroutine exhaustion.
**How:** add a `RetryingProvider` decorator implementing `IProvider`, wrapping the inner
provider's `StreamChatAsync`. Retry on timeout / 429 / 5xx with `delay = base * 2^attempt ±
jitter`, capped attempts (3) and a max delay. In .NET the body-drain hazard maps to **always
disposing the `HttpResponseMessage`/stream** even on a retry path — make that a `finally`, and
honor `Retry-After` when present. Compose in `Program.cs`:
`new RetryingProvider(new OpenAIProvider(...))`.

### A3. Network egress is default-deny — **already solved; lock it with a test** · *fix (D4.1, D6.1)*
PicoClaw's worst defect: `AllowRemote: true` defaulted **fail-open**, and the runtime guard
relied on a hardcoded internal-channel list that new channels silently bypassed.
**FreeAgent already prevents this**: `NetworkEgressCap` is *never* auto-allowed and requires an
explicit allow rule (`PermissionEngine` precedence). When network tools land (B7), there is no
"allowRemote" flag to get wrong — egress is a capability, denied unless a rule grants a
specific host glob. **Action:** add a regression test asserting a `NetworkEgressCap("*")` is
denied with no rule, and document this as the intentional answer to PicoClaw's GHSA.

### A4. Upper-bound every iteration/limit config at load time — **S** · *fix (D6.4)*
PicoClaw accepted any positive `MaxToolIterations` with no ceiling.
**How:** `MaxIterations` is currently a hard `const 90` (good). When iteration / tool-call /
recovery limits become configurable, validate them at parse time (reject `> 1000`, `<= 0`) the
same way `PermissionConfig.Validate()` rejects unknown capabilities — fail the config, don't
clamp silently.

### A5. Surface persistence failures; keep restore atomic — **S** · *fix (D2.1, D5.3)*
PicoClaw logged-and-swallowed every `memory.Store` write/compaction error (callers got no
signal), and a hard-abort restore mutated memory *then* saved, leaving disk mutated if save
failed.
**FreeAgent already** writes through `write-temp → fsync → rename → fsync-dir` (D5.3 solved)
and `SaveAsync` is awaited so exceptions propagate (D2.1 solved by construction). **Action:**
keep it that way — never wrap `SaveAsync` in a swallow; if a background/daemon mode buffers
saves later, expose a health/last-error signal rather than logging into the void. Guard with a
test that a failing `IAtomicFileSystem` surfaces, not hides.

### A6. Fault isolation at every future concurrency boundary — **S now, revisit per-surface** · *fix (D2.2, D3.1, D3.3)*
PicoClaw crashed the whole gateway when a single channel worker / dispatcher / TTL-janitor
goroutine panicked without `recover()`.
**FreeAgent already** maps tool exceptions to `Crash` and cancels siblings ("sibling abort")
inside `TurnExecutor`/`ToolPipeline` — no exception escapes. **Action:** make this a *standing
rule*: any new long-lived loop (the gateway/bus/channel workers in Tier D, hook execution in
B2) must wrap its body in a try/catch that isolates one failure from the process, mirroring
the pipeline's contract. Add it to `CLAUDE.md` "Adding things."

### A7. Secret separation + never-log-secrets discipline — **S** · *fix (D4.2, D4.3)*
PicoClaw leaked a Pico auth token via URL query strings (into logs/history) and stored a
reload-auth token in a world-readable PID file.
**How:** today the only secret is `OPENAI_API_KEY` (env, not logged). Bake the discipline in
*before* a config/credential store exists (Tier C cross-cutting): keep secrets in a separate
`secrets.json`/keyring with `0600` perms, never in the main config, never in the transcript or
event stream, and never in a URL. Add a redaction pass to any logging/event sink.

### A8. Default-deny origins / no query-string auth for any future network surface — **design rule** · *fix (D4.4, D4.2)*
PicoClaw's WebSocket upgrader allowed any origin when the allowlist was empty.
**How:** purely forward-looking — when a WebSocket/HTTP surface or web launcher (Tier D3)
arrives, default to an **empty allowlist = deny**, require an explicit origin allowlist, accept
auth only via headers (never query params), and bind to loopback unless explicitly opened.
Record as a non-negotiable in the surface's design doc.

---

## Tier B — Fill the kernel seams already modeled

These are the highest-leverage features: the pipeline has six labelled no-op seams and the
permission engine names four capabilities that no tool uses yet. Each item here is "implement
the seam / add the adapter."

### B1. Read-only result cache — **M** · seam: `cache-lookup` / `cache-write` / `invalidate`
*Source: PicoClaw tool registry; FreeAgent pipeline steps 6/11/12.*
**How:** introduce `IToolResultCache` with a key of `(toolName, canonicalArgsJson,
workingDir)`. In step 6, on a read-only tool, return a cached `Success` and short-circuit the
execute step. In step 11, store read-only `Success` results. In step 12, after a mutating tool
runs, invalidate entries whose key paths intersect the write (start coarse: clear all on any
`FileWriteCap`/`VcsMutationCap`). Bound the cache (LRU + TTL). The seams already log their
place, so wiring is additive and testable via `StepLog`.

### B2. Pre/Post hooks — **M** · seam: `pre-hook` / `post-hook`
*Source: roadmap; PicoClaw lacked first-class hooks. FreeAgent pipeline steps 7/9.*
**How:** define `IToolHook { ValueTask<HookOutcome> OnPreAsync(...); ValueTask OnPostAsync(...) }`
with matchers on tool name / argument substring. Pre-hooks are *non-fatal advisory* by default
but **may** veto (mapping to `PermissionDenied`/a new `HookBlocked` kind) — decide explicitly.
Post-hooks observe but don't mutate the result (preserve the current contract). Back them with
`SessionStart` / `PreToolUse` / `PostToolUse` shell scripts à la Claude Code. Per A6, run each
hook inside a fault boundary so a bad hook can't crash the turn.

### B3. Large-artifact offload — **M** · seam: `artifact-store`
*Source: roadmap; pipeline step 10.*
**How:** `IArtifactStore.Put(content) → ArtifactRef`. In step 10, when a `Success` content
exceeds a threshold (e.g. 8 KB), persist the full payload (atomic write, like sessions) and
replace the model-facing content with a preview + an `artifact://<id>` reference and a
`ReadArtifact` tool to page through it. Keeps long tool outputs from blowing the context
window and dovetails with B1 (cache the ref).

### B4. Real `sanity-check` (path-escape / workspace boundary) — **S** · seam: `sanity-check`
*Source: PicoClaw exec security relied on regex only (D4.5); FreeAgent pipeline step 3.*
**How:** today path resolution lives in `WorkspacePath.Resolve` and the permission engine.
Promote an explicit step-3 check that rejects symlink/`..` escapes *before* permission, so the
"checked path == acted path" invariant is enforced structurally rather than per-tool. Cheap,
and it closes the class of bug PicoClaw mitigated only with deny/allow regexes.

### B5. Cross-session memory — **M** · capability: `MemoryCap` (modeled, unused)
*Source: PicoClaw `pkg/memory` (JSONL); FreeAgent capability already exists.*
**How:** add `IMemoryStore` (JSONL, atomic writes, namespaced) and a `Memory` tool exposing
`read`/`write`/`list`. `RequiredCapabilities` returns `MemoryCap(ns, "read")` (auto-allowed
today) or `MemoryCap(ns, "write")` (needs a rule). Surface memory contents in the system prompt
(C3). This is the first consumer of an otherwise dormant capability — small and self-contained.

### B6. Sub-agents — **L** · capability: `AgentSpawnCap` (modeled, unused)
*Source: PicoClaw subturns / multi-agent dispatch; roadmap Explore/Plan/Coder/Verify.*
**How:** a `SpawnAgent` tool whose `RequiredCapabilities` returns `AgentSpawnCap(role,
summary)` (never auto-allowed → needs a rule). On execute, build a **child** `SessionRuntime`
with its own `SessionState`, a *restricted* `ToolRegistry` (e.g. Explore = read-only tools
only), and a depth/iteration budget; run a one-shot turn; return its final text as the tool
result. Reuse the doom-loop and iteration caps. Guard recursion depth (a child can't spawn
without budget). This is the natural home for the parallel-execution contract the kernel
already has.

### B7. Network tools: WebFetch / WebSearch — **M** · capability: `NetworkEgressCap` (modeled, unused)
*Source: PicoClaw web-search tool; roadmap.*
**How:** `WebFetchTool(url)` → `NetworkEgressCap(host, 443, "https")`; `WebSearchTool(query)` →
egress to the search host. Both deny unless an allow rule grants the host glob (see A3). Mark
`IsReadOnly = true, IsConcurrencySafe = true` so they parallelize. Stream/cap response size
(reuse A1/B3). A `web` permission preset in `.freeagent/config.json` lets users opt in to
specific hosts.

### B8. VCS tools — **M** · capability: `VcsMutationCap` (modeled, unused)
*Source: PicoClaw shell-driven git; FreeAgent auto-allows `git status|diff|log` already.*
**How:** dedicated `GitCommit` / `GitBranch` / `GitPush` tools returning
`VcsMutationCap(repo, op)` (never auto-allowed). This separates *mutating* git from the
read-only git already auto-allowed via `ProcessExecCap`, giving users a clean `allow:
VcsMutationCap` rule instead of opening arbitrary `git` exec. Pairs with the `/commit` slash
command (C10).

---

## Tier C — Core agent semantics ported from PicoClaw

These bring FreeAgent's *behavior* up to PicoClaw parity. They touch the runtime but mostly
extend it rather than reshape it.

### C1. System-prompt assembly — **M** · *highest-value gap*
*Source: PicoClaw per-agent system prompts; FreeAgent currently sends no system message.*
`RunTurnAsync` builds `ProviderRequest` straight from `_state.Messages` with **no system
prompt** — the model gets tools but no instructions, working-dir context, or project
conventions.
**How:** add a `SystemPromptBuilder` that composes, once per turn (or on first turn): base
agent instructions + a project file if present (`FREEAGENT.md` / `CLAUDE.md`) + working dir +
`git status`/branch + cross-session memory (B5). Prepend as a `MessageRole.System` message (add
the role if absent) or pass via `ProviderRequest`. Biggest single quality lever here, and it
unlocks B5/C7.

### C2. Context-window management & compaction — **L** · seam: relates to `artifact-store`, persistence
*Source: PicoClaw `pkg/seahorse` (SQLite FTS5) + session compaction via `skip`; FreeAgent has none.*
**How:** (1) **Track tokens** — `OpenAIProvider` already emits `Usage`; accumulate per session
and expose it. (2) **Checkpoint/compact** — when usage nears a configurable budget, summarize
the oldest messages into a synthetic summary message and mark the originals "skipped" (mirror
PicoClaw's `skip`/`.meta.json` so the JSONL transcript stays whole on disk but the *prompt* is
trimmed). (3) **Optional search** — PicoClaw used SQLite FTS5 with a `trigram` tokenizer and a
graceful `LIKE` fallback; in .NET, an embedded SQLite FTS5 (or a simple in-memory index) gives
"recall older context" without the CJK-tokenizer complexity. Start with summary-compaction;
add search later.

### C3. Provider fallback chain — **M** · seam: `IProvider` decorator
*Source: PicoClaw `FallbackChain`.*
**How:** `FallbackProvider(IReadOnlyList<IProvider>)` that tries each in order on
hard-failure (after A2's per-provider retry is exhausted), streaming from the first that
yields. Compose with `RetryingProvider` (A2). Config: an ordered model/provider list.

### C4. Model routing classifier — **M** · seam: provider selection
*Source: PicoClaw `pkg/routing` (rule-based light/heavy, threshold 0.35).*
**How:** an `IModelRouter.Select(turnContext) → IProvider` consulted at the top of
`RunTurnAsync`. Start rule-based (PicoClaw's features: has-attachments gate, input-token count,
code-fence presence, conversation depth) selecting a "light" vs "heavy" provider; pin the
choice for the whole turn. Pure function → trivially testable. Cost-saver, optional.

### C5. More providers — **M each** · seam: `IProvider`
*Source: PicoClaw provider factory (30+).*
**How:** implement `IProvider.StreamChatAsync` for **native Anthropic Messages API** first
(reasoning deltas + tool-use blocks differ from OpenAI's SSE shape), then Azure OpenAI,
Bedrock, Vertex/Gemini, Groq. Each mirrors `OpenAIProvider`'s per-id tool-call accumulation and
usage extraction. Keep the OpenAI-compatible path as the default; document Ollama/llama.cpp/vLLM
recipes (they already work via `OPENAI_BASE_URL`).

### C6. Steering, turn abort hierarchy, subturns — **L** · runtime
*Source: PicoClaw "one active turn per session; duplicates enqueue as steering"; abort = graceful/hook/hard.*
Mostly latent until a daemon/bus (Tier D) allows concurrent input, but the **abort hierarchy**
is useful even in the CLI: today Ctrl+C is a *hard* abort (cancels mid-stream). Add **graceful**
("finish the current tool, then stop") and a **hook** abort path. **How:** give `SessionState` a
thread-safe steering queue; at the top of each loop iteration, drain queued user messages and
inject them before the next provider call (PicoClaw's contract). Subturns are B6 (sub-agents)
viewed from the parent.

### C7. Skills loader — **L** · seam: tool registry + system prompt
*Source: PicoClaw `pkg/skills` (Markdown `SKILL.md`, ClawHub/GitHub registry, TTL-promoted tools).*
**How:** load `SKILL.md` files from a workspace `skills/` dir; parse front-matter (name, when
to use, allowed tools) + body (injected as guidance). Register each as either a prompt
fragment surfaced in C3, or a synthetic tool. PicoClaw's "hidden tools promoted with a TTL that
decays per turn" maps to a registry that exposes a skill's tools only after it's invoked and
hides them again after N idle turns — a registry feature, not a core change. (The MicroPython
spec dropped the Markdown parser for JSON; in C# a Markdown front-matter parser is cheap, so
keep `SKILL.md`.)

### C8. MCP client — **L** · seam: tool registry + capabilities
*Source: PicoClaw `pkg/mcp` (stdio JSON-RPC / SSE / HTTP, tool discovery, transparent reconnect).*
**How:** an `McpManager` that launches/connects servers, runs the initialize handshake,
discovers tools, and registers each as an `ITool` named `mcp__<server>__<tool>` whose
`RequiredCapabilities` reflect the transport — **stdio ⇒ `ProcessExecCap`** (spawning the
server binary) and **HTTP/SSE ⇒ `NetworkEgressCap`** — so MCP tools flow through the *same*
permission engine as everything else (a real advantage over PicoClaw, where MCP stdio relied on
optional Linux-namespace isolation). Preserve newline-delimited JSON-RPC framing and reconnect
on session-missing. .NET has an official MCP SDK to build on.

### C9. Cron / scheduler + heartbeat — **L** · requires daemon (Tier D1)
*Source: PicoClaw `pkg/cron` (JSON job store) + `pkg/heartbeat`.*
**How:** in a gateway/daemon mode, a `CronService` reads a JSON job store and injects scheduled
user messages into the bus (D1) at their due time; a `HeartbeatService` periodically pings a
default session. Validate schedules and bound exec time (PicoClaw used a 5-min cap). Pure-CLI
FreeAgent doesn't need this; it's the daemon story's payoff.

### C10. Slash-command expansion — **S–M** · seam: `HandleCommand` in `Program.cs`
*Source: PicoClaw `pkg/commands` (`/clear`, `/stop`, `/switch`, `/btw`).*
**How:** the host already routes `/`-prefixed input to a `switch`; add `/help`, `/status`
(session id, tokens, model, plan mode), `/model <name>` (swap provider), `/compact` (trigger
C2), `/commit` (B8), `/review`, `/resume` (C11), `/undo` (C12), `/clear`. Consider promoting
command dispatch into the kernel so non-CLI surfaces share it.

### C11. Session resume — **S** · seam: host flag
*Source: PicoClaw session reload; `JsonlSessionStore.LoadAsync` already exists.*
**How:** add `--resume <id>` (and `--session <path>`) to `Program.cs`: call `LoadAsync`,
rebuild `SessionState.Messages`, continue. The store already round-trips; this is host wiring +
a session directory/index so ids are discoverable (`/resume` lists recent).

### C12. File history & undo — **M** · seam: `post-hook` / `artifact-store`
*Source: roadmap; PicoClaw fs tools.*
**How:** before each `WriteFile`/edit, snapshot the prior file content (atomic write to a
`.freeagent/history/<turn>/` dir). Add `/undo` (revert last write) and session-revert-to-turn.
Implementable as a post-hook (B2) so the core stays untouched.

### C13. Richer editing tools — **M** · seam: new tools
*Source: PicoClaw fs tools; roadmap (MultiEdit, ApplyPatch, diff view).*
**How:** `EditTool` (find/replace with uniqueness check), `MultiEditTool` (atomic batch over
one file), `ApplyPatchTool` (unified diff). All return `FileWriteCap` (never auto-allowed). Add
a colored diff render in `ConsoleEventSink`. These materially improve code-editing UX over
whole-file `WriteFile`.

---

## Tier D — Surfaces, integrations & the daemon story

PicoClaw's reach (19+ channels, web launcher, voice, devices) all sits on one prerequisite: a
long-running daemon with an async message bus. FreeAgent is interactive-CLI today. This tier is
**XL and optional** — pursue it only if FreeAgent's product direction wants the
assistant-daemon shape rather than the dev-tool-CLI shape.

### D1. MessageBus + gateway daemon — **XL** · *architectural prerequisite*
*Source: PicoClaw `pkg/bus` (buffered channels) + `pkg/gateway` (lifecycle) + per-session AgentLoop.*
**How:** introduce `IMessageBus` (bounded async queues for inbound/outbound, à la
`System.Threading.Channels`), a `GatewayHost` that owns config load, provider creation, service
startup, signal handling, and **graceful drain** — and explicitly decide PicoClaw's lossy
drain (D5.2): either block-until-empty or document the loss. An `AgentLoop` reads inbound,
claims a per-session lock (PicoClaw used `sync.Map.LoadOrStore`; in C# a
`ConcurrentDictionary` + per-key gate), and runs `SessionRuntime` per session with bounded
concurrency (a `SemaphoreSlim`). Every worker wrapped per A6. This is what makes channels,
cron, and heartbeat possible.

### D2. Channel adapters — **L each, after D1** · seam: bus
*Source: PicoClaw `pkg/channels/*`; porting priority names Telegram + Discord + WebSocket as the dominant set.*
**How:** `IChannel` adapters that normalize platform messages to/from bus envelopes;
`pkg/agent` never sees a concrete channel (PicoClaw's clean port boundary — preserve it).
Start with **Telegram** (simple long-poll `getUpdates`, the MicroPython spec's v0 choice), then
**Discord**, then a WebSocket surface. Per-channel rate limiting (`token-bucket`) and
outbound text/media workers. Webhook channels (LINE/Slack/Weixin) need HMAC signature
verification (protocols doc) — defer.

### D3. Web launcher / management UI — **XL** · optional
*Source: PicoClaw `web/backend` + React SPA, gateway control, SQLite+bcrypt dashboard auth.*
**How:** an ASP.NET Core host serving config/status/chat over HTTP + WebSocket, embedding a
static SPA. **Apply A7/A8**: loopback-bind by default, default-deny origins, header-only auth,
secrets out of the transcript. The CLI + gateway must keep working without it (PicoClaw's
launcher is pure convenience).

### D4. Voice / audio, devices, vision, evolution — **defer**
*Source: PicoClaw `pkg/audio` (pion WebRTC), `pkg/devices` (udev), vision input, `pkg/evolution`.*
- **Vision / multimodal input** — **M**, do this one earlier than the rest: extend `Message`
  and the provider request to carry image parts; most OpenAI-compatible endpoints accept them.
- **Audio (ASR/TTS)** — XL, resource-heavy; defer (PicoClaw marks it optional).
- **Device service (USB/udev)** — niche embedded workflow; defer.
- **Evolution engine** — PicoClaw ships it off-by-default with a "do not deploy before v1.0"
  warning (arch-OQ3); treat as research, not backlog.

---

## Cross-cutting concerns

### X1. Configuration system — **M**
*Source: PicoClaw `config.json` schema v3 + `caarlos0/env` overrides + `.security.yml` secrets.*
FreeAgent reads three env vars + an optional permission config. As features land, grow a single
typed config (model/provider list, retry/cache/compaction budgets, hook definitions, channel
creds) with **env override** and **secrets in a separate file** (A7). Keep `PermissionConfig`'s
strict-validate-on-load posture (A4).

### X2. Observability / event bus — **M** · seam: `IEventSink`
*Source: PicoClaw `pkg/events` pub/sub.*
`IEventSink` already abstracts output. Add a richer event envelope (kind/severity/payload) and
an opt-in **OpenTelemetry** sink (traces for turns/tool calls, token-usage metrics). Redact
secrets per A7.

### X3. Build & packaging — **M**
*Source: PicoClaw multi-arch cross-compile (riscv64/loong64/mips), single static binary, Docker.*
.NET's analog: `dotnet publish` with **NativeAOT** or single-file + trimming for a small static
binary, `-r linux-arm64`/`linux-x64` RIDs for multi-arch, and a minimal container image.
FreeAgent is Linux-native-first (ADR 0003), so AOT-friendliness is worth protecting as adapters
are added (avoid heavy reflection in hot paths).

### X4. Full-screen TUI — **L** · seam: `IEventSink`
*Source: roadmap.* A `Spectre.Console`-based renderer alongside `ConsoleEventSink`, swapped at
composition — no kernel change, since the kernel only knows `IEventSink`.

---

## Suggested sequencing

A pragmatic order that front-loads low-cost wins and respects dependencies:

1. **Tier A hardening** (A1, A2, A4, A7) + regression tests for the already-solved ones
   (A3, A5, A6). Small, and they set the safety posture before surface area grows.
2. **C1 system-prompt assembly** — the biggest quality jump for the least code.
3. **B5 memory**, **B7 network tools**, **B8 VCS tools** — activate the three dormant
   capabilities; each is self-contained and demonstrates the capability model end-to-end.
4. **C11 session resume**, **C10 slash commands**, **C13 editing tools** — host/UX polish.
5. **B1 cache**, **B2 hooks**, **B3 artifacts**, **B4 sanity-check** — finish the pipeline seams.
6. **C2 context management** + **A2/C3 retry+fallback** + **C5 more providers** — robustness at scale.
7. **B6 sub-agents**, **C7 skills**, **C8 MCP** — the extensibility headline features.
8. **Tier D** (D1 bus/daemon → D2 channels, D4 vision early) — only if pursuing the
   assistant-daemon product shape.

---

*Generated from the CodeCartographer findings on 2026-05-27. Every feature traces to a cited
finding; "already solved" items reflect reading the FreeAgent kernel source, not just its docs.*
