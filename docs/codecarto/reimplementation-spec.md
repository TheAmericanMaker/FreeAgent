---
phase: reimplementation-spec
variant: language-agnostic
pipeline: workflow/pipeline-full-with-deep-audit.yaml
supersedes: ".codecarto.backup-full-5phase-20260524T211816Z/findings/reimplementation-spec/reimplementation-spec.md (5-phase, no defect scan)"
closes: port-CF1, port-CF2, port-CF3
sources:
  - .codecarto/findings/porting/reverse-engineering-bundle.md
  - .codecarto/findings/protocols/protocols-and-state.md
  - .codecarto/findings/contracts/behavioral-contracts.md
  - .codecarto/findings/architecture/architecture-map.md
  - .codecarto/findings/defect-scan-mechanical/mechanical-defects.md
  - .codecarto/findings/defect-scan-semantic/semantic-defects.md
inspiration_only_not_behavior:
  - /home/jamessesler/Documents/Github/pi-mono/README.md
  - /home/jamessesler/Documents/Github/pi-mono/packages/coding-agent/README.md
date: 2026-05-24
# ─────────────────────────────────────────────────────────────────────────────
# USER-APPROVED STRATEGIC ASSUMPTIONS (Strategic Alignment Hook, GUIDE.md §before synthesis)
# This spec stays language-agnostic per the default template, but it is written
# against the following pre-locked assumptions confirmed by the user in chat.
# ─────────────────────────────────────────────────────────────────────────────
assumptions:
  A1_target_platform: >
    Linux-native first. The reference deployment is a Linux workstation. POSIX
    file semantics (rename(2) atomicity, fsync, process groups, /bin/bash) may be
    assumed for the MVP; Windows/macOS portability is deferred, not designed-out.
  A2_architecture_style: >
    Modular and extensible in the style of /home/jamessesler/Documents/Github/pi-mono:
    a SMALL CORE (kernel) with stable PORT seams, and plugin / extension / package
    surfaces around it. Multiple run modes (interactive, print/JSON, RPC, SDK).
    SDK/RPC-friendly seams are first-class. Features that OpenMono hardwires into
    its core — sub-agents, MCP, LSP, plan mode, hooks, playbooks, memory,
    background bash — are reimplemented as EXTENSIONS/ADAPTERS registered through
    the kernel's extension points, not baked into core, wherever feasible.
  A3_language_neutral: >
    Do NOT lock the primary implementation language yet. The spec is stack-neutral.
    The architecture must be ready for any of three plausible paths without rework
    of the kernel contracts: (a) a clean Go rewrite, (b) a TypeScript/pi-like
    implementation, (c) a modular .NET refactor of the existing C#. Port seams are
    expressed as language-neutral interfaces; concrete primitives (async stream,
    cancellation) are named per-language in the hazard tables, not in the kernel.
  A4_build_order: >
    Least-error-prone path is KERNEL-FIRST: build the kernel against a fake LLM
    provider and fake tools, get the black-box acceptance suite green, and only
    then add real provider adapters, persistence, renderers, and extensions.
    Fakes precede UI and I/O adapters.
---

# Reimplementation Spec — OpenMonoAgent.ai (Deep-Audit Revision)

> **Revision note.** This document supersedes the earlier 5-phase reimplementation spec
> (archived under `.codecarto.backup-full-5phase-…`). It is regenerated from the
> `full-with-deep-audit` pipeline, so it folds in the mechanical (14 findings) and
> semantic (13 findings) defect scans as **Do-Not-Clone rules** (§Do-Not-Clone Defect Rules),
> and it re-frames the architecture around the user-approved assumptions in the front
> matter: Linux-native first, a pi-mono-style small-kernel-plus-extensions design,
> stack-neutral kernel contracts, and a kernel-first build order. Where the 5-phase spec
> still holds, its content is carried forward and cited; where the deep audit changes a
> recommendation, the change is called out.

Evidence tags used throughout: *observed fact* (read from source/docs), *strong inference*
(concluded from multiple facts), *portability hazard*, *design decision* (a choice this
spec makes for the reimplementation, not a property of the original).

---

## System Summary

OpenMonoAgent.ai is a self-hosted, local-first AI coding agent for developers who want full
control over inference cost and data privacy. A user types into a terminal; the agent streams
tokens from a local or cloud LLM, dispatches the model's tool calls through a 12-step
permission-and-execution pipeline, detects and aborts runaway loops, manages context-window
pressure by checkpointing and compaction, and persists the whole conversation as JSONL on the
host filesystem. The original ships as a .NET 10 CLI plus a bundled llama.cpp server, both in
Docker, sandboxed to a bind-mounted workspace. *observed fact — README.md, docs/ARCHITECTURE.md,
reverse-engineering-bundle.md §System Summary.*

**This reimplementation is scoped by four user-approved assumptions (see front matter):**

1. **Linux-native first (A1).** The reference target is a Linux workstation. The MVP may assume
   POSIX semantics — atomic `rename(2)`, `fsync`, process groups, a `/bin/bash` (or `/bin/sh`)
   shell. Cross-platform support is deferred but not architecturally precluded.
2. **pi-mono-style modular/extensible architecture (A2).** Instead of OpenMono's single
   high-fan-in `ConversationLoop` that directly constructs 15+ collaborators
   (*observed fact — architecture-map.md §Layer Map, `ConversationLoop.cs:39-72`*), this spec
   defines a **small kernel** that depends only on a handful of **port** interfaces, with
   everything else — providers, persistence, renderers, and the optional capabilities
   (MCP, LSP, hooks, sub-agents, plan mode, playbooks, memory, background bash) — attached as
   **adapters and extensions** through registration seams. The agent runs in multiple **modes**
   (interactive, print/JSON, RPC over LF-delimited JSONL, embedded SDK), mirroring pi-mono's
   "small core, do the rest with extensions/packages, four run modes" philosophy.
   *design decision; inspiration: pi-mono README §Philosophy, coding-agent README §Programmatic
   Usage, §Extensions — used for shape only, not behavior.*
3. **Stack-neutral kernel (A3).** Kernel contracts are expressed as language-neutral ports so the
   same design can land as a Go rewrite, a TypeScript/pi-like implementation, or a modular .NET
   refactor. Concrete async/cancellation primitives are named per-language in the hazard tables,
   never inside the kernel contract.
4. **Kernel-first build order (A4).** The kernel is built and proven against a **fake provider**
   (scripted `StreamChunk` sequences) and **fake tools** (deterministic results), with the
   black-box acceptance suite green, **before** any real LLM adapter, filesystem persistence,
   renderer, or extension is wired in. Fakes precede UI and I/O.

A reimplementation that reproduces the kernel (agentic loop, 12-step pipeline, permission engine,
doom-loop detection, context-pressure management) and one OpenAI-compatible provider adapter —
and that fixes the crash-safety / process-lifecycle / trust-boundary defects the deep audit
found rather than cloning them — is functionally equivalent on the critical path. The full-screen
TUI, MCP, LSP, hooks, sub-agents, playbooks, memory, and background bash are deliberately *not*
in the MVP (§Deliberate Non-Goals); they are added later as extensions. *strong inference —
reverse-engineering-bundle.md §Layer Map, §Defect Synthesis.*

---

## Architecture Overview (pi-mono-style layering)

The reimplementation is organized as concentric rings. Dependencies point inward only; the kernel
knows nothing about any adapter, extension, or delivery surface.

```
         ┌──────────────────────────────────────────────────────────────┐
         │  Delivery surfaces / MODES                                     │
         │  interactive REPL · classic scroll · full-screen TUI ·         │
         │  print · JSON · RPC (LF-JSONL) · embedded SDK · product shell  │
         └───────────────▲──────────────────────────────▲────────────────┘
                         │  EventSink / InputSource         │ SDK calls
         ┌───────────────┴──────────────────────────────────────────────┐
         │  EXTENSIONS (opt-in, registered, NOT in core)                  │
         │  MCP · LSP · hooks · sub-agents · plan mode · playbooks ·      │
         │  memory · background-bash · secret-scan · turn-journal         │
         └───────────────▲──────────────────────────────────────────────┘
                         │  ToolPort, ExtensionPoints, EventSink
         ┌───────────────┴──────────────────────────────────────────────┐
         │  ADAPTERS (concrete port implementations)                      │
         │  OpenAI-compat provider · Anthropic provider · FS session      │
         │  store (atomic) · subprocess runner · tokenizer · config loader │
         └───────────────▲──────────────────────────────────────────────┘
                         │  ProviderPort, PersistencePort, ClockPort, FsPort, ProcessPort
         ┌───────────────┴──────────────────────────────────────────────┐
         │  KERNEL (core semantics — stack-neutral, fully testable)       │
         │  Session Runtime (agentic loop, doom-loop, context pressure) · │
         │  Tool Pipeline (12 steps) · Permission Engine · Turn Executor  │
         │  (concurrency contract) · Session State model                  │
         └────────────────────────────────────────────────────────────────┘
```

