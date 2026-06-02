import { describe, expect, test } from 'bun:test';
import { parseCommand } from './commands';

describe('parseCommand', () => {
  test('plain text is a message (trimmed)', () => {
    expect(parseCommand('  refactor auth  ')).toEqual({ kind: 'message', text: 'refactor auth' });
  });

  test('a lone slash word maps to its command', () => {
    expect(parseCommand('/help')).toEqual({ kind: 'help' });
    expect(parseCommand('/new')).toEqual({ kind: 'new' });
    expect(parseCommand('/clear')).toEqual({ kind: 'clear' });
    expect(parseCommand('/settings')).toEqual({ kind: 'settings' });
    expect(parseCommand('/quit')).toEqual({ kind: 'quit' });
  });

  test('aliases resolve', () => {
    expect(parseCommand('/exit')).toEqual({ kind: 'quit' });
    expect(parseCommand('/q')).toEqual({ kind: 'quit' });
    expect(parseCommand('/?')).toEqual({ kind: 'help' });
    expect(parseCommand('/config')).toEqual({ kind: 'settings' });
  });

  test('is case-insensitive on the command name', () => {
    expect(parseCommand('/HELP')).toEqual({ kind: 'help' });
  });

  test('/model carries an optional argument', () => {
    expect(parseCommand('/model')).toEqual({ kind: 'model', value: undefined });
    expect(parseCommand('/model gpt-4o')).toEqual({ kind: 'model', value: 'gpt-4o' });
  });

  test('unknown slash command is reported, not sent as a message', () => {
    expect(parseCommand('/frobnicate now')).toEqual({ kind: 'unknown', name: '/frobnicate' });
  });

  test('a message that merely contains a slash is still a message', () => {
    expect(parseCommand('what is a/b testing')).toEqual({ kind: 'message', text: 'what is a/b testing' });
  });
});
