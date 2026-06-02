// Slash-command parsing for the chat input — the opencode-style command palette. Pure so it can be
// unit-tested; the Chat screen maps the parsed command to an action. Anything not starting with "/"
// (after trimming) is a normal message sent to the agent.

export type Command =
  | { kind: 'message'; text: string }
  | { kind: 'help' }
  | { kind: 'new' }
  | { kind: 'clear' }
  | { kind: 'settings' }
  | { kind: 'model'; value?: string }
  | { kind: 'quit' }
  | { kind: 'unknown'; name: string };

export interface CommandSpec {
  name: string;
  summary: string;
}

/** The commands shown by `/help` and offered for completion. Order is the display order. */
export const COMMANDS: CommandSpec[] = [
  { name: '/help', summary: 'Show this command list' },
  { name: '/new', summary: 'Start a fresh session (clears the conversation)' },
  { name: '/clear', summary: 'Clear the transcript on screen' },
  { name: '/model', summary: 'Show the active model, or /model <id> to switch' },
  { name: '/settings', summary: 'Open settings (provider, model, permissions, trust)' },
  { name: '/quit', summary: 'Exit FreeAgent' },
];

/** Parse a line of input into a command. Aliases: /exit→quit, /q→quit, /? →help, /config→settings. */
export function parseCommand(raw: string): Command {
  const text = raw.trim();
  if (!text.startsWith('/')) return { kind: 'message', text };

  const space = text.indexOf(' ');
  const name = (space === -1 ? text : text.slice(0, space)).toLowerCase();
  const rest = space === -1 ? '' : text.slice(space + 1).trim();

  switch (name) {
    case '/help':
    case '/?':
    case '/h':
      return { kind: 'help' };
    case '/new':
    case '/reset':
      return { kind: 'new' };
    case '/clear':
    case '/cls':
      return { kind: 'clear' };
    case '/settings':
    case '/config':
      return { kind: 'settings' };
    case '/model':
      return { kind: 'model', value: rest || undefined };
    case '/quit':
    case '/exit':
    case '/q':
      return { kind: 'quit' };
    default:
      return { kind: 'unknown', name };
  }
}
