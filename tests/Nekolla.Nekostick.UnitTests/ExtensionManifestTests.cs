using System.Reflection;
using System.Text;
using System.Text.Json;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Extensions;
using Nekolla.Nekostick.Tests.Fixtures.Extension;
using Xunit;

namespace Nekolla.Nekostick.UnitTests;

public sealed class ExtensionManifestTests
{
    [Fact]
    public void JsonAndYamlNormalizeToTheSameManifestSemantics()
    {
        using var json = TestExtensionDirectory.CreateJson(ManifestJsonFor("valid-with-dependency"));
        using var yaml = TestExtensionDirectory.CreateYaml(ManifestYamlFor("valid-with-dependency"));

        var jsonResult = ExtensionManifestDiscovery.Discover(json.RootPath);
        var yamlResult = ExtensionManifestDiscovery.Discover(yaml.RootPath);

        Assert.True(jsonResult.Succeeded);
        Assert.True(yamlResult.Succeeded);
        Assert.Equal(ManifestSourceFormat.Json, jsonResult.SourceFormat);
        Assert.Equal(ManifestSourceFormat.Yaml, yamlResult.SourceFormat);
        Assert.NotNull(jsonResult.Manifest);
        Assert.NotNull(yamlResult.Manifest);
        Assert.Equal(jsonResult.Manifest!.SchemaVersion, yamlResult.Manifest!.SchemaVersion);
        Assert.Equal(jsonResult.Manifest.Id, yamlResult.Manifest.Id);
        Assert.Equal(jsonResult.Manifest.Version, yamlResult.Manifest.Version);
        Assert.Equal(jsonResult.Manifest.EntryAssembly, yamlResult.Manifest.EntryAssembly);
        Assert.Equal(jsonResult.Manifest.EntryType, yamlResult.Manifest.EntryType);
        Assert.Equal(">=1.0.0", jsonResult.Manifest.RequiredHostApiVersion.Expression);
        Assert.Equal(
            jsonResult.Manifest.RequiredHostApiVersion.Expression,
            yamlResult.Manifest.RequiredHostApiVersion.Expression);
        Assert.Equal(jsonResult.Manifest.Dependencies.Length, yamlResult.Manifest.Dependencies.Length);
        Assert.Equal(jsonResult.Manifest.Dependencies[0].Id, yamlResult.Manifest.Dependencies[0].Id);
        Assert.Equal(
            jsonResult.Manifest.Dependencies[0].VersionRange.Expression,
            yamlResult.Manifest.Dependencies[0].VersionRange.Expression);
    }

    [Theory]
    [InlineData("unknown-field", ExtensionFailureCode.UnknownManifestField)]
    [InlineData("duplicate-field", ExtensionFailureCode.DuplicateManifestField)]
    [InlineData("malformed", ExtensionFailureCode.JsonInvalid)]
    [InlineData("nonscalar", ExtensionFailureCode.ManifestSchemaInvalid)]
    [InlineData("unsafe-path", ExtensionFailureCode.UnsafePath)]
    [InlineData("invalid-version", ExtensionFailureCode.InvalidVersion)]
    [InlineData("invalid-host-range", ExtensionFailureCode.InvalidVersionRange)]
    [InlineData("invalid-dependency-id", ExtensionFailureCode.InvalidIdentifier)]
    [InlineData("invalid-dependency-range", ExtensionFailureCode.InvalidVersionRange)]
    [InlineData("duplicate-dependency", ExtensionFailureCode.DuplicateExtensionId)]
    public void StrictJsonRejectsMalformedAndUnsafeManifestShapes(
        string scenario,
        ExtensionFailureCode expected)
    {
        using var fixture = TestExtensionDirectory.CreateJson(ManifestJsonFor(scenario));

        var result = ExtensionManifestDiscovery.Discover(fixture.RootPath);

        Assert.False(result.Succeeded);
        Assert.Equal(expected, result.FailureCode);
    }

    [Theory]
    [InlineData("unknown-field", ExtensionFailureCode.UnknownManifestField)]
    [InlineData("duplicate-field", ExtensionFailureCode.DuplicateManifestField)]
    [InlineData("malformed", ExtensionFailureCode.YamlInvalid)]
    [InlineData("nonscalar", ExtensionFailureCode.ManifestSchemaInvalid)]
    [InlineData("unsafe-path", ExtensionFailureCode.UnsafePath)]
    [InlineData("invalid-version", ExtensionFailureCode.InvalidVersion)]
    [InlineData("invalid-host-range", ExtensionFailureCode.InvalidVersionRange)]
    [InlineData("invalid-dependency-id", ExtensionFailureCode.InvalidIdentifier)]
    [InlineData("invalid-dependency-range", ExtensionFailureCode.InvalidVersionRange)]
    [InlineData("duplicate-dependency", ExtensionFailureCode.DuplicateExtensionId)]
    public void SafeYamlRejectsMalformedAndUnsafeManifestShapes(
        string scenario,
        ExtensionFailureCode expected)
    {
        using var fixture = TestExtensionDirectory.CreateYaml(ManifestYamlFor(scenario));

        var result = ExtensionManifestDiscovery.Discover(fixture.RootPath);

        Assert.False(result.Succeeded);
        Assert.Equal(expected, result.FailureCode);
    }

