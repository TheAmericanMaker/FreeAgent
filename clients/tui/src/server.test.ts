// Unit tests for the server launch-command resolution. Pure logic — no process is spawned — so we can
// assert the production-vs-dev precedence that makes "easily install and run" work. Paths are built
// with node:path so the assertions hold on both Windows (backslashes/drive) and POSIX.

import { describe, expect, test } from 'bun:test';
import { resolve } from 'node:path';
import { isDevCommand, resolveServerCommand } from './server';

const DIST = resolve('/dist/server');
const DEV = resolve('/repo/src/FreeAgent.Server');
const binPosix = resolve(DIST, 'FreeAgent.Server');
const binWin = resolve(DIST, 'FreeAgent.Server.exe');
const dll = resolve(DIST, 'FreeAgent.Server.dll');

const opts = (overrides: Partial<Parameters<typeof resolveServerCommand>[0]> = {}) => ({
  distDir: DIST,
  devProject: DEV,
  isWindows: false,
  exists: () => false,
  serverCmd: '',
  ...overrides,
});

describe('resolveServerCommand', () => {
  test('explicit FREEAGENT_SERVER_CMD wins and is space-split', () => {
    expect(resolveServerCommand(opts({ serverCmd: 'my-server --port 9' }))).toEqual(['my-server', '--port', '9']);
  });

  test('prefers a published self-contained binary when present', () => {
    const argv = resolveServerCommand(opts({ exists: (p) => p === binPosix }));
    expect(argv).toEqual([binPosix]);
    expect(isDevCommand(argv)).toBe(false);
  });

  test('uses the .exe name on Windows', () => {
    const argv = resolveServerCommand(opts({ isWindows: true, exists: (p) => p === binWin }));
    expect(argv).toEqual([binWin]);
  });

  test('falls back to the framework-dependent dll via dotnet', () => {
    const argv = resolveServerCommand(opts({ exists: (p) => p === dll }));
    expect(argv).toEqual(['dotnet', dll]);
  });

  test('falls back to dotnet run in dev when nothing is published', () => {
    const argv = resolveServerCommand(opts());
    expect(argv).toEqual(['dotnet', 'run', '--project', DEV]);
    expect(isDevCommand(argv)).toBe(true);
  });

  test('a published binary takes precedence over the dll', () => {
    const argv = resolveServerCommand(opts({ exists: () => true })); // both exist
    expect(argv).toEqual([binPosix]);
  });
});
