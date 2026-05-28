// Thin client for FreeAgent.Server's HTTP + SSE protocol surface. Pure — no UI here, so it can
// be unit-tested with `bun test` and reused by any frontend (TUI, web, future GUI). The OpenAPI
// document at /openapi/v1.json is the source of truth; keep these types narrow but in sync with
// what the server actually returns.

export interface ClientOptions {
  baseUrl: string;
  apiKey?: string;
}

export interface SessionSummary {
  sessionId: string;
  workingDirectory: string;
  messageCount: number;
  planMode: boolean;
  tags: string[];
  totalIterations: number;
}

export type SseEvent =
  | { event: 'text'; data: { chunk: string } }
  | { event: 'thinking'; data: { chunk: string } }
  | { event: 'tool_call'; data: { tool: string; arguments: string } }
  | { event: 'tool_result'; data: { tool: string; kind: string; content: string } }
  | { event: 'usage'; data: { input: number; output: number } }
  | { event: 'done'; data: { finalText: string; doomLoopDetected: boolean } }
  | { event: 'cancelled'; data: Record<string, never> }
  | { event: 'error'; data: { message: string } };

export class FreeAgentClient {
  constructor(private readonly opts: ClientOptions) {}

  private headers(): Record<string, string> {
    const h: Record<string, string> = { 'content-type': 'application/json' };
    if (this.opts.apiKey) h.authorization = `Bearer ${this.opts.apiKey}`;
    return h;
  }

  async createSession(workingDirectory?: string): Promise<{ sessionId: string; workingDirectory: string }> {
    const r = await fetch(`${this.opts.baseUrl}/sessions`, {
      method: 'POST',
      headers: this.headers(),
      body: JSON.stringify({ workingDirectory: workingDirectory ?? null }),
    });
    if (!r.ok) throw new Error(`createSession failed: ${r.status} ${await r.text()}`);
    return r.json() as Promise<{ sessionId: string; workingDirectory: string }>;
  }

  async listSessions(): Promise<string[]> {
    const r = await fetch(`${this.opts.baseUrl}/sessions`, { headers: this.headers() });
    if (!r.ok) throw new Error(`listSessions failed: ${r.status}`);
    return r.json() as Promise<string[]>;
  }

  async getSession(id: string): Promise<SessionSummary> {
    const r = await fetch(`${this.opts.baseUrl}/sessions/${id}`, { headers: this.headers() });
    if (!r.ok) throw new Error(`getSession failed: ${r.status}`);
    return r.json() as Promise<SessionSummary>;
  }

  async deleteSession(id: string): Promise<void> {
    const r = await fetch(`${this.opts.baseUrl}/sessions/${id}`, { method: 'DELETE', headers: this.headers() });
    if (!r.ok && r.status !== 404) throw new Error(`deleteSession failed: ${r.status}`);
  }

  /**
   * Submit a user turn and yield each SSE event as it arrives. The server streams text / thinking /
   * tool_call / tool_result / usage events while the turn runs, then a final `done` event with the
   * assembled reply, or `cancelled` / `error`. Caller controls cancellation via the AbortSignal.
   */
  async *streamTurn(id: string, userInput: string, signal?: AbortSignal): AsyncGenerator<SseEvent> {
    const response = await fetch(`${this.opts.baseUrl}/sessions/${id}/turns`, {
      method: 'POST',
      headers: this.headers(),
      body: JSON.stringify({ userInput }),
      signal,
    });
    if (!response.ok || !response.body) {
      throw new Error(`streamTurn failed: ${response.status} ${await response.text()}`);
    }
    yield* parseSseStream(response.body);
  }
}

/**
 * Parse the server's `event: <name>\ndata: <json>\n\n` framing into typed SseEvent objects. The
 * server-side `HttpSseEventSink` emits exactly that shape; the parser tolerates `:` comment lines
 * and missing-event-name records by skipping them.
 */
export async function* parseSseStream(stream: ReadableStream<Uint8Array>): AsyncGenerator<SseEvent> {
  const reader = stream.getReader();
  const decoder = new TextDecoder();
  let buffer = '';
  try {
    while (true) {
      const { done, value } = await reader.read();
      if (done) break;
      buffer += decoder.decode(value, { stream: true });

      // SSE records are separated by a blank line; flush as many complete ones as we have.
      let sepIndex: number;
      while ((sepIndex = buffer.indexOf('\n\n')) >= 0) {
        const record = buffer.slice(0, sepIndex);
        buffer = buffer.slice(sepIndex + 2);
        const parsed = parseRecord(record);
        if (parsed) yield parsed;
      }
    }
  } finally {
    reader.releaseLock();
  }
}

function parseRecord(record: string): SseEvent | null {
  let event: string | null = null;
  let data: string | null = null;
  for (const line of record.split('\n')) {
    if (line.startsWith(':') || line.length === 0) continue;
    const colon = line.indexOf(':');
    if (colon < 0) continue;
    const field = line.slice(0, colon);
    // Skip exactly one space after the colon (SSE convention).
    const value = line.slice(colon + 1).replace(/^ /, '');
    if (field === 'event') event = value;
    else if (field === 'data') data = data === null ? value : data + '\n' + value;
  }
  if (event === null || data === null) return null;
  try {
    return { event, data: JSON.parse(data) } as SseEvent;
  } catch {
    return null;
  }
}
