using System.IO;
using Xunit;

namespace Nekolla.Nekostick.UnitTests;

/// <summary>Checks the checked-in deployment and CI files at their delivery boundary.</summary>
public sealed class DeploymentArtifactContractTests
{
    [Fact]
    public void DockerfileAndComposeEnforceNonRootPrivateDelivery()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var dockerfile = ReadArtifact("deploy/Dockerfile");
        var compose = ReadArtifact("deploy/compose.example.yml");
        AssertArtifactContains("deploy/compose.example.yml", compose, "POSTGRES_DB: ${POSTGRES_DB:?POSTGRES_DB must be set}");
        AssertArtifactContains("deploy/compose.example.yml", compose, "POSTGRES_USER: ${POSTGRES_USER:?POSTGRES_USER must be set}");
        AssertArtifactContains("deploy/compose.example.yml", compose, "POSTGRES_PASSWORD: ${POSTGRES_PASSWORD:?POSTGRES_PASSWORD must be set}");

        AssertArtifactContains("deploy/Dockerfile", dockerfile, "FROM --platform=linux/amd64 mcr.microsoft.com/dotnet/aspnet:10.0 AS final");
        AssertArtifactContains("deploy/Dockerfile", dockerfile, "WORKDIR /app");
        AssertArtifactContains("deploy/Dockerfile", dockerfile, "USER $APP_UID");
        AssertArtifactContains("deploy/Dockerfile", dockerfile, "STOPSIGNAL SIGTERM");
        AssertArtifactContains("deploy/Dockerfile", dockerfile, "ENTRYPOINT [\"/app/Nekolla.Nekostick.Host\", \"run\"]");
        AssertArtifactNotContains("deploy/Dockerfile", dockerfile, "USER root");

