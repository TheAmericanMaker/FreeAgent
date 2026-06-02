import { describe, expect, test } from 'bun:test';
import { MASK, maskOf, reconstructSecret } from './secret';

const m = (n: number) => MASK.repeat(n);

describe('reconstructSecret', () => {
  test('typing a char at the end appends it', () => {
    // real "ab" shown as "••"; user types "c" -> buffer "••c"
    expect(reconstructSecret('ab', m(2) + 'c')).toBe('abc');
  });

  test('backspace at the end drops the last char', () => {
    // real "abc" shown as "•••"; backspace -> buffer "••"
    expect(reconstructSecret('abc', m(2))).toBe('ab');
  });

  test('pasting into an empty field captures the whole secret', () => {
    expect(reconstructSecret('', 'sk-test-123')).toBe('sk-test-123');
  });

  test('pasting at the end of an existing secret appends', () => {
    expect(reconstructSecret('sk-', m(3) + 'rest')).toBe('sk-rest');
  });

  test('select-all then delete clears it', () => {
    expect(reconstructSecret('whatever', '')).toBe('');
  });

  test('never loses characters (count is retained + typed)', () => {
    const real = 'abcdef';
    const buffer = m(6) + 'XY';
    expect(reconstructSecret(real, buffer)).toHaveLength(8);
  });
});

describe('maskOf', () => {
  test('produces one glyph per character', () => {
    expect(maskOf('abcd')).toBe(m(4));
    expect(maskOf('')).toBe('');
  });
});
