#!/usr/bin/env bash
# Guided uninstall for FreeAgent.
#
#   ./scripts/uninstall.sh                  # interactive — asks before deleting state
#   ./scripts/uninstall.sh --non-interactive # remove the tool only; keep config / cache
#   ./scripts/uninstall.sh --purge           # remove the tool AND all state, no prompts
#   ./scripts/uninstall.sh --help
#
# What gets removed:
#   • Always: the `freeagent` global .NET tool (from ~/.dotnet/tools).
#   • On request: ~/.config/freeagent/  (provider config, memory, playbooks)
#   • On request: ~/.cache/freeagent/   (model server pid + log, downloaded GGUFs)
#   • Per-project session.jsonl files stay put — they're scoped to your working dirs.

set -euo pipefail

INTERACTIVE=1
PURGE=0

usage() {
  cat <<EOF
Usage: uninstall.sh [options]

Options:
  --non-interactive    Skip all prompts; remove the tool only (keep config + cache).
  --purge              Remove the tool AND ~/.config/freeagent + ~/.cache/freeagent, no prompts.
  -h, --help           Show this help.
EOF
}

for arg in "$@"; do
  case "$arg" in
    --non-interactive) INTERACTIVE=0 ;;
    --purge)           INTERACTIVE=0; PURGE=1 ;;
    -h|--help)         usage; exit 0 ;;
    *)                 echo "Unknown option: $arg" >&2; usage; exit 1 ;;
  esac
done

# Tiny color helpers (only active on a TTY).
if [[ -t 1 ]]; then
  c_bold=$'\033[1m'; c_dim=$'\033[2m'; c_red=$'\033[31m'; c_green=$'\033[32m'
  c_yellow=$'\033[33m'; c_reset=$'\033[0m'
else
  c_bold=''; c_dim=''; c_red=''; c_green=''; c_yellow=''; c_reset=''
fi

heading() { printf '\n%s%s%s\n' "$c_bold" "$1" "$c_reset"; }
ok()      { printf '  %s✓%s %s\n' "$c_green" "$c_reset" "$1"; }
warn()    { printf '  %s!%s %s\n' "$c_yellow" "$c_reset" "$1"; }
fail()    { printf '  %s✗%s %s\n' "$c_red" "$c_reset" "$1"; }

ask_yes_no() {
  local prompt="$1" default="$2" hint reply
  if [[ "$default" == "Y" ]]; then hint="[Y/n]"; else hint="[y/N]"; fi
  if [[ "$INTERACTIVE" -eq 0 ]]; then
    [[ "$default" == "Y" ]] && return 0 || return 1
  fi
  while true; do
    read -r -p "  $prompt $hint " reply || reply=""
    reply="${reply,,}"
    if [[ -z "$reply" ]]; then [[ "$default" == "Y" ]] && return 0 || return 1; fi
    case "$reply" in
      y|yes) return 0 ;;
      n|no)  return 1 ;;
      *)     echo "  please answer y or n." ;;
    esac
  done
}

heading "FreeAgent uninstaller"

# ── 1. global tool ────────────────────────────────────────────────────────────
if ! command -v dotnet >/dev/null 2>&1; then
  warn "'dotnet' is not on PATH — skipping tool uninstall."
  echo "    If you have FreeAgent installed somewhere odd, remove it manually."
else
  if dotnet tool list --global 2>/dev/null | awk 'NR>2 {print tolower($1)}' | grep -q '^freeagent$'; then
    if [[ "$PURGE" -eq 1 ]] || ask_yes_no "Uninstall the freeagent global tool" "Y"; then
      dotnet tool uninstall --global FreeAgent
      ok "Removed the global tool."
    else
      warn "Kept the global tool installed."
    fi
  else
    ok "No global 'freeagent' tool registered with dotnet."
  fi
fi

# ── 2. running local model server ────────────────────────────────────────────
xdg_cache="${XDG_CACHE_HOME:-$HOME/.cache}"
pid_file="$xdg_cache/freeagent/model-server.pid"
if [[ -f "$pid_file" ]]; then
  pid="$(cat "$pid_file" 2>/dev/null | tr -d '[:space:]' || true)"
  if [[ -n "$pid" ]] && kill -0 "$pid" 2>/dev/null; then
    warn "A /serve local model server is running (pid $pid)."
    if [[ "$PURGE" -eq 1 ]] || ask_yes_no "Stop it now" "Y"; then
      kill "$pid" 2>/dev/null || true
      sleep 0.2
      kill -0 "$pid" 2>/dev/null && kill -9 "$pid" 2>/dev/null || true
      ok "Stopped pid $pid."
    fi
  fi
fi

# ── 3. user state ────────────────────────────────────────────────────────────
xdg_config="${XDG_CONFIG_HOME:-$HOME/.config}"
config_dir="$xdg_config/freeagent"
cache_dir="$xdg_cache/freeagent"

if [[ -d "$config_dir" ]]; then
  size="$(du -sh "$config_dir" 2>/dev/null | cut -f1 || echo '?')"
  if [[ "$PURGE" -eq 1 ]] || ask_yes_no "Delete $config_dir ($size — provider config, memory, playbooks)" "N"; then
    rm -rf "$config_dir"
    ok "Removed $config_dir."
  fi
else
  ok "No config directory at $config_dir."
fi

if [[ -d "$cache_dir" ]]; then
  size="$(du -sh "$cache_dir" 2>/dev/null | cut -f1 || echo '?')"
  if [[ "$PURGE" -eq 1 ]] || ask_yes_no "Delete $cache_dir ($size — downloaded GGUFs, model-server pid + log)" "N"; then
    rm -rf "$cache_dir"
    ok "Removed $cache_dir."
  fi
else
  ok "No cache directory at $cache_dir."
fi

# ── 4. PATH cleanup note ─────────────────────────────────────────────────────
if [[ "$INTERACTIVE" -eq 1 ]]; then
  heading "Manual cleanup (optional)"
  cat <<EOF
  • Per-project transcripts are NOT touched — search for them with:
      find ~ -name 'session.jsonl' -o -name 'session-fork-*.jsonl' 2>/dev/null
  • If the installer added '~/.dotnet/tools' to your shell profile, remove that line
    yourself if you don't use other .NET global tools.
  • The repo checkout itself (if you cloned one) is also untouched — 'rm -rf <repo>' when ready.
EOF
fi

heading "Done"