    [Theory]
    [InlineData("id: &named fixture.extension.deterministic\n", ExtensionFailureCode.YamlInvalid)]
    [InlineData("id: *named\n", ExtensionFailureCode.YamlInvalid)]
    [InlineData("id: !custom fixture.extension.deterministic\n", ExtensionFailureCode.YamlInvalid)]
    public void SafeYamlRejectsAliasesAnchorsAndTags(
        string idLine,
        ExtensionFailureCode expected)
    {
        var yaml = ManifestYamlFor("valid").Replace(
            "id: fixture.extension.deterministic\n",
            idLine,
            StringComparison.Ordinal);
        using var fixture = TestExtensionDirectory.CreateYaml(yaml);

        var result = ExtensionManifestDiscovery.Discover(fixture.RootPath);

        Assert.False(result.Succeeded);
        Assert.Equal(expected, result.FailureCode);
    }

    [Fact]
    public void JsonAndYamlDepthAndSizeBoundariesAreRejected()
    {
        var deepJson = "[" + new string('[', 40) + "0" + new string(']', 40) + "]";
        using var deepJsonFixture = TestExtensionDirectory.CreateJson(
            ManifestJsonFor("valid").Replace(
                "\"dependencies\": []",
                "\"dependencies\": " + deepJson,
                StringComparison.Ordinal));
        var deepJsonResult = ExtensionManifestDiscovery.Discover(deepJsonFixture.RootPath);

        var deepYaml = "id: " + string.Concat(Enumerable.Repeat("[", 40)) +
            "fixture.extension.deterministic" + string.Concat(Enumerable.Repeat("]", 40)) + "\n";
        using var deepYamlFixture = TestExtensionDirectory.CreateYaml(
            ManifestYamlFor("valid").Replace(
                "id: fixture.extension.deterministic\n",
                deepYaml,
                StringComparison.Ordinal));
        var deepYamlResult = ExtensionManifestDiscovery.Discover(deepYamlFixture.RootPath);
        var oversizedJson = ManifestJsonFor("valid") + new string(' ', 1024 * 1024);
        using var oversizedJsonFixture = TestExtensionDirectory.CreateJson(oversizedJson);
        var oversizedJsonResult = ExtensionManifestDiscovery.Discover(oversizedJsonFixture.RootPath);

        var oversizedYaml = ManifestYamlFor("valid") + new string(' ', 1024 * 1024);
        using var oversizedYamlFixture = TestExtensionDirectory.CreateYaml(oversizedYaml);
        var oversizedYamlResult = ExtensionManifestDiscovery.Discover(oversizedYamlFixture.RootPath);

        Assert.False(deepJsonResult.Succeeded);
        Assert.Equal(ExtensionFailureCode.JsonInvalid, deepJsonResult.FailureCode);
        Assert.False(deepYamlResult.Succeeded);
        Assert.Equal(ExtensionFailureCode.YamlInvalid, deepYamlResult.FailureCode);
        Assert.False(oversizedJsonResult.Succeeded);
        Assert.Equal(ExtensionFailureCode.JsonInvalid, oversizedJsonResult.FailureCode);
        Assert.False(oversizedYamlResult.Succeeded);
        Assert.Equal(ExtensionFailureCode.YamlInvalid, oversizedYamlResult.FailureCode);
    }

    [Fact]
    public void DiscoveryIsExplicitAndRequiresExactlyOneSupportedManifest()
    {
        using var missing = TestExtensionDirectory.CreateWithoutManifest();
        var missingResult = ExtensionManifestDiscovery.Discover(missing.RootPath);
        Assert.False(missingResult.Succeeded);
        Assert.Equal(ExtensionFailureCode.ManifestMissing, missingResult.FailureCode);

        File.WriteAllText(
            Path.Combine(missing.RootPath, "manifest.txt"),
            ManifestJsonFor("valid"));
        var unsupportedResult = ExtensionManifestDiscovery.Discover(missing.RootPath);
        Assert.False(unsupportedResult.Succeeded);
        Assert.Equal(ExtensionFailureCode.ManifestMissing, unsupportedResult.FailureCode);

        File.WriteAllText(
            Path.Combine(missing.RootPath, "manifest.json"),
            ManifestJsonFor("valid"));
        var explicitResult = ExtensionManifestDiscovery.Discover(missing.RootPath);
        Assert.True(explicitResult.Succeeded);

        File.WriteAllText(
            Path.Combine(missing.RootPath, "manifest.yaml"),
            ManifestYamlFor("valid"));
        var duplicateResult = ExtensionManifestDiscovery.Discover(missing.RootPath);
        Assert.False(duplicateResult.Succeeded);
        Assert.Equal(ExtensionFailureCode.DuplicateManifest, duplicateResult.FailureCode);

        var discoverMethods = typeof(ExtensionManifestDiscovery)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Select(method => method.Name)
            .ToArray();
        Assert.Equal(["Discover"], discoverMethods);
    }

