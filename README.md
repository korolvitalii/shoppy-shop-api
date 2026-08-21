# ShoppyShop API

Production-oriented REST API for the [ShoppyShop Angular storefront](https://github.com/korolvitalii/shoppy-shop).

The API provides product catalogue, authentication, favourites, checkout, order history, and catalogue administration functionality.

## Technology stack

* ASP.NET Core 10
* Entity Framework Core
* Npgsql
* PostgreSQL 17
* JWT authentication
* Docker
* Railway
* Neon PostgreSQL
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
* Demo payment token identifier

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
* Node.js 22 or newer
* Docker Desktop

Restore the local .NET tools:

```powershell
dotnet tool restore
```

Install the AWS CDK dependencies:

```powershell
npm ci
```

Start PostgreSQL and the required local services:

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

### Local bootstrap administrator

An optional administrator can be created during local development using .NET user secrets:

```powershell
dotnet user-secrets init --project src/ShoppyShop.Api

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

`ShoppyShop.UnitTests` verifies isolated domain and application behaviour without external infrastructure.

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
6. Legacy AWS CDK synthesis
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

## Cost controls

The portfolio deployment uses Railway and Neon's usage-based/free allowances instead of dedicated AWS App Runner and RDS resources. Railway usage limits and Neon project quotas should still be monitored because free allowances and provider pricing can change.

The API and database remain single-region services without multi-region failover. These are deliberate cost and complexity trade-offs rather than production-scale availability choices.

## Related project

* [ShoppyShop Angular frontend](https://github.com/korolvitalii/shoppy-shop)
