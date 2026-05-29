#!/usr/bin/env bash
# Install (or update) the `freeagent` global .NET tool.
#
# Run from a clone of the repo:
#   ./scripts/install.sh                  # interactive — pre-flight checks + setup wizard
#   ./scripts/install.sh --non-interactive # build + install only; skip prompts and setup wizard
#   ./scripts/install.sh --help            # show usage and exit
#
# Requirements: the .NET 10 SDK. The script does NOT install .NET for you — that's a system
# package decision (apt / brew / scoop / winget) and we don't want to surprise you with it.
# If the SDK is missing, the script points at https://dot.net/download and exits cleanly.

set -euo pipefail

INTERACTIVE=1
SHOULD_RUN_SETUP=1

usage() {
  cat <<EOF
Usage: install.sh [options]

Options:
  --non-interactive    Skip all prompts (build + install + done). Implies --skip-setup.
  --skip-setup         Don't run 'freeagent setup' at the end.
  -h, --help           Show this help.

Without flags the script asks a few questions, builds and installs the tool, then hands off
to the interactive provider-config wizard.
EOF
}

for arg in "$@"; do
  case "$arg" in
    --non-interactive) INTERACTIVE=0; SHOULD_RUN_SETUP=0 ;;
    --skip-setup)      SHOULD_RUN_SETUP=0 ;;
    -h|--help)         usage; exit 0 ;;
    *)                 echo "Unknown option: $arg" >&2; usage; exit 1 ;;
  esac
done

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

# ── tiny color helpers (only active on a TTY) ────────────────────────────────
if [[ -t 1 ]]; then
  c_bold=$'\033[1m'; c_dim=$'\033[2m'; c_red=$'\033[31m'; c_green=$'\033[32m'
  c_yellow=$'\033[33m'; c_cyan=$'\033[36m'; c_reset=$'\033[0m'
else
  c_bold=''; c_dim=''; c_red=''; c_green=''; c_yellow=''; c_cyan=''; c_reset=''
fi

heading() { printf '\n%s%s%s\n' "$c_bold" "$1" "$c_reset"; }
ok()      { printf '  %s✓%s %s\n' "$c_green" "$c_reset" "$1"; }
warn()    { printf '  %s!%s %s\n' "$c_yellow" "$c_reset" "$1"; }
fail()    { printf '  %s✗%s %s\n' "$c_red" "$c_reset" "$1"; }

ask_yes_no() {
  # ask_yes_no "prompt" default(Y|N) → returns 0 for yes, 1 for no
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

# ── 1. pre-flight checks ─────────────────────────────────────────────────────
heading "FreeAgent installer"
echo "$c_dim  repo: $repo_root$c_reset"

if ! command -v dotnet >/dev/null 2>&1; then
  fail "'dotnet' is not on your PATH."
  echo "    Install the .NET 10 SDK from https://dot.net/download and re-run this script."
  exit 1
fi

sdk_version="$(dotnet --version 2>/dev/null || echo 'unknown')"
sdk_major="${sdk_version%%.*}"
if [[ "$sdk_major" =~ ^[0-9]+$ ]] && (( sdk_major >= 10 )); then
  ok ".NET SDK $sdk_version detected."
else
  warn ".NET SDK $sdk_version detected — this repo pins net10.0; install the .NET 10 SDK."
  echo "    https://dot.net/download"
  if ! ask_yes_no "Continue anyway (the build will probably fail)" "N"; then
    exit 1
  fi
fi

# Detect where `dotnet tool install --global` puts binaries and whether that's on PATH.
tools_dir="${DOTNET_TOOLS_PATH:-$HOME/.dotnet/tools}"
if [[ ":$PATH:" == *":$tools_dir:"* ]]; then
  ok "$tools_dir is on PATH."
else
  warn "$tools_dir is NOT on PATH — freeagent won't be runnable as a bare command."
  if ask_yes_no "Add 'export PATH=\"\$PATH:$tools_dir\"' to your shell profile" "Y"; then
    # Pick the most likely profile file based on the shell.
    case "$(basename "${SHELL:-}")" in
      zsh)   profile="$HOME/.zshrc" ;;
      bash)  profile="$HOME/.bashrc" ;;
      fish)  profile="$HOME/.config/fish/config.fish" ;;
      *)     profile="$HOME/.profile" ;;
    esac
    line="export PATH=\"\$PATH:$tools_dir\""
    [[ "$(basename "${SHELL:-}")" == "fish" ]] && line="set -gx PATH \$PATH $tools_dir"
    if [[ -f "$profile" ]] && grep -qF "$tools_dir" "$profile"; then
      ok "PATH entry already present in $profile."
    else
      mkdir -p "$(dirname "$profile")"
      printf '\n# Added by FreeAgent installer\n%s\n' "$line" >> "$profile"
      ok "Appended PATH export to $profile (restart your shell or 'source' it to take effect)."
    fi
  fi
fi

# Detect an existing installation so the script's update message is honest.
already_installed=0
if dotnet tool list --global 2>/dev/null | awk 'NR>2 {print tolower($1)}' | grep -q '^freeagent$'; then
  already_installed=1
fi

if (( already_installed )); then
  heading "Existing installation detected"
  echo "  An older 'freeagent' global tool is already installed."
  if ! ask_yes_no "Update it from this repo" "Y"; then
    echo "  Aborting at user request."
    exit 0
  fi
fi

# ── 2. build + pack + install ────────────────────────────────────────────────
heading "Building (Release)"
pkg_out="$(mktemp -d)"
trap 'rm -rf "$pkg_out"' EXIT
dotnet pack "$repo_root/src/FreeAgent.Host" -c Release -o "$pkg_out" --nologo

heading "Installing the global tool"
if (( already_installed )); then
  dotnet tool update --global --add-source "$pkg_out" FreeAgent
else
  dotnet tool install --global --add-source "$pkg_out" FreeAgent
fi

if command -v freeagent >/dev/null 2>&1; then
  ok "Installed: $(command -v freeagent)"
else
  warn "Installed, but 'freeagent' is not yet resolvable in THIS shell."
  echo "    Restart your shell (or 'source' the profile you updated) and try 'freeagent --version'."
fi

# ── 3. interactive provider setup ────────────────────────────────────────────
if (( SHOULD_RUN_SETUP )); then
  if ask_yes_no "Run 'freeagent setup' now to pick a provider and write your config" "Y"; then
    # Use the freshly-built tool path so PATH-pending shells still work.
    "$tools_dir/freeagent" setup || true
  else
    echo
    echo "  Skipped. Run 'freeagent setup' later, or set provider env vars manually."
    echo "  See docs/usage.md for the per-provider env-var matrix."
  fi
fi

heading "Done"
echo "  Start a session from any project directory: ${c_cyan}cd ~/code/my-project && freeagent${c_reset}"
echo "  Type ${c_cyan}/help${c_reset} at the prompt for the command palette."
