# expertise-api

Self-hosted .NET 10 REST API for storing and serving expertise entries consumed by AI agents (GitHub Copilot, Claude). Entries are a running log of issues/fixes, workarounds, caveats, and requirements — either domain-specific or shared across agent domains.

Design document: GitHub issue #1. For architecture and implementation guidance, use the `@expertise-api-owner` agent.

## Tech Stack

- .NET 10 (LTS) with ASP.NET Core Minimal APIs
- PostgreSQL 17 with pgvector extension (vector(512)) and tsvector full-text search
- EF Core with repository pattern (`IExpertiseRepository`)
- PgBouncer 1.21+ sidecar (transaction mode)
- In-process ONNX embeddings: jina-embeddings-v2-small-en, 512-dim, 6144-token ceiling (ADR-017), via `Microsoft.SemanticKernel.Connectors.Onnx`
- OpenAPI via Scalar (`Scalar.AspNetCore`)
- Docker Compose for local dev; Helm chart for k3s deployment

## Prerequisites

```bash
# .NET 10 SDK, Docker + Docker Compose
# dotnet-ef is version-pinned in .config/dotnet-tools.json — never install globally:
dotnet tool restore
```

## Build & Run Commands

```bash
dotnet build src/ExpertiseApi/ExpertiseApi.csproj
dotnet run --project src/ExpertiseApi/ExpertiseApi.csproj
dotnet ef migrations add <MigrationName> --project src/ExpertiseApi
dotnet ef database update --project src/ExpertiseApi
docker compose -f deploy/local/docker-compose.yml up postgres pgbouncer
docker compose -f deploy/local/docker-compose.yml up
dotnet run --project src/ExpertiseApi -- reembed [--batch-size 50]
```

## Local Development Quick Start

```bash
# 1. Start database
cp deploy/local/.env.example deploy/local/.env
docker compose -f deploy/local/docker-compose.yml up -d postgres pgbouncer

# 2. Apply migrations
dotnet ef database update --project src/ExpertiseApi

# 2b. Download ONNX model files (required for embeddings and semantic search)
./scripts/download-models.sh

# 3. Run the API
dotnet run --project src/ExpertiseApi

# 4. Test
curl http://localhost:5000/health
# POSTs hard-require an Idempotency-Key header (ADR-010) — 400 without one.
curl -X POST http://localhost:5000/expertise \
  -H "Authorization: Bearer dev-api-key-change-me" \
  -H "Idempotency-Key: $(uuidgen)" \
  -H "Content-Type: application/json" \
  -d '{"domain":"shared","title":"Test","body":"Test entry","entryType":"Pattern","severity":"Info","source":"human"}'
```

All endpoints require a Bearer token in the Authorization header except the anonymous surfaces: `/health`, `/health/live`, `/health/ready`, `/metrics`, `/openapi/v1.json`, and `/query`. OpenAPI docs at `/scalar/v1` (Development only).

## Model Files

The jina-embeddings-v2-small-en ONNX model files (`model.onnx`, `vocab.txt`) are not tracked in git. Download them with:

```bash
./scripts/download-models.sh
```

This is required before `docker build` and for local semantic search. The script is idempotent and safe to re-run.

## CI/CD

- `ci.yml`: runs `dotnet build` + `dotnet test` on PRs to main and pushes to dev
- `release.yml`: runs on merge to main — downloads models (cached), builds multi-arch Docker image (amd64 + arm64), pushes to `ghcr.io/psmfd/agent-expertise-api`
- The full workflow roster (PR-title lint, install smokes, CodeQL/Trivy/Hadolint, Dependabot auto-merge, and more) is in CLAUDE.md's CI/CD table — treat that as the authoritative list.

## Architecture & Design

For data model, API surface, authentication, embedding architecture, deployment topology, and known gotchas, see `.agents/skills/expertise-api/references/DESIGN.md` (authoritative reference; the old `.claude/skills/expertise-api-design/` path is a deprecated shim). Use the `@expertise-api-owner` agent for design and implementation questions.
