# Security policy

## Not for production

Chameleon is a simulator for development and testing. It is not meant to hold real data, real credentials or to sit on the public internet. With the default `CHAMELEON_AUTH=none`, anyone who reaches an instance can control it. Read [Authentication](docs/authentication.md) and [Known limitations](docs/limitations.md) before sharing an instance.

## Reporting a vulnerability

Report privately through GitHub Security Advisories: open <https://github.com/Erbio-Digital/chameleon-mock/security/advisories/new>. Please do not open a public issue or pull request for a vulnerability.

Include the version (image tag or package version), what you did, what you expected and what happened. We aim to acknowledge a report within a few days and to coordinate a fix and a disclosure date with you.

## Scope

In scope:

- The host, the engine and the SDK in this repository, the official image and the official connectors.
- Authentication and authorization with `CHAMELEON_AUTH=oidc` (roles, personal tokens, the CI key), the webhook destination policy (SSRF), the plugin loader and the catalog verification.

Out of scope:

- Anything that needs `CHAMELEON_AUTH=none` on a shared network: that mode has no access control by design.
- Third-party connectors that are not in the catalog.
- Findings already listed in [Known limitations](docs/limitations.md).
- Missing features of a simulated provider: report those as normal issues.

## Supported versions

Only the latest release receives fixes.
