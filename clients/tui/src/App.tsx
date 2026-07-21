// Root component. Owns the connection lifecycle (find or launch the server, with an in-UI progress
// and error/retry screen), the live config, and the current screen. A brand-new user with no usable
// provider lands in setup; otherwise chat. Ctrl+S opens settings; saving a provider there refreshes
// the config and restarts the session on the new model.

import { useCallback, useEffect, useRef, useState } from 'react';
import { useKeyboard } from '@opentui/react';
import { theme } from './theme';
import { FreeAgentClient, type ConfigView } from './protocol';
import { ensureServer, type ServerHandle } from './server';
import { Chat } from './ui/Chat';
import { Setup } from './ui/Setup';
import { Settings } from './ui/Settings';

const CLOUD_CHAIN = new Set(['ollama', 'bedrock', 'vertex']); // providers that don't need an inline key

function needsSetup(config: ConfigView): boolean {
  const active = config.activeProvider;
  if (CLOUD_CHAIN.has(active)) return false;
  return !config.providers[active]?.apiKeySet;
}

export interface AppProps {
  baseUrl: string;
  apiKey?: string;
  serverCmd?: string;
}

type Phase =
  | { t: 'connecting'; status: string }
  | { t: 'error'; message: string }
  | { t: 'ready'; client: FreeAgentClient; config: ConfigView };

export function App({ baseUrl, apiKey, serverCmd }: AppProps) {
  const [phase, setPhase] = useState<Phase>({ t: 'connecting', status: 'Starting…' });
  const handleRef = useRef<ServerHandle | null>(null);
  const connectingRef = useRef(false);

  const connect = useCallback(async () => {
    if (connectingRef.current) return;
    connectingRef.current = true;
    setPhase({ t: 'connecting', status: 'Starting…' });
    try {
      handleRef.current = await ensureServer({
        baseUrl,
        apiKey,
        serverCmd,
        onStatus: (status) => setPhase({ t: 'connecting', status }),
      });
      const client = new FreeAgentClient({ baseUrl, apiKey });
      const config = await client.getConfig();
      setPhase({ t: 'ready', client, config });
    } catch (e) {
      setPhase({ t: 'error', message: (e as Error).message });
    } finally {
      connectingRef.current = false;
    }
  }, [baseUrl, apiKey, serverCmd]);

  useEffect(() => {
    void connect();
    const stop = () => handleRef.current?.stop();
    process.on('exit', stop);
    return () => {
      process.off('exit', stop);
    };
  }, [connect]);

  const quit = useCallback(() => {
    handleRef.current?.stop();
    const cleanQuit = (globalThis as any).__freeagentQuit;
    if (typeof cleanQuit === 'function') cleanQuit();
    else process.exit(0);
  }, []);

  if (phase.t === 'connecting') return <Connecting status={phase.status} />;
  if (phase.t === 'error') return <ConnectError message={phase.message} onRetry={connect} onQuit={quit} />;
  return <Ready client={phase.client} initialConfig={phase.config} onQuit={quit} />;
}

function Ready({ client, initialConfig, onQuit }: { client: FreeAgentClient; initialConfig: ConfigView; onQuit: () => void }) {
  const [config, setConfig] = useState<ConfigView>(initialConfig);
  const [screen, setScreen] = useState<'setup' | 'chat' | 'settings'>(needsSetup(initialConfig) ? 'setup' : 'chat');
  const [workingDir, setWorkingDir] = useState('');
  const [firstRun] = useState(needsSetup(initialConfig));
  const [chatEpoch, setChatEpoch] = useState(0);

  const handleSetupDone = async (dir: string) => {
    setWorkingDir(dir);
    try {
      setConfig(await client.getConfig());
    } catch {
      /* keep the old config view; the write already succeeded */
    }
    setChatEpoch((e) => e + 1);
    setScreen('chat');
  };

  // A provider/model change made in Settings should restart the session so it takes effect.
  const handleConfigChanged = (next: ConfigView) => {
    const changed =
      next.activeProvider !== config.activeProvider ||
      next.providers[next.activeProvider]?.model !== config.providers[config.activeProvider]?.model;
    setConfig(next);
    if (changed) setChatEpoch((e) => e + 1);
  };

  if (screen === 'setup') {
    return <Setup client={client} config={config} firstRun={firstRun} onDone={handleSetupDone} onCancel={() => setScreen('chat')} />;
  }
  if (screen === 'settings') {
    return <Settings client={client} config={config} workingDir={workingDir} onClose={() => setScreen('chat')} onConfigChanged={handleConfigChanged} />;
  }
  return (
    <Chat
      key={chatEpoch}
      client={client}
      config={config}
      workingDir={workingDir}
      onOpenSettings={() => setScreen('settings')}
      onQuit={onQuit}
    />
  );
}

function Connecting({ status }: { status: string }) {
  return (
    <box style={{ flexGrow: 1, backgroundColor: theme.bg, flexDirection: 'column', justifyContent: 'center', alignItems: 'center' }}>
      <text style={{ fg: theme.accent, attributes: 1 }} content="FreeAgent" />
      <text style={{ fg: theme.textDim, marginTop: 1 }} content={status} />
    </box>
  );
}

function ConnectError({ message, onRetry, onQuit }: { message: string; onRetry: () => void; onQuit: () => void }) {
  useKeyboard((key) => {
    if (key.name === 'r') onRetry();
    else if (key.name === 'q') onQuit();
  });
  return (
    <box style={{ flexGrow: 1, backgroundColor: theme.bg, flexDirection: 'column', justifyContent: 'center', alignItems: 'center', paddingLeft: 4, paddingRight: 4 }}>
      <text style={{ fg: theme.error, attributes: 1 }} content="Couldn't connect to the server" />
      <text style={{ fg: theme.textDim, marginTop: 1 }} content={message} />
      <text style={{ fg: theme.textFaint, marginTop: 1 }} content="Press r to retry · q to quit" />
    </box>
  );
}
