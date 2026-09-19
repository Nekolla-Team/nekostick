using System.Collections.Immutable;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Extensions;
using Xunit;

namespace Nekolla.Nekostick.UnitTests;

public sealed class ExtensionOptionalDependencyTests
{
    private const string SharedAssembly = "Shared.Contracts, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null";
    private static readonly string ContractType = typeof(IExtensionLogger).FullName!;

    [Fact]
    public void OptionalFlagsParseAcrossJsonAndYamlAndDefaultToFalse()
    {
        using var defaultJson = TestExtensionDirectory.CreateJson(Json(
            dependencies: "[{\"id\":\"other.extension\",\"versionRange\":\">=1.0.0\"}]"));
        using var defaultYaml = TestExtensionDirectory.CreateYaml(Yaml(
            dependencies: "dependencies:\n  - id: other.extension\n    versionRange: \">=1.0.0\"\n"));
        var jsonDefault = ExtensionManifestDiscovery.Discover(defaultJson.RootPath);
        var yamlDefault = ExtensionManifestDiscovery.Discover(defaultYaml.RootPath);
        Assert.True(jsonDefault.Succeeded, jsonDefault.FailureCode.ToString());
        Assert.True(yamlDefault.Succeeded, yamlDefault.FailureCode.ToString());
        Assert.False(jsonDefault.Manifest!.Dependencies[0].Optional);
        Assert.False(yamlDefault.Manifest!.Dependencies[0].Optional);

        using var optionalJson = TestExtensionDirectory.CreateJson(Json(
            dependencies: "[{\"id\":\"other.extension\",\"versionRange\":\">=1.0.0\",\"optional\":true}]"));
        using var optionalYaml = TestExtensionDirectory.CreateYaml(Yaml(
            dependencies: "dependencies:\n  - id: other.extension\n    versionRange: \">=1.0.0\"\n    optional: true\n"));
        var jsonOptional = ExtensionManifestDiscovery.Discover(optionalJson.RootPath);
        var yamlOptional = ExtensionManifestDiscovery.Discover(optionalYaml.RootPath);
        Assert.True(jsonOptional.Succeeded, jsonOptional.FailureCode.ToString());
        Assert.True(yamlOptional.Succeeded, yamlOptional.FailureCode.ToString());
        Assert.True(jsonOptional.Manifest!.Dependencies[0].Optional);
        Assert.True(yamlOptional.Manifest!.Dependencies[0].Optional);

        var importExtra = ",\n  \"imports\": [{\"contractId\": \"shared.logger\", \"versionRange\": \">=1.0.0\", \"assemblyIdentity\": \"" +
            SharedAssembly + "\", \"typeIdentity\": \"" + ContractType + "\", \"optional\": true}]";
        using var importJson = TestExtensionDirectory.CreateJson(Json(extra: importExtra));
        using var importYaml = TestExtensionDirectory.CreateYaml(Yaml(extra:
            "imports:\n  - contractId: shared.logger\n    versionRange: \">=1.0.0\"\n    assemblyIdentity: \"" +
            SharedAssembly + "\"\n    typeIdentity: " + ContractType + "\n    optional: true\n"));
        var jsonImport = ExtensionManifestDiscovery.Discover(importJson.RootPath);
        var yamlImport = ExtensionManifestDiscovery.Discover(importYaml.RootPath);
        Assert.True(jsonImport.Succeeded, jsonImport.FailureCode.ToString());
        Assert.True(yamlImport.Succeeded, yamlImport.FailureCode.ToString());
        Assert.True(jsonImport.Manifest!.Imports[0].Optional);
        Assert.True(yamlImport.Manifest!.Imports[0].Optional);
    }

