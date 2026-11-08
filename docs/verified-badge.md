# Verified badge

A connector in the catalog (`catalog/connectors.json`) is "verified". `CHAMELEON_CONNECTORS=<name>` loads only verified connectors, with the packages pinned by SHA-512. Anything else is loaded as `Id@version` with `CHAMELEON_ALLOW_UNVERIFIED=true` and shown as unverified. The badge means the package is what the catalog says it is and passed the checks below, and that a maintainer read its source. It is not a guarantee that the simulator is correct.

## Two steps

1. **`catalog-verify` (automatic).** A tool rebuilds each catalog entry from the package on nuget.org and checks the requirements below. It runs in the `catalog-update` workflow and on every pull request that touches `catalog/**` or the catalog tools. The pull request opened by `catalog-update` does not get an automatic `catalog-verify` check, because GitHub does not start workflows for pull requests opened with `GITHUB_TOKEN`; its body links the run that verified the entries. A maintainer who pushes a commit to that pull request triggers the check.
2. **Human review.** A maintainer reads the connector source for the things a tool cannot judge: fidelity to the public contract, no hidden network access, no proprietary material, honest naming and trademark use.

Both must pass before the entry is merged.

## Requirements

- **License.** An OSI-approved SPDX expression in the package (`Apache-2.0`, `MIT`, `BSD-2-Clause`, `BSD-3-Clause`, `ISC`, `MPL-2.0`, the LGPL and GPL families, `EPL-2.0`).
- **Public repository.** An `https` repository URL in the package metadata that answers without credentials.
- **Provider sheet.** `catalog/providers/<name>.md` with a short description and a `## Sources` section whose first `https://` link is the provider's public documentation. That link becomes the provider URL in the catalog.
- **Contract smoke, with no network.** The tool installs the package, starts the real host offline with a counting proxy and checks that: the connector loads as verified (the catalog hashes matched), `Describe()` has a title, at least one provider route is mapped, and nothing opens an outgoing connection while loading.
- **Compatible SDK.** Every assembly of the package that references `Chameleon.Sdk` must be compatible with the host's SDK (in 0.x, the same minor version).
- **Integrity.** The catalog closure (the package and the libraries it needs) matches the package and the SHA-512 of each file matches.
- **Naming.** The connector name matches `^[a-z0-9][a-z0-9-]*$` and is unique in the catalog. Community packages should not use the reserved `Chameleon.Mock.` prefix (the template avoids it).

## How to apply

1. Publish your package to nuget.org with the tags `chameleon-connector` and `chameleon-name:<name>` (the template does this).
2. Open a pull request that adds `catalog/providers/<name>.md`.
3. A maintainer runs the `catalog-update` workflow with `<PackageId>@<version>`. It downloads the package from nuget.org, builds the entry, runs `catalog-verify` and opens a pull request with the catalog change.
4. The maintainer reviews the source and merges.

To update a verified connector, publish the new version and repeat step 3 for it. The entry then points to the new version for everyone who uses the catalog name.

## Revocation

If a verified version turns out to be wrong or unsafe, a maintainer sets the entry's `revoked` field to a reason in a pull request to `catalog/connectors.json`. A host that reads the catalog then refuses to load that connector with the reason in the message, and `catalog-verify` skips revoked entries. A host that cannot read the catalog falls back to the one embedded in its image and does not see newer revocations; see [limitations](limitations.md). Revoking does not remove the package from nuget.org.
