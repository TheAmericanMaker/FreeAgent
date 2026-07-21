#!/usr/bin/env bash
# FreeAgent one-line installer (Linux / macOS).
#
#   curl -fsSL https://raw.githubusercontent.com/TheAmericanMaker/FreeAgent/main/scripts/get.sh | bash
#
# Detects your OS, installs the .NET 10 SDK if missing, clones the repo (or uses the
# current checkout), builds and installs the `freeagent` global tool, then hands off
# to the interactive setup wizard.
#
# Flags:
#   --skip-setup        don't run 'freeagent setup' at the end
#   --non-interactive   skip all prompts (implies --skip-setup)
#   --tui               also set up the full-screen TUI (Bun + self-contained server)
#   --repo <path>       use an existing clone instead of cloning fresh
#   -h, --help          show this help

set -euo pipefail

INTERACTIVE=1
SHOULD_RUN_SETUP=1
INSTALL_TUI=0
REPO_PATH=""

usage() {
  sed -n '2,18p' "$0" 2>/dev/null || cat <<'EOF'
Usage: get.sh [options]

Options:
  --skip-setup        don't run 'freeagent setup' at the end
  --non-interactive   skip all prompts (implies --skip-setup)
  --tui               also set up the full-screen TUI (Bun + self-contained server)
  --repo <path>       use an existing clone instead of cloning fresh
  -h, --help          show this help
EOF
}

for arg in "$@"; do
  case "$arg" in
    --non-interactive) INTERACTIVE=0; SHOULD_RUN_SETUP=0 ;;
    --skip-setup)      SHOULD_RUN_SETUP=0 ;;
    --tui)             INSTALL_TUI=1 ;;
    --repo)            shift_next=1; REPO_NEXT=1 ;;
    -h|--help)         usage; exit 0 ;;
    *)
      if [[ "${REPO_NEXT:-0}" == "1" ]]; then REPO_PATH="$arg"; REPO_NEXT=0
      else echo "Unknown option: $arg" >&2; usage; exit 1; fi ;;
  esac
done

# ── colors ───────────────────────────────────────────────────────────────────
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

# ── detect OS ────────────────────────────────────────────────────────────────
heading "FreeAgent installer"
os="$(uname -s)"
arch="$(uname -m)"

case "$os" in
  Linux)  os_name="linux" ;;
  Darwin) os_name="macos" ;;
  *)      fail "Unsupported OS: $os"; exit 1 ;;
esac

case "$arch" in
  x86_64|amd64) arch_name="x64" ;;
  arm64|aarch64) arch_name="arm64" ;;
  *)            fail "Unsupported architecture: $arch"; exit 1 ;;
esac

ok "Detected: $os_name $arch_name"

# ── 1. .NET SDK ──────────────────────────────────────────────────────────────
if command -v dotnet >/dev/null 2>&1; then
  sdk_version="$(dotnet --version 2>/dev/null || echo 'unknown')"
  sdk_major="${sdk_version%%.*}"
  if [[ "$sdk_major" =~ ^[0-9]+$ ]] && (( sdk_major >= 10 )); then
    ok ".NET SDK $sdk_version already installed."
  else
    warn ".NET SDK $sdk_version found — need 10+. Will install."
    dotnet_installed=0
  fi
else
  dotnet_installed=0
fi

# Install .NET SDK if missing or too old
if [[ "${dotnet_installed:-1}" == "0" ]]; then
  heading "Installing .NET 10 SDK"

  if [[ "$os_name" == "linux" ]]; then
    # Detect package manager
    if command -v dnf >/dev/null 2>&1; then
      # Fedora / RHEL
      SUDO=""; [ "$(id -u)" -ne 0 ] && SUDO="sudo"
      $SUDO dnf install -y dotnet-sdk-10.0 2>/dev/null && dotnet_installed=1 || {
        # Fall back to Microsoft's install script
        warn "dnf install failed — trying Microsoft's dotnet-install script."
      }
    elif command -v apt-get >/dev/null 2>&1; then
      # Ubuntu / Debian
      SUDO=""; [ "$(id -u)" -ne 0 ] && SUDO="sudo"
      export DEBIAN_FRONTEND=noninteractive
      $SUDO apt-get update -y || true
      $SUDO apt-get install -y --no-install-recommends dotnet-sdk-10.0 2>/dev/null && dotnet_installed=1 || {
        warn "apt install failed — trying Microsoft's dotnet-install script."
      }
    elif command -v brew >/dev/null 2>&1; then
      # Homebrew on Linux
      brew install dotnet-sdk 2>/dev/null && dotnet_installed=1 || true
    fi

    if [[ "${dotnet_installed:-0}" != "1" ]]; then
      # Microsoft's official dotnet-install.sh as a fallback for any Linux distro
      curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
      chmod +x /tmp/dotnet-install.sh
      /tmp/dotnet-install.sh --channel 10.0 --install-dir "$HOME/.dotnet"
      export PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$PATH"
      # Persist PATH for future shells
      profile=""
      case "$(basename "${SHELL:-}")" in
        zsh)  profile="$HOME/.zshrc" ;;
        bash) profile="$HOME/.bashrc" ;;
        *)    profile="$HOME/.profile" ;;
      esac
      if [[ -n "$profile" ]] && ! grep -q '.dotnet' "$profile" 2>/dev/null; then
        printf '\n# Added by FreeAgent installer\nexport PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$PATH"\n' >> "$profile"
        ok "Added .dotnet to PATH in $profile"
      fi
      dotnet_installed=1
    fi
  elif [[ "$os_name" == "macos" ]]; then
    if command -v brew >/dev/null 2>&1; then
      brew install dotnet-sdk 2>/dev/null && dotnet_installed=1 || true
    fi
    if [[ "${dotnet_installed:-0}" != "1" ]]; then
      curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
      chmod +x /tmp/dotnet-install.sh
      /tmp/dotnet-install.sh --channel 10.0 --install-dir "$HOME/.dotnet"
      export PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$PATH"
      dotnet_installed=1
    fi
  fi

  if command -v dotnet >/dev/null 2>&1; then
    ok ".NET SDK $(dotnet --version) installed."
  else
    fail "Could not install the .NET 10 SDK."
    echo "  Install it manually from https://dot.net/download and re-run."
    exit 1
  fi