    [Fact]
    public void FixtureBindsOnlyToThePinnedContractsAssemblyAndPublicAbi()
    {
        var fixtureAssembly = typeof(FixtureEntrypoint).Assembly;
        var contractsReference = fixtureAssembly
            .GetReferencedAssemblies()
            .Single(reference => string.Equals(
                reference.Name,
                typeof(IExtensionEntrypoint).Assembly.GetName().Name,
                StringComparison.Ordinal));

        Assert.Equal(typeof(IExtensionEntrypoint).Assembly.GetName().Name, contractsReference.Name);
        Assert.Equal(typeof(IExtensionEntrypoint).Assembly.GetName().Version, contractsReference.Version);
        Assert.Equal(
            typeof(IExtensionEntrypoint).Assembly.GetName().GetPublicKeyToken(),
            contractsReference.GetPublicKeyToken());
        Assert.True(typeof(IExtensionEntrypoint).IsAssignableFrom(typeof(FixtureEntrypoint)));
        Assert.Equal(
            ["ApiVersion", "Configuration", "Events", "Logger", "Status", "Tasks"],
            typeof(IExtensionHostBridge).GetProperties()
                .Select(property => property.Name)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray());
        Assert.Equal(
            ["Host", "Registration", "Reloading"],
            typeof(IExtensionStartContext).GetProperties()
                .Select(property => property.Name)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray());
        Assert.DoesNotContain(
            fixtureAssembly.GetReferencedAssemblies(),
            reference => reference.Name is "Nekolla.Nekostick.Host" or "Nekolla.Nekostick.Persistence");
    }

    private static string ManifestJsonFor(string scenario) => scenario switch
    {
        "unknown-field" => ManifestJson(extra: ",\n  " + "\"unknown\": true"),
        "duplicate-field" => ManifestJson(duplicateId: true),
        "malformed" => "{\"schemaVersion\":1,",
        "nonscalar" => ManifestJson(id: "[]"),
        "unsafe-path" => ManifestJson(entryAssembly: "\"../outside.dll\""),
        "invalid-version" => ManifestJson(version: "\"1\""),
        "invalid-host-range" => ManifestJson(hostApiVersion: "\"not-a-range\""),
        "invalid-dependency-id" => ManifestJson(dependencies: "[{\"id\":\"bad id\",\"versionRange\":\">=1.0.0\"}]"),
        "invalid-dependency-range" => ManifestJson(dependencies: "[{\"id\":\"other.extension\",\"versionRange\":\"bad\"}]"),
        "duplicate-dependency" => ManifestJson(dependencies: "[{\"id\":\"other.extension\",\"versionRange\":\">=1.0.0\"},{\"id\":\"other.extension\",\"versionRange\":\">=1.0.0\"}]"),
        "valid-with-dependency" => ManifestJson(dependencies: "[{\"id\":\"other.extension\",\"versionRange\":\">=1.0.0\"}]"),
        _ => ManifestJson()
    };

