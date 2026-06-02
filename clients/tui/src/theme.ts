// Shared visual language for the TUI. One palette so every screen looks like the same product —
// an opencode-grade dark theme with a teal accent, muted chrome, and semantic status colors.

export const theme = {
  bg: '#0d1117',
  panel: '#11161d',
  panelAlt: '#161c26',
  border: '#2a3441',
  borderFocus: '#3fb6a8',
  accent: '#3fb6a8',
  accentDim: '#2a7d74',
  text: '#d7dde5',
  textDim: '#8b95a3',
  textFaint: '#5a6472',
  user: '#7aa2f7',
  assistant: '#9ece6a',
  thinking: '#7d8590',
  tool: '#e0af68',
  toolOk: '#9ece6a',
  toolErr: '#f7768e',
  error: '#f7768e',
  ok: '#9ece6a',
  warn: '#e0af68',
} as const;

export type Theme = typeof theme;
