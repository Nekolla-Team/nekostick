# Nekostick

<p align="center">
  <img src="docs/nekostick-hero.svg" alt="Nekostick dynamic routing host" width="100%" />
</p>

<p align="center">
  <strong>A dynamic routing host for microservices</strong><br />
  Safely dispatch HTTP/1.1 traffic to local services, static files, and trusted extensions.
</p>

<p align="center">
  <a href="https://github.com/Nekolla-Team/nekostick/actions/workflows/test.yml"><img src="https://github.com/Nekolla-Team/nekostick/actions/workflows/test.yml/badge.svg?branch=main" alt="Test status" /></a>
  <a href="https://www.gnu.org/licenses/agpl-3.0.html"><img src="https://img.shields.io/badge/license-AGPL--3.0-blue.svg" alt="AGPL 3.0 license" /></a>
  <a href="https://github.com/Nekolla-Team/nekostick/commits"><img src="https://img.shields.io/github/last-commit/Nekolla-Team/nekostick" alt="Last commit" /></a>
  <a href="https://dotnet.microsoft.com/download/dotnet/10.0"><img src="https://img.shields.io/badge/.NET-10-512BD4" alt=".NET 10" /></a>
  <a href="https://www.postgresql.org/"><img src="https://img.shields.io/badge/PostgreSQL-16-336791" alt="PostgreSQL 16" /></a>
</p>

## Overview

Nekostick is a single-entry dynamic routing host. Route definitions are persisted in PostgreSQL, validated as a complete candidate, compiled into an immutable snapshot, and atomically published. Existing requests continue using their captured snapshot while new requests use the latest accepted configuration.

Supported route targets are:

- **Local microservices**: port leases, health checks, lifecycle management, and HTTP/WebSocket forwarding.
- **Static files**: absolute roots with path containment and traversal protections.
- **Trusted extensions**: unloadable extensions behind the stable `Nekolla.Nekostick.Contracts` ABI, with handlers, fallback, configuration, and event capabilities.

The Host accepts HTTP/1.1 only. TLS termination, authentication, and administrative APIs are intentionally outside the core Host boundary.

## Features

- PostgreSQL persistence with EF Core migrations and advisory-lock startup coordination.
- Immutable route snapshots with deterministic Exact, Prefix, and Regex matching.
- Header and path rewriting, proxy timeouts and retries, client-IP handling, and bounded request resources.
- HTTP and WebSocket forwarding with local process supervision.
- Collectible `AssemblyLoadContext` extension loading with staged reload/unload and ABI compatibility checks.
- Docker, Docker Compose, and Systemd deployment support.

## Requirements

- .NET 10 SDK 10.0.100 or a compatible feature band.
- PostgreSQL 16.
- A supported POSIX runtime environment, currently Linux/macOS.
- (Optional) An external reverse proxy for production TLS termination.

## Usage

### Build and test

```sh
dotnet restore Nekolla.Nekostick.slnx
dotnet build Nekolla.Nekostick.slnx --configuration Release
dotnet test Nekolla.Nekostick.slnx --configuration Release --no-build
```

PostgreSQL integration tests use the `NEKOSTICK_TEST_PG` environment variable. They do not substitute an in-memory provider when PostgreSQL is unavailable, instead bypass directly.

### Run the Host

The Host requires a PostgreSQL connection string before startup. Bootstrap precedence is `CLI > environment > default`.

```sh
export NEKOSTICK_CONNECTION_STRING='Host=127.0.0.1;Port=5432;Database=nekostick;Username=nekostick;Password=change-me'
export NEKOSTICK_NODE_ID='node-1'

dotnet run --project src/Nekolla.Nekostick.Host/Nekolla.Nekostick.Host.csproj -- \
  run --listen-address 127.0.0.1 --listen-port 8080
```

Supported commands:

```text
run       Start the node and serve dynamic routes.
status    Emit non-secret node and configuration status as JSON.
doctor    Check database, migration, and snapshot readiness.
```

Supported run switches:

```text
--skip-extensions       Do not load extensions for this invocation.
--disable-supervisor    Do not manage local microservice processes.
--read-only             Disable configuration writes from this node.
```

The equivalent bootstrap environment variables are:

