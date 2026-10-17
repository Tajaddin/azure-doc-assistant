#!/usr/bin/env bash
# One-command deploy: resource group + every resource, then the API, Function and web app code.
# Needs: az login (an account with Owner on the subscription), the two app registrations from docs/DEPLOY.md,
# main.bicepparam filled in, Node 20+ and the .NET 8 SDK. Costs money while the resources exist: run teardown.sh after.
set -euo pipefail
cd "$(dirname "$0")"
LOCATION="${LOCATION:-eastus2}"
export POSTGRES_PASSWORD="${POSTGRES_PASSWORD:-$(openssl rand -base64 24 | tr -d '/+=')}"

echo "== infrastructure"
az deployment sub create --location "$LOCATION" --template-file main.bicep --parameters main.bicepparam --name docassistant-"$(date +%Y%m%d%H%M%S)" --output none
RG=$(az deployment sub list --query "[?contains(name,'docassistant-')]|[0].properties.outputs.resourceGroup.value" -o tsv)
API_APP=$(az deployment sub list --query "[?contains(name,'docassistant-')]|[0].properties.outputs.apiAppName.value" -o tsv)
FUNC_APP=$(az deployment sub list --query "[?contains(name,'docassistant-')]|[0].properties.outputs.functionAppName.value" -o tsv)
SWA_NAME=$(az deployment sub list --query "[?contains(name,'docassistant-')]|[0].properties.outputs.staticWebAppName.value" -o tsv)
API_URL=$(az deployment sub list --query "[?contains(name,'docassistant-')]|[0].properties.outputs.apiUrl.value" -o tsv)

echo "== API"
dotnet publish ../../src/DocAssistant.Api -c Release -o /tmp/docassistant-api >/dev/null
(cd /tmp/docassistant-api && zip -qr ../docassistant-api.zip .)
az webapp deploy --resource-group "$RG" --name "$API_APP" --src-path /tmp/docassistant-api.zip --type zip --output none

echo "== Function"
dotnet publish ../../src/DocAssistant.Functions -c Release -o /tmp/docassistant-func >/dev/null
(cd /tmp/docassistant-func && zip -qr ../docassistant-func.zip .)
az functionapp deployment source config-zip --resource-group "$RG" --name "$FUNC_APP" --src /tmp/docassistant-func.zip --output none

echo "== web"
(cd ../../web && npm ci --no-audit --no-fund && VITE_API_BASE="$API_URL" VITE_AUTH_MODE=entra npm run build)
SWA_TOKEN=$(az staticwebapp secrets list --name "$SWA_NAME" --resource-group "$RG" --query properties.apiKey -o tsv)
npx --yes @azure/static-web-apps-cli@2 deploy ../../web/dist --deployment-token "$SWA_TOKEN" --env production

echo "API:  $API_URL"
echo "Web:  $(az staticwebapp show --name "$SWA_NAME" --resource-group "$RG" --query defaultHostname -o tsv)"
echo "Postgres password is in Key Vault (Postgres--ConnectionString); it was not printed."
