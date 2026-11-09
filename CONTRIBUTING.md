# Contributing

Thanks for helping. Chameleon is a simulator for development and testing; keep that in mind when you propose a change. By taking part you agree to the [Code of conduct](CODE_OF_CONDUCT.md). Report security problems as described in [SECURITY.md](SECURITY.md), not in public issues.

## Build and test

You need the .NET 8 SDK. Node 20 with pnpm 9 is only needed for the panel in `web/`, and Docker plus Python 3 for the smoke test.

```bash
dotnet build Chameleon.sln
dotnet test Chameleon.sln

# Panel
cd web && pnpm install --frozen-lockfile && pnpm lint && pnpm build
```

`pnpm lint` must report no warnings. Some template tests restore packages from nuget.org, so they need network access, and two test runs at the same time share `bin/plugin-run`: run one at a time (see [limitations](docs/limitations.md)).

## Smoke test

The smoke test runs the real image with the five official connectors and exercises them over HTTP. It needs the local packages and catalog first, as in [Getting started](docs/getting-started.md#local-development):

```bash
dotnet pack -c Release -o artifacts/packages
dotnet run -c Release --project tools/catalog -- artifacts/packages artifacts/packages/catalog.json --providers catalog/providers
docker compose up -d --build
python tests/smoke/smoke_test.py --skip-dotnet --skip-build
docker compose down -v
```

CI runs the same steps, plus a variant against a local Keycloak (`docker-compose.oidc.yml`, `--oidc`).

## Commits and pull requests

- Use [Conventional Commits](https://www.conventionalcommits.org/): `feat:`, `fix:`, `docs:`, `refactor:`, `test:`, `ci:`, `chore:`. Write the subject in English, in the imperative.
- Keep a pull request to one change. Say what changed and why, and how you tested it.
- Code, comments, messages and documentation in `docs/` are in English. Portuguese lives only in `docs/pt-BR/` and in `README.md`, which is Portuguese only; when you change a guide that has a Portuguese copy, update both.
- No emoji in code.
- Never commit credentials. CI runs gitleaks.

## A new connector

1. Read [Creating a connector](docs/creating-a-connector.md) and generate a project with the template: `dotnet new install Chameleon.Mock.Templates`, then `dotnet new chameleon-connector`.
2. You can keep the connector in your own repository and publish it to nuget.org with the `chameleon-connector` tag: it then works as an unverified community connector with no review needed.
3. To get it into the official catalog (verified), follow [Verified badge](docs/verified-badge.md): add the provider sheet `catalog/providers/<name>.md` (a short description and a `## Sources` section with the provider's public documentation) in a pull request. Maintainers add the catalog entry from the published package with the `catalog-update` workflow.
4. To propose a connector for the maintainers to build, open a "Connector request" issue. A public documentation link is required.

Connectors must follow the public contract of the provider and must not contain proprietary material, real credentials or personal data.

## Code owners

`.github/CODEOWNERS` assigns every path, and each folder under `connectors/`, to `@Erbio-Digital/maintainers`. Their review is required on a pull request. Releases are made by maintainers; see [Releasing](docs/releasing.md).
