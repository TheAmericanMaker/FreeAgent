// SSE-parser tests. The networked CRUD paths are exercised against the live server in
// FreeAgent.Kernel.Tests; here we cover the parser logic that runs purely in the client.

import { afterEach, describe, expect, test } from 'bun:test';
import { FreeAgentClient, parseSseStream } from './protocol';

function streamOf(...chunks: string[]): ReadableStream<Uint8Array> {
  const encoder = new TextEncoder();
  return new ReadableStream({
    start(controller) {
      for (const c of chunks) controller.enqueue(encoder.encode(c));
      controller.close();
    },
  });
}

describe('parseSseStream', () => {
  test('parses a single event with JSON data', async () => {
    const events: unknown[] = [];
    for await (const ev of parseSseStream(streamOf('event: text\ndata: {"chunk":"hi"}\n\n'))) {
      events.push(ev);
    }
    expect(events).toEqual([{ event: 'text', data: { chunk: 'hi' } }]);
  });

  test('joins multiple events in a single chunk', async () => {
    const events: unknown[] = [];
    for await (const ev of parseSseStream(streamOf(
      'event: text\ndata: {"chunk":"a"}\n\n' +
      'event: text\ndata: {"chunk":"b"}\n\n'
    ))) {
      events.push(ev);
    }
    expect(events).toHaveLength(2);
  });

  test('reassembles events split across chunks', async () => {
    const events: unknown[] = [];
    for await (const ev of parseSseStream(streamOf('event: do', 'ne\ndata: {"finalText":', '"ok","doomLoopDetected":false}\n\n'))) {
      events.push(ev);
    }
    expect(events).toEqual([{ event: 'done', data: { finalText: 'ok', doomLoopDetected: false } }]);
  });

  test('skips comment lines and malformed records', async () => {
    const events: unknown[] = [];
    for await (const ev of parseSseStream(streamOf(
      ': keep-alive\n\n' +              // pure comment record → skipped
      'data: orphan\n\n' +              // no event field → skipped
      'event: text\ndata: not-json\n\n' + // invalid JSON → skipped
      'event: text\ndata: {"chunk":"ok"}\n\n'
    ))) {
      events.push(ev);
    }
    expect(events).toEqual([{ event: 'text', data: { chunk: 'ok' } }]);
  });
});

describe('FreeAgentClient config methods', () => {
  const realFetch = globalThis.fetch;
  let calls: { url: string; init?: RequestInit }[] = [];

  function stubFetch(handler: (url: string, init?: RequestInit) => { status?: number; body?: unknown }) {
    calls = [];
    globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      calls.push({ url, init });
      const { status = 200, body = {} } = handler(url, init);
      return new Response(typeof body === 'string' ? body : JSON.stringify(body), { status });
    }) as typeof fetch;
  }

  afterEach(() => {
    globalThis.fetch = realFetch;
  });

  test('getModels encodes the provider query', async () => {
    stubFetch(() => ({ body: [{ id: 'gpt-4o', wireApi: 'openai' }] }));
    const client = new FreeAgentClient({ baseUrl: 'http://x' });
    const models = await client.getModels('openai');
    expect(calls[0]!.url).toBe('http://x/models?provider=openai');
    expect(models[0]!.id).toBe('gpt-4o');
  });

  test('updateProvider PUTs the request body with auth header', async () => {
    stubFetch(() => ({ status: 200, body: { provider: 'openai' } }));
    const client = new FreeAgentClient({ baseUrl: 'http://x', apiKey: 'secret' });
    await client.updateProvider({ provider: 'openai', apiKey: 'sk-1', setAsDefault: true });
    const call = calls[0]!;
    expect(call.url).toBe('http://x/config/provider');
    expect(call.init!.method).toBe('PUT');
    expect((call.init!.headers as Record<string, string>).authorization).toBe('Bearer secret');
    expect(JSON.parse(call.init!.body as string)).toMatchObject({ provider: 'openai', apiKey: 'sk-1' });
  });

  test('testProvider parses the probe result', async () => {
    stubFetch(() => ({ body: { ok: false, message: 'No API key set.', mode: 'fields' } }));
    const client = new FreeAgentClient({ baseUrl: 'http://x' });
    const result = await client.testProvider({ provider: 'openai' });
    expect(result.ok).toBe(false);
    expect(result.mode).toBe('fields');
  });

  test('ping returns false when the server is unreachable', async () => {
    globalThis.fetch = (async () => {
      throw new Error('ECONNREFUSED');
    }) as unknown as typeof fetch;
    const client = new FreeAgentClient({ baseUrl: 'http://x' });
    expect(await client.ping()).toBe(false);
  });

  test('a non-ok response rejects with the status', async () => {
    stubFetch(() => ({ status: 400, body: 'bad' }));
    const client = new FreeAgentClient({ baseUrl: 'http://x' });
    await expect(client.getConfig()).rejects.toThrow('400');
  });
});
