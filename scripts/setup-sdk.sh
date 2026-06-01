#!/usr/bin/env bash
set -euo pipefail

# Install the .NET 10 SDK so `dotnet build` / `dotnet test` work in this checkout.
#
# Why this exists: the Claude Code web sandbox (and some CI/containers) ship without `dotnet`,
# and the public dotnet-install mirrors (dot.net, builds.dotnet.microsoft.com, aka.ms) are
# network-blocked there. The Ubuntu 24.04 archive — which *is* reachable — ships
# `dotnet-sdk-10.0` (10.0.10x), and that satisfies global.json (version 10.0.100,
# rollForward: latestMinor). So on Ubuntu/Debian we install via apt.
#
# Usage (run once per fresh sandbox session, then build/test as usual):
#   ./scripts/setup-sdk.sh
#   dotnet build FreeAgent.slnx && dotnet test FreeAgent.slnx
#
# Idempotent: exits early when `dotnet` is already on PATH, so it's safe to re-run. A local
# machine that already has the SDK doesn't need this.

if command -v dotnet >/dev/null 2>&1; then
  echo "dotnet already installed: $(dotnet --version)"
  exit 0
fi

if ! command -v apt-get >/dev/null 2>&1; then
  echo "This helper installs the SDK via apt (Ubuntu/Debian)." >&2
  echo "On another OS, install the .NET 10 SDK from https://dotnet.microsoft.com/download" >&2
  echo "(or your package manager) and re-run the build." >&2
  exit 1
fi

SUDO=""
if [ "$(id -u)" -ne 0 ]; then SUDO="sudo"; fi

export DEBIAN_FRONTEND=noninteractive
# `apt-get update` can fail on unrelated third-party PPAs baked into a base image; the dotnet
# package lives in the main Ubuntu archive, so a PPA hiccup shouldn't abort the install.
$SUDO apt-get update -y || true
$SUDO apt-get install -y --no-install-recommends dotnet-sdk-10.0

echo "Installed .NET SDK: $(dotnet --version)"
