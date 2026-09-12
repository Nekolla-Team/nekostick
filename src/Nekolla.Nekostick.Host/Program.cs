using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Domain;
using DomainExtensionLoadState = Nekolla.Nekostick.Domain.ExtensionLoadState;
using Nekolla.Nekostick.Extensions;
using Nekolla.Nekostick.Persistence;
using Nekolla.Nekostick.Proxy;
using Nekolla.Nekostick.Supervision;

namespace Nekolla.Nekostick.Host;

internal static class Program
{
    private const string InvalidListenAddressMessage = "The listen address is invalid.";
    private const string HostStartupFailureMessage = "Host startup failed.";

    internal static async Task<int> Main(string[] args)
    {
        CliCommandKind? diagnosticCommand = SelectDiagnosticCommand(args);
        using var cancellationSource = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellationSource.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;

        try
        {
            var commandResult = CliCommandParser.Parse(args, ReadBootstrapEnvironment());
            if (!commandResult.IsSuccess)
            {
                if (diagnosticCommand is { } failedDiagnosticCommand)
                {
                    return WriteDiagnosticFailure(failedDiagnosticCommand);
                }

                var error = commandResult.Error!;
                Console.Error.WriteLine($"BOOTSTRAP {error.Code}: {error.Message}");
                return 1;
            }

            var command = commandResult.Command!;
            if (command.Kind is CliCommandKind.Status or CliCommandKind.Doctor)
            {
                diagnosticCommand = command.Kind;
            }

            if (command.Kind == CliCommandKind.Run &&
                !IPAddress.TryParse(command.BootstrapOptions.ListenAddress, out _))
            {
                Console.Error.WriteLine($"BOOTSTRAP {BootstrapErrorCode.InvalidListenAddress}: {InvalidListenAddressMessage}");
                return 1;
            }

            return await ExecuteAsync(command, cancellationSource.Token);
        }
        catch (OperationCanceledException)
        {
            if (diagnosticCommand is { } failedDiagnosticCommand)
            {
                return WriteDiagnosticFailure(failedDiagnosticCommand);
            }

            return 0;
        }
        catch (Exception exception)
        {
            if (diagnosticCommand is { } failedDiagnosticCommand)
            {
                Console.Error.WriteLine(exception.ToString());
                return WriteDiagnosticFailure(failedDiagnosticCommand);
            }

            Console.Error.WriteLine($"HOST_EVENT {HostEventIds.HostStartupFailed.Id}: {HostStartupFailureMessage}");
            Console.Error.WriteLine(exception.ToString());
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    private static async Task<int> ExecuteAsync(CliCommand command, CancellationToken cancellationToken)
    {
        var options = command.BootstrapOptions;
        var listenAddress = command.Kind == CliCommandKind.Run
            ? IPAddress.Parse(options.ListenAddress)
            : null;
        await using var app = BuildApplication(command, listenAddress);
        var logger = app.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger(HostLoggerCategory.Startup);

        var inspection = await InspectDatabaseAsync(
            app,
            command.Kind is CliCommandKind.Status or CliCommandKind.Doctor,
            cancellationToken);
        if (!inspection.Migration.IsSuccess)
        {
            var error = inspection.Migration.Error!;
            if (error.Detail is { } detail)
            {
                Console.Error.WriteLine(detail);
            }

            if (command.Kind == CliCommandKind.Status)
            {
                var report = CreateStatusFailureReport(error.Code);
                return DiagnosticJson.Write(report, logger);
            }

            if (command.Kind == CliCommandKind.Doctor)
            {
                var report = CreateDoctorFailureReport(error.Code);
                return DiagnosticJson.Write(report, logger);
            }

            HostLogMessages.DatabaseStartupFailed(logger, error.Code, error.Message);
            return 1;
        }

        if (command.Kind == CliCommandKind.Run)
        {
            var snapshotReader = app.Services.GetRequiredService<IHostConfigurationSnapshotReader>();
            var snapshotResult = await snapshotReader.ReadCompleteAsync(cancellationToken);
            if (!snapshotResult.IsSuccess || snapshotResult.Value is null)
            {
                if (snapshotResult.Errors.Any(error => error.Code == ConfigurationErrorCode.StorageUnavailable))
                {
                    HostLogMessages.ConfigurationRefreshUnavailable(logger);
                }
                else
                {
                    HostLogMessages.ConfigurationSnapshotRejected(logger);
                }

                foreach (var configurationError in snapshotResult.Errors)
                {
                    Console.Error.WriteLine(
                        $"HOST_EVENT {HostEventIds.ConfigurationSnapshotRejected.Id}: " +
                        $"{configurationError.Code}: {configurationError.Message}");
                }

                return 1;
            }

            var publisher = app.Services.GetRequiredService<HostConfigurationPublisher>();
            if (!await publisher.PublishAsync(snapshotResult.Value, cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                HostLogMessages.ConfigurationSnapshotRejected(logger);
                return 1;
            }

            app.Services.GetRequiredService<HostRuntimeState>().MarkSnapshotAccepted();
            ConfigureRunPipeline(app);
            var startupLogger = app.Services.GetRequiredService<ILoggerFactory>()
                .CreateLogger(HostLoggerCategory.Startup);
            var listenUrl = $"http://{options.ListenAddress}:{options.ListenPort}";
            app.Lifetime.ApplicationStarted.Register(() =>
            {
                HostLogMessages.NowListening(startupLogger, listenUrl);
                HostLogMessages.ApplicationStarted(startupLogger);
            });
            var terminationState = app.Services.GetRequiredService<HostTerminationState>();
            await app.RunAsync(cancellationToken);
            return terminationState.ExitCode;
        }

        if (inspection.Revision is null ||
            !inspection.Revision.IsSuccess ||
            inspection.Revision.Value is null)
        {
            if (command.Kind == CliCommandKind.Status)
            {
                var report = CreateStatusFailureReport(null);
                return DiagnosticJson.Write(report, logger);
            }

            var doctorReport = CreateDoctorFailureReport(null);
            return DiagnosticJson.Write(doctorReport, logger);
        }

        var configurationVersion = inspection.Revision.Value.Version;
        if (command.Kind == CliCommandKind.Status)
        {
            var extensionSummary = await ReadExtensionStatusAsync(app, cancellationToken);
            var statusExitCode = extensionSummary.State == "unavailable" ? 1 : 0;
            var report = new StatusReport(
                configurationVersion,
                "ready",
                "valid",
                statusExitCode,
                extensionSummary);
            return DiagnosticJson.Write(report, logger);
        }

        var doctorInspection = await InspectLocalExtensionsAsync(app, cancellationToken);
        var doctorExitCode = doctorInspection.IsHealthy ? 0 : 1;
        var doctorSuccessReport = new DoctorReport(
            configurationVersion,
            "passed",
            "passed",
            "unavailable",
            doctorExitCode,
            doctorInspection.ExtensionState,
            doctorInspection.LocalDirectoryState,
            doctorInspection.Checks);
        return DiagnosticJson.Write(doctorSuccessReport, logger);
    }

    private static WebApplication BuildApplication(CliCommand command, IPAddress? listenAddress)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = []
        });

        // Bootstrap settings are parsed explicitly. No configuration provider may supply
        // business settings to this host.
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        var minimumLevel = Enum.TryParse<LogLevel>(
            command.BootstrapOptions.MinimumLevel, ignoreCase: true, out var parsedLevel)
            ? parsedLevel
            : LogLevel.Information;

        // The factory-level filter decides what reaches the provider. Application output
        // follows the configured level; framework categories stay at Warning unless Debug
        // or Trace was requested. EF logs use the configured level only with explicit opt-in.
        builder.Logging.SetMinimumLevel(minimumLevel);
        var frameworkLevel = minimumLevel <= LogLevel.Debug ? minimumLevel : LogLevel.Warning;
        builder.Logging.AddFilter("Microsoft", frameworkLevel);
        builder.Logging.AddFilter("System", frameworkLevel);
        var entityFrameworkLevel = command.BootstrapOptions.IncludeEfLogs
            ? minimumLevel
            : LogLevel.Warning;
        builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", entityFrameworkLevel);
        builder.Logging.AddProvider(new SafeConsoleLoggerProvider(minimumLevel));
        builder.Host.UseConsoleLifetime();

        var bootstrap = command.BootstrapOptions;
        var nodeOptions = new HostNodeOptions(
            command.RunOptions.SkipExtensions,
            command.RunOptions.DisableSupervisor,
            command.RunOptions.ReadOnly,
            dataDirectory: bootstrap.DataDirectory);
        Directory.CreateDirectory(nodeOptions.DataDirectory);
        builder.Services.AddSingleton(nodeOptions);
        builder.Services.AddSingleton(new HostRuntimeOptions(
            bootstrap.ConnectionString,
            bootstrap.NodeId,
            command.RunOptions.ReadOnly));
        builder.Services.AddSingleton<HostConfigurationSnapshotHolder>(serviceProvider =>
            new HostConfigurationSnapshotHolder(
                serviceProvider.GetRequiredService<ILogger<HostConfigurationSnapshotHolder>>()));
        builder.Services.AddSingleton<IHostConfigurationSnapshotAccessor>(serviceProvider =>
            serviceProvider.GetRequiredService<HostConfigurationSnapshotHolder>());
        builder.Services.AddSingleton<IHostRoutingSnapshotAccessor>(serviceProvider =>
            new HostRoutingSnapshotAccessor(
                serviceProvider.GetRequiredService<HostConfigurationSnapshotHolder>()));
        builder.Services.AddSingleton<HostRequestAdmission>(_ => new HostRequestAdmission());
        builder.Services.AddSingleton<ExtensionRuntimeManager>(serviceProvider =>
            new ExtensionRuntimeManager(
                HostApiVersion.Current,
                capabilityFactory: serviceProvider.GetService<IExtensionCapabilityFactory>(),
                logger: serviceProvider
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger(HostLoggerCategory.Extensions),
                dataDirectory: serviceProvider.GetRequiredService<HostNodeOptions>().DataDirectory));
        builder.Services.AddSingleton<HostConfigurationPublisher>(serviceProvider =>
        {
            var runtimeManager = serviceProvider.GetRequiredService<ExtensionRuntimeManager>();
            return new HostConfigurationPublisher(
                serviceProvider.GetRequiredService<HostConfigurationSnapshotHolder>(),
                runtimeManager,
                serviceProvider.GetRequiredService<HostNodeOptions>(),
                serviceProvider.GetRequiredService<ILogger<HostConfigurationPublisher>>(),
                dbContextFactory: serviceProvider.GetRequiredService<IDbContextFactory<NekostickDbContext>>(),
                runtimeState: serviceProvider.GetRequiredService<HostRuntimeState>(),
                snapshotReader: serviceProvider.GetRequiredService<IHostConfigurationSnapshotReader>(),
                runtimeOptions: serviceProvider.GetRequiredService<HostRuntimeOptions>(),
                hostApiVersion: runtimeManager.ApiVersion);
        });
        builder.Services.AddSingleton<IRouteFallbackDispatcher, ExtensionRouteFallbackDispatcher>();
        builder.Services.AddMicroserviceProxy();
        builder.Services.AddSingleton<IRouteTargetExecutor>(serviceProvider =>
            new HostRouteTargetExecutor(
                serviceProvider.GetRequiredService<MicroserviceHttpExecutor>(),
                serviceProvider.GetService<IHostServiceLifecycleCoordinator>(),
                serviceProvider
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger(HostLoggerCategory.Routing)));
        builder.Services.AddSingleton<HostRouteDispatcher>(serviceProvider =>
            new HostRouteDispatcher(
                serviceProvider.GetRequiredService<IHostRoutingSnapshotAccessor>(),
                serviceProvider.GetRequiredService<IRouteFallbackDispatcher>(),
                serviceProvider.GetRequiredService<IRouteTargetExecutor>(),
                serviceProvider.GetRequiredService<HostRequestAdmission>(),
                serviceProvider
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger(HostLoggerCategory.Routing)));
        builder.Services.AddSingleton<HostRuntimeState>();
        builder.Services.AddSingleton<HostTerminationState>();
        builder.Services.AddDbContextFactory<NekostickDbContext>(dbContextOptions =>
            dbContextOptions.UseNekostickPostgres(bootstrap.ConnectionString));
        builder.Services.AddSingleton<IMigrationSchemaValidator>(serviceProvider =>
            new PostgresMigrationSchemaValidator(
                PersistenceDatabaseDefaults.Schema,
                serviceProvider.GetRequiredService<ILogger<PostgresMigrationSchemaValidator>>()));
        builder.Services.AddSingleton<IStartupDatabaseProbe>(serviceProvider =>
            new PostgresMigrationCoordinator(
                bootstrap.ConnectionString,
                serviceProvider.GetRequiredService<IMigrationSchemaValidator>(),
                logger: serviceProvider.GetRequiredService<ILogger<PostgresMigrationCoordinator>>()));
        builder.Services.AddScoped<IConfigurationRevisionReader>(serviceProvider =>
            new EfConfigurationRevisionReader(
                serviceProvider.GetRequiredService<NekostickDbContext>(),
                serviceProvider.GetRequiredService<ILogger<EfConfigurationRevisionReader>>()));
        builder.Services.AddSingleton<IHostConfigurationSnapshotReader, EfHostConfigurationSnapshotReader>();
        builder.Services.AddScoped<EfHostConfigApi>(serviceProvider =>
            new EfHostConfigApi(
                serviceProvider.GetRequiredService<NekostickDbContext>(),
                logger: serviceProvider.GetRequiredService<ILogger<EfHostConfigApi>>()));
        builder.Services.AddScoped<IHostConfigApi>(serviceProvider =>
            new HostConfigApiReadOnlyDecorator(
                serviceProvider.GetRequiredService<EfHostConfigApi>(),
                serviceProvider.GetRequiredService<HostRuntimeOptions>()));
        builder.Services.AddScoped<IExtensionOwnedConfigurationApi, EfExtensionOwnedConfigurationApi>();
        builder.Services.AddSingleton<IExtensionCapabilityFactory>(serviceProvider =>
            new ExtensionCapabilityFactory(
                serviceProvider.GetRequiredService<IServiceScopeFactory>(),
                serviceProvider.GetRequiredService<HostRuntimeState>(),
                serviceProvider));

        if (command.Kind == CliCommandKind.Run)
        {
            builder.Services.AddSingleton<IConfigurationChangeSignal, PostgresConfigurationChangeSignal>();
            builder.Services.AddSingleton<IHostNodeActivityLease>(serviceProvider =>
                new PostgresHostNodeActivityLease(
                    serviceProvider.GetRequiredService<HostRuntimeOptions>(),
                    logger: serviceProvider.GetRequiredService<ILogger<PostgresHostNodeActivityLease>>()));
            builder.Services.AddHostedService<HostConfigurationRefreshService>();
            if (!command.RunOptions.DisableSupervisor)
            {
                builder.Services.AddSingleton<HostServiceEndpointSnapshotPublisher>(serviceProvider =>
                    new HostServiceEndpointSnapshotPublisher(
                        serviceProvider.GetRequiredService<ExtensionRuntimeManager>(),
                        serviceProvider.GetRequiredService<ILogger<HostServiceEndpointSnapshotPublisher>>()));
                builder.Services.AddSingleton<IHostServiceEndpointSnapshotAccessor>(serviceProvider =>
                    serviceProvider.GetRequiredService<HostServiceEndpointSnapshotPublisher>());
                builder.Services.AddSingleton<IMicroserviceEndpointResolver, HostServiceEndpointResolver>();
                var helperPath = NativeHelperExtractor.TryExtract();
                builder.Services.AddSingleton<IProcessOutputSink>(serviceProvider =>
                    new HostProcessOutputLogSink(
                        serviceProvider
                            .GetRequiredService<ILoggerFactory>()
                            .CreateLogger(HostLoggerCategory.Supervision)));
                builder.Services.AddSingleton<IProcessExecutor>(serviceProvider =>
                    new PosixProcessExecutor(
                        helperPath,
                        outputSink: serviceProvider.GetRequiredService<IProcessOutputSink>(),
                        logger: serviceProvider.GetRequiredService<ILogger<PosixProcessExecutor>>()));
                builder.Services.AddSingleton<IServiceHealthProbe>(serviceProvider =>
                    new ServiceHealthProbe(
                        serviceProvider.GetRequiredService<IProcessExecutor>(),
                        logger: serviceProvider.GetRequiredService<ILogger<ServiceHealthProbe>>()));
                builder.Services.AddSingleton<HostPortLeaseStoreAdapter>(serviceProvider =>
                    new HostPortLeaseStoreAdapter(
                        serviceProvider.GetRequiredService<IDbContextFactory<NekostickDbContext>>(),
                        serviceProvider.GetRequiredService<HostRuntimeState>(),
                        serviceProvider.GetRequiredService<ILogger<HostPortLeaseStoreAdapter>>()));
                builder.Services.AddSingleton<HostServiceLifecycleManager>(serviceProvider =>
                    new HostServiceLifecycleManager(
                        serviceProvider.GetRequiredService<IProcessExecutor>(),
                        serviceProvider.GetRequiredService<IServiceHealthProbe>(),
                        serviceProvider.GetRequiredService<IPortLeaseStore>(),
                        serviceProvider.GetRequiredService<HostConfigurationSnapshotHolder>(),
                        serviceProvider.GetRequiredService<HostServiceEndpointSnapshotPublisher>(),
                        serviceProvider.GetRequiredService<HostRuntimeState>(),
                        serviceProvider.GetRequiredService<HostRuntimeOptions>(),
                        serviceProvider.GetRequiredService<ILogger<HostServiceLifecycleManager>>(),
                        serviceProvider.GetRequiredService<IMicroserviceDrainTracker>(),
                        serviceProvider.GetRequiredService<HostNodeOptions>(),
                        serviceProvider.GetRequiredService<ExtensionRuntimeManager>()));
                builder.Services.AddSingleton<IPortLeaseStore>(serviceProvider =>
                    serviceProvider.GetRequiredService<HostPortLeaseStoreAdapter>());
                builder.Services.AddSingleton<IHostServiceLifecycleCoordinator>(serviceProvider =>
                    serviceProvider.GetRequiredService<HostServiceLifecycleManager>());
                builder.Services.AddSingleton<IHostServiceRuntimeSnapshotAccessor>(serviceProvider =>
                    serviceProvider.GetRequiredService<HostServiceLifecycleManager>());
                builder.Services.AddSingleton<IHostServiceEndpointAuthority>(serviceProvider =>
                    serviceProvider.GetRequiredService<HostServiceLifecycleManager>());
                builder.Services.AddHostedService(serviceProvider =>
                    serviceProvider.GetRequiredService<HostServiceLifecycleManager>());
                builder.Services.AddHostedService<HostServiceEndpointPublicationService>();
                builder.Services.AddHostedService<HostNodeRegistrationService>();
            }

            // ConfigureKestrel only builds endpoint options. The endpoint is not bound until
            // RunAsync, which is called after InspectDatabaseAsync has succeeded.
            builder.WebHost.ConfigureKestrel(kestrelOptions =>
            {
                kestrelOptions.Limits.MaxRequestBodySize = GlobalSettingsConfiguration.HardMaximumRequestBodyBytes;
                kestrelOptions.Limits.MaxRequestHeadersTotalSize =
                    checked((int)GlobalSettingsConfiguration.HardMaximumRequestHeaderBytes);
                // This fixed server timeout protects only header reception; application body reads
                // use the immutable snapshot setting at the dispatcher boundary.
                kestrelOptions.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(30);
                kestrelOptions.Listen(listenAddress!, bootstrap.ListenPort, listenOptions =>
                {
                    listenOptions.Protocols = HttpProtocols.Http1;
                });
            });
        }

        return builder.Build();
    }

