# 0005: Headless core + protocol, with pluggable frontends

Status: Accepted (target architecture; adopted in phases)

## Context

FreeAgent is a C# kernel (`FreeAgent.Kernel`) with a thin interactive CLI host
(`FreeAgent.Host`). The kernel is already effectively headless: `SessionRuntime` drives the agentic
loop and emits everything through the `IEventSink` seam, and the host is a thin frontend that owns
only I/O and configuration.

We want, over time, an opencode-grade full-screen TUI, and plausibly a web frontend, editor
integrations (ACP), and remote access. Investigation of the relevant projects shaped this decision:

- **opencode** runs a headless agent **server** (HTTP API described by an OpenAPI spec + an SSE
  event stream); its TUI is a pure **client** that can `attach` to a local *or* remote server and
  holds zero agent logic. Frontends are generated against the spec.
- **opentui** (the TUI library opencode uses) is Zig + C-ABI + TypeScript and is **not** embeddable
  in .NET without reimplementing its TypeScript framework layer — so the only realistic way to get
  that TUI from a C# core is to run the frontend as a separate process over a protocol.
- **pi-mono** confirms a normalized provider seam is enough to add providers without core changes.
- **LocalAI / llama.cpp / Ollama / exo** all already expose OpenAI-compatible HTTP, so *running*
  models is orchestration, not embedding.

## Decision

Adopt a **headless core + protocol** architecture as the target. The C# kernel exposes a server —
an HTTP API described by an **OpenAPI spec** plus an **SSE event stream** — and every frontend (the
TUI, a web UI, editors via ACP, remote/CLI clients) is a **client of that one protocol**. This
generalizes ADR 0004's "future SDK/RPC modes" seam into the primary integration surface.

Local model *running* is **orchestrated** by the core (download a model, launch/health-check a local
OpenAI-compatible server, point the existing provider at it) — never an embedded inference engine.

## Phasing (so this is additive, not a rewrite)

1. Keep building near-term/coming-next features **in-process**; keep `SessionRuntime` / `IEventSink`
   / input strictly frontend-agnostic so the seam stays clean.
2. Add a **protocol server** project that hosts `SessionRuntime` and bridges `IEventSink` + input to
   HTTP + SSE, emitting an OpenAPI spec. The kernel does not change.
3. Build the first protocol **frontend** — a Bun/opentui TUI client (opencode-style). The existing
   console host remains the minimal built-in/fallback client.

## Consequences

- **Positive:** one protocol → many frontends; the opencode-style TUI becomes reachable from a C#
  core; web/editor/remote reuse the same surface; the kernel already supports it via `IEventSink`,
  so adoption is additive; local-model orchestration fits naturally beside the server.
- **Negative / cost:** a polyglot repo (a Bun/TypeScript frontend), a protocol/schema to design,
  version, and generate clients from, and more moving parts than a single in-process binary. The
  plain console host is retained as the zero-frontend path to contain that cost.

## Alternatives considered

- **Native .NET TUI embedded in-process** (`Spectre.Console` / `Terminal.Gui`): simplest, single
  language and process, but cannot deliver the opencode-grade TUI and does not generalize to web /
  editor / remote frontends. Rejected as the primary direction; may survive as a minimal fallback
  renderer.
- **Embedding inference in-process** (LLamaSharp / P/Invoke): rejected for local-model running —
  per-platform native dependencies and crash coupling, with no benefit given the kernel already
  speaks OpenAI-compatible.

## Related

ADR 0004 (extension-first capabilities); the roadmap's "Architecture direction", "Local model
runner", and provider-hardening items.