    [Fact]
    public void UnknownFieldsInsideDependencyAndImportDeclarationsAreRejected()
    {
        using var dependencyJson = TestExtensionDirectory.CreateJson(Json(
            dependencies: "[{\"id\":\"other.extension\",\"versionRange\":\">=1.0.0\",\"bogus\":1}]"));
        var dependencyJsonResult = ExtensionManifestDiscovery.Discover(dependencyJson.RootPath);
        Assert.False(dependencyJsonResult.Succeeded);
        Assert.Equal(ExtensionFailureCode.UnknownManifestField, dependencyJsonResult.FailureCode);

        using var dependencyYaml = TestExtensionDirectory.CreateYaml(Yaml(
            dependencies: "dependencies:\n  - id: other.extension\n    versionRange: \">=1.0.0\"\n    bogus: 1\n"));
        var dependencyYamlResult = ExtensionManifestDiscovery.Discover(dependencyYaml.RootPath);
        Assert.False(dependencyYamlResult.Succeeded);
        Assert.Equal(ExtensionFailureCode.UnknownManifestField, dependencyYamlResult.FailureCode);

        var importExtra = ",\n  \"imports\": [{\"contractId\": \"shared.logger\", \"versionRange\": \">=1.0.0\", \"assemblyIdentity\": \"" +
            SharedAssembly + "\", \"typeIdentity\": \"" + ContractType + "\", \"bogus\": 1}]";
        using var importJson = TestExtensionDirectory.CreateJson(Json(extra: importExtra));
        var importJsonResult = ExtensionManifestDiscovery.Discover(importJson.RootPath);
        Assert.False(importJsonResult.Succeeded);
        Assert.Equal(ExtensionFailureCode.UnknownManifestField, importJsonResult.FailureCode);

        using var importYaml = TestExtensionDirectory.CreateYaml(Yaml(extra:
            "imports:\n  - contractId: shared.logger\n    versionRange: \">=1.0.0\"\n    assemblyIdentity: \"" +
            SharedAssembly + "\"\n    typeIdentity: " + ContractType + "\n    bogus: 1\n"));
        var importYamlResult = ExtensionManifestDiscovery.Discover(importYaml.RootPath);
        Assert.False(importYamlResult.Succeeded);
        Assert.Equal(ExtensionFailureCode.UnknownManifestField, importYamlResult.FailureCode);
    }

    [Fact]
    public void NonBooleanOptionalValuesAreRejected()
    {
        using var json = TestExtensionDirectory.CreateJson(Json(
            dependencies: "[{\"id\":\"other.extension\",\"versionRange\":\">=1.0.0\",\"optional\":\"yes\"}]"));
        var jsonResult = ExtensionManifestDiscovery.Discover(json.RootPath);
        Assert.False(jsonResult.Succeeded);
        Assert.Equal(ExtensionFailureCode.ManifestSchemaInvalid, jsonResult.FailureCode);

        using var yaml = TestExtensionDirectory.CreateYaml(Yaml(
            dependencies: "dependencies:\n  - id: other.extension\n    versionRange: \">=1.0.0\"\n    optional: yes\n"));
        var yamlResult = ExtensionManifestDiscovery.Discover(yaml.RootPath);
        Assert.False(yamlResult.Succeeded);
        Assert.Equal(ExtensionFailureCode.ManifestSchemaInvalid, yamlResult.FailureCode);
    }

    [Fact]
    public void GraphSkipsUnsatisfiedOptionalDependencies()
    {
        using var consumerDirectory = TestExtensionDirectory.CreateJson(Json(
            id: "consumer.extension",
            dependencies: "[{\"id\":\"missing.extension\",\"versionRange\":\">=1.0.0\",\"optional\":true}]"));
        var consumer = Discover(consumerDirectory);

        var missingOptional = ExtensionManifestGraph.ValidateAndOrder([consumer], new SemVersion(1, 0, 0));
        Assert.True(missingOptional.Succeeded, missingOptional.FailureCode.ToString());

        using var providerDirectory = TestExtensionDirectory.CreateJson(Json(id: "provider.extension"));
        var provider = Discover(providerDirectory);
        using var staleConsumerDirectory = TestExtensionDirectory.CreateJson(Json(
            id: "stale.consumer",
            dependencies: "[{\"id\":\"provider.extension\",\"versionRange\":\">=2.0.0\",\"optional\":true}]"));
        var staleConsumer = Discover(staleConsumerDirectory);

        var versionMismatch = ExtensionManifestGraph.ValidateAndOrder(
            [staleConsumer, provider],
            new SemVersion(1, 0, 0));
        Assert.True(versionMismatch.Succeeded, versionMismatch.FailureCode.ToString());

        using var requiredDirectory = TestExtensionDirectory.CreateJson(Json(
            id: "required.consumer",
            dependencies: "[{\"id\":\"missing.extension\",\"versionRange\":\">=1.0.0\"}]"));
        var required = Discover(requiredDirectory);
        Assert.Equal(
            ExtensionFailureCode.MissingDependency,
            ExtensionManifestGraph.ValidateAndOrder([required], new SemVersion(1, 0, 0)).FailureCode);
    }

