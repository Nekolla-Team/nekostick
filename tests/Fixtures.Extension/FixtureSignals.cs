namespace Nekolla.Nekostick.Tests.Fixtures.Extension;

/// <summary>Contains deterministic, non-sensitive signals emitted by fixture components.</summary>
public static class FixtureSignals
{
    /// <summary>Gets the known handler success signal.</summary>
    public const string KnownHandler = "fixture.handler.ok";

    /// <summary>Gets the known service success signal.</summary>
    public const string KnownService = "fixture.service.ok";

    /// <summary>Gets the cancellation observation signal.</summary>
    public const string CancellationObserved = "fixture.cancellation.observed";

    /// <summary>Gets the deliberate constructor failure message.</summary>
    public const string ConstructorFailure = "Fixture constructor deliberately failed.";

    /// <summary>Gets the deliberate handler failure message.</summary>
    public const string HandlerFailure = "Fixture handler deliberately failed.";
}
