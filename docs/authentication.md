# Authentication

Chameleon has two modes, set by `CHAMELEON_AUTH`.

| Mode | Use it for | What it does |
|---|---|---|
| `none` (default) | A local machine | No login. Anyone who reaches the instance is an `OPERATOR`. The host logs a warning at start. |
| `oidc` | Anything shared | Login with any OpenID Connect provider that publishes discovery. Only allowed, verified e-mails get a role. |

Authentication protects the control surface under `/_chameleon` (panel API, sandboxes, faults, MCP). The simulated provider routes (`/pix/...`, `/s/<id>/pix/...`) keep their own simulated OAuth and are not behind the login. Chameleon is a test tool: even with `oidc`, do not expose it to the internet as if it were a product.

## Variables for `oidc`

The instance refuses to start half configured. Required:

| Variable | Meaning |
|---|---|
| `CHAMELEON_AUTH=oidc` | Turns login on. |
| `CHAMELEON_OIDC_ISSUER` | Issuer URL. Must be https; http is accepted only for a local issuer (loopback or `host.docker.internal`). Discovery is read from `{issuer}/.well-known/openid-configuration`. |
| `CHAMELEON_OIDC_CLIENT_ID` | Client id of the panel (a public client). |
| `CHAMELEON_ALLOWED_EMAIL_DOMAINS` and/or `CHAMELEON_ALLOWED_EMAILS` | Comma-separated allowlist: `example.com` allows `user@example.com` (no subdomains); `ana@example.com` allows one address. At least one is required. |
| `CHAMELEON_WEBHOOK_ALLOWED_HOSTS` | Hosts that webhooks may reach (see [Getting started](getting-started.md)). |
| `CHAMELEON_SECRET_KEY` | Base64 of 32 random bytes: `openssl rand -base64 32`. Seals issued credentials. |
| `CHAMELEON_CA_CERT`, `CHAMELEON_CA_KEY` | PEM of the Chameleon certificate authority (see below). The dev secrets file is ignored with `oidc`. |

Optional:

| Variable | Meaning |
|---|---|
| `CHAMELEON_OIDC_AUDIENCE` | Set it when the provider issues JWT access tokens for this API. The panel then sends the access token and Chameleon requires this audience. Unset, the panel sends the id token and the audience is the client id. |
| `CHAMELEON_CI_KEY` | A shared key for automation. Must start with `chm_ci_`. |
| `CHAMELEON_PUBLIC_URL` | The address clients use, when Chameleon is behind a proxy. Used in the MCP discovery metadata. |

Create a CA once and keep it with your other secrets:

```bash
openssl ecparam -name prime256v1 -genkey -noout | openssl pkcs8 -topk8 -nocrypt -out ca.key
openssl req -x509 -new -key ca.key -subj "/CN=Chameleon CA" -days 3650 \
  -addext "basicConstraints=critical,CA:TRUE" -addext "keyUsage=critical,keyCertSign,cRLSign" -out ca.crt
```

The panel's redirect URI is `{origin}/ui/callback` and it asks for the scopes `openid email profile`.

## Who gets in

A request needs `Authorization: Bearer <credential>` with one of these credentials:

| Credential | Role | Notes |
|---|---|---|
| A JWT from the configured provider | `OPERATOR` | Issued to the panel client. The `email` claim must be present, `email_verified` must be `true` and the address must be in the allowlist. |
| The same kind of JWT issued to another client of the provider | `AGENT` | The `azp` or `client_id` claim differs from `CHAMELEON_OIDC_CLIENT_ID`: a tool acting for the operator. |
| `chm_pat_...` personal token | `AGENT` | Created by an operator. Shown once, stored only as a hash, revocable. |
| `CHAMELEON_CI_KEY` (`chm_ci_...`) | `CI` | For pipelines. Compared in constant time. |

A valid JWT whose e-mail is not allowed or not verified gets `403`; no credential gets `401`.

| Capability | OPERATOR | AGENT | CI |
|---|---|---|---|
| Sandboxes, clock, faults, entities, actions, webhooks, journal | yes | yes | yes |
| Manage personal tokens (`/_chameleon/tokens`) | yes | no | no |
| Reset a permanent sandbox | yes | no | no |
| Download the client certificate as `.pfx` and set its password | yes | no | yes |
| Read the certificate private key and password in a subscription | yes | no | yes |

Public without login: `/_chameleon/config` and `/_chameleon/ca.crt` (the panel boots from them), connector logos, `/swagger`, `/scalar` and `/_chameleon/health`. See [limitations](limitations.md).

## Personal tokens and the CI key

```bash
# An operator creates a token (in the panel, or with an operator credential):
curl -X POST https://chameleon.example.com/_chameleon/tokens \
  -H "Authorization: Bearer $OPERATOR_JWT" -H "Content-Type: application/json" -d '{"name":"my-agent"}'
# -> {"id":"...","name":"my-agent","token":"chm_pat_..."}   keep the value, it is not shown again

curl https://chameleon.example.com/_chameleon/me -H "Authorization: Bearer $AGENT_TOKEN"
curl -X DELETE https://chameleon.example.com/_chameleon/tokens/<id> -H "Authorization: Bearer $OPERATOR_JWT"
```

Use `chm_pat_` for MCP clients and scripts that act for a person, and `chm_ci_` for pipelines. MCP clients that speak OAuth can also discover the provider at `/.well-known/oauth-protected-resource/_chameleon/mcp` (RFC 9728); dynamic client registration depends on the provider.

## Provider examples

Create the application as a public client (authorization code with PKCE, no secret) with redirect URI `https://chameleon.example.com/ui/callback` (for a local instance, `http://localhost:5000/ui/callback`).

### Keycloak

This is the provider exercised by the repository smoke test (`docker-compose.oidc.yml`).

```
CHAMELEON_AUTH=oidc
CHAMELEON_OIDC_ISSUER=https://keycloak.example.com/realms/main
CHAMELEON_OIDC_CLIENT_ID=chameleon-panel
CHAMELEON_ALLOWED_EMAIL_DOMAINS=example.com
```

Create a public client `chameleon-panel` and mark users' e-mails as verified.

### Entra ID

```
CHAMELEON_OIDC_ISSUER=https://login.microsoftonline.com/<tenant-id>/v2.0
CHAMELEON_OIDC_CLIENT_ID=<application (client) id>
CHAMELEON_ALLOWED_EMAILS=ana@example.com,bia@example.com
```

Register the app with the "Single-page application" platform. Entra does not send `email_verified`, and Chameleon refuses tokens without it, so a plain Entra setup will answer `403`. Add a custom claim named `email_verified` with the value `true` to the tokens of this app, and keep the e-mail allowlist as the real gate. Make sure the `email` claim is also present (optional claim `email`).

### Auth0

```
CHAMELEON_OIDC_ISSUER=https://<tenant>.auth0.com
CHAMELEON_OIDC_CLIENT_ID=<single page application client id>
CHAMELEON_ALLOWED_EMAIL_DOMAINS=example.com
```

Leave `CHAMELEON_OIDC_AUDIENCE` unset. With an audience, Auth0 issues an access token without the `email` claim, which Chameleon cannot map to an allowed user; without it the panel sends the id token, which carries the e-mail.

The Entra ID and Auth0 examples follow each provider's documented claims and were not exercised against a live tenant. If one does not work for you, open an issue with the claims in your token (without the signature).
