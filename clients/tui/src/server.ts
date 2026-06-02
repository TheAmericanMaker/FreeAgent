// Server lifecycle helper. The TUI is a client of FreeAgent.Server (ADR 0005), but a user shouldn't
// have to start a .NET process in another terminal — so we find a server or launch one ourselves.
//
// Launch command resolution (first match wins), so the same TUI works for end users and developers:
//   1. FREEAGENT_SERVER_CMD            — explicit override (space-split argv)
//   2. dist/server/FreeAgent.Server[.exe] — a published self-contained binary (no .NET SDK needed)
//   3. dist/server/FreeAgent.Server.dll   — a framework-dependent publish (needs the .NET runtime)
//   4. dotnet run --project src/FreeAgent.Server — dev fallback (needs the .NET SDK)
// `scripts/install.*` produces (2)/(3); see clients/tui/README.md.

import { spawn } from 'node:child_process';
import { existsSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { FreeAgentClient } from './protocol';

const here = dirname(fileURLToPath(import.meta.url));
// clients/tui/src -> clients/tui
const tuiRoot = resolve(here, '..');
// clients/tui -> repo root
const repoRoot = resolve(tuiRoot, '..', '..');

export interface ResolveOptions {
  serverCmd?: string;
  /** Directory a published server was emitted to (default clients/tui/dist/server). */
  distDir?: string;
  /** The .NET project to `dotnet run` in dev (default <repo>/src/FreeAgent.Server). */
  devProject?: string;
  isWindows?: boolean;
  /** Injected for testing; defaults to the real fs. */
  exists?: (p: string) => boolean;
}

/**
 * Pure resolution of the server launch argv. Exported so its precedence can be unit-tested without
 * spawning anything. The first token of the result is the program; the rest are args.
 */
export function resolveServerCommand(opts: ResolveOptions = {}): string[] {
  const {
    serverCmd = process.env.FREEAGENT_SERVER_CMD,
    distDir = resolve(tuiRoot, 'dist', 'server'),
    devProject = resolve(repoRoot, 'src', 'FreeAgent.Server'),
    isWindows = process.platform === 'win32',
    exists = existsSync,
  } = opts;

  if (serverCmd && serverCmd.trim()) return serverCmd.trim().split(/\s+/);

  const binary = resolve(distDir, isWindows ? 'FreeAgent.Server.exe' : 'FreeAgent.Server');
  if (exists(binary)) return [binary];

  const dll = resolve(distDir, 'FreeAgent.Server.dll');
  if (exists(dll)) return ['dotnet', dll];

  return ['dotnet', 'run', '--project', devProject];
}

/** True when the resolved command builds from source (slow dev path) rather than a published artifact. */
export function isDevCommand(argv: string[]): boolean {
  return argv[0] === 'dotnet' && argv[1] === 'run';
}

export interface ServerHandle {
  /** True if this process spawned the server (and therefore owns shutting it down). */
  spawned: boolean;
  stop(): void;
}

export interface EnsureServerOptions {
  baseUrl: string;
  apiKey?: string;
  serverCmd?: string;
  workingDirectory?: string;
  onStatus?: (line: string) => void;
  timeoutMs?: number;
}

/**
 * Ensures a reachable server, spawning one if needed. Resolves once `GET /config` answers, or rejects
 * with a clear message after the timeout. The returned handle lets the caller stop a server it started.
 */
export async function ensureServer(opts: EnsureServerOptions): Promise<ServerHandle> {
  const { baseUrl, apiKey, onStatus = () => {}, timeoutMs = 60_000 } = opts;
  const client = new FreeAgentClient({ baseUrl, apiKey });

  onStatus(`Connecting to ${baseUrl}…`);
  if (await client.ping()) {
    onStatus('Connected to a running server.');
    return { spawned: false, stop: () => {} };
  }

  const argv = resolveServerCommand({ serverCmd: opts.serverCmd });
  onStatus(isDevCommand(argv) ? 'No server found — building & starting one (first run is slow)…' : 'No server found — starting one…');

  const [program, ...args] = argv;
  const child = spawn(program!, args, {
    cwd: opts.workingDirectory,
    stdio: 'ignore',
    env: {
      ...process.env,
      FREEAGENT_SERVER_URLS: new URL(baseUrl).origin,
      ...(apiKey ? { FREEAGENT_SERVER_API_KEY: apiKey } : {}),
    },
    detached: false,
  });
  let launchError: string | null = null;
  child.on('error', (e) => {
    launchError = e.message;
    onStatus(`Failed to launch server: ${e.message}`);
  });

  const deadline = Date.now() + timeoutMs;
  let dots = 0;
  while (Date.now() < deadline) {
    if (launchError) break;
    if (await client.ping()) {
      onStatus('Server is up.');
      return { spawned: true, stop: () => safeKill(child) };
    }
    onStatus('Waiting for server' + '.'.repeat((dots++ % 3) + 1));
    await sleep(500);
  }

  safeKill(child);
  throw new Error(
    launchError
      ? `Could not launch the server (${launchError}). Run scripts/install, or set FREEAGENT_SERVER_CMD.`
      : `Server at ${baseUrl} did not come up in ${Math.round(timeoutMs / 1000)}s. ` +
        `Run scripts/install to publish it, start it manually, or set FREEAGENT_SERVER_CMD.`,
  );
}

function safeKill(child: ReturnType<typeof spawn>): void {
  try {
    child.kill();
  } catch {
    /* already gone */
  }
}

const sleep = (ms: number) => new Promise<void>((r) => setTimeout(r, ms));