    private static string ManifestJson(
        string id = "\"fixture.extension.deterministic\"",
        string version = "\"1.0.0\"",
        string entryAssembly = "\"Fixtures.Extension.dll\"",
        string entryType = "\"Nekolla.Nekostick.Tests.Fixtures.Extension.FixtureEntrypoint\"",
        string hostApiVersion = "\">=1.0.0\"",
        string dependencies = "[]",
        string extra = "",
        bool duplicateId = false)
    {
        var duplicate = duplicateId ? ",\n  \"id\": \"fixture.extension.deterministic\"" : string.Empty;
        return $"{{\n  \"schemaVersion\": 1,\n  \"id\": {id},\n  \"version\": {version},\n  \"entryAssembly\": {entryAssembly},\n  \"entryType\": {entryType},\n  \"dependencies\": {dependencies},\n  \"requiredHostApiVersion\": {hostApiVersion}{duplicate}{extra}\n}}";
    }
    private static string ManifestYamlFor(string scenario) => scenario switch
    {
        "unknown-field" => ManifestYamlFor("valid") + "unknown: true\n",
        "duplicate-field" => ManifestYamlFor("valid") + "id: duplicate.extension\n",
        "malformed" => "schemaVersion: [\n",
        "nonscalar" => ManifestYamlFor("valid").Replace(
            "id: fixture.extension.deterministic\n",
            "id: []\n",
            StringComparison.Ordinal),
        "unsafe-path" => ManifestYamlFor("valid").Replace(
            "entryAssembly: Fixtures.Extension.dll\n",
            "entryAssembly: ../outside.dll\n",
            StringComparison.Ordinal),
        "invalid-version" => ManifestYamlFor("valid").Replace(
            "version: 1.0.0\n",
            "version: 1\n",
            StringComparison.Ordinal),
        "invalid-host-range" => ManifestYamlFor("valid").Replace(
            "requiredHostApiVersion: \">=1.0.0\"\n",
            "requiredHostApiVersion: not-a-range\n",
            StringComparison.Ordinal),
        "invalid-dependency-id" => ManifestYamlFor("valid").Replace(
            "dependencies: []\n",
            "dependencies:\n  - id: bad id\n    versionRange: \">=1.0.0\"\n",
            StringComparison.Ordinal),
        "invalid-dependency-range" => ManifestYamlFor("valid").Replace(
            "dependencies: []\n",
            "dependencies:\n  - id: other.extension\n    versionRange: bad\n",
            StringComparison.Ordinal),
        "duplicate-dependency" => ManifestYamlFor("valid").Replace(
            "dependencies: []\n",
            "dependencies:\n  - id: other.extension\n    versionRange: \">=1.0.0\"\n  - id: other.extension\n    versionRange: \">=1.0.0\"\n",
            StringComparison.Ordinal),
        "valid-with-dependency" => ManifestYamlFor("valid").Replace(
            "dependencies: []\n",
            "dependencies:\n  - id: other.extension\n    versionRange: \">=1.0.0\"\n",
            StringComparison.Ordinal),
        _ => "schemaVersion: 1\nid: fixture.extension.deterministic\nversion: 1.0.0\nentryAssembly: Fixtures.Extension.dll\nentryType: Nekolla.Nekostick.Tests.Fixtures.Extension.FixtureEntrypoint\ndependencies: []\nrequiredHostApiVersion: \">=1.0.0\"\n"
    };
}

internal sealed class TestExtensionDirectory : IDisposable
{
    private TestExtensionDirectory(string rootPath)
    {
        RootPath = rootPath;
    }

    internal string RootPath { get; }

    internal static TestExtensionDirectory CreateJson(string? manifest = null)
    {
        var directory = CreateRoot();
        directory.StageFixtureAssets();
        File.WriteAllText(
            Path.Combine(directory.RootPath, "manifest.json"),
            manifest ?? ExtensionManifestTestDefaults.Json);
        return directory;
    }

    internal static TestExtensionDirectory CreateYaml(string? manifest = null)
    {
        var directory = CreateRoot();
        directory.StageFixtureAssets();
        File.WriteAllText(
            Path.Combine(directory.RootPath, "manifest.yaml"),
            manifest ?? ExtensionManifestTestDefaults.Yaml);
        return directory;
    }

    internal static TestExtensionDirectory CreateWithoutManifest()
    {
        var directory = CreateRoot();
        directory.StageFixtureAssets();
        return directory;
    }

    public void Dispose()
    {
        if (Directory.Exists(RootPath))
        {
            Directory.Delete(RootPath, recursive: true);
        }
    }

    private void StageFixtureAssets()
    {
        var fixtureAssembly = typeof(FixtureEntrypoint).Assembly;
        File.Copy(
            fixtureAssembly.Location,
            Path.Combine(RootPath, "Fixtures.Extension.dll"));
        File.Copy(
            typeof(IExtensionEntrypoint).Assembly.Location,
            Path.Combine(RootPath, "Nekolla.Nekostick.Contracts.dll"));
    }

    private static TestExtensionDirectory CreateRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "nekostick-extension-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return new TestExtensionDirectory(root);
    }
}

internal static class ExtensionManifestTestDefaults
{
    internal const string Json = "{\n  \"schemaVersion\": 1,\n  \"id\": \"fixture.extension.deterministic\",\n  \"version\": \"1.0.0\",\n  \"entryAssembly\": \"Fixtures.Extension.dll\",\n  \"entryType\": \"Nekolla.Nekostick.Tests.Fixtures.Extension.FixtureEntrypoint\",\n  \"dependencies\": [],\n  \"requiredHostApiVersion\": \">=1.0.0\"\n}";

    internal const string Yaml = "schemaVersion: 1\nid: fixture.extension.deterministic\nversion: 1.0.0\nentryAssembly: Fixtures.Extension.dll\nentryType: Nekolla.Nekostick.Tests.Fixtures.Extension.FixtureEntrypoint\ndependencies: []\nrequiredHostApiVersion: \">=1.0.0\"\n";
}

