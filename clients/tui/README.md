# @freeagent/tui

Protocol-client foundation for the FreeAgent TUI (ADR 0005 phase 3). Talks to the
`FreeAgent.Server` HTTP + SSE surface over `fetch`; no agent logic lives here.

This package currently ships the **protocol client** (`src/protocol.ts`) and a **smoke CLI**
(`src/index.ts`) that creates a session, submits one turn from argv, and prints the SSE stream.
The full-screen opentui UI builds on top of these primitives — when that lands, the `start` script
launches the renderer instead of the smoke CLI.

## Run it

```bash
# In one terminal, start the kernel server:
dotnet run --project ../../src/FreeAgent.Server          # http://localhost:5000

# In another, run the client:
cd clients/tui
bun install
bun run src/index.ts "what's in this directory?"
```

`FREEAGENT_BASE_URL` and `FREEAGENT_API_KEY` override the defaults (the latter is required when
the server has `FREEAGENT_SERVER_API_KEY` set).

## Why Bun

Per ADR 0005, opencode's TUI uses **opentui** (Zig + C-ABI + TypeScript), which isn't embeddable
in .NET. Bun is the runtime opentui targets, so the protocol-client foundation lives here so the
full UI can attach to it later without retooling.

## Tests

```bash
bun test
```

The SSE parser is fully unit-tested. Network CRUD paths are exercised against the live server in
`src/FreeAgent.Kernel.Tests/Server/SessionEndpointsTests.cs`.
