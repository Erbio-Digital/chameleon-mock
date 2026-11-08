# Getting started

Chameleon is a simulator for development and testing. Do not use it in production.

## Run with docker

```bash
docker run -p 5000:8080 -e CHAMELEON_CONNECTORS=pix ghcr.io/erbio-digital/chameleon-mock:v0.1.0
```

- Panel: <http://localhost:5000/ui>. Health: `/_chameleon/health`.
- API docs: `/scalar` (OpenAPI JSON at `/swagger/v1/swagger.json`).
- The container listens on 8080 (HTTP) and 8443 (mTLS, only when `CHAMELEON_MTLS_PORT=8443`).
- State is in memory by default and is lost when the container stops.
- Without `CHAMELEON_SECRET_KEY`, the image generates a development key and CA in `/data/dev-secrets.json`. Mount a volume on `/data` (`-v chameleon-data:/data`) to keep the same key and CA across containers (issued credentials and client certificates stay valid with a persistent database), or set `CHAMELEON_SECRET_KEY` (and the CA variables in [authentication](authentication.md)) yourself; an explicit variable always wins over the file.
- `CHAMELEON_CONNECTORS` is a comma-separated list of catalog names (`pix`, `bradesco,inter`). Packages are downloaded from NuGet at startup into `CHAMELEON_PLUGINS_DIR` (`/plugins` in the image). Mount a volume there to keep them between runs, or set `CHAMELEON_OFFLINE=true` to use only what is already downloaded.
- Without `CHAMELEON_AUTH` there is no login (see [Authentication](authentication.md)).
- `CHAMELEON_CATALOG_URL` sets where the connector catalog is read at startup (default: `catalog/connectors.json` on the `main` branch of this repository on GitHub). It accepts `https://`, `http://` only for a loopback address, and `file://` (read even with `CHAMELEON_OFFLINE=true`). If the catalog cannot be read within 5 seconds, is empty, or the instance is offline with a remote URL, Chameleon uses the catalog embedded in the image. Use it to point at a mirror or a local copy on a machine without internet access.

Install, list and search connectors without starting the server:

```bash
docker run --rm ghcr.io/erbio-digital/chameleon-mock:v0.1.0 list
docker run --rm ghcr.io/erbio-digital/chameleon-mock:v0.1.0 search zendesk
```

## Compose with Postgres

`CHAMELEON_STORE=postgres` keeps every store (state, journal, sandboxes, tokens) in Postgres, so it survives restarts.

```yaml
services:
  chameleon:
    image: ghcr.io/erbio-digital/chameleon-mock:v0.1.0
    ports:
      - "127.0.0.1:5000:8080"
    environment:
      CHAMELEON_CONNECTORS: bradesco,inter,nfse-sp,pix,zendesk
      CHAMELEON_STORE: postgres
      CHAMELEON_POSTGRES_CONNECTION: Host=postgres;Database=chameleon;Username=chameleon;Password=chameleon-local
      CHAMELEON_MIGRATE_ON_START: "true"
    volumes:
      - chameleon-data:/data
      - chameleon-plugins:/plugins
    depends_on:
      postgres:
        condition: service_healthy
  postgres:
    image: postgres:16
    environment:
      POSTGRES_DB: chameleon
      POSTGRES_USER: chameleon
      POSTGRES_PASSWORD: chameleon-local
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U chameleon -d chameleon"]
      interval: 5s
      retries: 10
volumes:
  chameleon-data:
  chameleon-plugins:
```

- `CHAMELEON_MIGRATE_ON_START=true` applies the schema migrations on start.
- Without `oidc`, the secret key and the mTLS certificate authority are generated into `/data/dev-secrets.json` on first start (`CHAMELEON_DEV_SECRETS_FILE`). Keep the `/data` volume: deleting it invalidates issued credentials and certificates, so reset the sandboxes too.
- `CHAMELEON_RETENTION_DAYS` (default 7) removes old journal entries, webhook deliveries and simulated tokens.
- The `docker-compose.yml` in the repository is the development version of this file (see Local development).

## Sandboxes

A sandbox is an isolated copy of the world: its own entities, journal, faults, webhook policy and clock. The `default` sandbox always exists. Your application reaches a sandbox by path prefix:

```
http://localhost:5000/pix/v2/cob/...                    # the default sandbox
http://localhost:5000/s/checkout-test/pix/v2/cob/...    # sandbox "checkout-test"
```

```bash
curl -X POST localhost:5000/_chameleon/sandboxes -H "Content-Type: application/json" -d '{"id":"checkout-test","ttlSeconds":3600}'
curl -X POST localhost:5000/_chameleon/sandboxes/checkout-test/reset
curl -X DELETE localhost:5000/_chameleon/sandboxes/checkout-test
```

