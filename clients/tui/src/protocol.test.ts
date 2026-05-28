// SSE-parser tests. The networked CRUD paths are exercised against the live server in
// FreeAgent.Kernel.Tests; here we cover the parser logic that runs purely in the client.

import { describe, expect, test } from 'bun:test';
import { parseSseStream } from './protocol';

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
