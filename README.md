# Chameleon

[Português (Brasil)](README.pt-BR.md)

Chameleon is an open-source, stateful simulator of external HTTP providers (payments, invoicing, support desks and similar APIs), for integration testing. Your application calls Chameleon instead of the real provider, and Chameleon keeps state: a charge is created, paid, expired or refunded, and webhooks are delivered when that happens.

> **Not for production.** Chameleon is a simulator. Never point real customers, real credentials or real money at it. With the default `CHAMELEON_AUTH=none` anyone who reaches the instance is an operator: run it on a local machine or a private network.

What you get:

- A .NET 8 host with a web panel, a control API and an MCP endpoint.
- Isolated sandboxes, each with its own state, journal, faults and clock.
- Fault injection (timeouts, errors, latency, malformed bodies, webhook duplication, reordering and holds).
- Connectors are NuGet plugins. Verified ones are listed in a catalog; anyone can publish their own.

## Quick start

```bash
docker run -p 5000:8080 -e CHAMELEON_CONNECTORS=pix ghcr.io/erbio-digital/chameleon-mock:v0.1.0
```

The panel is at <http://localhost:5000/ui>. Get a token and create a charge on the `pix` connector (in the `default` sandbox any client id is accepted):

```bash
CLIENT_ID=my-client CLIENT_SECRET=my-secret   # any values work in the default sandbox
TOKEN=$(curl -s -X POST http://localhost:5000/pix/oauth/token \
  -u "$CLIENT_ID:$CLIENT_SECRET" -d grant_type=client_credentials | sed -E 's/.*"access_token":"([^"]+)".*/\1/')

curl -s -X PUT http://localhost:5000/pix/v2/cob/chameleonexample0000000000001 \
  -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{"calendario":{"expiracao":3600},"valor":{"original":"10.00"},"chave":"11111111-1111-1111-1111-111111111111"}'
```

Connectors are downloaded from NuGet at startup, so the container needs internet access the first time. Next steps are in [Getting started](docs/getting-started.md).

## Connectors

| Name | Simulates | Sheet |
|---|---|---|
| `bradesco` | Bradesco Pix and account API | [catalog/providers/bradesco.md](catalog/providers/bradesco.md) |
| `inter` | Banco Inter Pix, billing and banking API | [catalog/providers/inter.md](catalog/providers/inter.md) |
| `nfse-sp` | São Paulo city service invoice (NFS-e) web service | [catalog/providers/nfse-sp.md](catalog/providers/nfse-sp.md) |
| `pix` | Central Bank Pix API (cob, cobv, lookup, refund, webhook per key) | [catalog/providers/pix.md](catalog/providers/pix.md) |
| `zendesk` | Zendesk Support API | [catalog/providers/zendesk.md](catalog/providers/zendesk.md) |

Load several with `CHAMELEON_CONNECTORS=bradesco,inter,nfse-sp,pix,zendesk`.

## Documentation

- [Getting started](docs/getting-started.md): docker, compose with Postgres, sandboxes, clock, faults, webhooks, MCP, local development.
- [Authentication](docs/authentication.md): `none` versus `oidc`, roles, tokens, provider examples.
- [Creating a connector](docs/creating-a-connector.md)
- [Verified badge](docs/verified-badge.md)
- [Known limitations](docs/limitations.md)
- [Releasing](docs/releasing.md) (maintainers)
- [Contributing](CONTRIBUTING.md), [Security](SECURITY.md), [Code of conduct](CODE_OF_CONDUCT.md)

Guides in Portuguese live in [docs/pt-BR](docs/pt-BR/getting-started.md).

## License

Apache-2.0, see [LICENSE](LICENSE) and [NOTICE](NOTICE). Provider names are trademarks of their owners; Chameleon is not affiliated with any of them.
