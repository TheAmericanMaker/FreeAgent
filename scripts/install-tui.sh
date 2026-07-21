#!/usr/bin/env bash
# FreeAgent TUI installer (Linux / macOS).
#
#   bash scripts/install-tui.sh
#
# Sets up the full-screen TUI app: installs Bun if missing, restores the TUI's deps, and publishes
# FreeAgent.Server as a self-contained binary into clients/tui/dist/server so the TUI launches it
# instantly with no .NET SDK at run time.
# After this, run the app with:   scripts/freeagent-ui   (or: cd clients/tui && bun run tui)
#
# (This installs the graphical TUI. For the headless CLI global tool instead, see scripts/install.sh.)
#
# Flags:
#   --skip-publish      only set up the TUI (use the dev `dotnet run` server path)
#   --runtime <rid>     override the publish RID (default: auto from uname)
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
tui_dir="$repo_root/clients/tui"
dist_server="$tui_dir/dist/server"

skip_publish=0
runtime=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --skip-publish) skip_publish=1; shift ;;
    --runtime) runtime="$2"; shift 2 ;;
    -h|--help) sed -n '2,16p' "$0"; exit 0 ;;
    *) echo "unknown flag: $1" >&2; exit 2 ;;
  esac
done

say()  { printf '\033[36m==> %s\033[0m\n' "$1"; }
ok()   { printf '\033[32m  + %s\033[0m\n' "$1"; }
warn() { printf '\033[33m  ! %s\033[0m\n' "$1"; }

say 'FreeAgent TUI setup'

# 1. Bun ------------------------------------------------------------------------------------------
if command -v bun >/dev/null 2>&1; then
  bun_bin="$(command -v bun)"
elif [[ -x "$HOME/.bun/bin/bun" ]]; then
  bun_bin="$HOME/.bun/bin/bun"
else
  say 'Installing Bun…'
  curl -fsSL https://bun.sh/install | bash
  bun_bin="$HOME/.bun/bin/bun"
fi
[[ -x "$bun_bin" ]] || { echo "Bun not found after install" >&2; exit 1; }
ok "Bun: $bun_bin"

# 2. TUI dependencies -----------------------------------------------------------------------------
say 'Installing TUI dependencies…'
(cd "$tui_dir" && "$bun_bin" install)
ok 'bun install complete'

# 3. Publish the server ---------------------------------------------------------------------------
if [[ "$skip_publish" -eq 1 ]]; then
  warn 'Skipping server publish (--skip-publish). The TUI will use `dotnet run` (slower; needs the .NET SDK).'
else
  command -v dotnet >/dev/null 2>&1 || { echo "The .NET SDK (dotnet) is required to publish the server. Install .NET 10 SDK, or re-run with --skip-publish." >&2; exit 1; }
  if [[ -z "$runtime" ]]; then
    os="$(uname -s)"; arch="$(uname -m)"
    case "$os" in Linux) plat=linux ;; Darwin) plat=osx ;; *) echo "unsupported OS: $os" >&2; exit 1 ;; esac
    case "$arch" in x86_64|amd64) cpu=x64 ;; arm64|aarch64) cpu=arm64 ;; *) echo "unsupported arch: $arch" >&2; exit 1 ;; esac
    runtime="$plat-$cpu"
  fi
  say "Publishing FreeAgent.Server ($runtime, self-contained)…"
  rm -rf "$dist_server"
  # Single-file + compression keeps the download small; no trimming (the kernel uses reflection to
  # discover capability types, which trimming would strip).
  dotnet publish "$repo_root/src/FreeAgent.Server" \
    -c Release -r "$runtime" --self-contained true \
    -p:PublishSingleFile=true -p:PublishTrimmed=false -p:EnableCompressionInSingleFile=true \
    -o "$dist_server"
  chmod +x "$dist_server/FreeAgent.Server" 2>/dev/null || true
  ok "Published to $dist_server"
fi

# Write a marker file so freeagent-ui-global can find the TUI source.
mkdir -p "$HOME/.config/freeagent"
echo "$tui_dir" > "$HOME/.config/freeagent/tui-path"
ok "TUI path registered"

# Install the global launcher so 'freeagent-ui' works from any directory.
local_bin="$HOME/.local/bin"
mkdir -p "$local_bin"
cp "$repo_root/scripts/freeagent-ui-global" "$local_bin/freeagent-ui"
chmod +x "$local_bin/freeagent-ui"

# Ensure ~/.local/bin is on PATH.
if [[ ":$PATH:" != *":$local_bin:"* ]]; then
  profile=""
  case "$(basename "${SHELL:-}")" in
    zsh)  profile="$HOME/.zshrc" ;;
    bash) profile="$HOME/.bashrc" ;;
    fish) profile="$HOME/.config/fish/config.fish" ;;
    *)    profile="$HOME/.profile" ;;
  esac
  if [[ -n "$profile" ]] && ! grep -q "$local_bin" "$profile" 2>/dev/null; then
    if [[ "$(basename "${SHELL:-}")" == "fish" ]]; then
      printf '\n# Added by FreeAgent installer\nfish_add_path %s\n' "$local_bin" >> "$profile"
    else
      printf '\n# Added by FreeAgent installer\nexport PATH="$PATH:%s"\n' "$local_bin" >> "$profile"
    fi
    ok "Added $local_bin to PATH in $profile"
  fi
fi
ok "Installed freeagent-ui to $local_bin/freeagent-ui"

echo
say 'Done.'
printf '  Run the app from any directory:\n    %sfreeagent-ui%s\n  or from the repo:\n    scripts/freeagent-ui\n  or:\n    cd clients/tui && bun run tui\n' "$c_cyan" "$c_reset" 2>/dev/null || printf '  Run the app from any directory:\n    freeagent-ui\n  or from the repo:\n    scripts/freeagent-ui\n'
