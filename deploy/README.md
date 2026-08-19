# Deployment artifacts

These templates deploy the Host as a framework-dependent, ReadyToRun,
single-file binary for the `linux-x64` RID. The Host publish target builds the
matching NativeHelper and embeds it in the Host; do not publish, copy, or install
a helper executable beside the Host.

Publish only the Host project. Its build target automatically publishes the
helper with the same configuration and RID before embedding it:

```sh
dotnet publish src/Nekolla.Nekostick.Host/Nekolla.Nekostick.Host.csproj \
  --configuration Release --runtime linux-x64 --self-contained false \
  --output out/linux-x64 \
  -p:PublishReadyToRun=true -p:PublishSingleFile=true -p:UseAppHost=true
```

The resulting directory contains the Host executable and its framework-
dependent publish metadata, but no side-by-side helper executable. At runtime,
Host extracts the embedded helper into the platform user cache, validates and
reuses the cached content safely, sets owner POSIX execute permission, and only
then starts it. On Darwin the cache is `~/Library/Caches`; on Linux it is
`$XDG_CACHE_HOME` when set, otherwise `~/.cache`. Unsupported OS/RID and
extraction failures fail closed.

Do not mix RIDs or releases: the Host and its embedded helper must be published
from the same commit and with the same RID. ReadyToRun publish requires the
matching runtime packs during restore.

## Runtime requirements

The Host is a framework-dependent, single-file application, not a self-contained
application. A target system must provide the matching .NET 10 runtime for
`linux-x64`; for systemd install the .NET 10 ASP.NET Core runtime (for example,
`aspnetcore-runtime-10.0` from the supported package source). The ASP.NET Core
runtime supplies the .NET runtime needed by the embedded helper as well. The
Dockerfile uses the .NET 10 ASP.NET runtime image.

## Docker and Compose

Build with the repository root as the Docker context and target amd64, which
matches the `linux-x64` publish:

```sh
docker buildx build --platform linux/amd64 -f deploy/Dockerfile -t nekostick:release .
```

The image publishes only the Host (including its embedded helper) and starts
`/app/Nekolla.Nekostick.Host run` directly; it does not invoke `dotnet Host.dll`
or require a helper file beside the Host. Compose keeps the services on an
internal network and does not publish a host port. It requires
`NEKOSTICK_CONNECTION_STRING` and `NEKOSTICK_NODE_ID`, supplies the documented
listen address and port, and mounts the `extensions` directory read-only:

```sh
docker compose -f deploy/compose.example.yml up --build
```

## systemd

Install the complete Host publish directory under `/opt/nekostick`, including
its publish metadata, with ownership assigned to the dedicated non-root
`nekostick` user. Install and enable `deploy/nekolla-nekostick.service` after
placing the environment file at `/etc/nekostick/nekostick.env`. The service
invokes `/opt/nekostick/Nekolla.Nekostick.Host run` directly. Host extracts the
embedded helper into the `nekostick` user's cache and grants it owner POSIX
execute permission before running it; no helper executable is installed under
`/opt/nekostick`.

## Operations and secrets

- PostgreSQL 16 is the baseline; apply the EF Core idempotent migration SQL artifact before startup.
- Supply bootstrap settings, including the connection string, through an
  environment manager, the systemd `EnvironmentFile`, or Compose environment
  interpolation. Never commit, print, or embed secret values or secret files.
- TLS is terminated by an external reverse proxy. The Host contract here is
  HTTP/1.1.
- Stop the service with `SIGTERM` and allow the configured grace period for
  request draining, extension shutdown, and child-process shutdown.