The seams between rings are the **ports** in the next section. Each ring is independently
buildable and testable. The kernel ring is the only ring that must survive the port unchanged.
*design decision; inspiration: pi-mono package split — pi-agent-core (runtime), pi-ai (provider
API), pi-tui (renderer), pi-coding-agent (CLI) — README §All Packages.*

---

## Conceptual Module Model

Modules are grouped by ring. Each carries the standard six fields. Names are conceptual, not
source identifiers; do not mirror C# class names where a clearer name exists (SKILL guidance).

### Ring 0 — Kernel Ports (the stable seams)

The kernel depends only on these abstract ports. They are the SDK/RPC-friendly seams (A2) and the
substitution points for the fake provider/tools used in kernel-first development (A4).

| Field | Value |
|---|---|
| **Responsibility** | Define the language-neutral interfaces the kernel calls outward through, so the kernel never references a concrete provider, filesystem, terminal, or clock |
| **Public inputs** | n/a (these are interface definitions) |
| **Public outputs** | `ProviderPort.streamChat(messages, tools, params, cancel) → AsyncStream<StreamChunk>`; `ToolPort{ name, inputSchema, isReadOnly, isConcurrencySafe, requiredCapabilities(input), execute(input, ctx) → ToolResult }`; `PersistencePort{ save(SessionState), load(id), updateIndex(...), listSessions(workdir) }`; `EventSink{ onThinking, onText, onToolStart, onToolResult, onUsage, onStatus }`; `InputSource.next() → UserInput`; `PermissionPrompt.ask(toolName, uncoveredCaps) → Decision`; `ClockPort.nowUtc()`; `ProcessPort.run(argv, env, cwd, cancel) → ProcessResult` (drains both streams — see §Do-Not-Clone) |
| **Owned state** | none |
| **Invariants** | The kernel imports no concrete adapter type; every outward dependency is a port; ports are pure data + async-stream contracts so they bind cleanly in Go/TS/.NET (A3); a fake implementation of each port must be sufficient to run the full kernel acceptance suite (A4) |
| **Collaborators** | every kernel module (callers); every adapter (implementors) |

### Ring 0 — Session Runtime (agentic loop)

