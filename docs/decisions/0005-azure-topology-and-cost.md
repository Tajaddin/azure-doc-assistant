# 0005. Azure topology: App Service + Functions + Static Web Apps, managed identities, one Bicep deployment

Date: 2026-10-17. Status: accepted (templates validated and linted, not yet deployed).

## Context

The deployment should look like the posting's environment, cost nothing to almost nothing on an Azure
for Students credit, and be removable in one command.

## Decision

`infra/bicep/main.bicep` (subscription scope) creates a resource group and `resources.bicep` creates:
storage (private container), Azure OpenAI (gpt-4o-mini and text-embedding-3-small deployments, local
auth disabled), Document Intelligence (F0), Azure Database for PostgreSQL Flexible Server (B1ms, `vector`
extension), Key Vault (RBAC), Log Analytics + Application Insights, an App Service plan (F1 by default) with
the API, a Consumption Function App for ingestion, a Free Static Web App, and three user-assigned
identities: API, Function and a deploy identity federated to the GitHub repository for OIDC.

Secrets exist only in Key Vault (`Postgres--ConnectionString`, `Storage--ConnectionString`); the apps read
them through Key Vault references and `KeyVault:Uri`. Azure OpenAI, Document Intelligence and Blob
Storage are called with the managed identities (RBAC role assignments in the template), so no model or
storage key is in app settings. `deploy.sh` applies the template and publishes the three apps;
`teardown.sh` deletes the resource group and purges the soft-deleted vault and cognitive accounts.

## Consequences

- Monthly cost while up (eastus2, October 2026 list prices): App Service F1 $0, Functions Consumption
  $0 within the free grant, Static Web App Free $0, Postgres B1ms about $13 (the only fixed line), Key
  Vault and storage cents, Azure OpenAI per token (the full eval set is well under $0.10 on gpt-4o-mini),
  Document Intelligence F0 free for 500 pages a month. The credit covers it; teardown stops it.
- Two things cannot be created by Bicep and are manual: the Entra app registrations (API with an exposed
  scope, SPA with a redirect URI) and the GitHub repository variables and secrets the deploy workflow
  reads. `docs/DEPLOY.md` lists them in order.
- The F1 plan has no Always On and 60 CPU-minutes a day: fine for a demo, cold starts included. B1 is a
  one-parameter change.
- Azure AI Search is intentionally absent (see 0001); the Postgres server carries both memory and vectors.
