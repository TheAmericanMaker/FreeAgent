// In-TUI setup wizard — the whole point of "no terminal commands". Three steps, all driven by the
// server's config API: pick a provider (GET /providers), enter credentials and test them
// (POST /config/provider/test), then choose a model (GET /models) + working directory and save
// (PUT /config/provider). Tab / Shift+Tab move between fields; Enter on an action runs it.

import { useCallback, useEffect, useState } from 'react';
import { useKeyboard } from '@opentui/react';
import type { SelectOption } from '@opentui/core';
import { theme } from '../theme';
import { OptionSelect, SecretInput, TextInput } from './controls';
import type { ConfigView, FreeAgentClient, ModelInfo, ProbeResult, ProviderInfo } from '../protocol';

type Step = 'provider' | 'credentials' | 'model';

interface SetupProps {
  client: FreeAgentClient;
  config: ConfigView;
  /** Called once the provider is saved; passes the chosen working directory (empty = server default). */
  onDone: (workingDir: string) => void;
  /** True for first-run onboarding (hides the "cancel" affordance since there's nothing to go back to). */
  firstRun: boolean;
  onCancel: () => void;
}

export function Setup({ client, config, onDone, firstRun, onCancel }: SetupProps) {
  const [providers, setProviders] = useState<ProviderInfo[]>([]);
  const [step, setStep] = useState<Step>('provider');
  const [provider, setProvider] = useState<string>(config.activeProvider);
  const [form, setForm] = useState<Record<string, string>>({});
  const [models, setModels] = useState<ModelInfo[]>([]);
  const [workingDir, setWorkingDir] = useState('');
  const [focus, setFocus] = useState(0);
  const [test, setTest] = useState<ProbeResult | null>(null);
  const [activity, setActivity] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    client.getProviders().then(setProviders).catch((e) => setError(String(e)));
  }, [client]);

  const info = providers.find((p) => p.id === provider);

  const beginProvider = useCallback(
    async (id: string) => {
      setProvider(id);
      setError(null);
      setTest(null);
      const current = config.providers[id];
      const pinfo = providers.find((p) => p.id === id);
      // Seed the form from the field defaults, overlaid with whatever's already configured.
      const seeded: Record<string, string> = {};
      for (const f of pinfo?.fields ?? []) seeded[f.slot] = f.default ?? '';
      if (current?.baseUrl) seeded.baseUrl = current.baseUrl;
      if (current?.model) seeded.model = current.model;
      if (current?.apiVersion) seeded.apiVersion = current.apiVersion;
      seeded.apiKey = ''; // never pre-fill (we don't have it) — blank means "keep existing"
      setForm(seeded);
      setFocus(0);
      setStep('credentials');
      client.getModels(id).then(setModels).catch(() => setModels([]));
    },
    [config, providers, client],
  );

  const setField = (slot: string, value: string) => setForm((f) => ({ ...f, [slot]: value }));

  const runTest = useCallback(async () => {
    setActivity('Testing connection…');
    setTest(null);
    try {
      const result = await client.testProvider({ provider, apiKey: form.apiKey, baseUrl: form.baseUrl, model: form.model, apiVersion: form.apiVersion });
      setTest(result);
    } catch (e) {
      setTest({ ok: false, message: String(e), mode: 'live' });
    } finally {
      setActivity(null);
    }
  }, [client, provider, form]);

  const save = useCallback(async () => {
    setActivity('Saving…');
    setError(null);
    try {
      await client.updateProvider({
        provider,
        apiKey: form.apiKey || undefined,
        baseUrl: form.baseUrl || undefined,
        model: form.model || undefined,
        apiVersion: form.apiVersion || undefined,
        setAsDefault: true,
      });
      onDone(workingDir.trim());
    } catch (e) {
      setError(String(e));
      setActivity(null);
    }
  }, [client, provider, form, workingDir, onDone]);

  // ── keyboard: Tab/Shift+Tab cycle focusable rows; Esc cancels (non-first-run) ────────────────
  const credentialFields = (info?.fields ?? []).filter((f) => f.slot !== 'model');
  // credentials rows: [..fields, Test, Continue, (Back if not first run)]
  const credentialRowCount = credentialFields.length + 2 + (firstRun ? 0 : 1);
  // model rows: [model-input, workingDir-input, Save, Back]
  const modelRowCount = 4;

  useKeyboard((key) => {
    if (key.name === 'escape' && !firstRun) {
      onCancel();
      return;
    }
    const count = step === 'credentials' ? credentialRowCount : step === 'model' ? modelRowCount : 0;
    if (count === 0) return;
    if (key.name === 'tab' && key.shift) setFocus((f) => (f - 1 + count) % count);
    else if (key.name === 'tab') setFocus((f) => (f + 1) % count);
  });

  if (error && step === 'provider') {
    return <Centered><text style={{ fg: theme.error }} content={`Could not reach the server: ${error}`} /></Centered>;
  }

  return (
    <box style={{ flexDirection: 'column', flexGrow: 1, backgroundColor: theme.bg, paddingLeft: 2, paddingRight: 2 }}>
      <box style={{ marginTop: 1, marginBottom: 1, flexDirection: 'column' }}>
        <text style={{ fg: theme.accent, attributes: 1 }} content="FreeAgent · Setup" />
        <text style={{ fg: theme.textDim }} content={firstRun ? "Let's connect a model provider. Nothing is written until you save." : 'Settings — update your provider and model.'} />
      </box>

      {step === 'provider' && (
        <box style={{ flexDirection: 'column' }}>
          <text style={{ fg: theme.text, marginBottom: 1 }} content="1 / 3   Choose a provider" />
          <OptionSelect
            focused
            options={providers.map<SelectOption>((p) => ({ name: p.id, description: p.description, value: p.id }))}
            onSelect={(_i, opt) => opt && beginProvider(String(opt.value))}
            style={{ height: Math.min(providers.length, 8), backgroundColor: theme.panel, focusedBackgroundColor: theme.panel, selectedBackgroundColor: theme.accentDim, descriptionColor: theme.textFaint }}
          />
          <text style={{ fg: theme.textFaint, marginTop: 1 }} content="↑↓ to move · Enter to select" />
        </box>
      )}

      {step === 'credentials' && (
        <box style={{ flexDirection: 'column' }}>
          <text style={{ fg: theme.text, marginBottom: 1 }} content={`2 / 3   Configure ${provider}`} />
          {credentialFields.map((f, i) => (
            <Field key={f.slot} label={f.label} secret={f.secret} value={form[f.slot] ?? ''} focused={focus === i} onInput={(v) => setField(f.slot, v)} onSubmit={() => setFocus((x) => x + 1)} />
          ))}
          <ActionRow label={activity === 'Testing connection…' ? 'Testing…' : 'Test connection'} focused={focus === credentialFields.length} onRun={runTest} />
          <ActionRow label="Continue → choose model" focused={focus === credentialFields.length + 1} onRun={() => { setFocus(0); setStep('model'); }} />
          {!firstRun && <ActionRow label="Cancel" focused={focus === credentialFields.length + 2} onRun={onCancel} />}
          {test && <text style={{ marginTop: 1, fg: test.ok ? theme.ok : theme.error }} content={`${test.ok ? '✓' : '✗'} ${test.message}`} />}
          <Hint />
        </box>
      )}

      {step === 'model' && (
        <box style={{ flexDirection: 'column' }}>
          <text style={{ fg: theme.text, marginBottom: 1 }} content="3 / 3   Model & working directory" />
          <Field label="Model" secret={false} value={form.model ?? ''} focused={focus === 0} onInput={(v) => setField('model', v)} onSubmit={() => setFocus(1)} />
          {models.length > 0 && (
            <text style={{ fg: theme.textFaint, marginBottom: 1 }} content={`known: ${models.map((m) => m.id).join(', ')}`} />
          )}
          <Field label="Working dir" secret={false} value={workingDir} focused={focus === 1} placeholder="(server's directory)" onInput={setWorkingDir} onSubmit={() => setFocus(2)} />
          <ActionRow label={activity === 'Saving…' ? 'Saving…' : 'Save & open chat'} focused={focus === 2} onRun={save} accent />
          <ActionRow label="Back" focused={focus === 3} onRun={() => { setFocus(0); setStep('credentials'); }} />
          {error && <text style={{ marginTop: 1, fg: theme.error }} content={`✗ ${error}`} />}
          <Hint />
        </box>
      )}
    </box>
  );
}

