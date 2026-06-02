// Settings screen, reachable from chat with Ctrl+S or /settings. A menu that opens one full-screen
// sub-panel at a time (Provider / Permissions / Trust) — deliberately one focused region per view so
// keyboard focus is never ambiguous. Everything persists through the server's config API.

import { useCallback, useEffect, useState } from 'react';
import { useKeyboard } from '@opentui/react';
import type { SelectOption } from '@opentui/core';
import { theme } from '../theme';
import { OptionSelect } from './controls';
import { Setup } from './Setup';
import type { CapabilityRule, ConfigView, FreeAgentClient, PermissionsView, TrustView } from '../protocol';

type View = 'menu' | 'provider' | 'permissions' | 'trust';

interface SettingsProps {
  client: FreeAgentClient;
  config: ConfigView;
  workingDir: string;
  onClose: () => void;
  onConfigChanged: (next: ConfigView) => void;
}

export function Settings({ client, config, workingDir, onClose, onConfigChanged }: SettingsProps) {
  const [view, setView] = useState<View>('menu');

  useKeyboard((key) => {
    if (key.name === 'escape') {
      if (view === 'menu') onClose();
      else setView('menu');
    }
  });

  if (view === 'provider') {
    return (
      <Setup
        client={client}
        config={config}
        firstRun={false}
        onCancel={() => setView('menu')}
        onDone={async () => {
          try {
            onConfigChanged(await client.getConfig());
          } catch {
            /* keep prior config */
          }
          setView('menu');
        }}
      />
    );
  }
  if (view === 'permissions') return <PermissionsPanel client={client} workingDir={workingDir} onBack={() => setView('menu')} />;
  if (view === 'trust') return <TrustPanel client={client} workingDir={workingDir} onBack={() => setView('menu')} />;

  const items: { key: View | 'close'; name: string; description: string }[] = [
    { key: 'provider', name: 'Provider & model', description: `Currently ${config.activeProvider}:${config.providers[config.activeProvider]?.model ?? '—'}` },
    { key: 'permissions', name: 'Permissions', description: 'Allow/deny which tools the agent may use' },
    { key: 'trust', name: 'Trust', description: 'Trust this project to run its .freeagent hooks / MCP / LSP' },
    { key: 'close', name: 'Back to chat', description: 'Return to the conversation' },
  ];

  return (
    <box style={{ flexDirection: 'column', flexGrow: 1, backgroundColor: theme.bg, paddingLeft: 2, paddingRight: 2 }}>
      <box style={{ marginTop: 1, marginBottom: 1, flexDirection: 'column' }}>
        <text style={{ fg: theme.accent, attributes: 1 }} content="FreeAgent · Settings" />
        <text style={{ fg: theme.textDim }} content="Pick a section. Esc returns to chat." />
      </box>
      <OptionSelect
        focused
        options={items.map<SelectOption>((it) => ({ name: it.name, description: it.description, value: it.key }))}
        onSelect={(_i, opt) => {
          const key = opt?.value as View | 'close' | undefined;
          if (key === 'close' || !key) onClose();
          else setView(key);
        }}
        style={{ height: items.length, backgroundColor: theme.panel, focusedBackgroundColor: theme.panel, selectedBackgroundColor: theme.accentDim, descriptionColor: theme.textFaint }}
      />
      <text style={{ fg: theme.textFaint, marginTop: 1 }} content="↑↓ move · Enter open · Esc back" />
    </box>
  );
}

// ── Permissions ─────────────────────────────────────────────────────────────────────────────────
type ToolState = 'default' | 'allow' | 'deny';