    [Fact]
    public void GraphSkipsUnsatisfiedOptionalImportsButNotIdentityConflicts()
    {
        var import = ",\n  \"imports\": [{\"contractId\": \"shared.logger\", \"versionRange\": \">=1.0.0\", \"assemblyIdentity\": \"" +
            SharedAssembly + "\", \"typeIdentity\": \"" + ContractType + "\", \"optional\": true}]";
        using var consumerDirectory = TestExtensionDirectory.CreateJson(Json(id: "consumer.extension", extra: import));
        var consumer = Discover(consumerDirectory);

        var missingProvider = ExtensionManifestGraph.ValidateAndOrder([consumer], new SemVersion(1, 0, 0));
        Assert.True(missingProvider.Succeeded, missingProvider.FailureCode.ToString());

        var export = ",\n  \"exports\": [{\"contractId\": \"shared.logger\", \"version\": \"1.0.0\", \"assemblyIdentity\": \"" +
            SharedAssembly + "\", \"typeIdentity\": \"" + ContractType + "\"}]";
        using var providerDirectory = TestExtensionDirectory.CreateJson(Json(id: "provider.extension", extra: export));
        var provider = Discover(providerDirectory);
        using var incompatibleDirectory = TestExtensionDirectory.CreateJson(Json(
            id: "incompatible.consumer",
            extra: import.Replace(">=1.0.0", ">=2.0.0", StringComparison.Ordinal)));
        var incompatible = Discover(incompatibleDirectory);

        var versionMismatch = ExtensionManifestGraph.ValidateAndOrder(
            [provider, incompatible],
            new SemVersion(1, 0, 0));
        Assert.True(versionMismatch.Succeeded, versionMismatch.FailureCode.ToString());

        using var identityDirectory = TestExtensionDirectory.CreateJson(Json(
            id: "identity.consumer",
            extra: import.Replace(ContractType, "Shared.Contracts.IOther", StringComparison.Ordinal)));
        var identity = Discover(identityDirectory);
        Assert.Equal(
            ExtensionFailureCode.ContractIdentityMismatch,
            ExtensionManifestGraph.ValidateAndOrder([provider, identity], new SemVersion(1, 0, 0)).FailureCode);
    }

    [Fact]
    public void GraphOrdersProvidersOnlyForSatisfiedOptionalImports()
    {
        var export = ",\n  \"exports\": [{\"contractId\": \"shared.logger\", \"version\": \"1.0.0\", \"assemblyIdentity\": \"" +
            SharedAssembly + "\", \"typeIdentity\": \"" + ContractType + "\"}]";
        using var providerDirectory = TestExtensionDirectory.CreateJson(Json(id: "beta.extension", extra: export));
        var provider = Discover(providerDirectory);

        var satisfiedImport = ",\n  \"imports\": [{\"contractId\": \"shared.logger\", \"versionRange\": \">=1.0.0\", \"assemblyIdentity\": \"" +
            SharedAssembly + "\", \"typeIdentity\": \"" + ContractType + "\", \"optional\": true}]";
        using var satisfiedDirectory = TestExtensionDirectory.CreateJson(Json(id: "alpha.extension", extra: satisfiedImport));
        var satisfied = Discover(satisfiedDirectory);
        var satisfiedOrder = ExtensionManifestGraph.ValidateAndOrder(
            [satisfied, provider],
            new SemVersion(1, 0, 0));
        Assert.True(satisfiedOrder.Succeeded, satisfiedOrder.FailureCode.ToString());
        Assert.Equal(["beta.extension", "alpha.extension"], satisfiedOrder.OrderedManifests.Select(item => item.Id));

        using var unsatisfiedDirectory = TestExtensionDirectory.CreateJson(Json(
            id: "alpha.extension",
            extra: satisfiedImport.Replace(">=1.0.0", ">=2.0.0", StringComparison.Ordinal)));
        var unsatisfied = Discover(unsatisfiedDirectory);
        var unsatisfiedOrder = ExtensionManifestGraph.ValidateAndOrder(
            [unsatisfied, provider],
            new SemVersion(1, 0, 0));
        Assert.True(unsatisfiedOrder.Succeeded, unsatisfiedOrder.FailureCode.ToString());
        Assert.Equal(["alpha.extension", "beta.extension"], unsatisfiedOrder.OrderedManifests.Select(item => item.Id));
    }

    [Fact]
    public void GraphBreaksCyclesThroughOptionalEdges()
    {
        using var alphaDirectory = TestExtensionDirectory.CreateJson(Json(
            id: "alpha.extension",
            dependencies: "[{\"id\":\"beta.extension\",\"versionRange\":\">=1.0.0\",\"optional\":true}]"));
        using var betaDirectory = TestExtensionDirectory.CreateJson(Json(
            id: "beta.extension",
            dependencies: "[{\"id\":\"alpha.extension\",\"versionRange\":\">=1.0.0\"}]"));
        var alpha = Discover(alphaDirectory);
        var beta = Discover(betaDirectory);

        var result = ExtensionManifestGraph.ValidateAndOrder([alpha, beta], new SemVersion(1, 0, 0));
        Assert.True(result.Succeeded, result.FailureCode.ToString());
        Assert.Equal(["alpha.extension", "beta.extension"], result.OrderedManifests.Select(item => item.Id));
    }

