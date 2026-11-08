# Known limitations

Chameleon is a test simulator. These are the rough edges known at the first release.

## Authentication

- **Entra ID has no `email_verified`.** Chameleon requires `email_verified: true`. Entra tokens do not carry it, so use an e-mail allowlist (`CHAMELEON_ALLOWED_EMAILS`) together with a custom `email_verified` claim. See [Authentication](authentication.md).
- **The Auth0 access token has no `email`.** Use the id token: leave `CHAMELEON_OIDC_AUDIENCE` unset so the panel sends the id token.
- **`/swagger` and `/scalar` are public in `oidc` mode.** They describe the API but do not execute anything without a credential. Block them at your proxy if that matters.
- **An open SignalR connection does not revalidate its token.** The live feed keeps working until the page disconnects, even after the token expired or a personal token was revoked.
- **MCP over OAuth depends on the provider.** Chameleon publishes Protected Resource Metadata (RFC 9728) so a client can find the authorization server, but dynamic client registration is up to the provider. A personal token (`chm_pat_`) works everywhere.
- The Entra ID and Auth0 setups were not run against live tenants; Keycloak is the one exercised by the smoke test.

## Connectors and the catalog

- **No pin to a verified version.** A catalog name (`pix`) always resolves to the version the catalog lists. `Id@version` pins a version but is unverified and needs `CHAMELEON_ALLOW_UNVERIFIED=true`.
- **The built-in catalog ignores newer revocations.** When the catalog URL cannot be read (or is empty, or `CHAMELEON_OFFLINE=true`), the host falls back to the catalog embedded in the image, which only knows revocations up to the image build.
- **Package attribution matches the main assembly only.** The host ties a loaded connector to its package through the package's main assembly. A connector class in another assembly of the package is registered as in-process and not attributed (or marked verified) as that package.
- **The contract smoke does not run in a container with `--network none`.** A counting proxy already fails the run on any outgoing connection; a network-less container would be a second layer.
- **A stray dll left in the package `lib` folder is not checked.**
- **One connector per package in the smoke.** The contract smoke exercises one connector per package.
- **The CLI has no tests for installing a revoked or unverified connector, or for `search`.**

## Behavior

- **State is in memory by default.** Use `CHAMELEON_STORE=postgres` to keep it.
- **`pix` token endpoint order.** It checks the requested scopes before it authenticates the client: an unknown scope with wrong credentials answers `400 invalid_scope`, not `401`.
- **Webhooks on `pix` may be plain http.** A real PSP requires https with mTLS.
- **Connectors that identify the client by certificate** (`bradesco`, `inter`, `pix`) drop calls to a non-default sandbox that do not use the mTLS port with the sandbox's certificate.

## Development

- **The template test needs network.** It restores targeting packs from nuget.org.
- **Two concurrent test runs share `bin/plugin-run`.** Run the test suite one at a time on a machine.
