# AGENTS.md

## PRINCIPLE ZERO — NO PERMISSION FENCES

Nekostick is a control-plane project whose control surface is **entirely extension-facing**. The
host ships behavior; extensions decide it. Keep the following rule in mind for every change:

**Everything controllable must be extension-controllable. Extension interface design
optimizes for coverage and usability, NEVER permission fencing.** Prefer exposing a
capable, ergonomic bridge over a narrow sandboxed one; NEVER design APIs around distrust
of the extension author. Restrictions exist ONLY where the runtime cannot otherwise keep
its invariants.

**Violating this principle is STRICTLY PROHIBITED.** Every feature implementation MUST be
audited against this principle before it is considered done: if an extension cannot
observe or control a governed behavior, that is a design gap to fix, not a hardening win.
Ownership checks, caller gating, capability whitelists, and similar fences require
explicit user approval before they are introduced.

## Project

Nekostick is a self-hosted reverse proxy / service gateway written in C# on .NET 10. It provides
HTTP routing and proxying, microservice process supervision (start/stop/health via a native POSIX
helper), an isolated extension runtime, and PostgreSQL-backed configuration persistence with
generational publication.

- Solution: `Nekolla.Nekostick.slnx`, all projects target `net10.0`, C# 14.
- The only deployable artifact is `Nekolla.Nekostick.Host` (published single-file for linux-x64,
  embedding the NativeHelper for process-group management).

## Layout

| Path | Role |
| --- | --- |
| `src/Nekolla.Nekostick.Host` | Deployable host: bootstrap, configuration publisher, HTTP adapter, CLI |
| `src/Nekolla.Nekostick.Contracts` | Public extension ABI: entrypoints, bridges, manifests, failure-code enums |
| `src/Nekolla.Nekostick.Extensions` | Extension runtime: collectible ALC loading, dispatch generations, reload/cascade |
| `src/Nekolla.Nekostick.Routing` | Route matching and routing snapshots |
| `src/Nekolla.Nekostick.Proxy` | Outbound proxy execution, retry/timeout policies |
| `src/Nekolla.Nekostick.Supervision` | Microservice process supervision (PosixProcessExecutor + native helper) |
| `src/Nekolla.Nekostick.Persistence` | EF Core (Npgsql) stores, migrations, configuration revision reader |
| `src/Nekolla.Nekostick.Domain` | Domain value objects (e.g. UuidV7) |
| `src/Nekolla.Nekostick.NativeHelper` | Native-aot-published POSIX helper (process groups, signals) |
| `tests/Nekolla.Nekostick.UnitTests` | xUnit unit suite |
| `tests/Nekolla.Nekostick.IntegrationTests` | xUnit integration suite (PostgreSQL + real fixture processes) |
| `tests/Fixtures.Microservice`, `tests/Fixtures.Extension` | Test fixture binaries used by integration tests |

## Build and test

```sh
dotnet build Nekolla.Nekostick.slnx
dotnet test tests/Nekolla.Nekostick.UnitTests
dotnet test tests/Nekolla.Nekostick.IntegrationTests   # requires NEKOSTICK_TEST_PG
```

- PostgreSQL integration tests read `NEKOSTICK_TEST_PG` (a full Npgsql connection string) and skip
  when unset. Each test gets an isolated schema via `SearchPath` and drops it on dispose, so any
  role with `CREATE` on the target database works; no shared state is left behind.
- Integration tests spawn the compiled fixture binaries and the native helper; build the solution
  first so `tests/Fixtures.Microservice` output and `src/Nekolla.Nekostick.Host/.nativehelper/`
  (or the NativeHelper project `bin`) exist.
- Container image: `docker buildx build --platform linux/amd64 -f Dockerfile -t nekostick:release .`
  (the linux/amd64 pin is deliberate — release output targets the linux-x64 RID).

## Conventions agents must follow

- **Warnings are errors** (`TreatWarningsAsErrors`, `AnalysisLevel 10.0-recommended`): analyzer
  rules (CA*, naming, nullability) fail the build. Public APIs require XML doc comments (CS1591).
  Fix root causes; do not suppress without a recorded reason.
- **Contracts assembly discipline**: the public ABI communicates failures through failure-code
  enums (e.g. `ExtensionFailureCode`), not exception types. Keep it dependency-free and stable;
  version via `HostApiVersion` / `ExtensionAbi`.
- **InternalsVisibleTo**: Host, Extensions, Routing, Supervision and Proxy expose internals to
  `Nekolla.Nekostick.UnitTests`. Prefer testing observable behavior; reach for internals only when
  the surface genuinely requires it.
- **Extension isolation invariants**: extensions run in collectible AssemblyLoadContexts; type
  identity across extensions holds only for the Contracts assembly (host-shared). Cross-extension
  calls go through per-extension dispatch turnstiles and contract proxies that suspend (not reject)
  during reload. Entry waits are strictly shorter than the drain deadline
  (`EntryTimeout = LifecycleTimeout - 5s`); preserve that budget ordering when touching reload,
  publication, or proxy code.
- **Runtime RID checks are OS+architecture only.** Never gate on
  `RuntimeInformation.RuntimeIdentifier` — distro-packaged runtimes report custom RIDs
  (`arch-x64`, `fedora-x64`, `linux-musl-x64`) that run the portable linux-x64 helper identically.
  The native helper itself is published per portable RID (`NETCoreSdkPortableRuntimeIdentifier`).
- **Commit style**: Conventional Commits with a lowercase summary, e.g.
  `feat(extensions): ...`, `fix(host): ...`, matching git history.

## Extension ABI versioning and release

- `HostApiVersion.Current` identifies the **in-development** API version. While that version is
  unreleased, extend it directly: add members to its bridge interfaces and capability set, and
  update `docs/extension-api/api-<version>.md` in place. NEVER bump minor/patch for work landing
  before the release ships.
- Bump `HostApiVersion.Current` only when cutting a release. Once a version is released its
  surface is frozen: further capabilities require an additive sibling bridge interface (the
  `IExtensionHostBridge13`/`14` pattern), a new `ExtensionApiCapabilityGate` check, and an
  `UnsupportedExtensionCapabilities` fallback for older negotiated versions.
- New capability checklist: Contracts DTOs/interfaces (XML docs, failure-code enums, no
  exceptions, dependency-free) -> `ExtensionCapabilitySet` entry -> member on the current
  in-development bridge -> capability gate + unsupported facade -> Host facade with
  caller-ownership binding -> docs page update -> fixture entrypoint + tests.
- **Contracts package versioning**: `Nekolla.Nekostick.Contracts.csproj` `PackageVersion` tracks
  the in-development ABI as `<minor>.0-preview.N`. Whenever Contracts content changes while the
  csproj itself has no uncommitted changes, bump it in the same change: increment `-preview.N`,
  or if the current version is not a preview, move to the next version's `-preview.1`. Skip the
  bump only when the user explicitly asks.
