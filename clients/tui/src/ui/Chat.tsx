// The chat screen — the main working surface. A header with provider/model context, a sticky
// auto-scrolling transcript that renders streamed assistant text (as Markdown once a turn finishes),
// extended thinking, and tool activity as it arrives, a slash-command-aware input, and a status bar.
// All agent work happens over the server's SSE turn stream; this component renders events, owns the
// per-turn AbortController, and handles the /command palette + session lifecycle.

import { useCallback, useEffect, useRef, useState } from 'react';
import { useKeyboard, useRenderer } from '@opentui/react';
import type { ScrollBoxRenderable } from '@opentui/core';
import { theme } from '../theme';
import { SafeMarkdown, TextInput } from './controls';
import { COMMANDS, parseCommand, type Command } from '../commands';
import type { ConfigView, FreeAgentClient } from '../protocol';

interface Block {
  id: number;
  kind: 'user' | 'assistant' | 'thinking' | 'tool' | 'tool-result' | 'notice';
  text: string;
  tool?: string;
  status?: string; // for tool-result: success | error | …
  done?: boolean; // assistant block: finalized → render as Markdown rather than streaming plain text
}

interface ChatProps {
  client: FreeAgentClient;
  config: ConfigView;
  /** Directory the agent operates on; empty/undefined uses the server's working directory. */
  workingDir?: string;
  onOpenSettings: () => void;
  onQuit: () => void;
}

