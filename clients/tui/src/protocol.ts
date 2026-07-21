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

// ── Config / setup types (mirror FreeAgent.Server's ConfigEndpoints DTOs) ─────────────────────
export interface ProviderField {
  slot: string;
  label: string;
  default: string | null;
  secret: boolean;
}

export interface ProviderInfo {
  id: string;
  description: string;
  requiresApiKey: boolean;
  fields: ProviderField[];
}

export interface ModelInfo {
  id: string;
  wireApi: string;
  contextTokens: number | null;
  maxOutputTokens: number | null;
  supportsTools: boolean;
  supportsVision: boolean;
  supportsThinking: boolean;
}

export interface ProviderView {
  baseUrl: string | null;
  model: string | null;
  apiVersion: string | null;
  apiKeySet: boolean;
  apiKeyHint: string | null;
}

export interface ConfigView {
  activeProvider: string;
  configPath: string;
  providers: Record<string, ProviderView>;
}

export interface UpdateProviderRequest {
  provider: string;
  apiKey?: string;
  baseUrl?: string;
  model?: string;
  apiVersion?: string;
  setAsDefault?: boolean;
}

export interface ProbeResult {
  ok: boolean;
  message: string;
  mode: 'live' | 'fields' | 'skipped';
}

export interface CapabilityRule {
  capability: string;
  pattern: string | null;
}

export interface PermissionsView {
  allowTools: string[];
  denyTools: string[];
  allow: CapabilityRule[];
  deny: CapabilityRule[];
  knownCapabilities: string[];
  builtinTools: string[];
  configPath: string;
  error: string | null;
}

export interface TrustView {
  workingDirectory: string;
  trusted: boolean;
  requests: string[];
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

  // ── Config / setup ───────────────────────────────────────────────────────────────────────────
  /** Lightweight GET that doubles as a reachability probe — used to decide whether to spawn the server. */
  async ping(signal?: AbortSignal): Promise<boolean> {
    try {
      const r = await fetch(`${this.opts.baseUrl}/config`, { headers: this.headers(), signal });
      return r.ok;
    } catch {
      return false;
    }
  }

  async getProviders(): Promise<ProviderInfo[]> {
    return this.getJson<ProviderInfo[]>('/providers');
  }

  async getModels(provider?: string): Promise<ModelInfo[]> {
    const q = provider ? `?provider=${encodeURIComponent(provider)}` : '';
    return this.getJson<ModelInfo[]>(`/models${q}`);
  }

  async getConfig(): Promise<ConfigView> {
    return this.getJson<ConfigView>('/config');
  }

  async updateProvider(req: UpdateProviderRequest): Promise<void> {
    await this.send('PUT', '/config/provider', req);
  }

  async testProvider(req: UpdateProviderRequest): Promise<ProbeResult> {
    return this.send<ProbeResult>('POST', '/config/provider/test', req);
  }

  async getPermissions(dir?: string): Promise<PermissionsView> {
    const q = dir ? `?dir=${encodeURIComponent(dir)}` : '';
    return this.getJson<PermissionsView>(`/config/permissions${q}`);
  }

  async updatePermissions(body: {
    dir: string;
    allowTools?: string[];
    denyTools?: string[];
    allow?: CapabilityRule[];
    deny?: CapabilityRule[];
  }): Promise<void> {
    await this.send('PUT', '/config/permissions', body);
  }

  async getTrust(dir?: string): Promise<TrustView> {
    const q = dir ? `?dir=${encodeURIComponent(dir)}` : '';
    return this.getJson<TrustView>(`/config/trust${q}`);
  }

  async trust(dir: string): Promise<void> {
    await this.send('POST', '/config/trust', { dir });
  }

  private async getJson<T>(path: string): Promise<T> {
    const r = await fetch(`${this.opts.baseUrl}${path}`, { headers: this.headers() });
    if (!r.ok) throw new Error(`GET ${path} failed: ${r.status} ${await r.text()}`);
    return r.json() as Promise<T>;
  }

  private async send<T = void>(method: string, path: string, body: unknown): Promise<T> {
    const r = await fetch(`${this.opts.baseUrl}${path}`, {
      method,
      headers: this.headers(),
      body: JSON.stringify(body),
    });
    if (!r.ok) throw new Error(`${method} ${path} failed: ${r.status} ${await r.text()}`);
    // Some endpoints (PUT) return a small ack we don't need; tolerate empty bodies.
    const text = await r.text();
    return (text ? JSON.parse(text) : undefined) as T;
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
