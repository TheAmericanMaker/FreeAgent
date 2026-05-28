# FreeAgent VS Code extension

A VS Code client for the `FreeAgent.Server` HTTP + SSE protocol. Scaffold today; the full UX
(inline diffs, code-action integration, tool-approval prompts) builds on top of these commands.

## What's here

- **`FreeAgent: New Session`** — POSTs to `/sessions` with the open workspace folder as the
  working directory; the session id appears in the bottom-right status bar.
- **`FreeAgent: Ask…`** — prompts for input, opens the "FreeAgent" output channel, and streams
  the SSE response into it (text + tool calls + done/error events).
- **Settings**: `freeagent.baseUrl` (default `http://localhost:5000`) and `freeagent.apiKey`
  (required if the server has `FREEAGENT_SERVER_API_KEY` set).

## Build + run

```bash
cd clients/vscode
npm install
npm run compile
code --extensionDevelopmentPath=$(pwd)
```

In the Extension Development Host that opens, run **FreeAgent: New Session**, then
**FreeAgent: Ask…**.

## Status

Scaffold. The shared TypeScript protocol client lives in [`clients/tui/src/protocol.ts`](../tui/src/protocol.ts);
this extension currently inlines a minimal copy so the two roots stay independent. A shared
package (`@freeagent/client`) is the obvious refactor once both clients grow more code.
