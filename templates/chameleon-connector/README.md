# Example Title

A Chameleon connector generated from the `chameleon-connector` template.

```bash
dotnet pack -c Release -o ./feed
CHAMELEON_CONNECTORS=example-connector-name@0.1.0 CHAMELEON_NUGET_SOURCES=./feed chameleon
```

Edit `ExampleConnector.cs`: routes, entities and actions. Guide: `docs/creating-a-connector.md` in the Chameleon repository.
