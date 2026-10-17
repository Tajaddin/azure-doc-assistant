# 0001. Every Azure service sits behind an interface with a free local implementation

Date: 2026-10-17. Status: accepted.

## Context

The target role (Publix's Generative AI Engineer posting) asks for Azure OpenAI, Document Intelligence,
Functions, App Service, Static Web Apps and Semantic Kernel. A portfolio project that only runs after an
Azure subscription is provisioned cannot be tested in CI, cannot be evaluated without spending credit, and
cannot be demonstrated offline.

## Decision

`DocAssistant.Core` depends on five ports (`IDocumentAnalyzer`, `IEmbedder`, `IDocumentIndex`,
`IDocumentStore`, `IIngestionQueue`) plus repositories and a cost ledger. `DocAssistant.Infrastructure`
ships two implementations of each Azure-specific one:

| Port | Azure | Local |
|---|---|---|
| analyzer | Document Intelligence layout + prebuilt-invoice | PdfPig (docstrum blocks in reading order) |
| chat and embeddings | Azure OpenAI through Semantic Kernel | Ollama through Semantic Kernel's OpenAI connector (same wire protocol), `FakeChatCompletionService` + `HashEmbeddingGenerator` in tests |
| hybrid index | Azure AI Search | Postgres + pgvector + `tsvector`, reciprocal rank fusion in SQL |
| files, triggers | Blob Storage + blob-triggered Function | Azurite with the same SDK + a worker polling a storage queue |
| secrets, identity, telemetry | Key Vault, Entra ID, Application Insights | environment variables, a dev token issuer, OTel to Tempo/Prometheus |

The ingestion pipeline is one class hosted twice (Function and worker). Configuration selects the
implementation (`Ai:Provider`, `Analyzer:Provider`, `Auth:Mode`); the code paths above the ports are
identical.

## Consequences

- The entire system, its integration tests (Testcontainers) and its evaluation run on a laptop with no
  account. CI exercises upload, ingestion, retrieval, answering, extraction and isolation with the fake
  model and PdfPig.
- The Azure implementations (`DocumentIntelligenceAnalyzer`, the Azure OpenAI kernel registration, the
  Bicep) are exercised only when he deploys; this is stated in the README rather than hidden.
- Azure AI Search is not implemented in this iteration. pgvector gives the same hybrid behaviour and runs
  in Azure Database for PostgreSQL (the Bicep enables the `vector` extension), which keeps the first
  deployment on one database. An `AzureSearchDocumentIndex` would be a third implementation of the same
  port, not a redesign.
- The fake model is deliberately simple (it quotes the first source and cites it), so any number produced
  with it measures plumbing, not quality, and is labelled that way everywhere.
