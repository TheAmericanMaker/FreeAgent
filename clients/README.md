# FreeAgent clients

Protocol clients for the `FreeAgent.Server` HTTP + SSE surface (per ADR 0005). The .NET kernel is
headless; every frontend lives in its own ecosystem and talks to the server over the same wire
contract (`/openapi/v1.json`).

| Path                          | Status   | Description                                                                 |
| ----------------------------- | -------- | --------------------------------------------------------------------------- |
| [`tui/`](tui/)                | Scaffold | Bun + TypeScript. Protocol client + smoke CLI. The opentui-based full-screen UI builds on top. |
| [`vscode/`](vscode/)          | Scaffold | VS Code extension. "New Session" + "Ask…" commands streaming into an output channel. |

Each client is self-contained (its own `package.json`); they do NOT share a build with the .NET
projects. Tests for the .NET surface they consume live in `src/FreeAgent.Kernel.Tests/Server/`.
