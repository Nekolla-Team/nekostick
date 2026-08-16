# Nekostick extension core

This project contains the pre-Host core for explicit extension manifest discovery, strict JSON validation, dependency ordering, collectible assembly loading, and immutable reload planning. It is intentionally not included in the solution by this change.

## Supported manifest

Only `manifest.json` is parsed. A directory supplied explicitly to `ExtensionManifestDiscovery.Discover` must contain exactly one of `manifest.json`, `manifest.yaml`, or `manifest.yml`. YAML selection returns `YamlParserDeferred`; no `YamlDotNet` package or package-version change is introduced.

The JSON schema is deliberately closed and requires all of these fields:

```json
{
  "schemaVersion": 1,
  "id": "example.extension",
  "version": "1.2.3",
  "entryAssembly": "Example.Extension.dll",
  "entryType": "Example.Extension.Entry",
  "dependencies": [
    { "id": "other.extension", "versionRange": "^1.0.0" }
  ],
  "requiredHostApiVersion": ">=1.0.0 <2.0.0"
}
```

Unknown or duplicate fields are rejected. Entry assembly paths are relative `.dll` paths, and both the manifest and entry assembly are canonicalized through existing symlinks before containment is checked. Discovery performs no directory scan, watcher registration, or automatic load.

The supported range syntax is exact versions, `*`, `x` wildcards, comparison sets such as `>=1.0.0 <2.0.0`, caret, tilde, and `||` alternatives. SemVer precedence follows SemVer 2.0.0, including prerelease ordering; build metadata does not affect compatibility.

## ABI and deferred bindings

The current Contracts project does not yet expose an extension entry ABI. The loader therefore checks an internal marker only, and its internal host bridge seam names future private services, tasks, logger, status, event, and configuration bindings without implementing any of them. Host-facing entry interfaces should be bound publicly in Contracts in a future phase; this project does not invent host configuration access, database access, HTTP registration, or handler execution.

The YAML parser interface/adaptor remains deferred until dependency integration fixes a `YamlDotNet` version and its safe scalar/map/list policy. Reload planning is immutable intent data only; replacement load or start failure follows the preserve-previous branch.