fi

# ── 2. clone or use existing repo ────────────────────────────────────────────
if [[ -n "$REPO_PATH" ]]; then
  repo_root="$REPO_PATH"
  ok "Using existing checkout: $repo_root"
elif [[ -f "$(dirname "$0")/../FreeAgent.slnx" ]]; then
  # Running from inside a clone
  repo_root="$(cd "$(dirname "$0")/.." && pwd)"
  ok "Running from existing clone: $repo_root"
else
  heading "Cloning FreeAgent"
  clone_dir="$(mktemp -d /tmp/freeagent-XXXXXX)"
  git clone --depth 1 https://github.com/TheAmericanMaker/FreeAgent.git "$clone_dir"
  repo_root="$clone_dir"
  ok "Cloned to $repo_root"
fi

# ── 3. PATH for dotnet tools ─────────────────────────────────────────────────
tools_dir="${DOTNET_TOOLS_PATH:-$HOME/.dotnet/tools}"
if [[ ":$PATH:" != *":$tools_dir:"* ]]; then
  export PATH="$PATH:$tools_dir"
  profile=""
  case "$(basename "${SHELL:-}")" in
    zsh)  profile="$HOME/.zshrc" ;;
    bash) profile="$HOME/.bashrc" ;;
    fish) profile="$HOME/.config/fish/config.fish" ;;
    *)    profile="$HOME/.profile" ;;
  esac
  if [[ -n "$profile" ]] && ! grep -q "$tools_dir" "$profile" 2>/dev/null; then
    if [[ "$(basename "${SHELL:-}")" == "fish" ]]; then
      printf '\n# Added by FreeAgent installer\nfish_add_path %s\n' "$tools_dir" >> "$profile"
    else
      printf '\n# Added by FreeAgent installer\nexport PATH="$PATH:%s"\n' "$tools_dir" >> "$profile"
    fi
    ok "Added $tools_dir to PATH in $profile"
  fi
fi

# ── 4. build + pack + install ────────────────────────────────────────────────
heading "Building FreeAgent"
pkg_out="$(mktemp -d)"
trap 'rm -rf "$pkg_out"' EXIT

dotnet pack "$repo_root/src/FreeAgent.Host" -c Release -o "$pkg_out" --nologo

heading "Installing the global tool"
if dotnet tool list --global 2>/dev/null | awk 'NR>2 {print tolower($1)}' | grep -q '^freeagent$'; then
  dotnet tool update --global --add-source "$pkg_out" FreeAgent
else
  dotnet tool install --global --add-source "$pkg_out" FreeAgent
fi

if command -v freeagent >/dev/null 2>&1; then
  ok "Installed: $(command -v freeagent)"
else
  warn "Installed — restart your shell or run 'source ~/.bashrc' to get 'freeagent' on PATH."
fi

# ── 5. TUI (optional) ────────────────────────────────────────────────────────
if [[ "$INSTALL_TUI" == "1" ]]; then
  heading "Setting up the TUI"
  bash "$repo_root/scripts/install-tui.sh"
fi

# ── 6. interactive setup ─────────────────────────────────────────────────────
if [[ "$SHOULD_RUN_SETUP" == "1" ]]; then
  heading "Provider setup"
  if [[ "$INTERACTIVE" == "1" ]]; then
    "$tools_dir/freeagent" setup || true
  else
    echo "  Skipped (--non-interactive). Run 'freeagent setup' later."
  fi
fi

# ── done ─────────────────────────────────────────────────────────────────────
heading "Done!"
echo "  Start a session:  ${c_cyan}cd ~/your-project && freeagent${c_reset}"
echo "  Get help:         ${c_cyan}freeagent --help${c_reset}"
echo "  Configure:        ${c_cyan}freeagent setup${c_reset}"
if [[ "$INSTALL_TUI" == "1" ]]; then
  echo "  Launch the TUI:   ${c_cyan}freeagent-ui${c_reset} (from any directory)"
fi
echo
echo "  Docs: ${c_cyan}https://github.com/TheAmericanMaker/FreeAgent${c_reset}"