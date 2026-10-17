# Running and deploying

## Local, no Docker

Prerequisites: .NET 8 SDK, Node 24, Docker (for Postgres and Azurite), and Ollama with the two models.

```bash
ollama pull qwen2.5:3b && ollama pull nomic-embed-text
docker compose up -d postgres azurite
dotnet run --project tools/DocAssistant.SynthData          # data/synthetic + evals/questions.json
dotnet run --project src/DocAssistant.Api                  # http://localhost:5000/swagger (dev auth)
dotnet run --project src/DocAssistant.Worker               # in a second terminal
cd web && cp .env.example .env.local && npm ci && npm run dev   # http://localhost:5173
```

Sign in with any test user name; the API mints a development token. Upload PDFs from `data/synthetic`.

## Compose

```bash
cp .env.example .env
make up            # api :8080, worker, postgres :5434, azurite; model = Ollama on the host
make up-obs        # + otel-collector, tempo, prometheus :9092, alertmanager :9095, grafana :3003
make evals LABEL=ollama-qwen3.5-4b   # uploads the corpus, asks 46 questions, extracts 20 invoices, writes evals/results
make e2e           # Playwright against the running stack
make down
```

`AI_PROVIDER=fake make up` runs without any model (deterministic answers) for plumbing tests;
`AI_PROVIDER=azure` with `AZURE_OPENAI_ENDPOINT` and either `AZURE_OPENAI_API_KEY` or `az login` uses Azure OpenAI
from the laptop. Set `OTEL_EXPORTER_OTLP_ENDPOINT=http://otel-collector:4318` in `.env` with the observability
profile so the API and worker export spans.

## Tests

```bash
make test-unit          # 47 tests, no external dependencies
make test-integration   # 7 tests against pgvector + Azurite in Testcontainers
make alerts-test        # promtool unit tests for the 6 alert rules
make bicep-build        # az bicep build + lint in the Azure CLI image
```

## Azure (manual, uses his credit)

Everything in Azure is created by `infra/bicep` and removed by `infra/bicep/teardown.sh`. Two things
cannot be created by Bicep and are done once by hand in the Entra admin centre:

1. **API app registration**: name `docassistant-api`; under *Expose an API* set the Application ID URI to
   `api://<client id>` and add a scope `access_as_user` (admins and users). Note the client id.
2. **SPA app registration**: name `docassistant-web`; platform *Single-page application* with redirect
   URIs `http://localhost:5173` and (after deploy) `https://<static web app host>`; under *API permissions*
   add the API's `access_as_user` scope and grant consent. Note the client id.

Then:

```bash
az login                                            # an account with Owner on the subscription
az provider register -n Microsoft.CognitiveServices -n Microsoft.DBforPostgreSQL -n Microsoft.Web
cd infra/bicep && cp main.bicepparam.example main.bicepparam   # fill in the two client ids and the repo
bash deploy.sh                                      # about 15 minutes; prints the API and web URLs
```

`deploy.sh` applies the template at subscription scope, publishes the API to App Service, the Function to
the Consumption plan and the web build to the Static Web App. After the first deploy add the SWA host to
the SPA registration's redirect URIs.

For the GitHub `deploy` workflow (code-only redeploys over OIDC): repository *Settings, Secrets and
variables, Actions*. Secrets: `AZURE_CLIENT_ID` (output `deployIdentityClientId`), `AZURE_TENANT_ID`,
`AZURE_SUBSCRIPTION_ID`, `SWA_DEPLOYMENT_TOKEN` (`az staticwebapp secrets list`). Variables:
`API_APP_NAME`, `FUNCTION_APP_NAME`, `API_URL`, `SPA_CLIENT_ID`, `API_SCOPE` (`api://<api client id>/access_as_user`).

Smoke test:

```bash
curl -s https://<api host>/readyz
```

Then sign in on the Static Web App with a Microsoft account from the tenant, upload a PDF from
`data/synthetic`, ask a question. Application Insights (Transaction search) shows the `ask` trace with one
span per model round.

Cost while up: Postgres B1ms about $13/month is the only fixed line; App Service F1, Functions Consumption,
Static Web Apps Free and Document Intelligence F0 are free tiers; Azure OpenAI is per token. **Tear it down
when done:**

```bash
make teardown        # compose down -v, then az group delete + purge of the soft-deleted vault and AI accounts
```

## CI

`ci.yml` on every push and pull request: `dotnet format --verify-no-changes`, build, unit tests,
integration tests with Testcontainers, coverage floor 65%; web lint, Prettier, typecheck, Vitest, build;
end to end: compose with the fake model, the eval gate through the API, Playwright; promtool alert tests;
`az bicep build` and lint; gitleaks; both container images. `deploy.yml` is manual.
