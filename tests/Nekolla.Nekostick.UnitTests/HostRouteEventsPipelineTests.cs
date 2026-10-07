using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Extensions;
using Nekolla.Nekostick.Host;
using Nekolla.Nekostick.Routing;
using Nekolla.Nekostick.Tests.Fixtures.Extension;
using Xunit;

namespace Nekolla.Nekostick.UnitTests;

public sealed class HostRouteEventsPipelineTests
{
    private static readonly int[] ExpectedRequestHookOrder = [1, 2];
    private static readonly int[] ExpectedReturnHookStatuses = [200, 201];

    [Fact]
    public async Task PublishRouteEventDeliversEveryRouteAndDoesNotWaitForSubscriber()
    {
        using var fixture = TestExtensionDirectory.CreateJson();
        var manifest = Discover(fixture.RootPath);
        var routeId = RoutingTestData.Id(900);
        var otherRouteId = RoutingTestData.Id(901);
        var entered = NewSignal();
        var delivered = NewSignal();
        var release = NewSignal();
        var deliveries = 0;
        var factory = new RouteTestFactory
        {
            Configure = events =>
            {
                Assert.Same(ExtensionRouteRegistrationResult.Success, events.TrySubscribe(async (@event, _) =>
                {
                    if (@event.Type is not (ExtensionRouteEventTypes.Trigger or ExtensionRouteEventTypes.Return))
                    {
                        return;
                    }

                    var count = Interlocked.Increment(ref deliveries);
                    if (count == 1)
                    {
                        entered.SetResult();
                    }

                    if (count == 2)
                    {
                        delivered.SetResult();
                    }

                    await release.Task.ConfigureAwait(false);
                }));
            }
        };
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current, capabilityFactory: factory);
        var generation = await PrepareAsync(manager, manifest, routeId);

        Assert.Equal(1, generation.PublishRouteEvent(new ExtensionRouteEvent(
            otherRouteId,
            RoutingTestData.Id(902),
            ExtensionRouteEventStage.Trigger,
            Request("/other"))));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        Assert.Equal(1, generation.PublishRouteEvent(new ExtensionRouteEvent(
            routeId,
            RoutingTestData.Id(903),
            ExtensionRouteEventStage.Trigger,
            Request("/matched"))));

