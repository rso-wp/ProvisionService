# ProvisioningService

The **coordinator of the WordPress sites** of RSO (a WordPress-as-a-service platform). It records every site a user has, makes the
user pay first, orders ProvisionFunction to build or remove the site, and records how that went. It creates no containers or
databases itself.

```
web app --REST + bearer token--> ProvisioningService --gRPC--> PaymentService   (pay first)
                                       |--queue provision-site / deprovision-site--> ProvisionFunction
                                       |<--queue provisioning-events-----------------+
                                       |<--SubscriptionEnded, TenantAssigned (NATS)
                                       v
                              PostgreSQL (ProvisioningServiceDb): ProvisionedServices, UserTenants
```

How it fits with the other services, and how to run all of it on your machine or on Azure: the **Deployment** repository.

## What it does

All endpoints need a Keycloak bearer token.

- **`POST /provisions`** `{ "name": "blog", "paymentMethodId": null }`: reserves the name (`blog.<tenant>.<domain>`), asks
  PaymentService to start the subscription, and on success queues the site (`202`). A declined card gives `402` and releases
  the name; an unknown payment outcome gives `502`, and the site stays `PendingPayment` until the reconciler finds out.
- **`GET /provisions`**: the caller's sites with their status and URL.
- **`DELETE /provisions/{name}`**: stops the subscription first, then marks the site `Deleting` and orders its removal.
- **Status of a site:** `PendingPayment`, `Queued`, `Provisioned`, `Failed`, `Deleting`.
- **Background work:**
  - reads the results ProvisionFunction sends (`provisioned`, `provisioning-failed`, `destroyed`) and updates the sites;
  - shuts a site down when PaymentService reports its subscription ended (NATS `SubscriptionEnded`);
  - keeps a local copy of each user's tenant slug (NATS `TenantAssigned`, or asks UserService when it is missing);
  - every minute: asks PaymentService about payments that are stuck, and sends a command again that made no progress for an hour.
- **Health and metrics:** `/health/live`, `/health/ready`, `/metrics`, `/swagger`.

A site's address is `<name>.<tenant>.<Provisioning:BaseDomain>`; the name is a DNS label of at most 30 characters, unique per
tenant.

## Run it locally

You need the .NET 10 SDK and Docker. The infrastructure (PostgreSQL on port 5433, NATS, Azurite for the queues, Keycloak) comes
from the compose file in the **Deployment** repository, which must sit next to this one:

```powershell
docker compose -f ..\Deployment\local\docker-compose.yml up -d
dotnet run --project Api                       # http://localhost:5151
```

To get a site created you also need PaymentService (gRPC :5263), UserService (:5076) and ProvisionFunction running; see the
**Deployment** repository (`local\run-local.ps1` starts them all).

## Configuration

Settings come from `appsettings*.json` and environment variables (`__` for nesting).

| Setting | Meaning |
|---|---|
| `ConnectionStrings__DefaultConnection` | PostgreSQL, database `ProvisioningServiceDb` |
| `Auth__Issuer`, `Auth__Audience`, `Auth__MetadataAddress` | Keycloak: the token's `iss`, `rso-api`, and the discovery document the signing keys come from |
| `Provisioning__StripePriceId` | the Stripe price of a site (`price_...`) |
| `Provisioning__BaseDomain` | sites are served at `<name>.<tenant>.<BaseDomain>` |
| `PaymentService__GrpcUrl` | PaymentService's gRPC address, e.g. `http://localhost:5263` |
| `UserService__Url` | UserService's address, used when a tenant slug has not arrived by NATS |
| `Nats__Url` | NATS server |
| `ProvisioningQueues` | the storage account of the queues (connection string; Azurite locally), or `ProvisioningQueues__queueServiceUri` for a managed identity |
| `Database__MigrateOnStartup` | `true` to run migrations on start (always on in Development) |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | optional: also send the logs to Application Insights |

## Tests

```powershell
dotnet test Tests/Tests.csproj
```

They run the real endpoints in-process with an in-memory database and mocked PaymentService, so no Docker is needed. They cover
creating and deleting sites (payment succeeds, is declined, is unreachable), the listing, the payment result handling and the
command handlers. Not covered: races on the same name (the in-memory database has no unique indexes) and the background
consumers and reconciler.

## Container image and release

`Api/Dockerfile` builds the image (build from this folder: `docker build -f Api/Dockerfile .`; port 8080). **Push a git tag like
`v1.2.3`** and the *Publish image* workflow pushes `armodilo/provisioningservice:v1.2.3` to Docker Hub (it needs the repository
secrets `DOCKERHUB_USERNAME` and `DOCKERHUB_TOKEN`); Argo CD Image Updater then deploys it. Pushes and pull requests run the
tests (`.github/workflows/ci.yml`).