    [Fact]
    public void RequiredDependencyEdgeIsNeverDowngradedByAnOptionalImport()
    {
        var export = ",\n  \"exports\": [{\"contractId\": \"shared.logger\", \"version\": \"1.0.0\", \"assemblyIdentity\": \"" +
            SharedAssembly + "\", \"typeIdentity\": \"" + ContractType + "\"}]";
        var optionalImport = ",\n  \"imports\": [{\"contractId\": \"shared.logger\", \"versionRange\": \">=1.0.0\", \"assemblyIdentity\": \"" +
            SharedAssembly + "\", \"typeIdentity\": \"" + ContractType + "\", \"optional\": true}]";
        using var alphaDirectory = TestExtensionDirectory.CreateJson(Json(
            id: "alpha.extension",
            dependencies: "[{\"id\":\"beta.extension\",\"versionRange\":\">=1.0.0\"}]",
            extra: optionalImport));
        using var betaDirectory = TestExtensionDirectory.CreateJson(Json(
            id: "beta.extension",
            dependencies: "[{\"id\":\"alpha.extension\",\"versionRange\":\">=1.0.0\"}]",
            extra: export));
        var alpha = Discover(alphaDirectory);
        var beta = Discover(betaDirectory);

        var result = ExtensionManifestGraph.ValidateAndOrder([alpha, beta], new SemVersion(1, 0, 0));

        Assert.False(result.Succeeded);
        Assert.Equal(ExtensionFailureCode.DependencyCycle, result.FailureCode);
    }

    [Fact]
    public void GraphOrdersProvidersForSatisfiedOptionalDependencies()
    {
        using var providerDirectory = TestExtensionDirectory.CreateJson(Json(id: "beta.extension"));
        using var consumerDirectory = TestExtensionDirectory.CreateJson(Json(
            id: "alpha.extension",
            dependencies: "[{\"id\":\"beta.extension\",\"versionRange\":\">=1.0.0\",\"optional\":true}]"));
        var provider = Discover(providerDirectory);
        var consumer = Discover(consumerDirectory);

        var result = ExtensionManifestGraph.ValidateAndOrder([consumer, provider], new SemVersion(1, 0, 0));

        Assert.True(result.Succeeded, result.FailureCode.ToString());
        Assert.Equal(["beta.extension", "alpha.extension"], result.OrderedManifests.Select(item => item.Id));
    }


    [Fact]
    public void TryImportNeverYieldsContractsOutsideTheDeclaredRange()
    {
        using var selfMismatched = new ExtensionContractRegistry(
            [new ExtensionContractExport("shared.logger", new SemVersion(1, 0, 0), SharedAssembly, ContractType)],
            [new ExtensionContractImport("shared.logger", Range(">=2.0.0"), SharedAssembly, ContractType, optional: true)],
            (_, _, _) => null);
        var logger = new TestLogger();
        Assert.True(selfMismatched.TryExport<IExtensionLogger>("shared.logger", logger));
        Assert.False(selfMismatched.TryImport<IExtensionLogger>("shared.logger", out var mismatched));
        Assert.Null(mismatched);

        using var selfSatisfied = new ExtensionContractRegistry(
            [new ExtensionContractExport("shared.logger", new SemVersion(1, 0, 0), SharedAssembly, ContractType)],
            [new ExtensionContractImport("shared.logger", Range(">=1.0.0"), SharedAssembly, ContractType, optional: true)],
            (_, _, _) => null);
        Assert.True(selfSatisfied.TryExport<IExtensionLogger>("shared.logger", logger));
        Assert.True(selfSatisfied.TryImport<IExtensionLogger>("shared.logger", out var satisfied));
        Assert.Same(logger, satisfied);

        SemVersionRange? observedRange = null;
        using var providerPath = new ExtensionContractRegistry(
            ImmutableArray<ExtensionContractExport>.Empty,
            [new ExtensionContractImport("shared.logger", Range(">=2.0.0"), SharedAssembly, ContractType, optional: true)],
            (_, _, requiredRange) =>
            {
                observedRange = requiredRange;
                return logger;
            });
        Assert.True(providerPath.TryImport<IExtensionLogger>("shared.logger", out var provided));
        Assert.Same(logger, provided);
        Assert.Equal(">=2.0.0", observedRange!.Expression);
    }

