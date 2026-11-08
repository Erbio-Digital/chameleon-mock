# Pix

Simulates the Pix API as the Central Bank publishes it, with no bank-specific extension, for local tests. Not affiliated with the Central Bank or any payment service provider.

## What the connector covers

- Pix charges: immediate (cob) and due-date (cobv), create, read, pay and remove.
- Pix lookup by end-to-end id and refunds (devolucao) with their lifecycle.
- Webhook per Pix key.
- Routes: the routes live under `/pix/v2`, with the cobv scopes `cobv.read` and `cobv.write` and no bank extension.
- Webhooks: webhook URLs may be plain http, so a local receiver works. A real PSP requires https with mTLS.
- OAuth: the token is at `POST /pix/oauth/token` with `grant_type=client_credentials`; the client goes in HTTP Basic or in the form. It grants the requested known scopes, or all of them when no scope is sent. The Central Bank leaves OAuth to each PSP.

## Sources

- https://bacen.github.io/pix-api/
- https://www.bcb.gov.br/estabilidadefinanceira/pix
