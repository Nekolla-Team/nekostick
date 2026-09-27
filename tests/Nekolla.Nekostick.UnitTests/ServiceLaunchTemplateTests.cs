using System.Collections.Immutable;
using Nekolla.Nekostick.Supervision;
using Xunit;

namespace Nekolla.Nekostick.UnitTests;

public sealed class ServiceLaunchTemplateTests
{
    [Fact]
    public void PlainTextPassesThroughUnchanged()
    {
        var result = ServiceLaunchTemplate.Expand("plain text", Context());

        Assert.Equal("plain text", result);
    }

    [Fact]
    public void LocalDynamicVariableExpands()
    {
        var result = ServiceLaunchTemplate.Expand(
            "port=${PORT}",
            Context(new Dictionary<string, string> { ["PORT"] = "23456" }));

        Assert.Equal("port=23456", result);
    }

    [Fact]
    public void LocalVariableCanComeFromOwnEnvironment()
    {
        var result = ServiceLaunchTemplate.Expand(
            "${SELF}",
            Context(new Dictionary<string, string> { ["SELF"] = "configured" }));

        Assert.Equal("configured", result);
    }

    [Fact]
    public void LocalVariablesExpandRecursively()
    {
        var result = ServiceLaunchTemplate.Expand(
            "${A}",
            Context(new Dictionary<string, string>
            {
                ["A"] = "${B}",
                ["B"] = "literal"
            }));

        Assert.Equal("literal", result);
    }

    [Fact]
    public void LocalVariableCycleThrows()
    {
        var exception = Assert.Throws<ServiceTemplateException>(() => ServiceLaunchTemplate.Expand(
            "${A}",
            Context(new Dictionary<string, string>
            {
                ["A"] = "${B}",
                ["B"] = "${A}"
            })));

        Assert.Equal("${A}", exception.Token);
    }