export function Chat({ client, config, workingDir: chosenDir, onOpenSettings, onQuit }: ChatProps) {
  const [blocks, setBlocks] = useState<Block[]>([]);
  const [draft, setDraft] = useState('');
  const [busy, setBusy] = useState(false);
  const [sessionId, setSessionId] = useState<string | null>(null);
  const [workingDir, setWorkingDir] = useState<string>('');
  const [tokens, setTokens] = useState<{ input: number; output: number } | null>(null);
  const [statusLine, setStatusLine] = useState('Starting session…');

  const abortRef = useRef<AbortController | null>(null);
  const scrollRef = useRef<ScrollBoxRenderable>(null);
  const nextId = useRef(1);
  const renderer = useRenderer();
  const provider = config.activeProvider;
  const [model, setModel] = useState<string>(config.providers[provider]?.model ?? '(default)');

  const push = useCallback((b: Omit<Block, 'id'>) => {
    setBlocks((prev) => [...prev, { ...b, id: nextId.current++ }]);
  }, []);

  const startSession = useCallback(async () => {
    setStatusLine('Starting session…');
    try {
      const s = await client.createSession(chosenDir || undefined);
      setSessionId(s.sessionId);
      setWorkingDir(s.workingDirectory);
      setTokens(null);
      setStatusLine('Ready');
      return s.sessionId;
    } catch (e) {
      setStatusLine(`Could not create session: ${(e as Error).message}`);
      return null;
    }
  }, [client, chosenDir]);

  // Create a session up front so the working directory shows in the header immediately.
  useEffect(() => {
    void startSession();
  }, [startSession]);

  // Keep the transcript pinned to the newest content.
  useEffect(() => {
    const sb = scrollRef.current;
    if (sb) sb.scrollTop = sb.scrollHeight;
  }, [blocks]);

  const submit = useCallback(
    async (input: string) => {
      if (!input || busy || !sessionId) return;
      push({ kind: 'user', text: input });
      setBusy(true);
      setStatusLine('Thinking…');

      const controller = new AbortController();
      abortRef.current = controller;

      const assistantId = nextId.current++;
      let assistantText = '';
      let thinkingId: number | null = null;
      setBlocks((prev) => [...prev, { id: assistantId, kind: 'assistant', text: '' }]);
      const setAssistant = (t: string) => setBlocks((prev) => prev.map((b) => (b.id === assistantId ? { ...b, text: t } : b)));

      try {
        for await (const ev of client.streamTurn(sessionId, input, controller.signal)) {
          switch (ev.event) {
            case 'text':
              assistantText += ev.data.chunk;
              setAssistant(assistantText);
              break;
            case 'thinking':
              if (thinkingId === null) {
                thinkingId = nextId.current++;
                const tid = thinkingId;
                setBlocks((prev) => {
                  const i = prev.findIndex((b) => b.id === assistantId);
                  const next = [...prev];
                  next.splice(Math.max(i, 0), 0, { id: tid, kind: 'thinking', text: ev.data.chunk });
                  return next;
                });
              } else {
                const tid = thinkingId;
                setBlocks((prev) => prev.map((b) => (b.id === tid ? { ...b, text: b.text + ev.data.chunk } : b)));
              }
              break;
            case 'tool_call':
              push({ kind: 'tool', tool: ev.data.tool, text: summarizeArgs(ev.data.arguments) });
              setStatusLine(`Running ${ev.data.tool}…`);
              break;
            case 'tool_result':
              push({ kind: 'tool-result', tool: ev.data.tool, status: ev.data.kind, text: clip(ev.data.content, 600) });
              break;
            case 'usage':
              setTokens({ input: ev.data.input, output: ev.data.output });
              break;
            case 'done':
              if (ev.data.doomLoopDetected) push({ kind: 'notice', text: '⚠ Stopped: repeated identical tool calls (doom loop).' });
              break;
            case 'cancelled':
              push({ kind: 'notice', text: '■ Turn cancelled.' });
              break;
            case 'error':
              push({ kind: 'notice', text: `✗ ${ev.data.message}` });
              break;
          }
        }
      } catch (e) {
        if (!controller.signal.aborted) push({ kind: 'notice', text: `✗ ${(e as Error).message}` });
      } finally {
        setBlocks((prev) => prev.map((b) => (b.id === assistantId ? { ...b, done: true } : b)));
        setBusy(false);
        setStatusLine('Ready');
        abortRef.current = null;
      }
    },
    [busy, sessionId, client, push],
  );

  const runCommand = useCallback(
    async (cmd: Command) => {
      switch (cmd.kind) {
        case 'message':
          return submit(cmd.text);
        case 'help':
          push({ kind: 'notice', text: 'Commands:\n' + COMMANDS.map((c) => `  ${c.name.padEnd(10)} ${c.summary}`).join('\n') });
          return;
        case 'clear':
          setBlocks([]);
          return;
        case 'new':
          setBlocks([]);
          await startSession();
          return;
        case 'settings':
          onOpenSettings();
          return;
        case 'model':
          if (!cmd.value) {
            push({ kind: 'notice', text: `Model: ${model}  (use /model <id> to switch, or /settings)` });
          } else {
            try {
              await client.updateProvider({ provider, model: cmd.value });
              setModel(cmd.value);
              setBlocks([]);
              push({ kind: 'notice', text: `✓ Model set to ${cmd.value}. Started a fresh session.` });
              await startSession();
            } catch (e) {
              push({ kind: 'notice', text: `✗ Could not set model: ${(e as Error).message}` });
            }
          }
          return;
        case 'quit':
          onQuit();
          return;
        case 'unknown':
          push({ kind: 'notice', text: `Unknown command ${cmd.name}. Type /help.` });
          return;
      }
    },
    [submit, push, startSession, onOpenSettings, onQuit, client, provider, model],
  );

  const onInputSubmit = useCallback(
    (value: string) => {
      const text = value.trim();
      setDraft('');
      if (text.length === 0) return;
      void runCommand(parseCommand(text));
    },
    [runCommand],
  );

  useKeyboard((key) => {
    if (key.name === 'escape' && busy) {
      abortRef.current?.abort();
      setStatusLine('Cancelling…');
    } else if (key.ctrl && key.name === 'y') {
      // Copy the last assistant response to clipboard via OSC 52.
      const lastAssistant = [...blocks].reverse().find((b) => b.kind === 'assistant');
      if (lastAssistant?.text) {
        const ok = renderer.copyToClipboardOSC52(lastAssistant.text);
        setStatusLine(ok ? 'Copied last response to clipboard' : 'Copy failed (OSC 52 not supported)');
      } else {
        setStatusLine('No assistant response to copy');
      }
    } else if (key.ctrl && key.name === 'q') {
      const cleanQuit = (globalThis as any).__freeagentQuit;
      if (typeof cleanQuit === 'function') cleanQuit();
      else process.exit(0);
    } else if (key.ctrl && key.name === 's') {
      onOpenSettings();
    } else if (key.name === 'pageup' || (key.ctrl && key.name === 'u')) {
      const sb = scrollRef.current;
      if (sb) sb.scrollTop = Math.max(0, sb.scrollTop - 10);
    } else if (key.name === 'pagedown' || (key.ctrl && key.name === 'd')) {
      const sb = scrollRef.current;
      if (sb) sb.scrollTop = Math.min(sb.scrollHeight, sb.scrollTop + 10);
    }
  });

  return (
    <box style={{ flexDirection: 'column', flexGrow: 1, backgroundColor: theme.bg }}>
      <Header provider={provider} model={model} workingDir={workingDir} sessionId={sessionId} />

      <scrollbox
        ref={scrollRef}
        stickyScroll
        stickyStart="bottom"
        style={{ flexGrow: 1, paddingLeft: 1, paddingRight: 1, backgroundColor: theme.bg }}
      >
        {blocks.length === 0 && <EmptyState />}
        {blocks.map((b) => (
          <BlockView key={b.id} block={b} />
        ))}
      </scrollbox>

      <box style={{ flexDirection: 'row', paddingLeft: 1, paddingRight: 1, backgroundColor: theme.panel, borderColor: busy ? theme.accentDim : theme.borderFocus, border: ['top'] }}>
        <text style={{ fg: busy ? theme.textFaint : theme.accent }} content={busy ? '…' : '❯'} />
        <TextInput
          focused={!busy}
          value={draft}
          onInput={setDraft}
          onSubmit={onInputSubmit}
          placeholder={busy ? 'Esc to cancel' : 'Message, or /help for commands'}
          style={{ flexGrow: 1, marginLeft: 1, backgroundColor: theme.panel }}
        />
      </box>

      <StatusBar statusLine={statusLine} tokens={tokens} busy={busy} />
    </box>
  );
}