function Field({ label, value, focused, secret, placeholder, onInput, onSubmit }: { label: string; value: string; focused: boolean; secret: boolean; placeholder?: string; onInput: (v: string) => void; onSubmit: () => void }) {
  return (
    <box style={{ flexDirection: 'row', marginBottom: 1 }}>
      <text style={{ fg: focused ? theme.accent : theme.textDim, width: 14 }} content={`${label}`} />
      <box style={{ flexGrow: 1, borderColor: focused ? theme.borderFocus : theme.border, border: true, paddingLeft: 1, height: 3 }}>
        {secret ? (
          <SecretInput focused={focused} value={value} onInput={onInput} onSubmit={onSubmit} placeholder={placeholder ?? 'paste key (masked)'} style={{ flexGrow: 1, backgroundColor: theme.panel }} />
        ) : (
          <TextInput focused={focused} value={value} onInput={onInput} onSubmit={onSubmit} placeholder={placeholder ?? ''} style={{ flexGrow: 1, backgroundColor: theme.panel }} />
        )}
      </box>
    </box>
  );
}

function ActionRow({ label, focused, onRun, accent }: { label: string; focused: boolean; onRun: () => void; accent?: boolean }) {
  useKeyboard((key) => {
    if (focused && (key.name === 'return' || key.name === 'enter')) onRun();
  });
  const fg = focused ? theme.bg : accent ? theme.accent : theme.text;
  const bg = focused ? (accent ? theme.accent : theme.borderFocus) : theme.panel;
  return (
    <box style={{ marginTop: 0, marginBottom: 0, backgroundColor: bg, paddingLeft: 1, paddingRight: 1 }}>
      <text style={{ fg, attributes: focused ? 1 : 0 }} content={`${focused ? '❯ ' : '  '}${label}`} />
    </box>
  );
}

function Hint() {
  return <text style={{ fg: theme.textFaint, marginTop: 1 }} content="Tab / Shift+Tab to move · Enter to activate" />;
}

function Centered({ children }: { children: React.ReactNode }) {
  return (
    <box style={{ flexGrow: 1, backgroundColor: theme.bg, justifyContent: 'center', alignItems: 'center' }}>{children}</box>
  );
}