| Field | Value |
|---|---|
| **Responsibility** | Drive a turn: stream the LLM, collect tool calls, run the tool pipeline, inject results, repeat until a text-only response or the iteration cap |
| **Public inputs** | `UserInput` (text, possibly with `@file` expansions already resolved); the active `ProviderPort`; the tool registry; the `PermissionEngine`; `EventSink`; a cancellation signal |
| **Public outputs** | events to `EventSink`; tool-execution requests to the Tool Pipeline; `SessionState` mutations (messages appended); a `PersistencePort.save` call after each completed turn |
| **Owned state** | `SessionState` (message list, session id, working directory, metadata flags); last-prompt token count; doom-loop detector ring buffer; per-turn iteration counter |
| **Invariants** | iteration cap = 1000 per turn (*observed fact — `ConversationLoop.cs:136`; README's "25" is wrong, arch-OQ5*); turn ends only on text-with-no-tool-calls; doom loop fires at **exactly 3** identical consecutive tool-call batches and resets each turn (*observed fact — `ConversationLoop.cs:288-300`*); context pressure is checked before each turn (≥65% → checkpoint, ≥80% → compact); session saved after every completed turn |
| **Collaborators** | ProviderPort; Tool Pipeline; Permission Engine; Turn Executor; Context Manager; PersistencePort; EventSink |

### Ring 0 — Tool Pipeline (12 steps)

| Field | Value |
|---|---|
| **Responsibility** | Run the fixed 12-step pipeline for every tool call from every source (model, sub-agent extension, slash command) |
| **Public inputs** | `ToolCall {id, name, argumentsJson}`; tool registry; Permission Engine; a `ToolContext` (plan-mode flag, working dir, cancellation token, EventSink, journal hook) |
| **Public outputs** | `ToolResult {class, content/preview}`; optional artifact references; optional journal events |
| **Owned state** | references to result cache and artifact store (both optional adapters); no durable state of its own |
| **Invariants** | strict order: 1 parse → 2 schema-validate → 3 sanity-check → 4 plan-mode-guard → 5 permission (5a capability / 5b legacy) → 6 cache-lookup → 7 pre-hook → 8 execute → 9 post-hook → 10 artifact-store → 11 cache-write → 12 invalidate (*observed fact — contracts §Tool Execution Pipeline, protocols §SM2*); artifact threshold = **50,000** chars (not 20,000; *observed fact — `ArtifactStore.cs:10`*); cache key = `{toolName}:{first16hex(SHA256(NormalizeJson(input)))}` with keys sorted ordinal, no indent (*observed fact — `ToolResultCache.BuildCacheKey`*); steps 6/7/9/10/11 are no-ops if their backing adapter/extension is absent (kernel still runs) |
| **Collaborators** | Permission Engine; tool implementations; Hook extension (steps 7/9); Cache/Artifact adapters (steps 6/10/11/12); Turn Journal extension |

### Ring 0 — Permission Engine

| Field | Value |
|---|---|
| **Responsibility** | Authorize every tool call via capability evaluation, with interactive prompting as the fallback |
| **Public inputs** | tool name; required `Capability` list (0–N of 7 subtypes); session allow/deny state; config allow/deny rules; a `PermissionPrompt` callback |
| **Public outputs** | `Allowed` boolean + message; session-state mutations (allow-all / deny-all / cap-type accumulation) |
| **Owned state** | `_sessionAllowAll`, `_sessionDenyAll` (tool names); `_sessionAllowCapTypes`, `_sessionDenyCapTypes` (cap class names); `_sessionCapRules`; consecutive/total denial counters |
| **Invariants** | 9-step capability evaluation order; 7-step legacy glob path for tools with no capabilities; hardcoded binary block (`sudo,su,doas,pkexec,chmod,chown,chattr,setfacl,icacls,takeown,attrib`); hardcoded write-path block (`/etc/,/usr/,/bin/,/sbin/,/System/,/Library/`); `FileReadCap` auto-allowed inside working dir; denial escalation at 3 consecutive or 20 total; **the `Ask` config list is dead in the original and must NOT be silently cloned — see Do-Not-Clone D-ASK** (*observed fact — contracts §Permission Model; semantic Pass 5 #3*) |
| **Collaborators** | Tool Pipeline (caller); Config (rule source); `PermissionPrompt` (UI seam) |

### Ring 0 — Turn Executor (concurrency contract)

| Field | Value |
|---|---|
| **Responsibility** | Own the two parallelism windows and the sibling-abort semantics for a batch of tool calls, behind one explicit contract |
| **Public inputs** | the tool-call batch; per-tool `isReadOnly`/`isConcurrencySafe` flags; the streaming signal (Window 1 vs Window 2); cancellation token |
| **Public outputs** | ordered `ToolResult[]`; sibling-abort cancellation broadcast |
| **Owned state** | in-flight task set; a **sibling-abort token distinct from the user Ctrl+C token** |
| **Invariants** | Window 1: `isConcurrencySafe` read-only tools may start while the provider stream is still open; Window 2: after `IsComplete`, remaining read-only tools batch concurrently, then writable tools run **serially**; if any parallel task returns `Crash`, broadcast sibling-abort and the cancelled siblings return `Cancelled("sibling abort")` (not `Crash`); **all UI writes, hook invocations, journal writes, and permission prompts triggered by in-flight tools must be serialized through a single executor** (deep-audit fix — see Do-Not-Clone D-INFLIGHT) (*observed fact — `ConversationLoop.cs:228-239,489-607`; semantic Pass 3 #4*) |
| **Collaborators** | Tool Pipeline; Permission Engine; EventSink |

### Ring 0 — Context Manager (Checkpointer + Compactor)

| Field | Value |
|---|---|
| **Responsibility** | Relieve context-window pressure: LLM-summarized checkpoint at 65%, in-memory compaction fallback at 80% |
| **Public inputs** | current message list; active `ProviderPort` (to generate summaries); configured `context_size`; last-prompt token count |
| **Public outputs** | checkpoint records (→ persistence sidecar) with `cutoffMessageIndex`; mutated message list (compaction) |
| **Owned state** | checkpoint list within `SessionState` |
| **Invariants** | checkpoint auto-triggers at ≥65% of `context_size`; compaction at ≥80% (fallback), retains last 4 turns verbatim; both failures are non-fatal (turn continues with full history); thresholds depend on an **accurate tokenizer for the active model family** — see Spike S-TOK and port-CF1 closure |
| **Collaborators** | Session Runtime (trigger); ProviderPort (summary generation); PersistencePort (sidecar write) |

### Ring 1 — Provider Adapters (ProviderPort implementors)

| Field | Value |
|---|---|
| **Responsibility** | Translate a provider-specific LLM SSE wire format into the normalized `StreamChunk` stream the kernel consumes |
| **Public inputs** | message history, tool definitions, sampling params, thinking flag, cancellation |
| **Public outputs** | `AsyncStream<StreamChunk>` where `StreamChunk` carries optional `ThinkingDelta`, `TextDelta`, `ToolCallDelta`, `Usage`, `IsComplete` |
| **Owned state** | HTTP client; retry counter (3 attempts, 1s/4s/16s); Qwen XML fragment accumulator (OpenAI-compat only) |
| **Invariants** | both adapters emit **structurally identical** `StreamChunk` values; consumer never branches on provider identity; `IsComplete` fires only after all tool-call argument deltas are fully accumulated; retry only **before** the stream starts (HTTP 429/5xx/network), never mid-stream; **the consumer must process all non-null fields of a chunk — see Do-Not-Clone D-MIXED** (*observed fact — protocols §B1/§B2; semantic Pass 5 #4*) |
| **Collaborators** | Session Runtime (consumer); Provider Registry (instantiates by name) |

### Ring 1 — Session Store (PersistencePort implementor)

| Field | Value |
|---|---|
| **Responsibility** | Read/write session JSONL, checkpoint sidecar, and the session index on the filesystem |
| **Public inputs** | full `SessionState`; checkpoint list; index update events; a session id to load |
| **Public outputs** | JSONL file; `.checkpoints.json` sidecar; `index.json`; a loaded `SessionState` |
| **Owned state** | path derivation from session id + date; last-saved message count |
| **Invariants** | **atomic save: write temp → fsync → atomic rename, in that order, for JSONL, sidecar, and index — see Do-Not-Clone D-ATOMIC**; **header is identified by schema/position, never by substring — see D-HEADER**; sidecar written after the main file's rename; index full-rewrite on update (*observed fact — protocols §Persistent Schema Notes; mechanical Pass 2 #1; semantic Pass 5 #5*) |
| **Collaborators** | Session Runtime; Context Manager; `/resume` command |

### Ring 1 — Configuration Loader (adapter)

| Field | Value |
|---|---|
| **Responsibility** | Merge configuration from 7 ordered sources into a validated immutable config, then **validate** it |
| **Public inputs** | defaults, user JSON, project JSON, explicit `--config` file, env vars, server probe response, CLI flags |
| **Public outputs** | immutable `AppConfig`; a validation result that can warn or fail startup |
| **Owned state** | none after load |
| **Invariants** | precedence (low→high): defaults → user JSON → project JSON → explicit file → env vars → server probe → CLI flags; scalar `llm.*` merge must **distinguish "unset" from "zero"** so `temperature/top_p/min_p = 0` survive — see D-ZERO; `permissions.tools.{allow,deny,ask}` and `hooks.*` are additive **with no dedup**, order = user then project then explicit (port-CF2); `providers`/`mcp_servers`/`model_presets` are last-write-wins per key; missing file silent; malformed JSON warns and is skipped; **invalid numeric env/file values must be validated, not silently ignored — see D-CFGVAL** (*observed fact — contracts §Configuration Model; mechanical Pass 1 #4, Pass 6 #1/#2*) |
| **Collaborators** | all modules (read-only consumer) |

### Ring 1 — Subprocess Runner (ProcessPort implementor)

| Field | Value |
|---|---|
| **Responsibility** | Spawn child processes (Bash tool, and later hooks/MCP/LSP) and capture output without deadlock |
| **Public inputs** | argv, env, cwd, cancellation, timeout |
| **Public outputs** | `{exitCode, stdout, stderr}` or a structured start-failure result |
| **Owned state** | child process handle / process-group id |
| **Invariants** | **stdout and stderr drained concurrently** from start; on timeout, **kill the whole process tree/group**, then await bounded drains; process-start failures returned as a result, not thrown; bounded output capture — see Do-Not-Clone D-DRAIN, D-STARTFAIL, D-TREEKILL (*mechanical Pass 2 #2/#4; semantic Pass 3 #1/#2*) |
| **Collaborators** | Bash tool; Hook / MCP / LSP / background-bash extensions |

### Ring 1 — Tokenizer (adapter)

| Field | Value |
|---|---|
| **Responsibility** | Count tokens for the active model family so context-pressure thresholds are accurate |
| **Public inputs** | text / message list; model family id |
| **Public outputs** | token count |
| **Owned state** | loaded vocab/encoding |
| **Invariants** | count must be within ±2% of the model's reference tokenizer (port-CF1; Spike S-TOK); selection is a target-stack decision (`tiktoken`/`tiktoken-go`/`tiktoken-rs`/`js-tiktoken`/`TiktokenSharp`, plus model-specific vocabs for Qwen/LLaMA/Claude) |
| **Collaborators** | Context Manager; Session Runtime (token tracking) |

### Ring 2 — Extension / Plugin Surface (the pi-mono lever)

| Field | Value |
|---|---|
| **Responsibility** | Let optional capabilities register tools, commands, event handlers, permission gates, and renderers into the kernel **without modifying core** — the mechanism by which MCP, LSP, hooks, sub-agents, plan mode, playbooks, memory, and background bash are added |
| **Public inputs** | an `ExtensionAPI` exposing: `registerTool(ToolPort)`, `registerCommand(name, handler)`, `on(event, handler)` (e.g. `tool_call`, `turn_start`), `registerPermissionGate(fn)`, `registerRenderer(EventSink)`, `registerProvider(ProviderPort)` |
| **Public outputs** | mutations to the tool registry, command registry, event-handler list, and provider registry |
| **Owned state** | the registries themselves |
| **Invariants** | extensions are discovered/loaded at startup and may be disabled per-config; the kernel runs fully with **zero** extensions loaded (this is the MVP); an extension is data-in/data-out against ports — it cannot reach into kernel internals; extension load order is deterministic; an extension that owns a subprocess owns that subprocess's output pumps and lifecycle (D-DRAIN, D-BGPROC) (*design decision; inspiration: pi-mono coding-agent README §Extensions, §Philosophy — "no MCP / sub-agents / plan mode / background bash baked in"*) |
| **Collaborators** | Tool Pipeline; Session Runtime; delivery surfaces |

**Capabilities expressed as extensions (Ring 2).** Each preserves the *behavior* documented in
contracts/protocols, but is attached through the Extension Surface rather than wired into core:

| Extension | Preserved behavior (source of truth) | Notes / deep-audit constraint |
|---|---|---|
| **MCP** | JSON-RPC 2.0 over stdio; `initialize`→`notifications/initialized`→`tools/list`; tools registered `mcp__{server}__{name}`, always `Ask`; serialized requests (protocols §B3, §SM3) | **id-aware response dispatch (D-RPCID)**; **owns stderr drain (D-DRAIN)**; document servers as trusted code execution (D-MCPTRUST) |
| **LSP** | JSON-RPC over stdio; lazy-start per language (architecture §Lsp) | **id-aware dispatch + cancellable reads (D-RPCID, D-LSPCANCEL)**; **owns stderr drain (D-DRAIN)** |
| **Hooks** | pre/post-tool shell hooks; 30s timeout; non-fatal; `{{tool_name/input/output}}` (contracts §pipeline 7/9) | **pass data as env/stdin/argv, never shell-interpolate (D-HOOKINJ)**; **kill process tree on timeout (D-HOOKKILL)** — or defer the feature entirely (it is a non-goal for MVP) |
| **Sub-agents** | 5 named types, turn budgets, parent PermissionEngine reuse, ephemeral session, self-spawn prevented, `IsConcurrencySafe=true` (contracts §Sub-agent) | reuse parent Permission Engine via port; runs the same Tool Pipeline |
| **Plan mode** | session-scoped read-only tool filter; pipeline step-4 guard message (contracts §`/plan`) | a permission-gate + tool-list-filter extension, not a core flag |
| **Playbooks** | YAML frontmatter, topological steps, gates, template tokens, per-step resume, 10-iter cap (protocols §SM4, §Playbook) | `{{shell:…}}` token is an injection surface — scope behind explicit user trust (D-PBSHELL) |
| **Memory** | YAML-frontmatter files injected into system prompt (architecture §Memory) | replaceable with any K/V store |
| **Background bash** | detached process, returns PID + log path (contracts) | **needs a lifecycle manager or stays out of MVP (D-BGPROC)** |
| **Turn journal** | append-only JSONL, 10 event types, SHA-256 args hash (protocols §Turn Journal) | observability only; an `on(event)` consumer |
| **Secret scanner** | present in original, integration point unconfirmed (contr-OQ1) | gate via `registerPermissionGate`/`on(tool_result)` once behavior is pinned (Spike S-SECRET) |

### Ring 3 — Delivery Surfaces / Modes

| Field | Value |
|---|---|
| **Responsibility** | Present the kernel to a human or a calling process; select the run mode |
| **Public inputs** | kernel `EventSink` events; user input; CLI flags / SDK calls / RPC frames |
| **Public outputs** | terminal output, JSON/JSONL frames, or SDK return values |
| **Owned state** | terminal/render buffer; RPC framing state |
| **Invariants** | the kernel is surface-agnostic via `EventSink`/`InputSource`; modes: **interactive** (classic scroll or full-screen TUI — TUI active only when stdin+stdout are a terminal and `--classic` absent), **print** (one response, exit), **JSON** (event lines), **RPC** (strict **LF-delimited** JSONL over stdin/stdout — split on `\n` only), **SDK** (embed the kernel in another program) (*design decision; inspiration: pi-mono coding-agent README §Modes, §RPC Mode, §Programmatic Usage; original surfaces from contracts §Surfaces*) |
| **Collaborators** | Session Runtime (event producer); Extension Surface (custom renderers/commands) |

**Slash commands** (`/undo,/checkpoint,/compact,/resume,/plan,/export,/model,/retry,/status,/stats,/init,/debug,/think,/clear`) are thin verbs over kernel operations, registered through the command registry; in the modular design third-party commands register the same way. *observed fact — contracts §Agent REPL; design decision for registration.*

---

## Layer Split

Per the SKILL's three-layer split, with the modular rings mapped onto it. "Extensions" is a
sub-class of adapters (opt-in, registered) and is called out because A2 requires it.

| Module | Layer | Notes |
|---|---|---|
| Kernel Ports | core semantics (contract) | The stable seam; must survive the port unchanged |
| Session Runtime (agentic loop) | core semantics | Turn lifecycle, iteration cap, doom-loop, context-pressure trigger |
| Tool Pipeline (12 steps) | core semantics | Strict-order execution contract |
| Permission Engine | core semantics | Capability model, evaluation order, hardcoded blocks |
| Turn Executor (concurrency contract) | core semantics | Two windows + sibling-abort + serialized side effects |
| Context Manager (Checkpointer/Compactor) | core semantics | 65%/80% thresholds are behavioral invariants |
| Provider Adapters (OpenAI-compat, Anthropic) | adapters | Wire-format normalization |
| Session Store (atomic FS) | adapters | Atomic save design (D-ATOMIC) |
| Configuration Loader | adapters | 7-layer merge + validation |
| Subprocess Runner | adapters | Concurrent drains + tree-kill |
| Tokenizer | adapters | Per-model-family; ±2% accuracy |
| Result Cache / Artifact Store | adapters | SHA-256 key; 50,000-char threshold |
| Extension Surface (registry) | adapters | Registration mechanism for Ring 2 |
| MCP / LSP integration | adapters (extension) | stdio JSON-RPC; id-aware; owns drains |
| Hook runner | adapters (extension) | Safe interpolation; tree-kill; **MVP non-goal** |
| Sub-agents | adapters (extension) | Reuses parent Permission Engine |
| Plan mode | adapters (extension) | Gate + tool filter |
| Playbook executor | adapters (extension) | Topological steps; resume |
| Memory store | adapters (extension) | YAML K/V |
| Background bash | adapters (extension) | Lifecycle manager required; **MVP non-goal** |
| Turn journal / Secret scanner | adapters (extension) | Observability / safety |
| Renderer (classic, TUI) | delivery surfaces | Surface-agnostic kernel via EventSink |
| Modes (interactive/print/JSON/RPC/SDK) | delivery surfaces | RPC = LF-delimited JSONL |
| Slash commands (14) | delivery surfaces | Thin verbs; registered |
| Product shell (`openmono` bash + Docker) | delivery surfaces | Deployment wrapper; **MVP non-goal** |
| File history (`/undo`) | delivery surfaces | In-memory stack; not persisted |

**Dependency direction:** delivery → extensions → adapters → kernel. The kernel imports nothing
from outer rings. *design decision; mirrors original's clean layering (architecture §Dependency
Direction: "no observed cycles") while removing the high-fan-in core constructor.*

---

## Required Behaviors

Non-negotiable. A port missing any of these is not functionally equivalent. Derived from
behavioral-contracts.md and protocols-and-state.md; deep-audit corrections noted inline.

**Agentic loop**
- Up to **1000** LLM iterations per turn (code-authoritative; README's "25" is wrong — arch-OQ5).
- Turn ends only on a text response with no tool calls.
- Context pressure checked before every turn; checkpoint at ≥65%, compact at ≥80%.
- Session persisted after every completed turn.

**Tool pipeline**
- Every tool call traverses all 12 steps in strict order, regardless of source.
- Step 4 blocks non-read-only tools when plan mode is active, with the exact message
  `"Plan mode is active — only read-only tools are allowed. Call ExitPlanMode first to make changes with {tool}."`
- Step 6 validates file mtime + size before serving a cache hit.
- Steps 7/9 hooks are non-fatal (30s timeout); **but a timed-out hook must be killed, not merely warned** (D-HOOKKILL).
- Step 10 triggers when a `Success` result's preview exceeds **50,000** chars; preview = first 20 + last 10 lines with the omission marker `[N lines omitted — full output in artifact {id} ({bytes} bytes)]`.
- Cache key = `{toolName}:{first16hex(SHA256(NormalizeJson(input)))}`.
- `ToolResult.IsError` is true for all non-`Success` classes; the model always receives `result.Content`.

**Permission engine**
- Capability path (9-step) primary; legacy glob path (7-step) for tools with no capabilities.
- Hardcoded binary and write-path blocks (lists above).
- `FileReadCap` auto-allowed inside the working directory.
- Denial escalation at 3 consecutive or 20 total denials.
- The `Ask` config field is **not** silently cloned (D-ASK).
- **`FileRead from_cursor` must not bypass per-file authorization** (D-CURSOR).

**Doom-loop detection**
- Exactly 3 identical consecutive tool-call batches → inject break message as an assistant turn; counter resets at turn start.

**Streaming + concurrency**
- Window 1 (during stream) and Window 2 (after `IsComplete`) as defined in Turn Executor.
- Writable tools never run in parallel.
- Sibling-abort on `Crash`; cancelled siblings return `Cancelled`, not `Crash`.
- Sibling-abort token is separate from the user Ctrl+C token.
- Side effects (UI/hook/journal/permission-prompt) of in-flight tools are serialized (D-INFLIGHT).
- The consumer processes **all** non-null fields of every `StreamChunk` (D-MIXED).

**Config**
- 7-layer precedence; additive no-dedup for lists (port-CF2); zero distinguishable from unset (D-ZERO); post-merge validation (D-CFGVAL).

**Persistence**
- Atomic save (D-ATOMIC) and schema/positional header detection (D-HEADER).
- `/resume` loads JSONL + checkpoint sidecar and replaces in-memory state.

**Provider normalization**
- Identical `StreamChunk` from both adapters; Qwen XML `<function=…>` fallback lives inside the OpenAI-compat adapter, transparent to the consumer.

**Cancellation/shutdown**
- Ctrl+C once cancels the active turn (stream + tool tasks); twice within 1.5s hard-exits; normal exit saves the session.

**Subprocess discipline (applies to Bash now; hooks/MCP/LSP/bg-bash when added)**
- Concurrent stdout/stderr drains; process-tree kill on timeout; start-failure returned as a result (D-DRAIN, D-TREEKILL, D-STARTFAIL).

---

## Protocols and Persisted State

Authoritative detail lives in protocols-and-state.md; this section states what the port must
preserve.

### Wire formats

**OpenAI-compatible SSE (B1).** HTTP/1.1 chunked `text/event-stream`; each event `data: <json>`;
stream ends with `data: [DONE]`. Tool-call argument deltas accumulate by
`choices[0].delta.tool_calls[].index`. `usage` only in the final chunk when
`stream_options.include_usage=true`. Retry ≤3 (1s/4s/16s) on 429/5xx/network before stream;
no mid-stream retry. Qwen XML `<function=name>` fallback: suppress text, regex-extract tool calls
at `[DONE]`. *observed fact — `OpenAiCompatClient.cs:82-310`.*

**Anthropic native SSE (B2).** SSE; ends with `type:"message_stop"` (no `[DONE]`); tool use via
`content_block_start`(`type:"tool_use"`)/`input_json_delta`/`content_block_stop`; system message is
a top-level `system` field; tool results are `user` messages with `tool_result` blocks; tools use
`input_schema`. Same pre-stream retry. *observed fact — `AnthropicClient.cs:95-193`.* **Per-provider
request builders are mandatory; request construction cannot be shared** (portability hazard).

**MCP JSON-RPC 2.0 over stdio (B3).** Newline-delimited JSON; handshake
`initialize`→`notifications/initialized`→`tools/list`; calls via `tools/call`; serialized one-at-a-time;
no reconnection. **Response correlation must be by `id` (D-RPCID).** *observed fact — `McpClient.cs`.*

**RPC mode framing (new delivery surface).** Strict **LF-delimited** JSONL on stdin/stdout —
split records on `\n` only; do not use generic line readers that also split on Unicode separators.
*design decision; inspiration: pi-mono coding-agent README §RPC Mode.*

### State machines

- **SM1 Turn lifecycle:** `Idle → ContextPressureCheck → Streaming ↔ ToolExecution → TurnComplete`;
  `IsComplete`+tool-calls → `DoomLoopCheck` (3-strike) → `ToolExecution`; hard cap 1000.
- **SM2 12-step pipeline:** linear parse→validate→sanity→plan-guard→permission→cache→pre-hook→execute→post-hook→artifact→cache-write→invalidate; any step may terminate with `Error`/`Cancelled`.
- **SM3 MCP lifecycle:** `Disconnected→Spawning→Initializing→SendingNotification→Ready→Registered`; failure terminal per server.
- **SM4 Playbook step:** `Pending→StepLoop→GetContent→GatePrompt?→RunStep→ScriptCheck→CompleteStep→Done`; topological order; 10-iter per-step cap; state saved after each step.

### Persistence schemas

- **Session JSONL** `~/.openmono/sessions/{yyyy-MM-dd}_{id}.jsonl` — **atomic** full rewrite (D-ATOMIC).
  Line 0 header `{session_id, started_at, working_directory}`; lines 1..N message records
  `{role∈{System,User,Assistant,Tool}, content, toolCalls[], toolCallId, toolName, timestamp}`.
  **Header detected by schema/position, never substring (D-HEADER).**
- **Checkpoint sidecar** `…/{date}_{id}.checkpoints.json` — JSON array of
  `{id, createdAt, turnIndex, cutoffMessageIndex, summary, messagesCompressed}`; on resume, messages
  before `cutoffMessageIndex` are replaced by the summary in the context window.
- **Session index** `~/.openmono/sessions/index.json` — array of
  `{id, startedAt, turnCount, totalTokens, workingDirectory, firstMessage}`; full rewrite; filtered by workdir.
- **Turn journal** `…/{date}_{id}.journal.jsonl` — append-only, `AutoFlush`, `type` discriminator, 10 event types, SHA-256 args hash.
- **Playbook state** `~/.openmono/playbook-state/{name}_{sessionId}.json` — full rewrite per step.
- **Artifact** `~/.openmono/artifacts/{sessionId}/{tool}_{ts}_{hash8}.txt` — written when preview > 50,000 chars.

*observed fact — protocols §Persistent Schema Notes; reverse-engineering-bundle.md §Persistence Schemas Summary.*

---

## External Dependencies

| Dependency | Stance | Rationale |
|---|---|---|
| LLM inference server (llama.cpp / OpenAI-compat) | wrap | OpenAI-compat adapter normalizes to `StreamChunk`; any OpenAI-compat endpoint substitutes |
| Anthropic API | wrap | Dedicated adapter; same `StreamChunk` output; parity tier |
| TUI library (Spectre.Console) | replace | .NET-specific; pick a target-stack TUI (`bubbletea`/`ratatui`/`ink`/`textual`) or keep classic-scroll for MVP; kernel is renderer-agnostic |
| Tokenizer (TiktokenSharp) | replace | Pick per target stack; ±2% accuracy gate (port-CF1, S-TOK) |
| YAML parser (YamlDotNet) | replace | Needed only for playbooks/memory (parity/full); must support hyphenated keys |
| MCP external tool servers | emulate | JSON-RPC 2.0 over stdio is language-neutral; reuse subprocess lifecycle; extension, not core |
| LSP language servers | emulate | JSON-RPC over stdio; lazy-start; extension; optional |
| Docker / container runtime | postpone | Deployment wrapper, not agent logic; Linux-native binary first (A1) |
| Roslyn (`Microsoft.CodeAnalysis`) | replace/drop | C#-specific `RoslynTool`; drop for non-.NET targets, or keep only in a .NET refactor; **MVP non-goal** |
| `Terminal.Gui` | postpone | Presence in `.csproj` unconfirmed at runtime (arch-OQ4); grep before including |

---

## Do-Not-Clone Defect Rules

This is the deep-audit's central contribution to the spec and satisfies the pipeline criterion
"defects … explicitly designed-around or noted as 'left behind', with the choice cited." Every
finding from `mechanical-defects.md` (M-prefixed) and `semantic-defects.md` (S-prefixed) is given a
disposition: **fix** (design correctly in the port), **port-differently** (re-architect), or
**leave-behind** (do not carry the behavior, or defer the whole feature). Reimplementers must treat
these as binding design rules, not optional cleanups.

| Rule | Source finding(s) | Sev | Disposition | Design rule for the port |
|---|---|---|---|---|
| **D-HEADER** | mech Pass 1 #1; prot hazard | High | fix | Parse JSONL line 0 as a `SessionHeader` by schema/position; parse the rest as messages. **Never** filter message lines by `Contains("session_id")`. Regression fixture: a message whose content literally contains `session_id` must survive a save/load round-trip. |
| **D-RPCID** | mech Pass 1 #2; sem Pass 5 #2 | High | port-differently | The JSON-RPC client (MCP and LSP) must read frames, skip notifications, and return the response whose `id == expectedId`; keep reading until it arrives or cancellation fires. Fixtures: out-of-order responses, interleaved diagnostics notifications. |
| **D-ATOMIC** | mech Pass 2 #1; sem Pass 5 #5 | High | fix | All durable writes (session JSONL, checkpoint sidecar, index) use temp-file → fsync → atomic `rename`. Sidecar + index written after the main file renames. Fault-injection test: SIGKILL mid-save leaves a complete, parseable prior file. |
| **D-DRAIN** | mech Pass 2 #2/#3; sem Pass 3 #1 | High | fix | Every subprocess (Bash, hooks, MCP, LSP, bg-bash) drains stdout and stderr **concurrently** from spawn, with bounded capture. Undrained stderr must never wedge the protocol. Test: a child that floods stderr while stdout is read. |
| **D-HOOKKILL** | sem Pass 3 #2; sem Pass 5 #1 | High | fix (or defer feature) | On the 30s hook timeout, kill the hook's **process tree** before continuing, then include truncated output in the warning. If hooks are not built for MVP (they are a non-goal), this rule binds when the hooks extension is added. Test: a hook that sleeps/writes after timeout leaves no surviving process. |
| **D-HOOKINJ** | sem Pass 4 #1; mech CF3 | High | port-differently (or defer) | Hook variables (`tool_input/output/name`) are **data, not shell source**. Pass them via environment variables, stdin, or an argv-style API — never string-interpolated into `bash -c`. Test: a tool output containing `; rm -rf ~` must not execute. (Hooks are an MVP non-goal; rule binds when added.) |
| **D-TREEKILL** | mech Pass 2 #2; relates D-DRAIN | High | fix | On timeout/cancel, kill the entire process group (POSIX `killpg`), not just the direct child. Linux-native (A1) may rely on process groups. |
| **D-ZERO** | mech Pass 1 #4 | Med | fix | Config scalar merge must distinguish "unset" from "0". Use nullable overlay fields or explicit presence flags so `temperature=0`, `top_p=0`, `min_p=0` survive layered merge + env override. Test: deterministic-temperature config (`0`) reaches the provider request body. |
| **D-CFGVAL** | mech Pass 6 #1/#2 | Med | fix | Validate config **after** all layers merge: malformed numeric env/file values produce explicit diagnostics; out-of-range values (`top_p=999`, negative penalties, absurd token limits) are rejected or clamped per documented policy, not silently passed to the provider. **A valid `0` is not "malformed" (interacts with D-ZERO).** |
| **D-STARTFAIL** | mech Pass 2 #4 | Med | fix | Process-start failures (bad shell path, perms, bad cwd) return a non-zero structured result with contextual stderr, not an unhandled throw. |
| **D-SHELL** | mech Pass 6 #4 | Med | leave-behind (Linux-native) | Original hardcodes `/bin/bash`. For Linux-native MVP (A1) this is acceptable; document the required shell and fall back to `/bin/sh`. Resolving `COMSPEC` etc. is deferred with cross-platform work, not designed-out. |
| **D-SUBENV** | mech Pass 6 #5 | Low | fix | Define the subprocess environment contract explicitly (inherit vs whitelist), don't leave it implicit at `HOME`+`PATH` only. |
| **D-ENDPOINT** | mech Pass 6 #3 | Low | fix | Render diagnostics/curl hints using the **actual configured endpoint**, not hardcoded `localhost:7474`/`:11434`. |
| **D-CLEANUP** | mech Pass 2 #5; sem Pass 3 #5 | Low | fix | Cleanup/dispose failures emit debug diagnostics (server name + command), not silent swallow. |
| **D-INFLIGHT** | sem Pass 3 #4 | Med | port-differently | Define the in-flight concurrency contract: restrict Window-1 execution to tools whose entire pipeline is non-interactive and thread-safe, **or** serialize UI/hook/journal/permission-prompt side effects through a single executor. Do not rely on incidental component thread-safety. |
| **D-BGPROC** | sem Pass 3 #3; mech CF2 | Med | port-differently (or leave-behind) | Background bash needs a real lifecycle manager (process groups, status polling, log tailing, shutdown cleanup, orphan recovery) **or** it stays out of the MVP. Returning only PID + log path is not acceptable. (MVP non-goal.) |
| **D-CURSOR** | sem Pass 4 #2 | Med | fix | `FileRead from_cursor` must not return an empty capability set. Re-evaluate capabilities after resolving cursor contents (one `FileReadCap` per resolved file) or restrict cursor stores to capability-provenance-preserving entries. Test: a cursor entry pointing outside the workspace is still permission-checked. |
| **D-MCPTRUST** | sem Pass 4 #3 | Med | port-differently | Treat MCP server config as trusted local code execution: warn on registering external servers; consider per-server trust levels. Document the trust boundary explicitly. |
| **D-ASK** | sem Pass 5 #3; contr-OQ2 | Med | leave-behind (decide) | The `Ask` permission list is loaded but never evaluated in the original. Do **not** silently clone dead config. Either implement the documented "always prompt" precedence **and test it**, or drop the field from the schema with a deprecation warning. Decision required (Spike S-ASK / known-unknown contr-OQ2). |
| **D-MIXED** | sem Pass 5 #4 | Med | fix | The `StreamChunk` consumer must process **all** non-null fields of a chunk. Do not `continue` on `ThinkingDelta` before checking `TextDelta`/`ToolCallDelta`/`Usage`/`IsComplete`. Alternatively, constrain provider adapters to emit one-field chunks and encode that as the stream contract — but the safe default is "process everything." Test: a single chunk carrying thinking + text + usage delivers all three. |

**Porting policy distilled from the audit** (carried from reverse-engineering-bundle.md §Defect Synthesis):
1. **Preserve behavior as tests, not source structure** — black-box the loop, permissions, stream normalization, persistence before any rewrite (this is exactly the kernel-first plan, A4).
2. **Fix crash-safety and process lifecycle in the new design** — non-atomic writes, untracked bg processes, undrained subprocess stderr, non-killing hook timeouts are defects, not compatibility requirements.
3. **Make trust boundaries explicit** — hooks, Bash, MCP, LSP, provider streams are external-input boundaries; pass data as data, authorize after resolution, keep each subprocess's output pumps under one owner.
4. **Narrow the MVP** — kernel + one OpenAI-compat provider + tool pipeline + permissions + atomic store + file/bash tools; defer the rest.

---

## Portability Hazards

Consolidated; [arch]/[prot] mark the originating phase. All are *portability hazard* unless noted.

| Hazard | Impact | Mitigation |
|---|---|---|
| `.NET async/await + Task.WhenAll + CancellationToken` [arch][prot] | High — entire kernel concurrency model | Map to goroutines+`context` (Go), `asyncio` + explicit cancel (Python), Tokio + `CancellationToken` (Rust), async + `AbortController` (TS); replicate the **separate** sibling-abort token (Turn Executor) |
| `IAsyncEnumerable<StreamChunk>` pull-based stream [prot] | High — token delivery + tool dispatch | Go channel / Python async generator / Rust `Stream` / JS `AsyncIterable`; keep `IsComplete`-after-accumulation invariant |
| OpenAI vs Anthropic request bodies fundamentally differ [prot] | High — can't share request construction | Two `buildRequestBody` impls; normalize to shared `StreamChunk` |
| Qwen XML `<function=…>` fallback [prot] | Medium — silently drops tool calls if omitted | Implement inside OpenAI-compat adapter; transparent to consumer |
| `process.Kill(entireProcessTree:true)` is .NET 5+ [prot] | Medium — misses grandchildren | POSIX `killpg`/process groups (Linux-native, A1) |
| `SemaphoreSlim(1,1)` MCP serialization [prot] | Medium — slow servers stall the loop | Acceptable for MVP; later pipeline with `id` correlation (D-RPCID makes this safe) |
| Spectre.Console full-screen TUI [arch][prot] | Medium — primary UX not portable | Replace per target stack; kernel is renderer-agnostic; classic-scroll suffices for MVP |
| TiktokenSharp tokenizer [arch] | Medium — thresholds need accurate counts | Per-stack tokenizer; ±2% gate (S-TOK) |
| Playbook `{{shell:<cmd>}}` arbitrary execution [prot] | Medium — injection in untrusted playbooks | Scope behind explicit user trust (D-PBSHELL) or drop |
| YamlDotNet `HyphenatedNamingConvention` [prot] | Low — camel/snake parsers won't bind | Use hyphenated key mapping |
| 12-char GUID-prefix session IDs [prot] | Low — not cryptographically uniform | `crypto/rand`/`secrets.token_hex(6)`/equivalent |
| `global.json` SDK pin unread [arch] | Low — only affects a .NET refactor's build | Read `global.json` before any .NET build (arch-OQ2) |
| Roslyn `RoslynTool` C#-specific [arch] | Low for non-.NET | Drop or replace with native AST tooling |

---

## Implementation Sequence (kernel-first, A4)

The build order is the least-error-prone path: prove the kernel against fakes, then add real I/O,
then surfaces, then extensions. Each phase ends with its acceptance scenarios green.

**Phase 0 — Kernel scaffold + ports + fakes (no real I/O).**
- Define all Ring-0 ports (ProviderPort, ToolPort, PersistencePort, EventSink, InputSource,
  PermissionPrompt, ClockPort, ProcessPort).
- Implement `FakeProvider` (emits scripted `StreamChunk` sequences, incl. mixed-field chunks and
  Qwen-XML text), `FakeTool` set (deterministic `Success`/`Crash`/`Cancelled`), `InMemorySessionStore`,
  `RecordingEventSink`, `ScriptedPermissionPrompt`, `FixedClock`.
- Stand up the black-box acceptance harness (§Acceptance Scenarios) driven entirely by fakes.

**Phase 1 — Kernel behaviors green against fakes.**
- Session Runtime (loop, iteration cap 1000, turn-end rule).
- Tool Pipeline (12 steps; steps 6/7/9/10/11 are no-ops without adapters).
- Permission Engine (capability + legacy paths, hardcoded blocks, escalation; D-ASK decision; D-CURSOR).
- Turn Executor (Window 1/2, sibling-abort separate token, serialized side effects — D-INFLIGHT).
- Context Manager (65%/80% via **injected** token counts — no real tokenizer yet).
- Doom-loop (exactly 3); D-MIXED consumer handling.
- Exit criterion: scenarios 1–14, 18–22, 24 (the fake-able subset) all pass.

**Phase 2 — Real adapters.**
- OpenAI-compat ProviderPort (SSE, retry, Qwen XML, D-MIXED) — validated against a real or recorded endpoint.
- Atomic FS Session Store (D-ATOMIC, D-HEADER) with fault-injection tests.
- Config Loader (7-layer, D-ZERO, D-CFGVAL, port-CF2 no-dedup).
- Subprocess Runner (D-DRAIN, D-TREEKILL, D-STARTFAIL, D-SUBENV) and the Bash/FileRead/FileWrite/FileEdit/Glob/Grep tools.
- Real tokenizer for the target model family (S-TOK ±2% gate before 65%/80% go live).
- Exit criterion: scenarios 15–17, 23, 25 + MVP end-to-end against a live OpenAI-compat server.

**Phase 3 — Delivery surfaces / modes.**
- Classic-scroll renderer first; then print and JSON modes; then RPC mode (LF-JSONL); then SDK entry points.
- Full-screen TUI last (parity tier).
- Slash commands registered through the command registry.

**Phase 4 — Extensions (each opt-in, behind the Extension Surface).**
- Anthropic provider adapter; MCP (D-RPCID, D-DRAIN, D-MCPTRUST); hooks (D-HOOKINJ, D-HOOKKILL);
  sub-agents; plan mode; playbooks (D-PBSHELL); memory; LSP (D-RPCID, D-LSPCANCEL); background bash
  (D-BGPROC); turn journal; secret scanner (after S-SECRET).
- Product shell / Docker packaging.

### Scope Tiers

**Minimum viable port (kernel + one provider, Linux-native):**
Config loader (7-layer, D-ZERO/D-CFGVAL) · OpenAI-compat adapter (`StreamChunk`, Qwen XML, D-MIXED) ·
Session Runtime (loop, doom-loop, 1000 cap) · 12-step Tool Pipeline (steps 1–5,8 minimum; 6/10/11/12
when cache/artifact added) · Permission Engine (capability path + hardcoded blocks, D-CURSOR, D-ASK
decision) · Turn Executor (D-INFLIGHT) · Atomic Session Store + index (D-ATOMIC, D-HEADER) ·
Tokenizer (S-TOK) · classic-scroll renderer + interactive mode · core tools
`FileRead/FileWrite/FileEdit/Bash/Glob/Grep` (Subprocess Runner with D-DRAIN/D-TREEKILL/D-STARTFAIL).
At this tier: submit messages, model calls file/bash tools, sessions persist and resume crash-safely.

**Major-workflow parity:**
All 20 built-in tools · result cache + artifact store · Anthropic adapter · Checkpointer (65%, S-TOK
equivalence) + Compactor (80%) · full-screen TUI · all 14 slash commands · print/JSON/RPC/SDK modes ·
MCP extension (D-RPCID, D-DRAIN, D-MCPTRUST) · provider hot-swap (`/model`) · hooks extension
(D-HOOKINJ, D-HOOKKILL) if desired.

**Full parity:**
Playbooks (D-PBSHELL) · sub-agents · turn journal · LSP · memory · `/undo` file history · KV-cache
warmup · session export (HTML/JSON/MD) · background bash (D-BGPROC) · product shell / Docker wrapper ·
Roslyn-equivalent semantic tooling (only in a .NET refactor; otherwise dropped).

---

## Acceptance Scenarios

Black-box checks with concrete inputs and observable outputs; no source-language internals. Tier
marks MVP / parity / full. The scenarios required by the prompt are starred (★) and grouped first.
Most starred scenarios are runnable in Phase 0/1 against fakes (kernel-first, A4).

| # | Scenario | Input | Expected Output / Side Effect | Tier |
|---|----------|-------|-------------------------------|------|
| ★1 | **Atomic persistence survives interruption** | SIGKILL the process during a session save | On next load the JSONL is complete and parseable; no truncated final line; index/sidecar consistent (D-ATOMIC) | MVP |
| ★2 | **JSONL header parsed by schema, not substring** | A session whose last user message content literally contains the text `session_id` | After save+load round-trip, that message is present and replayed; header is still line 0 only (D-HEADER) | MVP |
| ★3 | **id-aware JSON-RPC dispatch** | A JSON-RPC server emits a diagnostics notification, then an out-of-order response for request id=2, then id=1 | Client returns the response matching the requested `id`; notifications ignored; no cross-talk (D-RPCID) | parity |
| ★4 | **Subprocess stderr is drained** | A child tool/server writes > 1 MB to stderr while stdout is being read | No deadlock; the call completes; recent stderr captured (bounded) and surfaced on failure (D-DRAIN) | MVP (Bash) / parity (MCP/LSP) |
| ★5a | **Hook timeout kills the process tree** | A pre-hook spawns a child that sleeps 60s and writes a file at t=45s; hook timeout = 30s | At t≈30s the hook's whole process tree is killed; the file is **never** written; tool proceeds with a warning (D-HOOKKILL) | parity |
| ★5b | **Hooks deferred (MVP)** | MVP build, hook configured in settings | Hooks are a documented non-goal: settings parse but no hook runs; no silent partial behavior | MVP |
| ★6 | **Safe hook interpolation** | A tool output is `"; rm -rf $HOME #"`; a post-hook is configured to receive `{{tool_output}}` | The value is delivered as data (env/stdin/argv); no shell command executes; `$HOME` is not deleted (D-HOOKINJ) | parity |
| ★7a | **Background process lifecycle decided** | MVP build, model requests a background Bash job | Documented decision: background bash is an MVP non-goal; the tool is absent or returns "not enabled," never a bare PID+log with no manager (D-BGPROC) | MVP |
| ★7b | **Background process managed (when enabled)** | bg-bash extension enabled; start a long job; then quit the session | Job is tracked (process group), status pollable, killed/cleaned on shutdown; no orphan (D-BGPROC) | full |
| ★8 | **Config validation incl. valid zero** | User config `temperature: 0`; env `OPENMONO_TOP_P=abc` | `temperature=0` reaches the provider request body (not dropped — D-ZERO); `abc` produces an explicit validation diagnostic, not silent default (D-CFGVAL) | MVP |
| ★9 | **`Ask` rule decision is explicit** | Config sets `permissions.tools.ask: ["Bash"]` | Behavior matches the chosen policy: either an interactive prompt fires for `Bash` (implemented + tested) or startup warns the field is deprecated/unsupported — never silently ignored (D-ASK) | MVP |
| ★10 | **Cursor read preserves permission provenance** | A cursor entry references a file **outside** the working directory; model calls `FileRead from_cursor` | The read is still permission-checked per resolved file; an out-of-workspace file prompts/denies as a normal `FileReadCap` would (D-CURSOR) | parity |
| ★11 | **Mixed StreamChunk fields all processed** | FakeProvider emits one chunk with `ThinkingDelta` + `TextDelta` + `Usage` set together | Thinking panel updates, output text appended, and token usage recorded from the same chunk; nothing dropped (D-MIXED) | MVP (fake) |
| ★12 | **Doom-loop fires at exactly 3 batches** | Model emits the identical tool-call batch 3 consecutive times | A break message is injected as an assistant turn on the 3rd repeat; 2 repeats do **not** trigger it (exact-3 boundary) | MVP (fake) |
| ★13 | **12-step pipeline runs in order** | Model calls a writable tool with valid args, plan mode off, no cache | Steps 1→12 execute in order; journal (if present) shows schema→sanity→permission→execute→post sequence; result returned | MVP |
| 14 | Hardcoded binary block | `Bash` with `sudo rm -rf /` | Step 5 error contains "Blocked binary: sudo"; nothing runs | MVP |
| 15 | Hardcoded write-path block | `FileWrite` to `/etc/passwd` | Step 5 error contains "Protected path"; no write | MVP |
| 16 | Plan-mode guard | `/plan` on; model calls `FileWrite` | Exact message: `"Plan mode is active — only read-only tools are allowed. Call ExitPlanMode first to make changes with FileWrite."` | parity |
| 17 | Artifact threshold at 50,000 | `FileRead` returns > 50,000 chars (success) | Preview shows `[N lines omitted — full output in artifact {id} ({bytes} bytes)]`; full file under `~/.openmono/artifacts/` | parity |
| 18 | Cache hit / invalidation | Read-only tool called twice identically; then a write to the same path; then read again | 2nd call `[cached]`, no I/O; after write the next read is fresh, no `[cached]` (steps 6/11/12) | parity |
| 19 | Sibling-abort on crash | Two concurrent read-only tools; one returns `Crash` | The other receives `Cancelled("sibling abort")`, not `Crash`; no cascade; user Ctrl+C token untouched | MVP (fake) |
| 20 | Config additive no-dedup | user `deny:["FileWrite"]`, project `deny:["Bash","FileWrite"]` | Merged deny = `["FileWrite","Bash","FileWrite"]` (order preserved, duplicate retained) — port-CF2 | MVP |
| 21 | Scalar precedence | user `temperature:0.5`, project `temperature:0.8` | Effective `0.8` (project wins) | MVP |
| 22 | Malformed config skipped | project `settings.json` is invalid JSON | Warning printed; session starts on user config + defaults; no crash | MVP |
| 23 | Anthropic adapter parity | Anthropic provider active | `StreamChunk` field shapes identical to OpenAI-compat output; consumer code unchanged | parity |
| 24 | Qwen XML fallback | OpenAI-compat stream text contains `<function=foo>...</function>` | Text stream suppressed; `ToolCallDelta` for `foo` produced at `[DONE]` | MVP (fake) |
| 25 | Iteration cap | Model issues tool calls for 1000 straight iterations | Turn ends at 1000; session saved; no 1001st iteration | MVP |
| 26 | Resume restores history | Prior session with N messages | `/resume <id>` → next turn includes all N messages; sidecar loaded if present | MVP |
| 27 | RPC mode framing | `--mode rpc`; client sends a request frame containing a JSON string with embedded ` ` | Records split on `\n` only; the embedded separator does not corrupt framing | parity |
| 28 | Kernel runs with zero extensions | MVP build, no extensions loaded | Full agentic loop + tools + persistence work; no MCP/LSP/hooks/sub-agents present (A2 small-core proof) | MVP |

---

## Deliberate Non-Goals (MVP)

These are intentionally excluded from the minimum viable port. Each is an extension/adapter to be
added later through the Extension Surface (A2), not a core feature. Excluding them is what makes the
MVP small and the kernel provable (A4).

- **Full-screen TUI.** Classic-scroll renderer is sufficient for MVP; the Spectre.Console-equivalent TUI is parity-tier and renderer-agnostic via `EventSink`. *(inspiration: pi ships powerful defaults but defers UX flourishes to later/extensions.)*
- **MCP and LSP.** stdio JSON-RPC extensions; valuable but optional. When built they must obey D-RPCID and D-DRAIN. *(pi-mono README §Philosophy: "No MCP … build an extension that adds MCP support.")*
- **Hooks.** Pre/post-tool shell hooks. Deferred; when added they must obey D-HOOKINJ (data not shell source) and D-HOOKKILL (process-tree kill on timeout).
- **Background bash.** Deferred; requires the D-BGPROC lifecycle manager before it ships. *(pi-mono README: "No background bash. Use tmux.")*
- **Sub-agents.** Deferred; an extension that reuses the parent Permission Engine and runs the same Tool Pipeline. *(pi-mono README: "No sub-agents … build your own with extensions.")*
- **Plan mode.** Deferred; a permission-gate + tool-filter extension, not a core flag. *(pi-mono README: "No plan mode.")*
- **Playbooks.** YAML workflow engine; full-tier. `{{shell:…}}` must be scoped behind explicit user trust (D-PBSHELL).
- **Memory.** Cross-session YAML memory injected into the system prompt; full-tier; replaceable with any K/V store.
- **Docker wrapper / `openmono` product shell.** Deployment artifact; Linux-native binary first (A1).
- **Roslyn / language-semantic tooling (`RoslynTool`).** C#-specific; dropped for non-.NET targets, or kept only in a .NET refactor.
- **Carried minor non-goals from the 5-phase spec:** `StepDefinition.Playbook`/`Output` reserved fields (prot-OQ3), `Terminal.Gui` dependency (arch-OQ4), KV-cache warmup probe (deprioritized until inference integration is stable), `SecretScanner` (until S-SECRET pins its behavior).

---

## Known Unknowns

Items still genuinely unknown — need a prototype, runtime test, or maintainer/spec decision.
Terminal `open_questions` for this pipeline.

| ID | Kind | Description | Deferred Reason |
|---|---|---|---|
| arch-OQ2 | needs-spec-ruling | `global.json` .NET 10 SDK pin (preview vs stable) unread | Only affects a .NET-refactor build; read `global.json` before building that target |
| arch-OQ4 | needs-runtime-test | `Terminal.Gui` present in `.csproj` but `AnsiTuiRenderer` uses Spectre.Console; is it live or dead? | Grep usage; omit from port deps if unused |
| arch-OQ5 | needs-maintainer-decision | README "25 iterations" vs code `maxIterations=1000`; which is the documented contract? | Code (1000) is authoritative for the port; README needs maintainer correction |
| contr-OQ1 | needs-runtime-test | `SecretScanner` integration point (inputs? outputs? session content?) unconfirmed | Trace call sites before deciding inclusion (Spike S-SECRET) |
| contr-OQ2 | needs-maintainer-decision | `Ask` permission field documented as "always prompt" but never evaluated — implement or deprecate? | Port can't faithfully clone unimplemented behavior; gates D-ASK (Spike S-ASK) |
| prot-OQ1 | needs-runtime-test | LSP wire format depth + whether `LspServerManager` serializes like MCP's `SemaphoreSlim(1,1)` | LSP is optional; spike after MCP adapter is stable |
| prot-OQ2 | needs-runtime-test | Is `SessionState.Meta.PlanMode` persisted to JSONL? (current inference: no) | Affects whether plan mode survives resume |
| prot-OQ3 | needs-maintainer-decision | `StepDefinition.Playbook`/`Output` exist in schema but unused by executor — intended or unfinished? | Determines whether ports implement nested playbooks |
| spec-OQ1 | needs-maintainer-decision | Primary implementation language not yet locked (A3): Go rewrite vs TS/pi-like vs .NET refactor | Kernel contracts are language-neutral; the choice changes adapter primitives only, so it can be made after Phase 1 |

---

## Carry-Forward

Post-pipeline items (spikes/amendments) — see Spike List for the runnable ones.

| ID | Target Phase | Description | Deferred Reason |
|---|---|---|---|
| spike-01 | spike | Verify `SecretScanner` integration point (closes contr-OQ1) | Requires instrumenting the original at runtime |
| spike-02 | spike | `Ask` field behavioral decision + test (closes contr-OQ2, gates D-ASK) | Requires a maintainer decision |
| spike-03 | spike | LSP serialization model vs MCP (closes prot-OQ1) | LSP optional; after MCP adapter is stable |
| amend-01 | amendment | Lock the primary implementation language and instantiate the per-language adapter primitives (closes spec-OQ1) | Decision deferred until kernel acceptance suite is green (A4) |

---

## Spike List

- **S-TOK — Tokenizer equivalence.** For each target model family the port will support
  (GPT-4o, Claude 3.x, Qwen2, LLaMA 3.x), run the selected tokenizer against a fixed 10-message
  transcript and compare to the reference; must be within ±2% before the 65%/80% thresholds go live
  (port-CF1).
- **S-ATOMIC — Atomic rename / cross-filesystem.** On the Linux target, confirm `rename(2)` is atomic
  when temp and destination share a filesystem; if `~/.openmono/` is a separate mount, document the
  copy+fsync+delete fallback (D-ATOMIC).
- **S-ABORT — Sibling-abort timing.** With 3+ concurrent read-only tools, verify sibling-abort reaches
  all siblings within ~100 ms of the crashing task returning `Crash`, and that none flips to `Crash`
  (Turn Executor, D-INFLIGHT).
- **S-INFLIGHT — In-flight side-effect serialization.** Drive a Window-1 tool that emits UI events and
  requests a permission prompt while the provider stream is still open; confirm prompts/UI/journal
  writes are serialized and never interleave (D-INFLIGHT).
- **S-RPC — JSON-RPC robustness.** Fuzz an MCP/LSP server with interleaved notifications, out-of-order
  ids, and stderr floods; confirm D-RPCID + D-DRAIN hold and the agent loop never wedges.
- **S-MCPLAT — MCP high-latency.** With a 1 s/response MCP server, measure loop latency under
  `SemaphoreSlim(1,1)` serialization; if > 5 s/call, evaluate `id`-correlated pipelining.
- **S-SECRET — Secret-scanner behavior.** Trace `SecretScanner` call sites in the original; decide
  whether the port gates tool inputs/outputs/session content (closes contr-OQ1).
- **S-ASK — `Ask` rule decision.** Integration-test the original to confirm `Ask` is dead; then make
  the implement-or-deprecate call (closes contr-OQ2, gates D-ASK).
- **S-LANG — Language fit.** Prototype the Session Runtime + ProviderPort + FakeProvider in the two
  leading candidate languages (e.g., Go and TypeScript) to confirm the async-stream/cancellation
  primitives bind cleanly before locking the stack (spec-OQ1, amend-01, A3).

---

## Validation

| # | Criterion | Result | Evidence |
|---|-----------|--------|----------|
| 1 | Concept-level modules are defined. | PASS | §Conceptual Module Model defines kernel ports + 6 kernel modules + 5 adapters + the Extension Surface (with a 10-row capability-as-extension table) + delivery surfaces, each with Responsibility / Public inputs / Public outputs / Owned state / Invariants / Collaborators; §Layer Split maps every module to core/adapter/extension/delivery. |
| 2 | Required behaviors are stated. | PASS | §Required Behaviors enumerates non-negotiables across loop, pipeline, permissions, doom-loop, streaming/concurrency, config, persistence, provider normalization, cancellation, and subprocess discipline, with deep-audit corrections inlined. |
| 3 | Protocol and persisted state expectations are stated. | PASS | §Protocols and Persisted State covers B1/B2/B3 + RPC framing, SM1–SM4, and all six persistence schemas with field-level detail and atomicity/header rules. |
| 4 | Acceptance scenarios and known unknowns are included. | PASS | §Acceptance Scenarios — 28 black-box checks including all 13 prompt-required emphases (★1–★13 + ★5b/★7a/★7b) with tiers; §Known Unknowns — 9 items with kind + deferred reason; §Spike List — 9 spikes. |
| 5 | Defects identified in either scan are explicitly designed-around or noted as "left behind", with the choice cited. | PASS | §Do-Not-Clone Defect Rules dispositions all 14 mechanical + 13 semantic findings into fix / port-differently / leave-behind, each citing its source finding (mech Pass N / sem Pass N) and giving a binding design rule; cross-referenced from module invariants and acceptance scenarios. |
| 6 | Findings are marked with evidence levels. | PASS | Tags used throughout (*observed fact / strong inference / portability hazard / design decision*); pi-mono references explicitly marked inspiration-only in front matter and inline; user-approved assumptions recorded in front matter and §System Summary. |

**Validated by:** 2026-05-24 (reimplementation-spec phase — implementing session, deep-audit revision)
**Overall:** PASS