    [Fact]
    public void MissingLocalVariableThrows()
    {
        var exception = Assert.Throws<ServiceTemplateException>(() => ServiceLaunchTemplate.Expand(
            "${MISSING}",
            Context()));

        Assert.Equal("${MISSING}", exception.Token);
        Assert.DoesNotContain("value", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExpansionDepthLimitThrows()
    {
        var variables = new Dictionary<string, string>();
        for (var index = 0; index < 34; index++)
        {
            variables[$"A{index}"] = index == 33 ? "literal" : $"${{A{index + 1}}}";
        }

        Assert.Throws<ServiceTemplateException>(() => ServiceLaunchTemplate.Expand("${A0}", Context(variables)));
    }

    [Fact]
    public void RemoteVariableUsesResolver()
    {
        var serviceId = Guid.Parse("018f0000-0000-7000-8000-000000000101");
        Guid resolvedServiceId = Guid.Empty;
        string? resolvedName = null;
        var result = ServiceLaunchTemplate.Expand(
            RemoteToken("VAR", serviceId),
            new ServiceTemplateContext(
                new Dictionary<string, string>(),
                (id, name) =>
                {
                    resolvedServiceId = id;
                    resolvedName = name;
                    return "remote";
                }));

        Assert.Equal("remote", result);
        Assert.Equal(serviceId, resolvedServiceId);
        Assert.Equal("VAR", resolvedName);
    }

    [Fact]
    public void RemoteVariableWithoutResolverThrows()
    {
        Assert.Throws<ServiceTemplateException>(() => ServiceLaunchTemplate.Expand(
            RemoteToken("VAR", Guid.NewGuid()),
            Context()));
    }

    [Fact]
    public void RemoteVariableWithNullResolverResultThrows()
    {
        Assert.Throws<ServiceTemplateException>(() => ServiceLaunchTemplate.Expand(
            RemoteToken("VAR", Guid.NewGuid()),
            new ServiceTemplateContext(
                new Dictionary<string, string>(),
                (_, _) => null)));
    }

    [Fact]
    public void RemoteVariableValueIsInsertedVerbatim()
    {
        var result = ServiceLaunchTemplate.Expand(
            RemoteToken("VAR", Guid.NewGuid()),
            new ServiceTemplateContext(
                new Dictionary<string, string>(),
                (_, _) => "${MISSING}"));

        Assert.Equal("${MISSING}", result);
    }

    [Fact]
    public void HostTokenPassesThroughVerbatim()
    {
        var result = ServiceLaunchTemplate.Expand("before${HOST:X}after", Context());

        Assert.Equal("before${HOST:X}after", result);
    }

    [Fact]
    public void EscapedHostTokenPassesThroughWithBackslash()
    {
        var result = ServiceLaunchTemplate.Expand(@"before\${HOST:X}after", Context());

        Assert.Equal(@"before\${HOST:X}after", result);
    }

    [Fact]
    public void EscapedLocalTokenProducesLiteralToken()
    {
        var result = ServiceLaunchTemplate.Expand(
            @"\${PORT}",
            Context(new Dictionary<string, string> { ["PORT"] = "23456" }));

        Assert.Equal("${PORT}", result);
    }

    [Fact]
    public void EscapedLegacyVariableProducesLiteralDollarText()
    {
        var result = ServiceLaunchTemplate.Expand(
            @"\$PORT",
            Context(legacyVariables: new Dictionary<string, string> { ["PORT"] = "23456" }));

        Assert.Equal("$PORT", result);
    }

    [Fact]
    public void LegacyVariableExpandsAtBoundariesAndBeforePunctuation()
    {
        var result = ServiceLaunchTemplate.Expand(
            "left:$PORT right:$PORT! end:$PORT",
            Context(legacyVariables: new Dictionary<string, string> { ["PORT"] = "23456" }));

        Assert.Equal("left:23456 right:23456! end:23456", result);
    }

    [Fact]
    public void LegacyVariablePrefixWithoutBoundaryDoesNotExpand()
    {
        var result = ServiceLaunchTemplate.Expand(
            "$PORTX",
            Context(legacyVariables: new Dictionary<string, string> { ["PORT"] = "23456" }));

        Assert.Equal("$PORTX", result);
    }

    [Fact]
    public void UnterminatedTokenThrows()
    {
        Assert.Throws<ServiceTemplateException>(() => ServiceLaunchTemplate.Expand("${", Context()));
    }

    [Fact]
    public void EmptyTokenThrows()
    {
        Assert.Throws<ServiceTemplateException>(() => ServiceLaunchTemplate.Expand("${}", Context()));
    }

    [Fact]
    public void InvalidRemoteGuidThrows()
    {
        Assert.Throws<ServiceTemplateException>(() => ServiceLaunchTemplate.Expand(
            "${VAR@not-a-guid}",
            Context()));
    }

    [Fact]
    public void ExpandArgumentsExpandsEachArgumentAndDefaultIsEmpty()
    {
        var context = Context(new Dictionary<string, string> { ["PORT"] = "23456" });

        var result = ServiceLaunchTemplate.ExpandArguments(
            ImmutableArray.Create("--port", "${PORT}"),
            context);

        Assert.Equal(["--port", "23456"], result);
        Assert.Empty(ServiceLaunchTemplate.ExpandArguments(default, context));
    }

    [Fact]
    public void ExpandEnvironmentUsesDynamicValuesForConfiguredValuesAndOverlaysResults()
    {
        var result = ServiceLaunchTemplate.ExpandEnvironment(
            new Dictionary<string, string>
            {
                ["VALUE"] = "${PORT}",
                ["${KEY}"] = "configured"
            },
            new Dictionary<string, string>
            {
                ["PORT"] = "23456",
                ["HOST"] = "127.0.0.1"
            },
            remoteResolver: null);

        Assert.Equal("23456", result["VALUE"]);
        Assert.Equal("configured", result["${KEY}"]);
        Assert.Equal("23456", result["PORT"]);
        Assert.Equal("127.0.0.1", result["HOST"]);
    }

    [Fact]
    public void ExtractDependenciesCollectsAndDeduplicatesRemoteIds()
    {
        var first = Guid.Parse("018f0000-0000-7000-8000-000000000201");
        var second = Guid.Parse("018f0000-0000-7000-8000-000000000202");
        var texts = new string?[]
        {
            $"{RemoteToken("FIRST", first)} {RemoteToken("ALSO_FIRST", first)}",
            RemoteToken("SECOND", second),
            @"\${ESCAPED@018f0000-0000-7000-8000-000000000203}",
            "${SELF}",
            "${HOST:X}",
            "${BAD@not-a-guid}",
            "${UNTERMINATED",
            null,
            string.Empty
        };

        var result = ServiceLaunchTemplate.ExtractDependencies(texts);

        Assert.Equal(2, result.Count);
        Assert.Contains(first, result);
        Assert.Contains(second, result);
    }

    [Fact]
    public void MalformedNameTokenThrows()
    {
        Assert.Throws<ServiceTemplateException>(() => ServiceLaunchTemplate.Expand("${1VAR}", Context()));
    }

    private static ServiceTemplateContext Context(
        IReadOnlyDictionary<string, string>? variables = null,
        Func<Guid, string, string?>? remoteResolver = null,
        IReadOnlyDictionary<string, string>? legacyVariables = null) =>
        new(
            variables ?? new Dictionary<string, string>(StringComparer.Ordinal),
            remoteResolver,
            legacyVariables);

    private static string RemoteToken(string name, Guid serviceId) =>
        $"${{{name}@{serviceId:D}}}";
}
