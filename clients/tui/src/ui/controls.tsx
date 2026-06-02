// Thin wrappers around the opentui intrinsics whose names collide with React's built-in DOM elements
// (`input`, `select`). Because @types/react also declares `input`/`select` in JSX.IntrinsicElements,
// the merged element type intersects opentui's `onInput(value: string)` with the DOM
// `onInput(event)`, which no plain handler satisfies. Building the element via `createElement` here
// (instead of JSX) sidesteps the intrinsic-element type checking once, so the rest of the app gets
// clean, correctly-typed props instead of scattering casts at every call site.

import { Component, createElement, type ReactNode } from 'react';
import type { SelectOption } from '@opentui/core';
import { maskOf, reconstructSecret } from '../secret';

interface Style {
  [key: string]: unknown;
}

export interface TextInputProps {
  focused?: boolean;
  value?: string;
  placeholder?: string;
  onInput?: (value: string) => void;
  onSubmit?: (value: string) => void;
  style?: Style;
}

export function TextInput(props: TextInputProps): ReactNode {
  return createElement('input' as never, props as never);
}

export interface OptionSelectProps {
  focused?: boolean;
  options: SelectOption[];
  onSelect?: (index: number, option: SelectOption | null) => void;
  onChange?: (index: number, option: SelectOption | null) => void;
  style?: Style;
}

export function OptionSelect(props: OptionSelectProps): ReactNode {
  return createElement('select' as never, props as never);
}

export interface SecretInputProps {
  focused?: boolean;
  /** The real secret value (the parent owns it); this component renders it masked. */
  value: string;
  placeholder?: string;
  /** Show the real value instead of mask glyphs (a reveal toggle). */
  reveal?: boolean;
  onInput?: (value: string) => void;
  onSubmit?: (value: string) => void;
  style?: Style;
}

/**
 * A password-style input. Renders mask glyphs while keeping the real value out of the on-screen
 * buffer, reconstructing it from each edit (see `reconstructSecret`). Set `reveal` to show the value.
 */
export function SecretInput({ focused, value, placeholder, reveal, onInput, onSubmit, style }: SecretInputProps): ReactNode {
  const display = reveal ? value : maskOf(value);
  return createElement('input' as never, {
    focused,
    value: display,
    placeholder,
    onInput: (next: string) => onInput?.(reveal ? next : reconstructSecret(value, next)),
    onSubmit: () => onSubmit?.(value),
    style,
  } as never);
}

interface SafeMarkdownProps {
  content: string;
  /** Fallback text color used if the markdown renderable fails to render. */
  fallbackColor?: string;
}

/**
 * Renders assistant output as Markdown (code blocks, emphasis, lists) for an opencode-grade look,
 * wrapped in an error boundary so that — should the native markdown renderable ever fail — the turn
 * still shows as plain text instead of crashing the whole TUI. Production safety over prettiness.
 */
export class SafeMarkdown extends Component<SafeMarkdownProps, { failed: boolean }> {
  state = { failed: false };
  static getDerivedStateFromError() {
    return { failed: true };
  }
  render(): ReactNode {
    if (this.state.failed) {
      return createElement('text' as never, { content: this.props.content, style: { fg: this.props.fallbackColor, flexGrow: 1 } } as never);
    }
    return createElement('markdown' as never, { content: this.props.content, style: { flexGrow: 1 } } as never);
  }
}
