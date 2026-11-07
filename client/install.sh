#!/usr/bin/env bash
# Builds netcheck (command line + collector) and the desktop app, installs them for the current user,
# and (re)starts the collector as a systemd user service. Run it again after pulling changes.
#
#   ~/.local/lib/netcheck/{cli,app}     the programs
#   ~/.local/bin/netcheck, netcheck-app  links on PATH
#   ~/.config/systemd/user/netcheck.service, and the app's launcher entry and icon
set -euo pipefail
cd "$(dirname "$0")"
export AVALONIA_TELEMETRY_OPTOUT=1 DOTNET_CLI_TELEMETRY_OPTOUT=1

lib="$HOME/.local/lib/netcheck"
staging="$(mktemp -d)"
trap 'rm -rf "$staging"' EXIT

# Build into a staging folder first, so a failed build leaves the installed copy alone.
dotnet publish OpenRouter.Client -c Release -o "$staging/cli"
dotnet publish OpenRouter.Client.App -c Release -o "$staging/app"

systemctl --user stop netcheck 2>/dev/null || true
rm -rf "$lib"
mkdir -p "$lib" ~/.local/bin ~/.config/systemd/user ~/.local/share/applications ~/.local/share/icons/hicolor/scalable/apps
cp -r "$staging/cli" "$staging/app" "$lib/"
ln -sf "$lib/cli/netcheck" ~/.local/bin/netcheck
ln -sf "$lib/app/netcheck-app" ~/.local/bin/netcheck-app

install -m 644 netcheck.service ~/.config/systemd/user/netcheck.service
install -m 644 OpenRouter.Client.App/netcheck.desktop ~/.local/share/applications/netcheck.desktop
install -m 644 OpenRouter.Client.App/Assets/netcheck.svg ~/.local/share/icons/hicolor/scalable/apps/netcheck.svg

systemctl --user daemon-reload
systemctl --user enable --now netcheck
echo
echo "Installed. The collector is running:   systemctl --user status netcheck"
echo "Open the app from your launcher (netcheck), or run: netcheck-app"
