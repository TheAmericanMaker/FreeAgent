// Minimal protocol-client demo. Spins up a session against FreeAgent.Server, sends one user turn
// from argv, and prints the SSE stream as it arrives. Not the full opentui-based TUI yet — this
// is the protocol-client foundation the full TUI builds on (per ADR 0005, phase 3).
//
// Usage:
//   bun run src/index.ts "what's in this directory?"
//   FREEAGENT_BASE_URL=http://localhost:5000 FREEAGENT_API_KEY=secret bun run src/index.ts "hi"

import { FreeAgentClient } from './protocol';

const baseUrl = process.env.FREEAGENT_BASE_URL ?? 'http://localhost:5000';
const apiKey = process.env.FREEAGENT_API_KEY;

const userInput = process.argv.slice(2).join(' ') || 'Hello.';
const client = new FreeAgentClient({ baseUrl, apiKey });

const { sessionId, workingDirectory } = await client.createSession();
console.log(`[freeagent-tui] session ${sessionId} in ${workingDirectory}`);

const controller = new AbortController();
process.on('SIGINT', () => {
  console.error('\n[freeagent-tui] cancelling…');
  controller.abort();
});

try {
  for await (const ev of client.streamTurn(sessionId, userInput, controller.signal)) {
    switch (ev.event) {
      case 'text':
        process.stdout.write(ev.data.chunk);
        break;
      case 'thinking':
        process.stderr.write(`\x1b[2m${ev.data.chunk}\x1b[0m`);
        break;
      case 'tool_call':
        console.log(`\n[tool] ${ev.data.tool} ${ev.data.arguments}`);
        break;
      case 'tool_result':
        console.log(`[result/${ev.data.kind}] ${ev.data.content.slice(0, 200)}${ev.data.content.length > 200 ? '…' : ''}`);
        break;
      case 'usage':
        console.error(`\x1b[2m[Tokens: ${ev.data.input} → ${ev.data.output}]\x1b[0m`);
        break;
      case 'done':
        console.log(`\n[done${ev.data.doomLoopDetected ? ' — doom loop' : ''}]`);
        break;
      case 'cancelled':
        console.error('[cancelled]');
        break;
      case 'error':
        console.error(`[error] ${ev.data.message}`);
        break;
    }
  }
} finally {
  await client.deleteSession(sessionId).catch(() => undefined);
}