function EmptyState() {
  return (
    <box style={{ flexDirection: 'column', marginTop: 1 }}>
      <text style={{ fg: theme.accent, attributes: 1 }} content="Welcome to FreeAgent" />
      <text style={{ fg: theme.textDim }} content="Ask anything — the agent can read, search, edit files, and run commands in the working directory." />
      <text style={{ fg: theme.textFaint, marginTop: 1 }} content="/help for commands · Ctrl+S settings · Ctrl+Y copy · Esc cancels · Ctrl+Q quits" />
    </box>
  );
}

function Header({ provider, model, workingDir, sessionId }: { provider: string; model: string; workingDir: string; sessionId: string | null }) {
  return (
    <box style={{ flexDirection: 'row', paddingLeft: 1, paddingRight: 1, backgroundColor: theme.panelAlt, borderColor: theme.border, border: ['bottom'] }}>
      <text style={{ fg: theme.accent, attributes: 1 }} content=" FreeAgent " />
      <text style={{ fg: theme.textDim }} content={`  ${provider}:${model}`} />
      <box style={{ flexGrow: 1 }} />
      <text style={{ fg: theme.textFaint }} content={`${shortenPath(workingDir)}${sessionId ? `  ·  ${sessionId}` : ''} `} />
    </box>
  );
}

function StatusBar({ statusLine, tokens, busy }: { statusLine: string; tokens: { input: number; output: number } | null; busy: boolean }) {
  const tok = tokens ? `  ${tokens.input}→${tokens.output} tok` : '';
  return (
    <box style={{ flexDirection: 'row', paddingLeft: 1, paddingRight: 1, backgroundColor: theme.panelAlt }}>
      <text style={{ fg: busy ? theme.warn : theme.textDim }} content={statusLine} />
      <box style={{ flexGrow: 1 }} />
      <text style={{ fg: theme.textFaint }} content={`${tok}  /help · Ctrl+S · Ctrl+Y · Ctrl+Q `} />
    </box>
  );
}

function BlockView({ block }: { block: Block }) {
  switch (block.kind) {
    case 'user':
      return (
        <box style={{ flexDirection: 'row', marginTop: 1 }}>
          <text style={{ fg: theme.user, attributes: 1 }} content="you  " />
          <text style={{ fg: theme.text, flexGrow: 1 }} content={block.text} />
        </box>
      );
    case 'assistant':
      return (
        <box style={{ flexDirection: 'row', marginTop: 1 }}>
          <text style={{ fg: theme.assistant, attributes: 1 }} content="ai   " />
          {block.done && block.text.trim().length > 0 ? (
            <box style={{ flexGrow: 1, flexDirection: 'column' }}>
              <SafeMarkdown content={block.text} fallbackColor={theme.text} />
            </box>
          ) : (
            <text style={{ fg: theme.text, flexGrow: 1 }} content={block.text || '…'} />
          )}
        </box>
      );
    case 'thinking':
      return (
        <box style={{ flexDirection: 'row', marginTop: 1 }}>
          <text style={{ fg: theme.thinking }} content="     " />
          <text style={{ fg: theme.thinking, attributes: 2, flexGrow: 1 }} content={block.text} />
        </box>
      );
    case 'tool':
      return (
        <box style={{ flexDirection: 'row', marginTop: 1 }}>
          <text style={{ fg: theme.tool }} content={`  ⚙ ${block.tool}`} />
          <text style={{ fg: theme.textFaint, marginLeft: 1, flexGrow: 1 }} content={block.text} />
        </box>
      );
    case 'tool-result': {
      const ok = block.status === 'success' || block.status === 'Success';
      return (
        <box style={{ flexDirection: 'column', marginLeft: 4, borderColor: theme.border, border: ['left'], paddingLeft: 1 }}>
          <text style={{ fg: ok ? theme.toolOk : theme.toolErr }} content={`${ok ? '✓' : '✗'} ${block.tool} (${block.status})`} />
          {block.text.trim().length > 0 && <text style={{ fg: theme.textDim }} content={block.text} />}
        </box>
      );
    }
    case 'notice':
      return <text style={{ fg: theme.warn, marginTop: 1 }} content={block.text} />;
  }
}

function summarizeArgs(raw: string): string {
  try {
    const obj = JSON.parse(raw);
    const parts = Object.entries(obj).map(([k, v]) => `${k}=${clip(String(typeof v === 'string' ? v : JSON.stringify(v)), 60)}`);
    return parts.join('  ');
  } catch {
    return clip(raw, 80);
  }
}

function clip(s: string, n: number): string {
  return s.length > n ? s.slice(0, n) + '…' : s;
}

function shortenPath(p: string): string {
  if (!p) return '';
  const parts = p.split(/[/\\]/).filter(Boolean);
  return parts.length <= 2 ? p : '…/' + parts.slice(-2).join('/');
}