        AssertArtifactContains("deploy/compose.example.yml", compose, "postgres:16");
        AssertArtifactContains("deploy/compose.example.yml", compose, "NEKOSTICK_CONNECTION_STRING: ${NEKOSTICK_CONNECTION_STRING:?NEKOSTICK_CONNECTION_STRING must be set}");
        AssertArtifactContains("deploy/compose.example.yml", compose, "NEKOSTICK_NODE_ID: ${NEKOSTICK_NODE_ID:?NEKOSTICK_NODE_ID must be set to a stable unique value}");
        AssertArtifactContains("deploy/compose.example.yml", compose, "NEKOSTICK_LISTEN_ADDRESS: \"0.0.0.0\"");
        AssertArtifactContains("deploy/compose.example.yml", compose, "NEKOSTICK_LISTEN_PORT: \"8080\"");
        AssertArtifactContains("deploy/compose.example.yml", compose, "- ./extensions:/app/extensions:ro");
        AssertArtifactContains("deploy/compose.example.yml", compose, "networks:\n  internal:\n    internal: true");
        AssertArtifactContains("deploy/compose.example.yml", compose, "security_opt:\n      - no-new-privileges:true");
        AssertArtifactNotContains("deploy/compose.example.yml", compose, "ports:");
    }

    [Fact]
    public void SystemdAndReadmeDocumentStopIsolationAndSecretInputs()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var unit = ReadArtifact("deploy/nekolla-nekostick.service");
        var readme = ReadArtifact("deploy/README.md");

        AssertArtifactContains("deploy/nekolla-nekostick.service", unit, "Type=exec");
        AssertArtifactContains("deploy/nekolla-nekostick.service", unit, "User=nekostick");
        AssertArtifactContains("deploy/nekolla-nekostick.service", unit, "Group=nekostick");
        AssertArtifactContains("deploy/nekolla-nekostick.service", unit, "EnvironmentFile=/etc/nekostick/nekostick.env");
        AssertArtifactContains("deploy/nekolla-nekostick.service", unit, "ExecStart=/opt/nekostick/Nekolla.Nekostick.Host run");
        AssertArtifactContains("deploy/nekolla-nekostick.service", unit, "Restart=on-failure");
        AssertArtifactContains("deploy/nekolla-nekostick.service", unit, "KillSignal=SIGTERM");
        AssertArtifactContains("deploy/nekolla-nekostick.service", unit, "KillMode=control-group");
        AssertArtifactContains("deploy/nekolla-nekostick.service", unit, "ReadOnlyPaths=/opt/nekostick/extensions");
        AssertArtifactContains("deploy/nekolla-nekostick.service", unit, "NoNewPrivileges=true");
        AssertArtifactContains("deploy/nekolla-nekostick.service", unit, "TimeoutStopSec=45s");

        AssertArtifactContains("deploy/README.md", readme, "PostgreSQL 16");
        AssertArtifactContains("deploy/README.md", readme, "internal network");
        AssertArtifactContains("deploy/README.md", readme, "does not publish a host port");
        AssertArtifactContains("deploy/README.md", readme, "dedicated non-root");
        AssertArtifactContains("deploy/README.md", readme, "EnvironmentFile");
        AssertArtifactContains("deploy/README.md", readme, "NEKOSTICK_CONNECTION_STRING");
        AssertArtifactContains("deploy/README.md", readme, "NEKOSTICK_NODE_ID");
        AssertArtifactContains("deploy/README.md", readme, "read-only");
        AssertArtifactContains("deploy/README.md", readme, "idempotent");
        AssertArtifactContains("deploy/README.md", readme, "Never commit, print, or embed secret values or secret files.");
        AssertArtifactContains("deploy/README.md", readme, "SIGTERM");
        AssertArtifactNotContains("deploy/README.md", readme, "Password=");
    }

    [Fact]
    public void GitHubWorkflowProvesPostgresMigrationBuildAndTestDelivery()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var workflow = ReadArtifact(".github/workflows/test.yml");

        AssertArtifactContains(".github/workflows/test.yml", workflow, "name: Linux with Postgres 16");
        AssertArtifactContains(".github/workflows/test.yml", workflow, "image: postgres:16");
        AssertArtifactContains(".github/workflows/test.yml", workflow, "POSTGRES_DB: nekostick_ci");
        AssertArtifactContains(".github/workflows/test.yml", workflow, "Verify PostgreSQL service health");
        AssertArtifactContains(".github/workflows/test.yml", workflow, "dotnet restore Nekolla.Nekostick.slnx");
        AssertArtifactContains(".github/workflows/test.yml", workflow, "dotnet build Nekolla.Nekostick.slnx --configuration Release --no-restore");
        AssertArtifactContains(".github/workflows/test.yml", workflow, "migrations script --idempotent");
        AssertArtifactContains(".github/workflows/test.yml", workflow, "test -s \"$script_path\"");
        AssertArtifactContains(".github/workflows/test.yml", workflow, "cmp -s \"$script_path\" \"$repeat_script_path\"");
        AssertArtifactContains(".github/workflows/test.yml", workflow, "dotnet test Nekolla.Nekostick.slnx --configuration Release --no-build --no-restore");
        AssertArtifactContains(".github/workflows/test.yml", workflow, "NEKOSTICK_TEST_PG=Host=%s;Port=%s;Database=%s;Username=%s;Password=%s");
        AssertArtifactNotContains(".github/workflows/test.yml", workflow, "set -x");
        AssertArtifactContains(".github/workflows/test.yml", workflow, "permissions:\n  contents: read");
    }

    private static string ReadArtifact(string relativePath)
    {
        var root = FindRepositoryRoot();
        var fullPath = Path.GetFullPath(Path.Combine(root.FullName, relativePath));
        Assert.True(
            File.Exists(fullPath),
            $"Required delivery artifact '{relativePath}' was not found from test output directory '{AppContext.BaseDirectory}'. Resolved path: '{fullPath}'.");
        return File.ReadAllText(fullPath);
    }

    private static DirectoryInfo FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Nekolla.Nekostick.slnx")) &&
                Directory.Exists(Path.Combine(directory.FullName, "deploy")))
            {
                return directory;
            }
        }

        Assert.Fail(
            $"Repository root was not found while resolving delivery artifacts from '{AppContext.BaseDirectory}'.");
        return new DirectoryInfo(AppContext.BaseDirectory);
    }

    private static void AssertArtifactContains(string relativePath, string artifact, string expected)
    {
        Assert.True(
            artifact.Contains(expected, StringComparison.Ordinal),
            $"Delivery artifact '{relativePath}' is missing required invariant '{expected}'.");
    }

    private static void AssertArtifactNotContains(string relativePath, string artifact, string forbidden)
    {
        Assert.False(
            artifact.Contains(forbidden, StringComparison.Ordinal),
            $"Delivery artifact '{relativePath}' contains forbidden marker '{forbidden}'.");
    }
}
