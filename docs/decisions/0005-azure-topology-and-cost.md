# 0005. Azure topology: App Service + Functions + Static Web Apps, managed identities, one Bicep deployment

Date: 2026-10-17. Status: accepted (templates validated and linted on 2026-10-17, not yet deployed;
the 2026-11 changes are not yet built).

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

The one secret, `Postgres--ConnectionString`, exists only in Key Vault; the apps read it through Key
Vault references and `KeyVault:Uri`. Azure OpenAI, Document Intelligence and Storage (blobs, the queue,
the Function's blob trigger and its host storage) are called with the managed identities (RBAC role
assignments in the template), so no model or storage key is in app settings or Key Vault; the apps get
the storage endpoints (`Storage__BlobServiceUri`, `Storage__QueueServiceUri`) instead of a connection
string. `deploy.sh` applies the template and publishes the three apps;
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
- Postgres keeps the "allow Azure services" firewall rule, which admits connections from any Azure
  resource, other tenants' included. An allowlist of the apps' outbound addresses does not work:
  on the Consumption plan the Function can be given new outbound addresses as it scales, and Microsoft
  states that its reported addresses cannot serve as a definitive allowlist, so ingestion would lose the
  database at random. The protection is the admin password, generated per deployment by `deploy.sh` and
  stored only in Key Vault, and TLS with `SSL Mode=VerifyFull`. The residual exposure: the port is
  reachable from all of Azure, and both apps sign in as the server admin. Narrowing it means moving the
  Function to a dedicated App Service plan (B1 or above, where the possible outbound addresses are stable
  enough to allowlist, though shared with the stamp's other apps), or VNet integration with private access
  for Postgres (B1 for the API, Flex Consumption or Premium for the Function); then Entra authentication
  with a least-privileged role per app. Each adds a fixed monthly cost or a role-setup step the template
  cannot run, so none is done here.