| Setting | CLI option | Environment variable | Default |
| --- | --- | --- | --- |
| PostgreSQL connection | `--connection-string` | `NEKOSTICK_CONNECTION_STRING` | Required |
| Listen address | `--listen-address` | `NEKOSTICK_LISTEN_ADDRESS` | `127.0.0.1` |
| Listen port | `--listen-port` | `NEKOSTICK_LISTEN_PORT` | `8080` |
| Node identifier (max 128 characters) | `--node-id` | `NEKOSTICK_NODE_ID` | `0` |
| Minimum log level | `--log-level` | `NEKOSTICK_LOG_LEVEL` | `Information` |
| Include EF Core logs | `--include-ef-logs` | `NEKOSTICK_INCLUDE_EF_LOGS` | off |

Accepted log levels are case-insensitive: `trace`, `debug`, `information` (alias `info`), `warning` (alias `warn`), `error`, `critical`, `none`. `--include-ef-logs` is a flag whose presence enables EF Core logging; the environment form accepts `true` or `false` and any other value fails startup validation. Diagnostics commands (`status`, `doctor`) accept the same bootstrap options and emit JSON instead of serving routes.

## Deployment

The deployment templates at the repository root deploy the Host as a framework-dependent, ReadyToRun, single-file binary for the `linux-x64` RID. The Host publish target builds the matching NativeHelper and embeds it in the Host; do not publish, copy, or install a helper executable beside the Host.

### Publish

Publish only the Host project. Its build target automatically publishes the helper with the same configuration and RID before embedding it:

```sh
dotnet publish src/Nekolla.Nekostick.Host/Nekolla.Nekostick.Host.csproj \
  --configuration Release --runtime linux-x64 --self-contained false \
  --output out/linux-x64 \
  -p:PublishReadyToRun=true -p:PublishSingleFile=true -p:UseAppHost=true
```

The resulting directory contains the Host executable and its framework-dependent publish metadata, but no side-by-side helper executable. At runtime, Host extracts the embedded helper into the platform user cache, validates and reuses the cached content safely, sets owner POSIX execute permission, and only then starts it. On Darwin the cache is `~/Library/Caches`; on Linux it is `$XDG_CACHE_HOME` when set, otherwise `~/.cache`. Unsupported OS/RID and extraction failures fail closed.

Do not mix RIDs or releases: the Host and its embedded helper must be published from the same commit and with the same RID. ReadyToRun publish requires the matching runtime packs during restore.

### Runtime requirements

The Host is a framework-dependent, single-file application, not a self-contained application. A target system must provide the matching .NET 10 runtime for `linux-x64`; for systemd install the .NET 10 ASP.NET Core runtime (for example, `aspnetcore-runtime-10.0` from the supported package source). The ASP.NET Core runtime supplies the .NET runtime needed by the embedded helper as well. The Dockerfile uses the .NET 10 ASP.NET runtime image.

### Docker and Compose

Build with the repository root as the Docker context and target amd64, which matches the `linux-x64` publish:

```sh
docker buildx build --platform linux/amd64 -f Dockerfile -t nekostick:release .
```

The image publishes only the Host (including its embedded helper) and starts `/app/Nekolla.Nekostick.Host run` directly; it does not invoke `dotnet Host.dll` or require a helper file beside the Host. Compose keeps the services on an internal network and does not publish a host port. It requires `NEKOSTICK_CONNECTION_STRING` and a stable, unique `NEKOSTICK_NODE_ID`, supplies the documented listen address and port, and mounts the `extensions` directory read-only:

```sh
docker compose -f compose.example.yml up --build
```

### systemd

Install the complete Host publish directory under `/opt/nekostick`, including its publish metadata, with ownership assigned to the dedicated non-root `nekostick` user. Install and enable [`nekostick.service`](nekostick.service) after placing the environment file at `/etc/nekostick/nekostick.env`. The service invokes `/opt/nekostick/Nekolla.Nekostick.Host run` directly. Host extracts the embedded helper into the `nekostick` user's cache and grants it owner POSIX execute permission before running it; no helper executable is installed under `/opt/nekostick`.

### Operations and secrets

