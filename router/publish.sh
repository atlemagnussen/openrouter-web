#!/usr/bin/env bash
# Builds the TypeScript client and the app, installs it to /opt/openrouter-web and, if the
# openrouter-web systemd service is enabled, restarts it. Otherwise restart the app yourself (run.sh).
set -euo pipefail
cd "$(dirname "$0")"

# Client: exact versions from package-lock.json, built into OpenRouter.Web/wwwroot/dist.
(cd client && npm ci && npm run build)

# App: the client is already built, so the project's own client build step is skipped.
rm -rf ./publish
dotnet publish OpenRouter.Web -c Release -r linux-x64 --self-contained -o ./publish -p:SkipClientBuild=true

# Replace the installed copy entirely, so files removed from the app don't linger.
sudo rm -rf /opt/openrouter-web
sudo mkdir -p /opt/openrouter-web
sudo cp -r ./publish/. /opt/openrouter-web/
sudo chown -R root:root /opt/openrouter-web
sudo chmod -R u=rwX,go=rX /opt/openrouter-web

if systemctl is-enabled --quiet openrouter-web 2>/dev/null; then
    sudo systemctl restart openrouter-web
    echo "Installed to /opt/openrouter-web and restarted the openrouter-web service."
else
    echo "Installed to /opt/openrouter-web. Restart the app to use it."
fi
