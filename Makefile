.PHONY: build test test-unit test-integration format corpus up up-obs down logs evals evals-fake e2e web-dev alerts-test bicep-build images teardown

COMPOSE ?= docker compose
API ?= http://localhost:8080

build:
	dotnet build -c Debug

format:
	dotnet format
	cd web && npx prettier --write src e2e

test: test-unit test-integration

test-unit:
	dotnet test tests/DocAssistant.UnitTests -c Debug

test-integration:  ## Testcontainers: needs Docker
	dotnet test tests/DocAssistant.IntegrationTests -c Debug

corpus:  ## synthetic PDFs, truth.json and evals/questions.json
	dotnet run --project tools/DocAssistant.SynthData -- --out data/synthetic --questions evals/questions.json

up:  ## API + worker + Postgres(pgvector) + Azurite; model = Ollama on the host unless AI_PROVIDER is set
	$(COMPOSE) up -d --build api worker postgres azurite

up-obs:
	$(COMPOSE) --profile observability up -d --build

down:
	$(COMPOSE) --profile observability down -v

logs:
	$(COMPOSE) logs -f api worker

evals:  ## against the running stack with whatever model it is configured for
	dotnet run --project tools/DocAssistant.Evals -- --api $(API) --label $(LABEL)

evals-fake:
	dotnet run --project tools/DocAssistant.Evals -- --api $(API) --label fake --gate

e2e:  ## Playwright against the running stack (builds the web app with API_BASE baked in)
	cd web && API_BASE=$(API) npx playwright test

web-dev:
	cd web && npm run dev

alerts-test:
	docker run --rm -v "$(CURDIR)/observability:/w" -w /w --entrypoint promtool prom/prometheus:v3.5.0 check rules alert_rules.yml
	docker run --rm -v "$(CURDIR)/observability:/w" -w /w --entrypoint promtool prom/prometheus:v3.5.0 test rules alerts_test.yml

bicep-build:  ## validates the templates with the Azure CLI image; no Azure account needed
	docker run --rm -v "$(CURDIR)/infra/bicep:/w" -w /w mcr.microsoft.com/azure-cli:2.67.0 sh -c "az bicep install >/dev/null 2>&1 && az bicep build --file main.bicep --stdout >/dev/null && az bicep lint --file main.bicep && echo bicep ok"

images:
	docker build -f src/DocAssistant.Api/Dockerfile -t docassistant-api:local .
	docker build -f src/DocAssistant.Worker/Dockerfile -t docassistant-worker:local .

teardown:  ## local stack down, then every Azure resource (needs az login)
	$(COMPOSE) --profile observability down -v --remove-orphans
	bash infra/bicep/teardown.sh