- PostgreSQL 16 is the baseline; apply the EF Core idempotent migration SQL artifact before startup.
- Supply bootstrap settings, including the connection string, through an environment manager, the systemd `EnvironmentFile`, or Compose environment interpolation. Never commit, print, or embed secret values or secret files.
- TLS is terminated by an external reverse proxy. The Host contract here is HTTP/1.1.
- Stop the service with `SIGTERM` and allow the configured grace period for request draining, extension shutdown, and child-process shutdown.

## Extension API

- [Extension API guide](docs/extension-api/)
- [Contracts package README](src/Nekolla.Nekostick.Contracts/)

Extensions are trusted in-process code. A collectible `AssemblyLoadContext` provides dependency isolation and unloadability; it is not a security sandbox. Manifest validation, capability boundaries, lifecycle behavior, handler semantics, and configuration rules are defined by the [Extension API guide](docs/extension-api/) and its versioned detail documents.

### Consume the Contracts package

The stable Host and extension contract surface is distributed as the `Nekolla.Nekostick.Contracts` NuGet package:

```sh
dotnet add package Nekolla.Nekostick.Contracts --version 1.3.4
```

Extension projects should reference Contracts and their explicitly declared shared-contract assemblies only. They should not reference Host, Persistence, ASP.NET, EF Core, or another extension's implementation assembly.

## Configuration and operations

Business configuration is read from validated PostgreSQL snapshots. Bootstrap settings include `NEKOSTICK_CONNECTION_STRING`, `NEKOSTICK_LISTEN_ADDRESS`, `NEKOSTICK_LISTEN_PORT`, `NEKOSTICK_NODE_ID`, `NEKOSTICK_LOG_LEVEL`, and `NEKOSTICK_INCLUDE_EF_LOGS`; every setting also has an equivalent `--` CLI option (see above), and CLI values override environment values. Do not commit connection strings, secrets, or environment files, and do not write them to logs or diagnostic output.

Operational constraints include:

- Startup applies and validates the EF Core idempotent migration artifact before the Host becomes ready.
- Multi-node deployments require a stable, unique `nodeId` for every node.
- The Host and NativeHelper must be published from the same commit, configuration, and runtime identifier.
- Production deployments should use separate least-privilege runtime and migration credentials where practical.

## Project layout

```text
src/
  Nekolla.Nekostick.Contracts/      Stable Host and extension ABI and DTOs.
  Nekolla.Nekostick.Domain/         Route and bootstrap domain models.
  Nekolla.Nekostick.Routing/        Immutable matching and dispatch snapshots.
  Nekolla.Nekostick.Proxy/          HTTP/WebSocket proxy and static targets.
  Nekolla.Nekostick.Supervision/    Child-process lifecycle and health.
  Nekolla.Nekostick.Extensions/     Manifest, ALC, lifecycle, and capabilities.
  Nekolla.Nekostick.Persistence/    PostgreSQL, EF Core, and migrations.
  Nekolla.Nekostick.Host/           Executable composition root.

tests/                              Unit, integration, and process fixtures.
docs/                               Technical design and extension API guides.
```

## Documentation

- [Technical design](docs/technical-design.md)
- [Extension API guide](docs/extension-api/)
- [Contracts package](src/Nekolla.Nekostick.Contracts/)

## Development

Keep the Contracts assembly as the stable ABI boundary. Do not expose database connections, EF entities, ASP.NET types, or runtime handles to extensions. Before submitting changes, run the canonical solution restore, build, and test commands. Use a real PostgreSQL instance for integration coverage through `NEKOSTICK_TEST_PG`.

## License

Copyright 2026 Nekolla Team.

Licensed under the GNU Affero General Public License, Version 3.0. See [`LICENSE`](LICENSE) or <https://www.gnu.org/licenses/agpl-3.0.html> for the full text.

**Exception**: the Contracts package ([`src/Nekolla.Nekostick.Contracts`](src/Nekolla.Nekostick.Contracts)) is separately licensed under the Apache License, Version 2.0, so extension authors can build against the stable ABI without copyleft obligations. See [`src/Nekolla.Nekostick.Contracts/LICENSE`](src/Nekolla.Nekostick.Contracts/LICENSE) or <https://www.apache.org/licenses/LICENSE-2.0> for the full text.
