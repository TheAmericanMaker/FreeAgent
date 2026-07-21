# @freeagent/tui

The full-screen, opencode-style TUI for FreeAgent (ADR 0005 phase 3). A React + [opentui](https://github.com/anomalyco/opentui)
app that talks to the `FreeAgent.Server` HTTP + SSE surface over `fetch` — no agent logic lives here;
the kernel stays a separate process behind the documented protocol.

## What's here

| File              | Purpose                                                                          |
| ----------------- | -------------------------------------------------------------------------------- |
| `src/tui.tsx`        | Entry point. Creates the renderer and mounts the app.                            |
| `src/App.tsx`        | Connection (find/launch server) + connecting/error screens + screen routing.     |
| `src/ui/Chat.tsx`    | Streaming chat: assistant Markdown, thinking, tool calls/results, /commands.     |
| `src/ui/Setup.tsx`   | In-app setup wizard: provider → credentials (+ test) → model + working dir.      |
| `src/ui/Settings.tsx`| Settings menu: provider/model, permissions editor, project trust.                |
| `src/server.ts`      | Finds or launches `FreeAgent.Server` (published binary, else `dotnet run`).      |
| `src/protocol.ts`    | Typed client for the server's sessions + config API (the SSE parser too).        |

**No terminal setup needed.** On first run the TUI detects there's no usable provider and drops you
into an in-app wizard: pick a provider, paste a key (masked), test the connection, choose a model and
working directory, and save — all without leaving the UI. Re-open it anytime with Ctrl+S.

## Install & run

One-time setup (installs Bun if needed, restores deps, and publishes the server as a self-contained
binary so it launches instantly with **no .NET SDK needed at run time**):

```powershell
# Windows
powershell -ExecutionPolicy Bypass -File ..\..\scripts\install-tui.ps1
..\..\scripts\freeagent-ui.ps1
```

```bash
# Linux / macOS
bash ../../scripts/install-tui.sh
../../scripts/freeagent-ui
```

Prefer to do it by hand, or developing? `cd clients/tui && bun install && bun run tui` — the TUI
finds the published binary if present, otherwise falls back to `dotnet run` (needs the .NET SDK).

### Environment

| Var                    | Default                                     | Meaning                                            |
| ---------------------- | ------------------------------------------- | -------------------------------------------------- |
| `FREEAGENT_BASE_URL`   | `http://localhost:5000`                     | Server URL to connect to.                          |
| `FREEAGENT_API_KEY`    | —                                           | Bearer token, if the server requires one.          |
| `FREEAGENT_SERVER_CMD` | published binary, else `dotnet run …Server` | Override how the server is launched.               |

### Keys & commands

`Enter` send · `Esc` cancel the running turn · `Ctrl+S` settings · `Ctrl+C` quit ·
`PageUp`/`PageDown` scroll · `Tab`/`Shift+Tab` move between fields in setup.

Slash commands: `/help`, `/new`, `/clear`, `/model [id]`, `/settings`, `/quit`.

## Why Bun + opentui

Per ADR 0005, opencode-grade terminal UI uses **opentui** (Zig + C-ABI + TypeScript), which isn't
embeddable in .NET — so the frontend is a separate process speaking the protocol. Bun is the runtime
opentui targets.

## Develop

```bash
bun test         # protocol client + SSE parser unit tests
bun run typecheck   # tsc --noEmit over the whole app
bun run smoke "what's in this directory?"   # one-shot protocol CLI (no UI), handy for debugging
```

The networked CRUD + config paths are also exercised against the live server in
`src/FreeAgent.Kernel.Tests/Server/`.
