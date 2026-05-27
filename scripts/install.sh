#!/usr/bin/env bash
# Build FreeAgent and install (or update) it as the global `freeagent` command.
# Requires the .NET 10 SDK. The command is placed in ~/.dotnet/tools.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
pkg_out="$(mktemp -d)"
trap 'rm -rf "$pkg_out"' EXIT

echo "Packing FreeAgent (Release)…"
dotnet pack "$repo_root/src/FreeAgent.Host" -c Release -o "$pkg_out" --nologo

if dotnet tool list --global 2>/dev/null | grep -qi '^freeagent'; then
  echo "Updating existing global tool…"
  dotnet tool update --global --add-source "$pkg_out" FreeAgent
else
  echo "Installing global tool…"
  dotnet tool install --global --add-source "$pkg_out" FreeAgent
fi

echo
if command -v freeagent >/dev/null 2>&1; then
  echo "Installed: $(command -v freeagent)"
  echo "Run 'freeagent' from any project directory."
else
  echo "Installed, but 'freeagent' is not on your PATH yet. Add this to your shell profile:"
  echo '  export PATH="$PATH:$HOME/.dotnet/tools"'
fi
