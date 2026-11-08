# Releasing

For maintainers. Releases are driven by git tags and GitHub Actions; no long-lived NuGet secret exists (NuGet Trusted Publishing).

## What each tag does

| Tag | Workflow | Publishes |
|---|---|---|
| `v<version>` | `release.yml` | `Chameleon.Mock.Engine`, `Chameleon.Mock.Sdk` and `Chameleon.Mock.Templates` to nuget.org; the image `ghcr.io/erbio-digital/chameleon-mock:v<version>` and `:latest`. |
| `connectors/<name>/v<version>` | `connector-release.yml` | That connector plus the `connectors/*.Common` libraries it references (for `pix`, also `Chameleon.Mock.Connectors.Pix.Common`). |

Both workflows run the full test suite first and check the tag against the version in the code: `v<version>` against `<Version>` in `Directory.Build.props`, and the connector tag against the `Version` of the project whose `ChameleonConnectorName` is `<name>`. The image tag keeps the `v` (`:v0.1.0`). Pushes use `--skip-duplicate`, so a re-run does not fail on a package that is already there.

## One-time setup

Do this before the first tag.

1. In the GitHub organization `Erbio-Digital`, with the repository `chameleon-mock`, create the `maintainers` team (used by `.github/CODEOWNERS`).
2. On nuget.org, reserve the `Chameleon.Mock.` package id prefix for the publishing account.
3. On nuget.org, create two Trusted Publishing policies for the repository `Erbio-Digital/chameleon-mock`: one for the workflow file `release.yml` and one for `connector-release.yml`.
4. In the repository, set the Actions variable `NUGET_USER` to the nuget.org user name that owns the policies (the workflows read `vars.NUGET_USER`).
5. In the repository settings, allow the workflow `GITHUB_TOKEN` to write packages. The first image push creates the ghcr package; then open the package page and link it to the repository, and make it public.
6. Allow GitHub Actions to create pull requests (Settings, Actions, General): `catalog-update` opens its pull request with `GITHUB_TOKEN`.
7. Protect `main`: require pull requests and the CI checks.

## First release, in order

1. **Tag the core.** Check that `Directory.Build.props` has `<Version>0.1.0</Version>`, then push the tag `v0.1.0`. Wait for `release.yml` to finish (packages and image).
2. **Tag the five connectors.** Push `connectors/bradesco/v0.1.0`, `connectors/inter/v0.1.0`, `connectors/nfse-sp/v0.1.0`, `connectors/pix/v0.1.0` and `connectors/zendesk/v0.1.0`. The first `pix` tag also publishes `Chameleon.Mock.Connectors.Pix.Common`.
3. **Wait for NuGet validation.** A pushed package is not installable until nuget.org finishes validation and indexing, which can take from minutes to a few hours. Check that each package page shows the version as listed before going on.
4. **Update the catalog.** Run the `catalog-update` workflow (Actions, run manually) with the input

   ```
   Chameleon.Mock.Connectors.Bradesco@0.1.0,Chameleon.Mock.Connectors.Inter@0.1.0,Chameleon.Mock.Connectors.NfseSaoPaulo@0.1.0,Chameleon.Mock.Connectors.Pix@0.1.0,Chameleon.Mock.Connectors.Zendesk@0.1.0
   ```

   It downloads the packages from nuget.org, rebuilds the entries from the provider sheets, runs `catalog-verify` against nuget.org and opens a pull request.
5. **Review and merge the catalog pull request.** Read the entries (hashes, provider URLs, closure) and merge. Until it is merged the committed catalog is empty and `CHAMELEON_CONNECTORS=pix` answers "unknown connector". Check the quick start from the README on a clean machine afterwards.

Later releases follow the same order: tag, wait, `catalog-update` with the new `Id@version`, review. A connector can be released on its own without a core release as long as its SDK range still matches.

The connectors and `Chameleon.Mock.Connectors.Pix.Common` have no `<Version>` of their own: they inherit it from `Directory.Build.props`, like the core. To release one connector alone, set `<Version>` in its csproj and tag that version. When `Pix.Common` changes, bump its version too (set `<Version>` in its csproj; the connectors reference the project, so their package dependency follows): the push uses `--skip-duplicate`, so an existing `Pix.Common` version on nuget.org is kept silently and the new connector would run against the old library.

## Hotfix and revocation

- To retire a bad connector version, open a pull request that sets `revoked` on its catalog entry (see [Verified badge](verified-badge.md)). Hosts read it at their next start.
- Never move or reuse a published tag or version; publish a new patch version instead.
