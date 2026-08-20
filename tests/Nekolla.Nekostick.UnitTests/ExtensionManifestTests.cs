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
    [Fact]
    public void OptionalContractDeclarationsDefaultAndStrictlyNormalizeAcrossJsonAndYaml()
    {
        using var defaultJson = TestExtensionDirectory.CreateJson(ManifestJsonFor("valid"));
        using var defaultYaml = TestExtensionDirectory.CreateYaml(ManifestYamlFor("valid"));
        var jsonDefaults = ExtensionManifestDiscovery.Discover(defaultJson.RootPath);
        var yamlDefaults = ExtensionManifestDiscovery.Discover(defaultYaml.RootPath);

        Assert.Empty(jsonDefaults.Manifest!.Exports);
        Assert.Empty(jsonDefaults.Manifest.Imports);
        Assert.Empty(yamlDefaults.Manifest!.Exports);
        Assert.Empty(yamlDefaults.Manifest.Imports);

        const string assembly = "Shared.Contracts, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null";
        const string type = "Shared.Contracts.ILogger";
        var json = ManifestJson(extra:
            ",\n  \"exports\": [{\"contractId\": \"fixture.logger\", \"version\": \"1.0.0\", \"assemblyIdentity\": \"" +
            assembly + "\", \"typeIdentity\": \"" + type + "\"}],\n" +
            "  \"imports\": [{\"contractId\": \"fixture.logger\", \"versionRange\": \">=1.0.0\", \"assemblyIdentity\": \"" +
            assembly + "\", \"typeIdentity\": \"" + type + "\"}]");
        var yaml = ManifestYamlFor("valid") +
            "exports:\n  - contractId: fixture.logger\n    version: 1.0.0\n    assemblyIdentity: \"" + assembly + "\"\n    typeIdentity: " + type + "\n" +
            "imports:\n  - contractId: fixture.logger\n    versionRange: \">=1.0.0\"\n    assemblyIdentity: \"" + assembly + "\"\n    typeIdentity: " + type + "\n";
        using var contractJson = TestExtensionDirectory.CreateJson(json);
        using var contractYaml = TestExtensionDirectory.CreateYaml(yaml);
        var jsonContracts = ExtensionManifestDiscovery.Discover(contractJson.RootPath);
        var yamlContracts = ExtensionManifestDiscovery.Discover(contractYaml.RootPath);

        Assert.True(jsonContracts.Succeeded, jsonContracts.FailureCode.ToString());
        Assert.True(yamlContracts.Succeeded, yamlContracts.FailureCode.ToString());
        Assert.Equal("fixture.logger", jsonContracts.Manifest!.Exports.Single().ContractId);
        Assert.Equal(">=1.0.0", yamlContracts.Manifest!.Imports.Single().VersionRange.Expression);

        using var malformed = TestExtensionDirectory.CreateJson(
            ManifestJson(extra: ",\n  \"exports\": {\"contractId\": \"fixture.logger\"}"));
        var malformedResult = ExtensionManifestDiscovery.Discover(malformed.RootPath);
        Assert.False(malformedResult.Succeeded);
        Assert.Equal(ExtensionFailureCode.ManifestSchemaInvalid, malformedResult.FailureCode);
    }
    [Fact]
    public void ContractGraphRejectsMissingVersionAndIdentityProviders()
    {
        const string assembly = "Shared.Contracts, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null";
        const string type = "Shared.Contracts.ILogger";
        var assemblyJson = JsonSerializer.Serialize(assembly);
        var typeJson = JsonSerializer.Serialize(type);
        var export = $",\n  \"exports\": [{{\"contractId\": \"shared.logger\", \"version\": \"1.0.0\", \"assemblyIdentity\": {assemblyJson}, \"typeIdentity\": {typeJson}}}]";
        var import = $",\n  \"imports\": [{{\"contractId\": \"shared.logger\", \"versionRange\": \">=1.0.0\", \"assemblyIdentity\": {assemblyJson}, \"typeIdentity\": {typeJson}}}]";
        using var providerDirectory = TestExtensionDirectory.CreateJson(ManifestJson(id: "\"provider.extension\"", extra: export));
        using var consumerDirectory = TestExtensionDirectory.CreateJson(ManifestJson(id: "\"consumer.extension\"", extra: import));
        var providerResult = ExtensionManifestDiscovery.Discover(providerDirectory.RootPath);
        var consumerResult = ExtensionManifestDiscovery.Discover(consumerDirectory.RootPath);
        Assert.True(providerResult.Succeeded, providerResult.FailureCode.ToString());
        Assert.True(consumerResult.Succeeded, consumerResult.FailureCode.ToString());
        var provider = providerResult.Manifest!;
        var consumer = consumerResult.Manifest!;

        var valid = ExtensionManifestGraph.ValidateAndOrder(
            [consumer, provider],
            new SemVersion(1, 0, 0));
        Assert.True(valid.Succeeded, valid.FailureCode.ToString());
        Assert.Equal(["provider.extension", "consumer.extension"], valid.OrderedManifests.Select(item => item.Id));

        var missing = ExtensionManifestGraph.ValidateAndOrder([consumer], new SemVersion(1, 0, 0));
        Assert.Equal(ExtensionFailureCode.MissingContractProvider, missing.FailureCode);

        using var incompatibleDirectory = TestExtensionDirectory.CreateJson(
            ManifestJson(id: "\"incompatible.extension\"", extra: import.Replace(">=1.0.0", ">=2.0.0", StringComparison.Ordinal)));
        var incompatibleResult = ExtensionManifestDiscovery.Discover(incompatibleDirectory.RootPath);
        Assert.True(incompatibleResult.Succeeded, incompatibleResult.FailureCode.ToString());
        var incompatible = incompatibleResult.Manifest!;
        Assert.Equal(
            ExtensionFailureCode.ContractVersionIncompatible,
            ExtensionManifestGraph.ValidateAndOrder([provider, incompatible], new SemVersion(1, 0, 0)).FailureCode);

        using var identityDirectory = TestExtensionDirectory.CreateJson(
            ManifestJson(id: "\"identity.extension\"", extra: import.Replace(type, "Shared.Contracts.IOther", StringComparison.Ordinal)));
        var identityResult = ExtensionManifestDiscovery.Discover(identityDirectory.RootPath);
        Assert.True(identityResult.Succeeded, identityResult.FailureCode.ToString());
        var identity = identityResult.Manifest!;
        Assert.Equal(
            ExtensionFailureCode.ContractIdentityMismatch,
            ExtensionManifestGraph.ValidateAndOrder([provider, identity], new SemVersion(1, 0, 0)).FailureCode);
    }

    [Fact]
    public void ContractGraphRejectsContractInducedCycle()
    {
        const string assembly = "Shared.Contracts, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null";
        const string type = "Shared.Contracts.ILogger";
        var assemblyJson = JsonSerializer.Serialize(assembly);
        var typeJson = JsonSerializer.Serialize(type);
        var alphaExtra = $",\n  \"exports\": [{{\"contractId\": \"alpha.contract\", \"version\": \"1.0.0\", \"assemblyIdentity\": {assemblyJson}, \"typeIdentity\": {typeJson}}}],\n  \"imports\": [{{\"contractId\": \"beta.contract\", \"versionRange\": \">=1.0.0\", \"assemblyIdentity\": {assemblyJson}, \"typeIdentity\": {typeJson}}}]";
        var betaExtra = $",\n  \"exports\": [{{\"contractId\": \"beta.contract\", \"version\": \"1.0.0\", \"assemblyIdentity\": {assemblyJson}, \"typeIdentity\": {typeJson}}}],\n  \"imports\": [{{\"contractId\": \"alpha.contract\", \"versionRange\": \">=1.0.0\", \"assemblyIdentity\": {assemblyJson}, \"typeIdentity\": {typeJson}}}]";
        using var alphaDirectory = TestExtensionDirectory.CreateJson(
            ManifestJson(id: "\"alpha.extension\"", extra: alphaExtra));
        using var betaDirectory = TestExtensionDirectory.CreateJson(
            ManifestJson(id: "\"beta.extension\"", extra: betaExtra));

        var alphaResult = ExtensionManifestDiscovery.Discover(alphaDirectory.RootPath);
        var betaResult = ExtensionManifestDiscovery.Discover(betaDirectory.RootPath);
        Assert.True(alphaResult.Succeeded, alphaResult.FailureCode.ToString());
        Assert.True(betaResult.Succeeded, betaResult.FailureCode.ToString());

        var result = ExtensionManifestGraph.ValidateAndOrder(
            [alphaResult.Manifest!, betaResult.Manifest!],
            new SemVersion(1, 0, 0));

        Assert.False(result.Succeeded);
        Assert.Equal(ExtensionFailureCode.DependencyCycle, result.FailureCode);
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
            ["ApiVersion", "Configuration", "ConfigurationApi", "Contracts", "Endpoints", "Events", "FullConfiguration", "Lifecycle", "Logger", "Routes", "Services", "Status", "Tasks"],
            typeof(IExtensionHostBridge).GetProperties()
                .Select(property => property.Name)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray());
        Assert.Equal(
            ["Contracts", "Host", "Registration", "Reloading"],
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
        File.Copy(
            GetKnownOutputAssemblyPath(typeof(FixtureEntrypoint).Assembly),
            Path.Combine(RootPath, "Fixtures.Extension.dll"));
        File.Copy(
            GetKnownOutputAssemblyPath(typeof(IExtensionEntrypoint).Assembly),
            Path.Combine(RootPath, "Nekolla.Nekostick.Contracts.dll"));
    }

    private static string GetKnownOutputAssemblyPath(Assembly assembly)
    {
        var name = assembly.GetName().Name;
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException("The fixture assembly name is unavailable.");
        }

        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, name + ".dll"));
        if (!File.Exists(path))
        {
            throw new InvalidOperationException("The fixture assembly is not present in the test output.");
        }

        var actual = AssemblyName.GetAssemblyName(path);
        if (!string.Equals(actual.FullName, assembly.GetName().FullName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The fixture assembly identity is not the expected output assembly.");
        }

        return path;
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

