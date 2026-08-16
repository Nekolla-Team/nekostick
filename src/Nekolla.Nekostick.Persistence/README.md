# Nekostick PostgreSQL persistence

This project owns the `nekostick` PostgreSQL schema, EF Core migrations, the fixed
transaction-scoped migration advisory lock, and the startup schema probe. Business
configuration remains in PostgreSQL; this layer does not read it from files and does
not implement phase-B configuration CRUD.

The committed `Migrations/NekostickDbContext.migrations.sql` file is an EF-style
idempotent delivery artifact. After a release build and a migration change, regenerate
it with `dotnet ef migrations script --idempotent` using the host startup project and
the design-time `NEKOSTICK_CONNECTION_STRING` environment variable. The connection
string is never included in diagnostics or generated source.

The host must configure `NekostickDbContext` with `NekostickDbContextOptions.Create`,
invoke `IStartupDatabaseProbe.MigrateAndValidateAsync` before serving traffic, and fail
closed when the returned result is unsuccessful. Status and doctor may use
`IConfigurationRevisionReader`; configuration mutation belongs to phase B.
