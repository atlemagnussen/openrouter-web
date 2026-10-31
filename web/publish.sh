dotnet publish OpenRouter.Web -c Release -r linux-x64 --self-contained -o ./publish
sudo mkdir -p /opt/openrouter-web
sudo cp -r ./publish/. /opt/openrouter-web/
sudo chown -R root:root /opt/openrouter-web
sudo chmod -R u=rwX,go=rX /opt/openrouter-web