- Ids use `[a-z0-9-]`, up to 40 characters. A sandbox expires after `ttlSeconds` (default 24 hours); `"permanent": true` disables expiry and asks for `?confirm=<id>` on delete.
- `"from": "<id>"` copies entities, faults, webhook policy and clock from another sandbox (not the journal).
- Control routes inside a sandbox use the same prefix: `/s/checkout-test/_chameleon/faults`.
- Outside `default`, a connector answers only after the sandbox subscribes to it: `POST /s/<id>/_chameleon/connectors/<name>/subscription`, then `GET` the same path for the issued `CLIENT_ID`, `CLIENT_SECRET` and base URL. The panel does this for you.
- Connectors that identify the client by certificate (`clientCertificate: true` in `GET /_chameleon/connectors`: `bradesco`, `inter`, `pix`) drop a non-default sandbox call that does not present the sandbox's certificate. Enable the mTLS port with `CHAMELEON_MTLS_PORT=8443`, `CHAMELEON_MTLS_HOSTNAMES` (names in the server certificate) and `CHAMELEON_MTLS_URL` (what the subscription shows), publish 8443, and fetch the CA at `/_chameleon/ca.crt`. The repository `docker-compose.yml` has a working setup.

## Clock

Each sandbox has its own clock, either `Frozen` (the default: time moves only when you advance it) or `Realtime`.

```bash
curl -X POST localhost:5000/_chameleon/clock/advance -H "Content-Type: application/json" -d '{"seconds":3600}'
curl -X POST localhost:5000/_chameleon/sandboxes/default/clock -H "Content-Type: application/json" -d '{"mode":"Realtime"}'
curl -X POST localhost:5000/_chameleon/sandboxes/default/clock/now
```

Advancing applies what came due: a Pix charge expires, an NFS-e is processed. Going back in time undoes nothing. The last call returns a sandbox to real time.

## Faults

Arm a failure on one route of a connector (or `*` for all of them). Route names are `METHOD pattern`, as listed under `routes` in `GET /_chameleon/connectors`.

```bash
curl -X POST localhost:5000/_chameleon/faults -H "Content-Type: application/json" \
  -d '{"connector":"pix","route":"PUT /pix/v2/cob/{txid}","kind":"Error","status":503,"times":1}'
curl localhost:5000/_chameleon/faults
curl -X DELETE "localhost:5000/_chameleon/faults?connector=pix&route=*"
```

- Kinds: `Timeout`, `Error` (`status` 400 to 599), `Latency` (`delayMs` up to 30000), `Malformed`, and for the `webhook` route `Duplicate`, `Reorder` and `Hold`. Connectors with asynchronous settlement also accept `Hold` on the `settlement` route.
- A fault lasts `times` uses (1 to 1000) or `durationSeconds` on the sandbox clock, not both.
- Every request lands in the journal: `GET /_chameleon/interactions`. `POST /_chameleon/verify` counts matching calls.

## Webhooks

Register the receiver the way the real provider does (for `pix`, `PUT /pix/v2/webhook/{chave}` with `{"webhookUrl": "..."}`), then drive the entity and Chameleon delivers the event with retries (default 5 attempts, doubling from 2 s).

- Run an entity action through the generic route: `POST /_chameleon/connectors/pix/entities/cob/<txid>/actions/pay`.
- Inspect and resend deliveries: `GET /_chameleon/webhooks`, `POST /_chameleon/webhooks/{id}/resend`. Tune retries per sandbox with `PUT /_chameleon/sandboxes/{id}/webhook-policy`.
- Chameleon blocks webhook destinations on private addresses (SSRF protection). Allow your receiver with `CHAMELEON_WEBHOOK_ALLOWED_HOSTS` (comma-separated host names; a leading dot allows a domain suffix), for example `host.docker.internal` to reach the host machine. When the variable is set, hosts outside the list are refused.
- Plain http receivers are accepted on the `pix` connector. A real PSP requires https with mTLS.

## MCP

Chameleon serves a Model Context Protocol endpoint (streamable HTTP, stateless) at `/_chameleon/mcp`, and inside a sandbox at `/s/<id>/_chameleon/mcp`. Point an MCP client at `http://localhost:5000/_chameleon/mcp`. Its tools (`chameleon_create_sandbox`, `chameleon_advance_clock`, `chameleon_arm_fault`, `chameleon_run_action`, `chameleon_search_interactions` and others) wrap the control API, so an agent can set up a scenario, drive it and check the result. With `oidc`, the endpoint needs a personal token or OAuth, see [Authentication](authentication.md).

## Local development

The repository `docker-compose.yml` builds the image from the checkout and points the host at a local feed and catalog, so the packages must exist first:

```bash
dotnet pack -c Release -o artifacts/packages
dotnet run --project tools/catalog -- artifacts/packages artifacts/packages/catalog.json --providers catalog/providers
docker compose up -d --build
```

- The panel is at <http://localhost:5000/ui> and the mTLS port at `https://localhost:5443`.
- `docker compose down -v` also drops the generated secrets and the Postgres data.
- Run the tests with `dotnet test Chameleon.sln`; the end-to-end smoke is `python tests/smoke/smoke_test.py --skip-dotnet --skip-build`. See [CONTRIBUTING](../CONTRIBUTING.md).
- To work on a connector, see [Creating a connector](creating-a-connector.md).
