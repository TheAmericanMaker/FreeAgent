// Masked-input reconstruction. opentui's <input> has no password mode, so SecretInput renders a row
// of mask glyphs and keeps the real value in React state. After each edit the input hands back its
// new buffer (mask glyphs for retained chars + literal glyphs for freshly typed/pasted ones); this
// pure function rebuilds the real value from that buffer and the previous real value.
//
// It's exact for the operations that matter on an API-key field — type at end, paste at end,
// backspace, select-all-delete — and for mid-string edits it preserves every character (it may just
// re-anchor the inserted run at the end). No secret is ever lost, and nothing leaks to the screen.

export const MASK = '•';

export function reconstructSecret(prevReal: string, nextBuffer: string, mask: string = MASK): string {
  let retained = 0;
  let literal = '';
  for (const ch of nextBuffer) {
    if (ch === mask) retained++;
    else literal += ch;
  }
  // Retained mask glyphs map to the first `retained` chars of the old secret; literals are the new input.
  return prevReal.slice(0, retained) + literal;
}

/** The masked display string for a secret of the given length. */
export function maskOf(value: string, mask: string = MASK): string {
  return mask.repeat(value.length);
}
