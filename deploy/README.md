# Deploying Wings Notification Hub

There are two processes, configured entirely through `appsettings.json` and environment variables. A `__` in an
environment variable name stands for `:`.

| Process | Project | Role |
|---|---|---|
| **API** | `src/Wings.NotificationHub.Api` | REST API, OAuth token endpoint, SignalR hubs (`/hubs/inapp`, `/hubs/admin`), provider callbacks, admin UI (`wwwroot`) |
| **Worker** | `src/Wings.NotificationHub.Worker` | Outbox relay, queue consumers and dispatch, scheduler, reconciliation, provider health, retention. Instances coordinate through leases, so you can run as many as you need. |

By default the API runs the workers in-process (`Workers__RunInApi=true`). When you deploy the worker separately,
set `Workers__RunInApi=false` on the API.

| Target | Files |
|---|---|
| Docker / Compose (SQL Server, RabbitMQ, Redis, API, worker) | `docker/Dockerfile.api`, `docker/Dockerfile.worker`, `docker/docker-compose.yml`, `docker/.env.example` |
| Windows: IIS (API) | `iis/web.config`, `iis/README.md` |
| Windows: service (worker) | `windows-service/install-worker.ps1` |
| Linux: systemd (worker) | `systemd/wings-notification-hub-worker.service`, `systemd/worker.env.example` |

## Required settings outside Development

| Setting | Notes |
|---|---|
| `Database__Provider` | `sqlserver` (production) or `sqlite` |
| `ConnectionStrings__NotificationHub` | |
| `Security__EncryptionKeys__k1` | 256-bit AES-GCM key, base64 (`openssl rand -base64 32`). It must be the **same on the API and every worker**. Losing it makes stored provider/webhook secrets and message bodies unreadable. To rotate, add `k2` and set `Security__ActiveKeyId=k2`. |
| `Auth__SigningKey` | API only. 256-bit HMAC key, base64 (`openssl rand -base64 32`). It must be identical on every API instance. |
| `Hub__PublicBaseUrl` | The public https URL, used in unsubscribe links, provider callbacks and tracking URLs |
| `Hub__MessageTransport` | `database` (default, no extra infrastructure) or `rabbitmq` together with `Messaging__RabbitMq__Uri` |
| `Redis__Connection` | Optional with one API instance. **Required for SignalR across several API instances** (it is the backplane). It also shares provider rate limits. |
| `Workers__RunInApi` | `false` when the worker runs separately |
| `Cors__Origins__0…n` | Origins of web apps that embed the in-app inbox (REST + SignalR) |
| `ReverseProxy__KnownProxies__0…n` | Proxy addresses whose `X-Forwarded-*` headers are trusted |
| `Bootstrap__AdminEmail` / `Bootstrap__AdminPassword` | First administrator, created on first start. Without a password, a generated one is written to `<Hub__DataDirectory>/dev-credentials.env`. |

`Hub__DataDirectory` holds uploaded attachments and generated files. **The API and the workers must share it**
(the Compose file uses a shared volume; on several hosts use a network share).

## Docker quick start

```bash
cd deploy/docker
cp .env.example .env            # generate every <generate> value as described in the file
docker compose up -d --build    # API on http://localhost:8080, RabbitMQ management on http://127.0.0.1:15672
docker compose logs -f api worker
docker compose up -d --scale worker=3
```

Details:

- Both images are multi-stage builds (SDK → `mcr.microsoft.com/dotnet/aspnet:10.0`) and run as the non-root `app` user
  (`$APP_UID`). The build context is the repository root. BuildKit picks up `Dockerfile.*.dockerignore`, which
  excludes `bin/`, `obj/`, `node_modules/`, `App_Data/` and any local `wwwroot` build.
- The API image builds the Angular admin UI from `web/` when `web/package.json` exists. It handles an `outputPath`
  of `../src/Wings.NotificationHub.Api/wwwroot` or `dist/…`. Without `web/package.json` it logs that it skipped the
  UI and builds an API-only image. The UI build is cached on the contents of `web/` alone.
- The worker image also uses the **aspnet** base image. `Wings.NotificationHub.Infrastructure` references the
  `Microsoft.AspNetCore.App` shared framework, so the plain `runtime` image cannot start the worker.
- Startup order is SQL Server, RabbitMQ and Redis (all healthy), then the API, which creates the schema
  (`Database__AutoCreate=true`) and must report `/health/ready`, then the worker (`Database__AutoCreate=false`).
- SQL Server images are linux/amd64 only. On Apple silicon they run under emulation; in production use a managed
  SQL Server or a dedicated server.
- Put a TLS-terminating reverse proxy in front of `api:8080` with WebSockets enabled for `/hubs/*`. The containers
  set `Https__Redirect=false` because TLS ends at the proxy.

## What was verified when these files were written

These files were written on macOS **without Docker, IIS or Windows**:

- `docker-compose.yml` parses as YAML. Every `${VAR}` without a default is defined in `.env.example`, and the merged
  API/worker environments contain all required settings. `docker compose config` was **not** run.
- The images were **not** built, and the RabbitMQ/Redis/SQL Server combination was **not** run.
- The *environment-variable configuration* was exercised with the published binaries: the API in Production with
  `Workers__RunInApi=false`, plus a separate worker process configured only through `DOTNET_ENVIRONMENT=Production`,
  `Security__EncryptionKeys__k1`, `Hub__DataDirectory` and `Database__AutoCreate=false`, both on SQLite with the
  database transport. A notification stayed `Queued` until the worker started, then completed.
- `iis/web.config` is well-formed XML using the ANCM v2 schema. It was not deployed to IIS.
- `windows-service/install-worker.ps1` and `samples/wings-code/register-manifest.ps1` parse without errors with the
  PowerShell 7.6 parser (`Microsoft.PowerShell.SDK`). They were not executed.
- The systemd unit was not checked with `systemd-analyze verify`, because no Linux host was available.
