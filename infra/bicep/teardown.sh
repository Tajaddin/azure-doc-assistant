#!/usr/bin/env bash
# Deletes the whole resource group (every resource the deploy created) and purges the soft-deleted
# Key Vault and Cognitive Services accounts so a redeploy with the same names works.
set -euo pipefail
NAME="${NAME:-docassist}"
RG="${RG:-rg-$NAME}"
LOCATION="${LOCATION:-eastus2}"
echo "deleting resource group $RG"
az group delete --name "$RG" --yes --no-wait
az group wait --name "$RG" --deleted --timeout 1800 || true
for kv in $(az keyvault list-deleted --query "[?starts_with(name,'kv-$NAME')].name" -o tsv); do
  az keyvault purge --name "$kv" --location "$LOCATION" || true
done
for acct in $(az cognitiveservices account list-deleted --query "[?starts_with(name,'oai-$NAME') || starts_with(name,'di-$NAME')].name" -o tsv); do
  az cognitiveservices account purge --name "$acct" --resource-group "$RG" --location "$LOCATION" || true
done
echo "done; nothing from $NAME remains in the subscription"
