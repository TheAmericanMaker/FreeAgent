// FreeAgent VS Code extension scaffold. Two commands: "New Session" prepares a session against
// the configured FreeAgent.Server and surfaces the id in a status-bar item; "Ask…" prompts for
// input and streams the reply into an output channel. The networked protocol code lives in the
// shared `clients/tui` package; this file is the editor glue only.
//
// Status: scaffold. The full UX (inline diffs, code-action integration, tool-approval prompts)
// builds on top of this — see the roadmap's "Editor & remote" entry for what's outstanding.

import * as vscode from 'vscode';

type Config = { baseUrl: string; apiKey: string | undefined };

function readConfig(): Config {
  const c = vscode.workspace.getConfiguration('freeagent');
  return {
    baseUrl: c.get<string>('baseUrl', 'http://localhost:5000'),
    apiKey: c.get<string>('apiKey') || undefined,
  };
}

function headers(c: Config): Record<string, string> {
  const h: Record<string, string> = { 'content-type': 'application/json' };
  if (c.apiKey) h.authorization = `Bearer ${c.apiKey}`;
  return h;
}

export function activate(context: vscode.ExtensionContext): void {
  const channel = vscode.window.createOutputChannel('FreeAgent');
  const status = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Right, 100);
  status.text = '$(circle-outline) FreeAgent';
  status.tooltip = 'No active FreeAgent session — run "FreeAgent: New Session".';
  status.show();

  let activeSessionId: string | undefined;

  context.subscriptions.push(
    vscode.commands.registerCommand('freeagent.newSession', async () => {
      const cfg = readConfig();
      const workingDirectory = vscode.workspace.workspaceFolders?.[0]?.uri.fsPath;
      try {
        const r = await fetch(`${cfg.baseUrl}/sessions`, {
          method: 'POST',
          headers: headers(cfg),
          body: JSON.stringify({ workingDirectory }),
        });
        if (!r.ok) throw new Error(`HTTP ${r.status}`);
        const body = (await r.json()) as { sessionId: string };
        activeSessionId = body.sessionId;
        status.text = `$(check) FreeAgent: ${activeSessionId}`;
        status.tooltip = `Session ${activeSessionId} at ${workingDirectory ?? '(no workspace)'}`;
        channel.appendLine(`[freeagent] session ${activeSessionId}`);
      } catch (err) {
        vscode.window.showErrorMessage(`FreeAgent: could not create session — ${(err as Error).message}`);
      }
    }),

    vscode.commands.registerCommand('freeagent.askInline', async () => {
      const cfg = readConfig();
      if (!activeSessionId) {
        await vscode.commands.executeCommand('freeagent.newSession');
        if (!activeSessionId) return;
      }
      const userInput = await vscode.window.showInputBox({ prompt: 'Ask FreeAgent…', ignoreFocusOut: true });
      if (!userInput) return;
      channel.show(true);
      channel.appendLine(`\nYOU> ${userInput}\n`);
      try {
        const response = await fetch(`${cfg.baseUrl}/sessions/${activeSessionId}/turns`, {
          method: 'POST',
          headers: headers(cfg),
          body: JSON.stringify({ userInput }),
        });
        if (!response.ok || !response.body) throw new Error(`HTTP ${response.status}`);
        const reader = response.body.getReader();
        const decoder = new TextDecoder();
        let buf = '';
        while (true) {
          const { done, value } = await reader.read();
          if (done) break;
          buf += decoder.decode(value, { stream: true });
          let sep: number;
          while ((sep = buf.indexOf('\n\n')) >= 0) {
            const record = buf.slice(0, sep);
            buf = buf.slice(sep + 2);
            handleRecord(channel, record);
          }
        }
      } catch (err) {
        vscode.window.showErrorMessage(`FreeAgent: turn failed — ${(err as Error).message}`);
      }
    }),

    status,
    channel,
  );
}

function handleRecord(channel: vscode.OutputChannel, record: string): void {
  let event: string | null = null;
  let data: string | null = null;
  for (const line of record.split('\n')) {
    if (line.startsWith(':') || line.length === 0) continue;
    const colon = line.indexOf(':');
    if (colon < 0) continue;
    const field = line.slice(0, colon);
    const value = line.slice(colon + 1).replace(/^ /, '');
    if (field === 'event') event = value;
    else if (field === 'data') data = data === null ? value : data + '\n' + value;
  }
  if (!event || data === null) return;
  let payload: unknown;
  try { payload = JSON.parse(data); } catch { return; }
  switch (event) {
    case 'text': channel.append((payload as { chunk: string }).chunk); break;
    case 'tool_call': channel.appendLine(`\n[tool] ${(payload as { tool: string }).tool}`); break;
    case 'tool_result': channel.appendLine(`[result/${(payload as { kind: string }).kind}]`); break;
    case 'done': channel.appendLine('\n[done]'); break;
    case 'error': channel.appendLine(`[error] ${(payload as { message: string }).message}`); break;
  }
}

export function deactivate(): void { /* no-op */ }