    [Fact]
    public void DependencyContextReportsResolutionStatesAndGatesImport()
    {
        using var manifestDirectory = TestExtensionDirectory.CreateJson(Json(
            id: "consumer.extension",
            dependencies: "[{\"id\":\"present.extension\",\"versionRange\":\"^1.0.0\"}," +
                "{\"id\":\"stale.extension\",\"versionRange\":\">=2.0.0\",\"optional\":true}," +
                "{\"id\":\"absent.extension\",\"versionRange\":\">=1.0.0\",\"optional\":true}]",
            extra: ",\n  \"imports\": [{\"contractId\": \"shared.logger\", \"versionRange\": \">=1.0.0\", \"assemblyIdentity\": \"" +
                SharedAssembly + "\", \"typeIdentity\": \"" + ContractType + "\"}]"));
        var manifest = Discover(manifestDirectory);
        using var contracts = new ExtensionContractRegistry(
            ImmutableArray<ExtensionContractExport>.Empty,
            manifest.Imports,
            (_, _, _) => new TestLogger());
        var availableVersions = new Dictionary<string, SemVersion>(StringComparer.Ordinal)
        {
            ["present.extension"] = new(1, 5, 0),
            ["stale.extension"] = new(1, 0, 0),
            ["consumer.extension"] = new(1, 0, 0)
        };
        var api = ExtensionDependencyApi.Create(manifest, availableVersions, contracts);

        var present = api.GetDependencyContext("present.extension");
        Assert.Equal(ExtensionDependencyState.Satisfied, present.State);
        Assert.False(present.IsOptional);
        Assert.Equal("^1.0.0", present.VersionRange);
        Assert.Equal("1.5.0", present.InstalledVersion);
        Assert.True(present.TryImport<IExtensionLogger>("shared.logger", out var imported));
        Assert.NotNull(imported);

        var stale = api.GetDependencyContext("stale.extension");
        Assert.Equal(ExtensionDependencyState.VersionMismatch, stale.State);
        Assert.True(stale.IsOptional);
        Assert.Equal("1.0.0", stale.InstalledVersion);
        Assert.False(stale.TryImport<IExtensionLogger>("shared.logger", out _));

        var absent = api.GetDependencyContext("absent.extension");
        Assert.Equal(ExtensionDependencyState.NotInstalled, absent.State);
        Assert.Null(absent.InstalledVersion);
        Assert.False(absent.TryImport<IExtensionLogger>("shared.logger", out _));

        var undeclared = api.GetDependencyContext("other.extension");
        Assert.Equal(ExtensionDependencyState.NotDeclared, undeclared.State);
        Assert.False(undeclared.TryImport<IExtensionLogger>("shared.logger", out _));

        Assert.ThrowsAny<ArgumentException>(() => api.GetDependencyContext(" "));
    }

    private static ExtensionManifest Discover(TestExtensionDirectory directory)
    {
        var result = ExtensionManifestDiscovery.Discover(directory.RootPath);
        Assert.True(result.Succeeded, result.FailureCode.ToString());
        return result.Manifest!;
    }

    private static SemVersionRange Range(string expression) =>
        SemVersionRange.TryParse(expression, out var range) && range is not null
            ? range
            : throw new InvalidOperationException(expression);

    private static string Json(
        string id = "fixture.extension.deterministic",
        string dependencies = "[]",
        string extra = "") =>
        "{\n  \"schemaVersion\": 1,\n  \"id\": \"" + id +
        "\",\n  \"version\": \"1.0.0\",\n  \"entryAssembly\": \"Fixtures.Extension.dll\",\n" +
        "  \"entryType\": \"Nekolla.Nekostick.Tests.Fixtures.Extension.FixtureEntrypoint\",\n" +
        "  \"dependencies\": " + dependencies + ",\n  \"requiredHostApiVersion\": \">=1.0.0\"" + extra + "\n}";

    private static string Yaml(string dependencies = "dependencies: []\n", string extra = "") =>
        "schemaVersion: 1\nid: fixture.extension.deterministic\nversion: 1.0.0\nentryAssembly: Fixtures.Extension.dll\n" +
        "entryType: Nekolla.Nekostick.Tests.Fixtures.Extension.FixtureEntrypoint\n" +
        dependencies + "requiredHostApiVersion: \">=1.0.0\"\n" + extra;

    private sealed class TestLogger : IExtensionLogger
    {
        public void Report(ExtensionLogLevel level, string code)
        {
        }
    }
}
