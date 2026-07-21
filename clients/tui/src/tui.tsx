// Entry point for the full-screen TUI. Creates the renderer and hands off to the React/opentui app,
// which owns connecting to (or launching) the server, setup, and chat — so connection progress and
// errors render inside the UI with retry, rather than as pre-launch console noise.
//
// Env:
//   FREEAGENT_BASE_URL    server URL (default http://localhost:5000)
//   FREEAGENT_API_KEY     bearer token, if the server requires one
//   FREEAGENT_SERVER_CMD  override the launch command (default: a published binary, else dotnet run)

import { createCliRenderer } from '@opentui/core';
import { createRoot } from '@opentui/react';
import { App } from './App';

const baseUrl = process.env.FREEAGENT_BASE_URL ?? 'http://localhost:5000';
const apiKey = process.env.FREEAGENT_API_KEY;
const serverCmd = process.env.FREEAGENT_SERVER_CMD;

const renderer = await createCliRenderer({ exitOnCtrlC: false });
const root = createRoot(renderer).render(<App baseUrl={baseUrl} apiKey={apiKey} serverCmd={serverCmd} />);

// Expose a clean-quit function that tears down the renderer before exiting.
// process.exit(0) alone leaves the terminal in raw mode — calling destroy()
// first restores the terminal to its normal state.
(globalThis as any).__freeagentQuit = () => {
  try { renderer.destroy(); } catch { /* best-effort */ }
  process.exit(0);
};