    private static async Task<DatabaseInspection> InspectDatabaseAsync(
        WebApplication app,
        bool readRevision,
        CancellationToken cancellationToken)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NekostickDbContext>();
        var probe = scope.ServiceProvider.GetRequiredService<IStartupDatabaseProbe>();
        var migration = await probe.MigrateAndValidateAsync(dbContext, cancellationToken);
        if (!readRevision || !migration.IsSuccess)
        {
            return new DatabaseInspection(migration, null);
        }

        var revisionReader = scope.ServiceProvider.GetRequiredService<IConfigurationRevisionReader>();
        var revision = await revisionReader.ReadCurrentAsync(cancellationToken);
        return new DatabaseInspection(migration, revision);
    }

    private static async Task<ExtensionSummary> ReadExtensionStatusAsync(
        WebApplication app,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var dbContext = await app.Services
                .GetRequiredService<IDbContextFactory<NekostickDbContext>>()
                .CreateDbContextAsync(cancellationToken);
            var records = await dbContext.ExtensionRecords
                .AsNoTracking()
                .OrderBy(value => value.ExtensionId)
                .ToListAsync(cancellationToken);
            var nodeStates = await dbContext.ExtensionNodeStates
                .AsNoTracking()
                .OrderBy(value => value.NodeId)
                .ToListAsync(cancellationToken);

            var groups = new List<ExtensionNodeGroup>(records.Count);
            var loaded = 0;
            var failed = 0;
            foreach (var record in records)
            {
                var states = nodeStates
                    .Where(value => value.ExtensionRecordId == record.Id)
                    .OrderBy(value => value.NodeId, StringComparer.Ordinal)
                    .Select(value =>
                    {
                        var loadState = value.LoadState.ToString();
                        if (string.Equals(loadState, nameof(DomainExtensionLoadState.Loaded), StringComparison.Ordinal))
                        {
                            loaded++;
                        }
                        else if (string.Equals(loadState, nameof(DomainExtensionLoadState.Failed), StringComparison.Ordinal))
                        {
                            failed++;
                        }

                        return new ExtensionNodeStatus(
                            value.NodeId,
                            loadState,
                            value.FailureCode,
                            value.ObservedContentHash);
                    })
                    .ToArray();
                groups.Add(new ExtensionNodeGroup(
                    record.ExtensionId,
                    record.ContentHash,
                    GetContentHashConsistency(record.ContentHash, states.Select(value => value.ObservedContentHash).ToArray()),
                    states));
            }

            var state = groups.Count == 0
                ? "not-started"
                : failed > 0 || groups.Any(value => value.ContentHashConsistency is "inconsistent" or "missing" or "unobserved")
                    ? "degraded"
                    : "ready";
            return new ExtensionSummary(loaded, failed, state, groups);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new ExtensionSummary(0, 0, "unavailable");
        }
    }

    private static async Task<DoctorExtensionInspection> InspectLocalExtensionsAsync(
        WebApplication app,
        CancellationToken cancellationToken)
    {
        var nodeOptions = app.Services.GetRequiredService<HostNodeOptions>();
        var logger = app.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger(HostLoggerCategory.Startup);
        var localHashes = new Dictionary<string, string?>(StringComparer.Ordinal);
        var duplicateIds = new HashSet<string>(StringComparer.Ordinal);
        var hasUnreadableDirectories = false;
        var localDirectoryState = "passed";
        try
        {
            if (Directory.Exists(nodeOptions.ExtensionsRootPath))
            {
                foreach (var directory in Directory.EnumerateDirectories(nodeOptions.ExtensionsRootPath))
                {
                    var discovery = ExtensionManifestDiscovery.Discover(directory, logger);
                    if (!discovery.Succeeded || discovery.Manifest is not { } manifest)
                    {
                        hasUnreadableDirectories = true;
                        continue;
                    }

                    if (!localHashes.TryAdd(manifest.Id, ExtensionContentDigest.TryCompute(manifest, logger)))
                    {
                        duplicateIds.Add(manifest.Id);
                        hasUnreadableDirectories = true;
                    }
                }

                localDirectoryState = hasUnreadableDirectories ? "failed" : "passed";
            }
        }
        catch (Exception exception)
        {
            HostLogMessages.ExtensionScanFailed(logger, exception, "InspectLocalExtensions");
            localDirectoryState = "failed";
        }

        try
        {
            await using var dbContext = await app.Services
                .GetRequiredService<IDbContextFactory<NekostickDbContext>>()
                .CreateDbContextAsync(cancellationToken);
            var records = await dbContext.ExtensionRecords
                .AsNoTracking()
                .OrderBy(value => value.ExtensionId)
                .ToListAsync(cancellationToken);
            var checks = new List<ExtensionLocalCheck>(records.Count);
            foreach (var record in records)
            {
                if (duplicateIds.Contains(record.ExtensionId))
                {
                    checks.Add(new ExtensionLocalCheck(
                        record.ExtensionId,
                        "failed",
                        "duplicate",
                        record.ContentHash,
                        null));
                    continue;
                }

                if (!localHashes.TryGetValue(record.ExtensionId, out var observedHash))
                {
                    checks.Add(new ExtensionLocalCheck(
                        record.ExtensionId,
                        "failed",
                        "missing",
                        record.ContentHash,
                        null));
                    continue;
                }

                if (record.ContentHash is null)
                {
                    checks.Add(new ExtensionLocalCheck(
                        record.ExtensionId,
                        "passed",
                        "not-required",
                        null,
                        observedHash));
                    continue;
                }

                if (observedHash is null)
                {
                    checks.Add(new ExtensionLocalCheck(
                        record.ExtensionId,
                        "failed",
                        "unavailable",
                        record.ContentHash,
                        null));
                    continue;
                }

                var consistency = string.Equals(record.ContentHash, observedHash, StringComparison.OrdinalIgnoreCase)
                    ? "consistent"
                    : "inconsistent";
                checks.Add(new ExtensionLocalCheck(
                    record.ExtensionId,
                    consistency == "consistent" ? "passed" : "failed",
                    consistency,
                    record.ContentHash,
                    observedHash));
            }

            var healthy = localDirectoryState == "passed" && checks.All(value => value.Status == "passed");
            return new DoctorExtensionInspection(
                healthy ? "passed" : "failed",
                localDirectoryState,
                checks,
                healthy);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new DoctorExtensionInspection(
                "unavailable",
                localDirectoryState,
                Array.Empty<ExtensionLocalCheck>(),
                false);
        }
    }

    private static string GetContentHashConsistency(
        string? expectedContentHash,
        string?[] observedContentHashes)
    {
        if (expectedContentHash is null)
        {
            return "not-required";
        }

        if (observedContentHashes.Length == 0)
        {
            return "unobserved";
        }

        if (observedContentHashes.Any(value => value is null))
        {
            return "missing";
        }

        return observedContentHashes.All(value =>
                string.Equals(expectedContentHash, value, StringComparison.OrdinalIgnoreCase))
            ? "consistent"
            : "inconsistent";
    }

    private static void ConfigureRunPipeline(WebApplication app)
    {
        app.UseWebSockets();
        app.Run(context =>
            context.RequestServices.GetRequiredService<HostRouteDispatcher>().DispatchAsync(context));
    }

    private static StatusReport CreateStatusFailureReport(StartupDatabaseErrorCode? errorCode)
    {
        var databaseState = errorCode is null ? "ready" : "failed";
        var revisionState = errorCode is null ? "unavailable" : "not-read";
        return new StatusReport(null, databaseState, revisionState, 1);
    }

    private static DoctorReport CreateDoctorFailureReport(StartupDatabaseErrorCode? errorCode)
    {
        var databaseState = errorCode is StartupDatabaseErrorCode.DatabaseUnavailable or
            StartupDatabaseErrorCode.AdvisoryLockUnavailable
            ? "failed"
            : "passed";
        var migrationState = errorCode is null
            ? "passed"
            : "failed";
        return new DoctorReport(null, databaseState, migrationState, "unavailable", 1);
    }

    private static int WriteDiagnosticFailure(CliCommandKind command) => command switch
    {
        CliCommandKind.Status => DiagnosticJson.Write(CreateStatusFailureReport(null)),
        CliCommandKind.Doctor => DiagnosticJson.Write(CreateDoctorFailureReport(null)),
        _ => 1
    };

    internal static CliCommandKind? SelectDiagnosticCommand(string[]? args)
    {
        if (args is null)
        {
            return null;
        }

        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (argument is null)
            {
                return null;
            }

            if (argument.StartsWith("--", StringComparison.Ordinal))
            {
                var equalsIndex = argument.IndexOf('=');
                var optionName = equalsIndex < 0 ? argument : argument[..equalsIndex];
                if (equalsIndex < 0 && IsBootstrapValueOption(optionName))
                {
                    index++;
                }

                continue;
            }

            return argument switch
            {
                "status" => CliCommandKind.Status,
                "doctor" => CliCommandKind.Doctor,
                "run" => null,
                _ => CliCommandKind.Status
            };
        }

        return null;
    }

    private static bool IsBootstrapValueOption(string optionName) => optionName is
        BootstrapDefaults.ConnectionStringOption or
        BootstrapDefaults.ListenAddressOption or
        BootstrapDefaults.ListenPortOption or
        BootstrapDefaults.NodeIdOption or
        BootstrapDefaults.LogLevelOption or
        BootstrapDefaults.DataDirectoryOption;
    private static Dictionary<string, string?> ReadBootstrapEnvironment() =>
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [BootstrapDefaults.ConnectionStringEnvironmentVariable] =
                Environment.GetEnvironmentVariable(BootstrapDefaults.ConnectionStringEnvironmentVariable),
            [BootstrapDefaults.ListenAddressEnvironmentVariable] =
                Environment.GetEnvironmentVariable(BootstrapDefaults.ListenAddressEnvironmentVariable),
            [BootstrapDefaults.ListenPortEnvironmentVariable] =
                Environment.GetEnvironmentVariable(BootstrapDefaults.ListenPortEnvironmentVariable),
            [BootstrapDefaults.NodeIdEnvironmentVariable] =
                Environment.GetEnvironmentVariable(BootstrapDefaults.NodeIdEnvironmentVariable),
            [BootstrapDefaults.LogLevelEnvironmentVariable] =
                Environment.GetEnvironmentVariable(BootstrapDefaults.LogLevelEnvironmentVariable),
            [BootstrapDefaults.DataDirectoryEnvironmentVariable] =
                Environment.GetEnvironmentVariable(BootstrapDefaults.DataDirectoryEnvironmentVariable)
        };

    private sealed record DatabaseInspection(
        StartupDatabaseResult Migration,
        ConfigurationReadResult<ConfigurationRevisionStatus>? Revision);
    private sealed record DoctorExtensionInspection(
        string ExtensionState,
        string LocalDirectoryState,
        IReadOnlyList<ExtensionLocalCheck> Checks,
        bool IsHealthy);
}
