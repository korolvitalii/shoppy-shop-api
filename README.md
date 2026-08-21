# ShoppyShop API

REST API for the [ShoppyShop Angular storefront](https://github.com/korolvitalii/shoppy-shop).

The API provides catalogue search, a shopping assistant, authentication, favourites, checkout, order history, and catalogue administration.

## Technology stack

* ASP.NET Core 10
* Entity Framework Core
* Npgsql
* PostgreSQL 17
* JWT authentication
* Docker
* Railway
* Neon PostgreSQL
* Anthropic API and .NET SDK
* xUnit
* Testcontainers
* OpenAPI and Scalar

## Architecture

The application is implemented as a modular monolith divided into four main layers:

```text
src/
├── ShoppyShop.Api
├── ShoppyShop.Application
├── ShoppyShop.Domain
└── ShoppyShop.Infrastructure
```

### `ShoppyShop.Api`

Responsible for:

* Endpoint registration
* Middleware
* Authentication and authorization configuration
* Dependency injection
* Application composition
* OpenAPI configuration

### `ShoppyShop.Application`

Contains:

* Application use cases
* Service contracts
* Request and response DTOs
* Business workflow orchestration

### `ShoppyShop.Domain`

Contains:

* Domain entities
* Value objects
* Domain rules
* Core business logic

### `ShoppyShop.Infrastructure`

Contains:

* Entity Framework Core persistence
* PostgreSQL integration
* Identity implementation
* External service implementations
* Database migrations

Additional projects:

```text
tests/
├── ShoppyShop.UnitTests
└── ShoppyShop.IntegrationTests

infra/
└── ShoppyShop.Cdk
```

## Features

### Authentication and authorization

* Customer registration and login
* Short-lived JWT access tokens
* Rotating refresh tokens
* Refresh tokens stored as hashes
* Refresh-token reuse detection
* Role-based endpoint authorization
* Optional bootstrap administrator

### Product catalogue

* Public product groups
* Product search
* Category filtering
* Price filtering
* Sorting
* Product details
* Soft-delete catalogue administration

### Shopping assistant

* Anthropic-backed conversational product discovery
* Catalogue-grounded product recommendations
* Product searches by category, keywords, price, and sort order
* Category listing through a dedicated model tool
* Product cards returned alongside assistant replies
* Conversation history limited to the ten most recent turns
* Messages limited to 1,000 characters
* Fixed-window rate limit of 20 requests per IP per hour

The model can use only the `search_products` and `list_categories` tools. Product cards are accepted only when their identifiers came from the current catalogue tool results.

### Customer functionality

* Persistent favourites
* Shopping basket integration
* Customer order history
* Protected customer endpoints

### Checkout and orders

* Product prices recalculated by the server
* Client-submitted prices are never trusted
* Order creation protected with an `Idempotency-Key`
* Idempotency records remain valid for 24 hours
* Mock payment metadata storage

The application stores only:

* Card brand
* Last four digits
* Mock payment token identifier

Full card numbers are never processed or stored.

### API infrastructure

* RFC-style Problem Details responses
* Rate limiting
* CORS configuration
* Liveness and readiness health checks
* OpenAPI specification
* Scalar interactive API documentation

## Running locally

### Requirements

* .NET SDK 10.0.302 or newer
* Docker Desktop

Node.js 22 or newer and npm 11 are needed only for the retained AWS CDK tooling and its CI checks.

Restore the local .NET tools:

```powershell
dotnet tool restore
```

To work with the retained AWS CDK templates, install their dependencies separately:

```powershell
npm ci
```

Start PostgreSQL:

```powershell
docker compose up -d
```

Run the API:

```powershell
dotnet run --project src/ShoppyShop.Api
```

Available local endpoints:

```text
OpenAPI specification: /openapi/v1.json
Scalar documentation:  /scalar/v1
Liveness check:        /health/live
Readiness check:       /health/ready
```

## Postman

Import the following files into Postman:

```text
postman/ShoppyShop API.postman_collection.json
postman/ShoppyShop API.postman_environment.json
```

Select the **ShoppyShop API - Local** environment.

The collection:

* Captures customer access tokens automatically
* Captures administrator access tokens automatically
* Uses Postman's cookie jar for refresh-token requests
* Includes catalogue, authentication, favourite, checkout, and administration requests

## Environment configuration

Configuration is loaded in the following order:

```text
appsettings.json
→ appsettings.Development.json
→ user secrets
→ environment variables
```

No secrets are committed to the repository.

Important settings include:

| Setting                      | Purpose                                   |
| ---------------------------- | ----------------------------------------- |
| `ConnectionStrings:Postgres` | PostgreSQL connection string              |
| `Jwt:Issuer`                 | JWT token issuer                          |
| `Jwt:Audience`               | JWT token audience                        |
| `Jwt:SigningKey`             | JWT signing key                           |
| `Database:AutoMigrate`       | Applies pending migrations during startup |
| `Cors:AllowedOrigins`        | Permitted frontend origins                |
| `Anthropic:ApiKey`           | Anthropic API credential                  |
| `Anthropic:Model`            | Anthropic model used by the assistant     |
| `Features:AssistantEnabled`  | Controls assistant visibility in the UI   |

To use the shopping assistant locally, store the Anthropic credential outside source control:

```powershell
dotnet user-secrets set `
  "Anthropic:ApiKey" `
  "<your-api-key>" `
  --project src/ShoppyShop.Api
```

The default model is configured in `src/ShoppyShop.Api/appsettings.json`. It can be overridden with `Anthropic:Model` or the `Anthropic__Model` environment variable.

### Local bootstrap administrator

An optional administrator can be created during local development using .NET user secrets. The API project already defines its user-secrets identifier:

```powershell
dotnet user-secrets set `
  "BootstrapAdmin:Email" `
  "admin@example.test" `
  --project src/ShoppyShop.Api

dotnet user-secrets set `
  "BootstrapAdmin:Password" `
  "A-Strong-Local-Password-123!" `
  --project src/ShoppyShop.Api
```

In production, the database connection string, JWT signing key, bootstrap administrator password, and external API keys are stored as encrypted Railway service variables. They are not stored in source control.

## Database migrations

Entity Framework Core migrations are stored in:

```text
src/ShoppyShop.Infrastructure/Migrations
```

By default, the API applies pending migrations during startup:

```json
{
  "Database": {
    "AutoMigrate": true
  }
}
```

Set `Database:AutoMigrate` to `false` when migrations should be managed manually.

Create a migration:

```powershell
dotnet ef migrations add <MigrationName> `
  --project src/ShoppyShop.Infrastructure `
  --startup-project src/ShoppyShop.Api
```

Apply migrations:

```powershell
dotnet ef database update `
  --project src/ShoppyShop.Infrastructure `
  --startup-project src/ShoppyShop.Api
```

The repository-local `dotnet-ef` tool is installed by:

```powershell
dotnet tool restore
```

## Testing

Run the complete test suite:

```powershell
dotnet test ShoppyShop.slnx
```

### Unit tests

`ShoppyShop.UnitTests` verifies service behaviour, assistant orchestration, persistence rules using SQLite, and infrastructure configuration without starting external services.

### Integration tests

`ShoppyShop.IntegrationTests`:

* Starts a real PostgreSQL 17 container through Testcontainers
* Applies the database configuration
* Tests the API against real persistence
* Seeds the same 54-product catalogue used by the frontend

Docker must be running before executing the integration tests.

## Continuous integration

The workflow in `.github/workflows/ci.yml` runs on:

* Every pull request
* Every push to `develop`

The pipeline performs:

1. Dependency restoration
2. Solution build
3. Unit and integration tests
4. `dotnet format` verification
5. NuGet vulnerability audit
6. Retained AWS CDK template synthesis
7. Container image build
8. Trivy container image scan

## API documentation

Available documentation resources:

```text
OpenAPI specification:
  /openapi/v1.json

Interactive Scalar documentation:
  /scalar/v1

Postman collection:
  postman/ShoppyShop API.postman_collection.json

Postman environment:
  postman/ShoppyShop API.postman_environment.json
```

## Deployment architecture

The production deployment consists of:

* Railway running the Dockerized ASP.NET Core API
* Neon PostgreSQL in the Frankfurt AWS region
* Railway encrypted service variables for production secrets
* GitHub Actions for continuous deployment

Railway checks `/health/ready` during deployment. The application reads Railway's dynamic `PORT` environment variable and applies pending Entity Framework Core migrations at startup.

The production assistant configuration is stored in Railway using:

```text
Anthropic__ApiKey
Anthropic__Model
Features__AssistantEnabled
```

## Continuous deployment

The deployment workflow is defined in:

```text
.github/workflows/deploy.yml
```

Every push to `main` builds and tests the solution, then deploys the repository to the Railway production service.

Configure the following GitHub Actions values before enabling deployment:

| Type       | Name                     | Value                                  |
| ---------- | ------------------------ | -------------------------------------- |
| Secret     | `RAILWAY_TOKEN`          | Railway project deployment token       |
| Variable   | `RAILWAY_PROJECT_ID`     | Railway project identifier             |
| Variable   | `RAILWAY_ENVIRONMENT_ID` | Railway production environment ID      |
| Variable   | `RAILWAY_SERVICE_ID`     | Railway API service identifier         |

Production application secrets remain in Railway and are not copied into GitHub Actions.

## Infrastructure notes

The running API uses Railway and Neon. The application no longer depends on AWS App Runner or Amazon RDS.

The `infra/ShoppyShop.Cdk` project is retained in the repository and synthesized in CI, but it is not used by the current Railway deployment.

Railway usage and Neon quotas should be monitored in their provider dashboards. The API and database are single-region services without multi-region failover.

## Related project

* [ShoppyShop Angular frontend](https://github.com/korolvitalii/shoppy-shop)