        release.SetResult();
        await delivered.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.Equal(2, Volatile.Read(ref deliveries));
    }

    [Fact]
    public async Task HooksRunInRegistrationOrderAndSeePriorRequestMutation()
    {
        using var fixture = TestExtensionDirectory.CreateJson();
        var manifest = Discover(fixture.RootPath);
        var routeId = RoutingTestData.Id(904);
        var order = new List<int>();
        var factory = new RouteTestFactory
        {
            Configure = events =>
            {
                Assert.Same(ExtensionRouteRegistrationResult.Success, events.TryRegisterHook(ExtensionRouteEventStage.Trigger, (context, _) =>
                {
                    order.Add(1);
                    return ValueTask.FromResult(new ExtensionRouteHookResult(
                        ExtensionRouteHookAction.ReplaceRequest,
                        Request("/first")));
                }));
                Assert.Same(ExtensionRouteRegistrationResult.Success, events.TryRegisterHook(ExtensionRouteEventStage.Trigger, (context, _) =>
                {
                    order.Add(context.Request.Path == "/first" ? 2 : -2);
                    return ValueTask.FromResult(new ExtensionRouteHookResult(
                        ExtensionRouteHookAction.ReplaceRequest,
                        Request("/second")));
                }));
            }
        };
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current, capabilityFactory: factory);
        var generation = await PrepareAsync(manager, manifest, routeId);

        var result = await generation.DispatchRouteHooksAsync(
            routeId,
            RoutingTestData.Id(905),
            ExtensionRouteEventStage.Trigger,
            Request("/initial"),
            null,
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.False(result.Cancelled);
        Assert.Equal(ExpectedRequestHookOrder, order);
        Assert.Equal("/second", result.Request.Path);
    }

    [Fact]
    public async Task ReturnHooksRunSeriallyAndSeePriorResponseMutation()
    {
        using var fixture = TestExtensionDirectory.CreateJson();
        var manifest = Discover(fixture.RootPath);
        var routeId = RoutingTestData.Id(915);
        var statuses = new List<int>();
        var factory = new RouteTestFactory
        {
            Configure = events =>
            {
                Assert.Same(ExtensionRouteRegistrationResult.Success, events.TryRegisterHook(ExtensionRouteEventStage.Return, (context, _) =>
                {
                    statuses.Add(context.Response!.StatusCode);
                    return ValueTask.FromResult(new ExtensionRouteHookResult(
                        ExtensionRouteHookAction.ReplaceResponse,
                        response: new ExtensionRouteResponseSnapshot(201)));
                }));
                Assert.Same(ExtensionRouteRegistrationResult.Success, events.TryRegisterHook(ExtensionRouteEventStage.Return, (context, _) =>
                {
                    statuses.Add(context.Response!.StatusCode);
                    return ValueTask.FromResult(new ExtensionRouteHookResult(
                        ExtensionRouteHookAction.ReplaceResponse,
                        response: new ExtensionRouteResponseSnapshot(202)));
                }));
            }
        };
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current, capabilityFactory: factory);
        var generation = await PrepareAsync(manager, manifest, routeId);

        var result = await generation.DispatchRouteHooksAsync(
            routeId,
            RoutingTestData.Id(916),
            ExtensionRouteEventStage.Return,
            Request("/return"),
            new ExtensionRouteResponseSnapshot(200),
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(ExpectedReturnHookStatuses, statuses);
        Assert.Equal(202, result.Response!.StatusCode);
    }

    [Fact]
    public async Task InvalidLaterActionDoesNotExposeEarlierRequestMutation()
    {
        using var fixture = TestExtensionDirectory.CreateJson();
        var manifest = Discover(fixture.RootPath);
        var routeId = RoutingTestData.Id(906);
        var original = Request("/original");
        var factory = new RouteTestFactory
        {
            Configure = events =>
            {
                Assert.Same(ExtensionRouteRegistrationResult.Success, events.TryRegisterHook(ExtensionRouteEventStage.Trigger, (_, _) =>
                    ValueTask.FromResult(new ExtensionRouteHookResult(
                        ExtensionRouteHookAction.ReplaceRequest,
                        Request("/partial")))));
                Assert.Same(ExtensionRouteRegistrationResult.Success, events.TryRegisterHook(ExtensionRouteEventStage.Trigger, (_, _) =>
                    ValueTask.FromResult(new ExtensionRouteHookResult(
                        ExtensionRouteHookAction.ReplaceResponse,
                        response: new ExtensionRouteResponseSnapshot(200)))));
            }
        };
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current, capabilityFactory: factory);
        var generation = await PrepareAsync(manager, manifest, routeId);

        var result = await generation.DispatchRouteHooksAsync(
            routeId,
            RoutingTestData.Id(907),
            ExtensionRouteEventStage.Trigger,
            original,
            null,
            TestContext.Current.CancellationToken);

        AssertFailClosed(result, original);
    }

    [Fact]
    public async Task GlobalHookExecutesForUnownedRouteAndReceivesRouteIdentity()
    {
        using var fixture = TestExtensionDirectory.CreateJson();
        var manifest = Discover(fixture.RootPath);
        var ownedRouteId = RoutingTestData.Id(906);
        var otherRouteId = RoutingTestData.Id(917);
        var executions = 0;
        var seenRouteId = Guid.Empty;
        var factory = new RouteTestFactory
        {
            Configure = events => Assert.Same(ExtensionRouteRegistrationResult.Success, events.TryRegisterHook(
                ExtensionRouteEventStage.Trigger,
                (context, _) =>
                {
                    executions++;
                    seenRouteId = context.RouteId;
                    return ValueTask.FromResult(new ExtensionRouteHookResult(ExtensionRouteHookAction.Continue));
                }))
        };
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current, capabilityFactory: factory);
        var generation = await PrepareAsync(manager, manifest, ownedRouteId);
        var result = await generation.DispatchRouteHooksAsync(
            otherRouteId,
            RoutingTestData.Id(918),
            ExtensionRouteEventStage.Trigger,
            Request("/other"),
            null,
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(1, executions);
        Assert.Equal(otherRouteId, seenRouteId);
    }

    [Fact]
    public async Task InvalidStageFailsClosed()
    {
        using var fixture = TestExtensionDirectory.CreateJson();
        var manifest = Discover(fixture.RootPath);
        var routeId = RoutingTestData.Id(921);
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current);
        var generation = await PrepareAsync(manager, manifest, routeId);
        var original = Request("/original");

        var result = await generation.DispatchRouteHooksAsync(
            routeId,
            RoutingTestData.Id(922),
            (ExtensionRouteEventStage)99,
            original,
            null,
            TestContext.Current.CancellationToken);

        AssertFailClosed(result, original);
    }

    [Fact]
    public async Task ExceptionAndCancellationFailClosedWithoutMutation()
    {
        using var fixture = TestExtensionDirectory.CreateJson();
        var manifest = Discover(fixture.RootPath);
        var routeId = RoutingTestData.Id(908);
        var original = Request("/original");
        var factory = new RouteTestFactory
        {
            Configure = events => Assert.Same(ExtensionRouteRegistrationResult.Success, events.TryRegisterHook(
                ExtensionRouteEventStage.Trigger,
                async (_, token) =>
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
                    throw new InvalidOperationException("unreachable");
                }))
        };
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current, capabilityFactory: factory);
        var generation = await PrepareAsync(manager, manifest, routeId);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var result = await generation.DispatchRouteHooksAsync(
            routeId,
            RoutingTestData.Id(909),
            ExtensionRouteEventStage.Trigger,
            original,
            null,
            cancellation.Token);

        AssertFailClosed(result, original);
    }

    [Fact]
    public async Task CallbackTimeoutFailsClosedAndSuppressesLateResult()
    {
        using var fixture = TestExtensionDirectory.CreateJson();
        var manifest = Discover(fixture.RootPath);
        var routeId = RoutingTestData.Id(910);
        var lateResult = NewSignal();
        var factory = new RouteTestFactory
        {
            Configure = events => Assert.Same(ExtensionRouteRegistrationResult.Success, events.TryRegisterHook(
                ExtensionRouteEventStage.Trigger,
                async (_, token) =>
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(400), token).ConfigureAwait(false);
                    lateResult.SetResult();
                    return new ExtensionRouteHookResult(
                        ExtensionRouteHookAction.ReplaceRequest,
                        Request("/late"));
                }))
        };
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current, capabilityFactory: factory);
        var generation = await PrepareAsync(manager, manifest, routeId);
        var original = Request("/original");

        var result = await generation.DispatchRouteHooksAsync(
            routeId,
            RoutingTestData.Id(911),
            ExtensionRouteEventStage.Trigger,
            original,
            null,
            TestContext.Current.CancellationToken);

        AssertFailClosed(result, original);
        Assert.False(lateResult.Task.IsCompleted);
    }

    [Fact]
    public async Task RetirementClearsRegistrationsAndPreventsLateCallbackInfluence()
    {
        using var fixture = TestExtensionDirectory.CreateJson();
        var manifest = Discover(fixture.RootPath);
        var routeId = RoutingTestData.Id(912);
        var entered = NewSignal();
        var release = NewSignal();
        var factory = new RouteTestFactory
        {
            Configure = events => Assert.Same(ExtensionRouteRegistrationResult.Success, events.TryRegisterHook(
                ExtensionRouteEventStage.Trigger,
                async (_, _) =>
                {
                    entered.SetResult();
                    await release.Task.ConfigureAwait(false);
                    return new ExtensionRouteHookResult(
                        ExtensionRouteHookAction.ReplaceRequest,
                        Request("/late"));
                }))
        };
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current, capabilityFactory: factory);
        var generation = await PrepareAsync(manager, manifest, routeId);
        var dispatch = generation.DispatchRouteHooksAsync(
            routeId,
            RoutingTestData.Id(913),
            ExtensionRouteEventStage.Trigger,
            Request("/original"),
            null,
            TestContext.Current.CancellationToken).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        var retirement = generation.RetireAsync(TestContext.Current.CancellationToken).AsTask();
        Assert.True(generation.IsRetiring);
        release.SetResult();

        var result = await dispatch;
        AssertFailClosed(result, Request("/original"));
        Assert.True(await retirement);
        var registrationResult = factory.RouteEvents!.TryRegisterHook(
            ExtensionRouteEventStage.Trigger,
            (_, _) => ValueTask.FromResult(new ExtensionRouteHookResult(ExtensionRouteHookAction.Continue)));
        var failure = Assert.IsType<ExtensionRouteRegistrationFailureResult>(registrationResult);
        Assert.Equal(ExtensionRouteRegistrationFailureCode.Unavailable, failure.Code);
        Assert.NotNull(failure.Detail);
    }

    [Fact]
    public async Task RegistrationCapsRejectRegistrationsBeyondGenerationBounds()
    {
        using var fixture = TestExtensionDirectory.CreateJson();
        var manifest = Discover(fixture.RootPath);
        var routeId = RoutingTestData.Id(914);
        var factory = new RouteTestFactory
        {
            Configure = events =>
            {
                for (var index = 0; index < ExtensionRouteHookLimits.MaximumHookRegistrations + 1; index++)
                {
                    var hookResult = events.TryRegisterHook(
                        ExtensionRouteEventStage.Trigger,
                        (_, _) => ValueTask.FromResult(new ExtensionRouteHookResult(ExtensionRouteHookAction.Continue)));
                    if (index < ExtensionRouteHookLimits.MaximumHookRegistrations)
                    {
                        Assert.Same(ExtensionRouteRegistrationResult.Success, hookResult);
                    }
                    else
                    {
                        var failure = Assert.IsType<ExtensionRouteRegistrationFailureResult>(hookResult);
                        Assert.Equal(ExtensionRouteRegistrationFailureCode.LimitReached, failure.Code);
                        Assert.NotNull(failure.Detail);
                    }
                }

                for (var index = 0; index < ExtensionRouteHookLimits.MaximumSubscriptionRegistrations + 1; index++)
                {
                    var subscriptionResult = events.TrySubscribe((_, _) => ValueTask.CompletedTask);
                    if (index < ExtensionRouteHookLimits.MaximumSubscriptionRegistrations)
                    {
                        Assert.Same(ExtensionRouteRegistrationResult.Success, subscriptionResult);
                    }
                    else
                    {
                        var failure = Assert.IsType<ExtensionRouteRegistrationFailureResult>(subscriptionResult);
                        Assert.Equal(ExtensionRouteRegistrationFailureCode.LimitReached, failure.Code);
                        Assert.NotNull(failure.Detail);
                    }
                }
            }
        };
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current, capabilityFactory: factory);
        _ = await PrepareAsync(manager, manifest, routeId);
    }

    [Fact]
    public async Task StreamingRouteHooksObserveEmptyBodiesWithoutResponseBuffering()
    {
        using var fixture = TestExtensionDirectory.CreateJson();
        var manifest = Discover(fixture.RootPath);
        var routeId = RoutingTestData.Id(950);
        var triggerBody = (byte[]?)null;
        var returnBody = (byte[]?)null;
        var returnStatus = 0;
        var factory = new RouteTestFactory
        {
            Configure = events =>
            {
                Assert.Same(ExtensionRouteRegistrationResult.Success, events.TryRegisterHook(ExtensionRouteEventStage.Trigger, (context, _) =>
                {
                    triggerBody = context.Request.Body.ToArray();
                    return ValueTask.FromResult(new ExtensionRouteHookResult(ExtensionRouteHookAction.Continue));
                }));
                Assert.Same(ExtensionRouteRegistrationResult.Success, events.TryRegisterHook(ExtensionRouteEventStage.Return, (context, _) =>
                {
                    returnBody = context.Response!.Body.ToArray();
                    returnStatus = context.Response!.StatusCode;
                    return ValueTask.FromResult(new ExtensionRouteHookResult(ExtensionRouteHookAction.Continue));
                }));
            }
        };
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current, capabilityFactory: factory);
        var settingsJson = JsonSerializer.Serialize(new
        {
            label = "streaming-hooks",
            streamingHandlerId = "fixture.streaming",
            registerStreamingHandler = true
        });
        var settings = new ExtensionSettingsConfiguration(manifest.Id, 1, settingsJson, 0);
        var generation = await PrepareAsync(manager, manifest, routeId, settings, ["fixture.streaming"]);

        var route = new RouteConfiguration(
            routeId,
            true,
            new RouteMatcherConfiguration(RouteMatcherType.Exact, "/original", default, default),
            new ExtensionHandlerRouteTargetConfiguration("fixture.streaming"),
            0,
            new ForwardingConfiguration(ForwardingMode.Preserve, null),
            ImmutableArray<HeaderRewriteConfiguration>.Empty,
            ImmutableArray<HeaderRewriteConfiguration>.Empty,
            "{}",
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            1);
        var snapshot = new HostRoutingSnapshot(
            RoutingTestData.CreateSnapshot(1, ImmutableArray.Create(route)),
            RoutingTestData.Build(route),
            ImmutableDictionary<Guid, ExecutableRoute>.Empty,
            generation,
            ImmutableDictionary<Guid, string?>.Empty);
        var match = RoutingTestData.Build(route)
            .Match(new RouteMatchInput("/original", "example.test", "GET"))
            .Match!;

        using var requestBody = new MemoryStream(Encoding.UTF8.GetBytes("request-body"));
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/original";
        context.Request.Host = new HostString("example.test");
        context.Request.Body = requestBody;
        var originalResponseBody = new MemoryStream();
        context.Response.Body = originalResponseBody;

        var session = await HostRouteEvents.BeginAsync(
            context,
            snapshot,
            match,
            TestContext.Current.CancellationToken);
        Assert.NotNull(session);
        Assert.False(session!.Cancelled);
        Assert.NotNull(triggerBody);
        Assert.Empty(triggerBody!);

        // Streaming handlers produce the response themselves, so the host must not install a
        // buffering body; Return hooks still dispatch and observe an empty body snapshot.
        Assert.Null(session.ResponseBuffer);
        Assert.Same(originalResponseBody, context.Response.Body);

        context.Response.StatusCode = StatusCodes.Status200OK;
        await context.Response.Body.WriteAsync(
            Encoding.UTF8.GetBytes("response-body"),
            TestContext.Current.CancellationToken);
        var result = await HostRouteEvents.CompleteAsync(
            context,
            session,
            RouteTargetExecutionResult.Handled,
            TestContext.Current.CancellationToken);

        Assert.Equal(RouteTargetExecutionResult.Handled, result);
        Assert.NotNull(returnBody);
        Assert.Empty(returnBody!);
        Assert.Equal(StatusCodes.Status200OK, returnStatus);
        Assert.Null(session.ResponseBuffer);
        Assert.Same(originalResponseBody, context.Response.Body);
        Assert.Equal("response-body", Encoding.UTF8.GetString(await ReadBodyAsync(originalResponseBody)));
    }

    [Fact]
    public async Task TriggerContinueReturnsSameInstanceAndNoReplaceFlag()
    {
        using var fixture = TestExtensionDirectory.CreateJson();
        var manifest = Discover(fixture.RootPath);
        var routeId = RoutingTestData.Id(960);
        var factory = new RouteTestFactory
        {
            Configure = events => Assert.Same(ExtensionRouteRegistrationResult.Success, events.TryRegisterHook(
                ExtensionRouteEventStage.Trigger,
                (_, _) => ValueTask.FromResult(new ExtensionRouteHookResult(ExtensionRouteHookAction.Continue))))
        };
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current, capabilityFactory: factory);
        var generation = await PrepareAsync(manager, manifest, routeId);
        var original = Request("/original");

        var result = await generation.DispatchRouteHooksAsync(
            routeId,
            RoutingTestData.Id(961),
            ExtensionRouteEventStage.Trigger,
            original,
            null,
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.False(result.Cancelled);
        Assert.Same(original, result.Request);
        Assert.False(result.RequestReplaced);
        Assert.False(result.ResponseReplaced);
        Assert.Null(result.Response);
    }

    [Fact]
    public async Task TriggerReplaceRequestSetsFlagAndCarriesPayload()
    {
        using var fixture = TestExtensionDirectory.CreateJson();
        var manifest = Discover(fixture.RootPath);
        var routeId = RoutingTestData.Id(962);
        var original = Request("/original");
        var replacement = Request("/replaced");
        var factory = new RouteTestFactory
        {
            Configure = events =>
            {
                Assert.Same(ExtensionRouteRegistrationResult.Success, events.TryRegisterHook(
                    ExtensionRouteEventStage.Trigger,
                    (_, _) => ValueTask.FromResult(new ExtensionRouteHookResult(ExtensionRouteHookAction.Continue))));
                Assert.Same(ExtensionRouteRegistrationResult.Success, events.TryRegisterHook(
                    ExtensionRouteEventStage.Trigger,
                    (_, _) => ValueTask.FromResult(new ExtensionRouteHookResult(
                        ExtensionRouteHookAction.ReplaceRequest,
                        replacement))));
            }
        };
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current, capabilityFactory: factory);
        var generation = await PrepareAsync(manager, manifest, routeId);

        var result = await generation.DispatchRouteHooksAsync(
            routeId,
            RoutingTestData.Id(963),
            ExtensionRouteEventStage.Trigger,
            original,
            null,
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.False(result.Cancelled);
        Assert.True(result.RequestReplaced);
        Assert.False(result.ResponseReplaced);
        Assert.Same(replacement, result.Request);
        Assert.Equal("/replaced", result.Request.Path);
    }

    [Fact]
    public async Task ReturnContinueReturnsSameInstanceAndNoReplaceFlag()
    {
        using var fixture = TestExtensionDirectory.CreateJson();
        var manifest = Discover(fixture.RootPath);
        var routeId = RoutingTestData.Id(964);
        var factory = new RouteTestFactory
        {
            Configure = events => Assert.Same(ExtensionRouteRegistrationResult.Success, events.TryRegisterHook(
                ExtensionRouteEventStage.Return,
                (_, _) => ValueTask.FromResult(new ExtensionRouteHookResult(ExtensionRouteHookAction.Continue))))
        };
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current, capabilityFactory: factory);
        var generation = await PrepareAsync(manager, manifest, routeId);
        var original = Request("/original");
        var originalResponse = new ExtensionRouteResponseSnapshot(200);

        var result = await generation.DispatchRouteHooksAsync(
            routeId,
            RoutingTestData.Id(965),
            ExtensionRouteEventStage.Return,
            original,
            originalResponse,
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.False(result.Cancelled);
        Assert.Same(original, result.Request);
        Assert.Same(originalResponse, result.Response);
        Assert.False(result.RequestReplaced);
        Assert.False(result.ResponseReplaced);
    }

    [Fact]
    public async Task ReturnCancelForwardingFailsClosedUnchanged()
    {
        using var fixture = TestExtensionDirectory.CreateJson();
        var manifest = Discover(fixture.RootPath);
        var routeId = RoutingTestData.Id(966);
        var order = new List<int>();
        var factory = new RouteTestFactory
        {
            Configure = events =>
            {
                Assert.Same(ExtensionRouteRegistrationResult.Success, events.TryRegisterHook(ExtensionRouteEventStage.Return, (_, _) =>
                {
                    order.Add(1);
                    return ValueTask.FromResult(new ExtensionRouteHookResult(
                        ExtensionRouteHookAction.ReplaceResponse,
                        response: new ExtensionRouteResponseSnapshot(201)));
                }));
                Assert.Same(ExtensionRouteRegistrationResult.Success, events.TryRegisterHook(ExtensionRouteEventStage.Return, (_, _) =>
                {
                    order.Add(2);
                    return ValueTask.FromResult(new ExtensionRouteHookResult(ExtensionRouteHookAction.CancelForwarding));
                }));
            }
        };
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current, capabilityFactory: factory);
        var generation = await PrepareAsync(manager, manifest, routeId);
        var original = Request("/original");
        var originalResponse = new ExtensionRouteResponseSnapshot(200);

        var result = await generation.DispatchRouteHooksAsync(
            routeId,
            RoutingTestData.Id(967),
            ExtensionRouteEventStage.Return,
            original,
            originalResponse,
            TestContext.Current.CancellationToken);

        Assert.Equal(ExpectedRequestHookOrder, order);
        Assert.False(result.Succeeded);
        Assert.True(result.Cancelled);
        Assert.False(result.RequestReplaced);
        Assert.False(result.ResponseReplaced);
        Assert.Same(original, result.Request);
        Assert.Same(originalResponse, result.Response);
        Assert.Equal(200, result.Response!.StatusCode);
    }

    [Fact]
    public async Task ContinueLargeRequestBodyForwardsUnmodified()
    {
        using var fixture = TestExtensionDirectory.CreateJson();
        var manifest = Discover(fixture.RootPath);
        var routeId = RoutingTestData.Id(968);
        var maximum = ExtensionRouteSnapshotLimits.MaximumBodyBytes;
        var payload = new byte[maximum + 4096];
        for (var index = 0; index < payload.Length; index++)
        {
            payload[index] = (byte)(index % 251);
        }

        var observedBody = (byte[]?)null;
        var factory = new RouteTestFactory
        {
            Configure = events => Assert.Same(ExtensionRouteRegistrationResult.Success, events.TryRegisterHook(
                ExtensionRouteEventStage.Trigger,
                (context, _) =>
                {
                    observedBody = context.Request.Body.ToArray();
                    return ValueTask.FromResult(new ExtensionRouteHookResult(ExtensionRouteHookAction.Continue));
                }))
        };
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current, capabilityFactory: factory);
        var generation = await PrepareAsync(manager, manifest, routeId);
        var context = CreateContext(payload);

        var session = await HostRouteEvents.BeginAsync(
            context,
            CreateRoutingSnapshot(routeId, generation),
            CreateMatch(routeId),
            TestContext.Current.CancellationToken);

        Assert.NotNull(session);
        Assert.False(session!.Cancelled);
        Assert.NotNull(observedBody);
        Assert.Equal(maximum, observedBody!.Length);
        Assert.True(payload.AsSpan(0, maximum).SequenceEqual(observedBody));
        Assert.Equal(payload, await ReadBodyAsync(context.Request.Body));

        context.Response.StatusCode = StatusCodes.Status200OK;
        var result = await HostRouteEvents.CompleteAsync(
            context,
            session,
            RouteTargetExecutionResult.Handled,
            TestContext.Current.CancellationToken);

        Assert.Equal(RouteTargetExecutionResult.Handled, result);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task ContinueLargeResponseBodyPassesUnmodified()
    {
        using var fixture = TestExtensionDirectory.CreateJson();
        var manifest = Discover(fixture.RootPath);
        var routeId = RoutingTestData.Id(969);
        var maximum = ExtensionRouteSnapshotLimits.MaximumBodyBytes;
        var payload = new byte[maximum + 512];
        for (var index = 0; index < payload.Length; index++)
        {
            payload[index] = (byte)(index % 239);
        }

        var observedBody = (byte[]?)null;
        var factory = new RouteTestFactory
        {
            Configure = events => Assert.Same(ExtensionRouteRegistrationResult.Success, events.TryRegisterHook(
                ExtensionRouteEventStage.Return,
                (context, _) =>
                {
                    observedBody = context.Response!.Body.ToArray();
                    return ValueTask.FromResult(new ExtensionRouteHookResult(ExtensionRouteHookAction.Continue));
                }))
        };
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current, capabilityFactory: factory);
        var generation = await PrepareAsync(manager, manifest, routeId);
        var context = CreateContext([]);
        var originalBody = context.Response.Body;

        var session = await HostRouteEvents.BeginAsync(
            context,
            CreateRoutingSnapshot(routeId, generation),
            CreateMatch(routeId),
            TestContext.Current.CancellationToken);

        Assert.NotNull(session);
        Assert.False(session!.Cancelled);
        Assert.NotNull(session.ResponseBuffer);

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.Headers["X-Large"] = "yes";
        await context.Response.Body.WriteAsync(payload, TestContext.Current.CancellationToken);

        var result = await HostRouteEvents.CompleteAsync(
            context,
            session,
            RouteTargetExecutionResult.Handled,
            TestContext.Current.CancellationToken);

        Assert.Equal(RouteTargetExecutionResult.Handled, result);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("yes", context.Response.Headers["X-Large"].ToString());
        Assert.Same(originalBody, context.Response.Body);
        Assert.Null(session.ResponseBuffer);
        Assert.Equal(payload, await ReadBodyAsync(originalBody));
        Assert.NotNull(observedBody);
        Assert.Equal(maximum, observedBody!.Length);
        Assert.True(payload.AsSpan(0, maximum).SequenceEqual(observedBody));
    }

    [Fact]
    public async Task TriggerOnlyHookLeavesResponseStreamingUntouched()
    {
        using var fixture = TestExtensionDirectory.CreateJson();
        var manifest = Discover(fixture.RootPath);
        var routeId = RoutingTestData.Id(970);
        var factory = new RouteTestFactory
        {
            Configure = events => Assert.Same(ExtensionRouteRegistrationResult.Success, events.TryRegisterHook(
                ExtensionRouteEventStage.Trigger,
                (_, _) => ValueTask.FromResult(new ExtensionRouteHookResult(ExtensionRouteHookAction.Continue))))
        };
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current, capabilityFactory: factory);
        var generation = await PrepareAsync(manager, manifest, routeId);
        var context = CreateContext([]);
        var originalBody = context.Response.Body;

        var session = await HostRouteEvents.BeginAsync(
            context,
            CreateRoutingSnapshot(routeId, generation),
            CreateMatch(routeId),
            TestContext.Current.CancellationToken);

        Assert.NotNull(session);
        Assert.False(session!.Cancelled);
        Assert.Null(session.ResponseBuffer);
        Assert.Same(originalBody, context.Response.Body);

        context.Response.StatusCode = StatusCodes.Status200OK;
        await context.Response.Body.WriteAsync(
            Encoding.UTF8.GetBytes("streamed"),
            TestContext.Current.CancellationToken);
        var result = await HostRouteEvents.CompleteAsync(
            context,
            session,
            RouteTargetExecutionResult.Handled,
            TestContext.Current.CancellationToken);

        Assert.Equal(RouteTargetExecutionResult.Handled, result);
        Assert.Same(originalBody, context.Response.Body);
        Assert.Equal("streamed", Encoding.UTF8.GetString(await ReadBodyAsync(originalBody)));
    }

    [Fact]
    public async Task ContinueOnWebSocketUpgradeResponsePasses()
    {
        using var fixture = TestExtensionDirectory.CreateJson();
        var manifest = Discover(fixture.RootPath);
        var routeId = RoutingTestData.Id(971);
        var observedStatus = 0;
        var observedBody = (byte[]?)null;
        var factory = new RouteTestFactory
        {
            Configure = events => Assert.Same(ExtensionRouteRegistrationResult.Success, events.TryRegisterHook(
                ExtensionRouteEventStage.Return,
                (context, _) =>
                {
                    observedStatus = context.Response!.StatusCode;
                    observedBody = context.Response.Body.ToArray();
                    return ValueTask.FromResult(new ExtensionRouteHookResult(ExtensionRouteHookAction.Continue));
                }))
        };
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current, capabilityFactory: factory);
        var generation = await PrepareAsync(manager, manifest, routeId);
        var context = CreateContext([]);
        context.Request.Headers["Connection"] = "Upgrade";
        context.Request.Headers["Upgrade"] = "websocket";
        var originalBody = context.Response.Body;

        var session = await HostRouteEvents.BeginAsync(
            context,
            CreateRoutingSnapshot(routeId, generation),
            CreateMatch(routeId),
            TestContext.Current.CancellationToken);

        Assert.NotNull(session);
        Assert.False(session!.Cancelled);
        Assert.Null(session.ResponseBuffer);
        Assert.Same(originalBody, context.Response.Body);

        context.Response.StatusCode = StatusCodes.Status101SwitchingProtocols;
        context.Response.Headers["Connection"] = "Upgrade";
        context.Response.Headers["Upgrade"] = "websocket";
        var result = await HostRouteEvents.CompleteAsync(
            context,
            session,
            RouteTargetExecutionResult.Handled,
            TestContext.Current.CancellationToken);

        Assert.Equal(RouteTargetExecutionResult.Handled, result);
        Assert.Equal(StatusCodes.Status101SwitchingProtocols, context.Response.StatusCode);
        Assert.Equal("Upgrade", context.Response.Headers["Connection"].ToString());
        Assert.Equal("websocket", context.Response.Headers["Upgrade"].ToString());
        Assert.Equal(StatusCodes.Status101SwitchingProtocols, observedStatus);
        Assert.NotNull(observedBody);
        Assert.Empty(observedBody!);
        Assert.Same(originalBody, context.Response.Body);
        Assert.Empty(await ReadBodyAsync(originalBody));
    }

    [Fact]
    public async Task ReplaceResponseOnUpgradeStillFailsClosed()
    {
        using var fixture = TestExtensionDirectory.CreateJson();
        var manifest = Discover(fixture.RootPath);
        var routeId = RoutingTestData.Id(972);
        var factory = new RouteTestFactory
        {
            Configure = events => Assert.Same(ExtensionRouteRegistrationResult.Success, events.TryRegisterHook(
                ExtensionRouteEventStage.Return,
                (_, _) => ValueTask.FromResult(new ExtensionRouteHookResult(
                    ExtensionRouteHookAction.ReplaceResponse,
                    response: new ExtensionRouteResponseSnapshot(
                        StatusCodes.Status200OK,
                        [new("X-Tampered", ["yes"])],
                        Encoding.UTF8.GetBytes("tampered"))))))
        };
        await using var manager = new ExtensionRuntimeManager(HostApiVersion.Current, capabilityFactory: factory);
        var generation = await PrepareAsync(manager, manifest, routeId);
        var context = CreateContext([]);

        var session = await HostRouteEvents.BeginAsync(
            context,
            CreateRoutingSnapshot(routeId, generation),
            CreateMatch(routeId),
            TestContext.Current.CancellationToken);

        Assert.NotNull(session);
        Assert.False(session!.Cancelled);

        context.Response.StatusCode = StatusCodes.Status101SwitchingProtocols;
        context.Response.Headers["Connection"] = "Upgrade";
        context.Response.Headers["Upgrade"] = "websocket";
        var result = await HostRouteEvents.CompleteAsync(
            context,
            session,
            RouteTargetExecutionResult.Handled,
            TestContext.Current.CancellationToken);

        Assert.Equal(RouteTargetExecutionResult.Cancelled, result);
        Assert.Equal(499, context.Response.StatusCode);
        Assert.Empty(context.Response.Headers);
        Assert.Empty(await ReadBodyAsync(context.Response.Body));
    }

    private static async Task<ExtensionDispatchGeneration> PrepareAsync(
        ExtensionRuntimeManager manager,
        ExtensionManifest manifest,
        Guid routeId,
        ExtensionSettingsConfiguration? settings = null,
        IEnumerable<string>? handlerIds = null)
    {
        settings ??= new ExtensionSettingsConfiguration(manifest.Id, 1, "{}", 0);
        var prepared = await manager.PrepareGenerationAsync(
            ImmutableArray.Create(new ExtensionRuntimeDescriptor(
                manifest,
                settings,
                handlerIds ?? ["fixture.handler"],
                routeIds: [routeId])),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(prepared.Succeeded, prepared.FailureCode.ToString());
        var preparation = Assert.IsType<ExtensionGenerationPreparation>(prepared.Preparation);
        var ready = await preparation.ReadyToPublishAsync(TestContext.Current.CancellationToken);
        Assert.True(ready.Succeeded, ready.FailureCode.ToString());
        Assert.True(await preparation.CompletePublicationAsync());
        return Assert.IsType<ExtensionDispatchGeneration>(ready.Generation);
    }

    private static ExtensionManifest Discover(string rootPath)
    {
        var result = ExtensionManifestDiscovery.Discover(rootPath);
        Assert.True(result.Succeeded, result.FailureCode.ToString());
        return Assert.IsType<ExtensionManifest>(result.Manifest);
    }

    private static ExtensionRouteRequestSnapshot Request(string path) =>
        new("GET", path, host: "example.test");

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void AssertFailClosed(
        ExtensionRouteHookDispatchResult result,
        ExtensionRouteRequestSnapshot original)
    {
        Assert.False(result.Succeeded);
        Assert.True(result.Cancelled);
        Assert.False(result.RequestReplaced);
        Assert.False(result.ResponseReplaced);
        Assert.Equal(original.Path, result.Request.Path);
        Assert.Null(result.Response);
    }

    private static HostRoutingSnapshot CreateRoutingSnapshot(
        Guid routeId,
        ExtensionDispatchGeneration generation)
    {
        var route = RoutingTestData.CreateRoute(routeId, RouteMatcherType.Exact, "/original");
        var configuration = RoutingTestData.CreateSnapshot(1, ImmutableArray.Create(route));
        return new HostRoutingSnapshot(
            configuration,
            RoutingTestData.Build(route),
            ImmutableDictionary<Guid, ExecutableRoute>.Empty,
            generation,
            ImmutableDictionary<Guid, string?>.Empty);
    }

    private static RouteMatch CreateMatch(Guid routeId) =>
        RoutingTestData.Build(RoutingTestData.CreateRoute(routeId, RouteMatcherType.Exact, "/original"))
            .Match(new RouteMatchInput("/original", "example.test", "GET"))
            .Match!;

    private static DefaultHttpContext CreateContext(byte[] requestBody)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/original";
        context.Request.Host = new HostString("example.test");
        context.Request.Body = new MemoryStream(requestBody);
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static async Task<byte[]> ReadBodyAsync(Stream body)
    {
        if (body.CanSeek)
        {
            body.Position = 0;
        }

        using var buffer = new MemoryStream();
        await body.CopyToAsync(buffer, TestContext.Current.CancellationToken);
        return buffer.ToArray();
    }

    private sealed class RouteTestFactory : IExtensionCapabilityFactory, IExtensionCapabilityFactoryRouteEvents
    {
        internal Action<IExtensionRouteEvents>? Configure { get; init; }
        internal IExtensionRouteEvents? RouteEvents { get; private set; }

        public ExtensionCapabilitySet Create(string extensionId, Func<string, bool> handlerIsOwned) =>
            throw new NotSupportedException();

        public ExtensionCapabilitySet CreateWithRouteEvents(
            string extensionId,
            Func<string, bool> handlerIsOwned,
            IExtensionRouteEvents routeEvents)
        {
            RouteEvents = routeEvents;
            Configure?.Invoke(routeEvents);
            var unsupported = UnsupportedExtensionCapabilities.Create();
            return new ExtensionCapabilitySet(
                unsupported.ConfigurationApi,
                unsupported.Routes,
                unsupported.Services,
                unsupported.Endpoints,
                unsupported.FullConfiguration,
                unsupported.Supervisor,
                routeEvents,
                unsupported.LogWriter);
        }
    }
}
