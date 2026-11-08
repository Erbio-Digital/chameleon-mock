# Chameleon

Chameleon is an open-source, stateful simulator of external HTTP providers (payments, invoicing, support desks and similar APIs), for development and testing.

Repository: https://github.com/Erbio-Digital/chameleon-mock

## About this package

- Connector packages are loaded by the Chameleon Host through the `CHAMELEON_CONNECTORS` setting. Do not reference them directly from your application.
- `Chameleon.Mock.Engine` and `Chameleon.Mock.Sdk` are meant for connector authors only.

See the repository for documentation, the connector catalog and the license (Apache-2.0).