function PermissionsPanel({ client, workingDir, onBack }: { client: FreeAgentClient; workingDir: string; onBack: () => void }) {
  const [data, setData] = useState<PermissionsView | null>(null);
  const [states, setStates] = useState<Record<string, ToolState>>({});
  const [status, setStatus] = useState('Loading…');
  const [saving, setSaving] = useState(false);

  useEffect(() => {
    client
      .getPermissions(workingDir || undefined)
      .then((p) => {
        setData(p);
        const s: Record<string, ToolState> = {};
        for (const t of p.builtinTools) s[t] = p.allowTools.includes(t) ? 'allow' : p.denyTools.includes(t) ? 'deny' : 'default';
        setStates(s);
        setStatus(p.error ? `Config warning: ${p.error}` : `Editing ${p.configPath}`);
      })
      .catch((e) => setStatus(`Could not load permissions: ${String(e)}`));
  }, [client, workingDir]);

  const cycle = (tool: string) =>
    setStates((s) => ({ ...s, [tool]: s[tool] === 'default' ? 'allow' : s[tool] === 'allow' ? 'deny' : 'default' }));

  const save = useCallback(async () => {
    if (!data) return;
    setSaving(true);
    setStatus('Saving…');
    const allowTools = Object.entries(states).filter(([, v]) => v === 'allow').map(([k]) => k);
    const denyTools = Object.entries(states).filter(([, v]) => v === 'deny').map(([k]) => k);
    try {
      // Preserve any capability rules the UI doesn't edit; we only own the per-tool allow/deny lists.
      await client.updatePermissions({ dir: workingDir, allowTools, denyTools, allow: data.allow as CapabilityRule[], deny: data.deny as CapabilityRule[] });
      setStatus('✓ Saved.');
    } catch (e) {
      setStatus(`✗ ${String(e)}`);
    } finally {
      setSaving(false);
    }
  }, [client, workingDir, states, data]);

  // The select lists each tool plus Save/Back actions; Enter cycles a tool or runs an action.
  const toolNames = data?.builtinTools ?? [];
  const options: SelectOption[] = [
    ...toolNames.map((t) => ({ name: `${marker(states[t] ?? 'default')} ${t}`, description: stateLabel(states[t] ?? 'default'), value: `tool:${t}` })),
    { name: saving ? 'Saving…' : '💾 Save', description: 'Write these rules to .freeagent/config.json', value: 'save' },
    { name: '← Back', description: 'Return to settings', value: 'back' },
  ];

  return (
    <box style={{ flexDirection: 'column', flexGrow: 1, backgroundColor: theme.bg, paddingLeft: 2, paddingRight: 2 }}>
      <box style={{ marginTop: 1, marginBottom: 1, flexDirection: 'column' }}>
        <text style={{ fg: theme.accent, attributes: 1 }} content="Permissions" />
        <text style={{ fg: theme.textDim }} content="Enter cycles a tool: default → allow → deny. Deny always wins; reads in the working dir are allowed by default." />
      </box>
      <OptionSelect
        focused
        options={options}
        onSelect={(_i, opt) => {
          const v = String(opt?.value ?? '');
          if (v === 'save') void save();
          else if (v === 'back') onBack();
          else if (v.startsWith('tool:')) cycle(v.slice(5));
        }}
        style={{ height: Math.min(options.length, 16), backgroundColor: theme.panel, focusedBackgroundColor: theme.panel, selectedBackgroundColor: theme.accentDim, descriptionColor: theme.textFaint }}
      />
      <text style={{ fg: theme.textFaint, marginTop: 1 }} content={status} />
    </box>
  );
}

function marker(s: ToolState): string {
  return s === 'allow' ? '✓' : s === 'deny' ? '✗' : '·';
}
function stateLabel(s: ToolState): string {
  return s === 'allow' ? 'allowed' : s === 'deny' ? 'denied' : 'default (prompt/auto)';
}

// ── Trust ───────────────────────────────────────────────────────────────────────────────────────
function TrustPanel({ client, workingDir, onBack }: { client: FreeAgentClient; workingDir: string; onBack: () => void }) {
  const [data, setData] = useState<TrustView | null>(null);
  const [status, setStatus] = useState('Loading…');

  const load = useCallback(() => {
    client
      .getTrust(workingDir || undefined)
      .then((t) => {
        setData(t);
        setStatus(t.trusted ? '✓ This directory is trusted.' : t.requests.length ? 'This project wants extra privileges (below).' : 'Nothing privileged here — trust is optional.');
      })
      .catch((e) => setStatus(`Could not load trust: ${String(e)}`));
  }, [client, workingDir]);

  useEffect(() => load(), [load]);

  const options: SelectOption[] = [
    ...(data && !data.trusted ? [{ name: '🔓 Trust this directory', description: 'Allow its hooks / MCP / LSP and allow-rules', value: 'trust' }] : []),
    { name: '← Back', description: 'Return to settings', value: 'back' },
  ];

  return (
    <box style={{ flexDirection: 'column', flexGrow: 1, backgroundColor: theme.bg, paddingLeft: 2, paddingRight: 2 }}>
      <box style={{ marginTop: 1, marginBottom: 1, flexDirection: 'column' }}>
        <text style={{ fg: theme.accent, attributes: 1 }} content="Trust" />
        <text style={{ fg: theme.textDim }} content={data ? data.workingDirectory : workingDir || '(server directory)'} />
      </box>
      {data && data.requests.length > 0 && (
        <box style={{ flexDirection: 'column', marginBottom: 1, borderColor: theme.border, border: ['left'], paddingLeft: 1 }}>
          {data.requests.map((r, i) => (
            <text key={i} style={{ fg: theme.warn }} content={`• ${r}`} />
          ))}
        </box>
      )}
      <OptionSelect
        focused
        options={options}
        onSelect={async (_i, opt) => {
          const v = String(opt?.value ?? '');
          if (v === 'trust') {
            setStatus('Trusting…');
            try {
              await client.trust(data?.workingDirectory ?? workingDir);
              load();
            } catch (e) {
              setStatus(`✗ ${String(e)}`);
            }
          } else onBack();
        }}
        style={{ height: Math.max(options.length, 1), backgroundColor: theme.panel, focusedBackgroundColor: theme.panel, selectedBackgroundColor: theme.accentDim, descriptionColor: theme.textFaint }}
      />
      <text style={{ fg: theme.textFaint, marginTop: 1 }} content={status} />
    </box>
  );
}
