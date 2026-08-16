# Deployment artifacts

These are deployment templates for the future Host project. Build the image with the repository root as the Docker context and use a Release build plus the migration SQL artifact before startup.

- PostgreSQL 16 is the baseline.
- Supply bootstrap secrets, including the connection string, through an environment manager, the systemd `EnvironmentFile`, or Compose environment interpolation. Never commit the values or secret files.
- TLS is terminated by an external reverse proxy. The Host contract here is HTTP/1.1.
- Stop the service with `SIGTERM` and allow the configured grace period for request draining, extension shutdown, and child-process shutdown.

The systemd template assumes a dedicated non-root `nekostick` user and an installation under `/opt/nekostick`. The Compose example keeps its services on an internal network and does not publish a host port.
