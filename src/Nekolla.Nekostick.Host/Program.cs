using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nekolla.Nekostick.Contracts;
using Nekolla.Nekostick.Domain;
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
        catch (Exception)
        {
            if (diagnosticCommand is { } failedDiagnosticCommand)
            {
                return WriteDiagnosticFailure(failedDiagnosticCommand);
            }

            Console.Error.WriteLine($"HOST_EVENT {HostEventIds.HostStartupFailed.Id}: {HostStartupFailureMessage}");
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

        var inspection = await InspectDatabaseAsync(
            app,
            command.Kind is CliCommandKind.Status or CliCommandKind.Doctor,
            cancellationToken);
        if (!inspection.Migration.IsSuccess)
        {
            var error = inspection.Migration.Error!;

            if (command.Kind == CliCommandKind.Status)
            {
                var report = CreateStatusFailureReport(error.Code);
                return DiagnosticJson.Write(report);
            }

            if (command.Kind == CliCommandKind.Doctor)
            {
                var report = CreateDoctorFailureReport(error.Code);
                return DiagnosticJson.Write(report);
            }

            var loggerFactory = app.Services.GetRequiredService<ILoggerFactory>();
            var logger = loggerFactory.CreateLogger(HostLoggerCategory.Startup);
            HostLogMessages.DatabaseStartupFailed(logger, error.Code, error.Message);
            return 1;
        }

        if (command.Kind == CliCommandKind.Run)
        {
            var snapshotReader = app.Services.GetRequiredService<IHostConfigurationSnapshotReader>();
            var snapshotResult = await snapshotReader.ReadCompleteAsync(cancellationToken);
            if (!snapshotResult.IsSuccess || snapshotResult.Value is null)
            {
                var loggerFactory = app.Services.GetRequiredService<ILoggerFactory>();
                var logger = loggerFactory.CreateLogger(HostLoggerCategory.Startup);
                if (snapshotResult.Errors.Any(error => error.Code == ConfigurationErrorCode.StorageUnavailable))
                {
                    HostLogMessages.ConfigurationRefreshUnavailable(logger);
                }
                else
                {
                    HostLogMessages.ConfigurationSnapshotRejected(logger);
                }

                return 1;
            }

            var publisher = app.Services.GetRequiredService<HostConfigurationPublisher>();
            if (!await publisher.PublishAsync(snapshotResult.Value, cancellationToken).ConfigureAwait(false))
            {
                var loggerFactory = app.Services.GetRequiredService<ILoggerFactory>();
                var logger = loggerFactory.CreateLogger(HostLoggerCategory.Startup);
                HostLogMessages.ConfigurationSnapshotRejected(logger);
                return 1;
            }

            app.Services.GetRequiredService<HostRuntimeState>().MarkSnapshotAccepted();
            ConfigureRunPipeline(app);
            await app.RunAsync(cancellationToken);
            return 0;
        }

        if (inspection.Revision is null ||
            !inspection.Revision.IsSuccess ||
            inspection.Revision.Value is null)
        {
            if (command.Kind == CliCommandKind.Status)
            {
                var report = CreateStatusFailureReport(null);
                return DiagnosticJson.Write(report);
            }

            var doctorReport = CreateDoctorFailureReport(null);
            return DiagnosticJson.Write(doctorReport);
        }

        var configurationVersion = inspection.Revision.Value.Version;
        if (command.Kind == CliCommandKind.Status)
        {
            var report = new StatusReport(configurationVersion, "ready", "valid", 0);
            return DiagnosticJson.Write(report);
        }

        var doctorSuccessReport = new DoctorReport(
            configurationVersion,
            "passed",
            "passed",
            "unavailable",
            0);
        return DiagnosticJson.Write(doctorSuccessReport);
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
        builder.Logging.AddProvider(new SafeConsoleLoggerProvider());
        builder.Host.UseConsoleLifetime();

        var bootstrap = command.BootstrapOptions;
        builder.Services.AddSingleton(new HostNodeOptions(
            command.RunOptions.SkipExtensions,
            command.RunOptions.DisableSupervisor,
            command.RunOptions.ReadOnly));
        builder.Services.AddSingleton(new HostRuntimeOptions(
            bootstrap.ConnectionString,
            bootstrap.NodeId,
            command.RunOptions.ReadOnly));
        builder.Services.AddSingleton<HostConfigurationSnapshotHolder>();
        builder.Services.AddSingleton<IHostConfigurationSnapshotAccessor>(serviceProvider =>
            serviceProvider.GetRequiredService<HostConfigurationSnapshotHolder>());
        builder.Services.AddSingleton<IHostRoutingSnapshotAccessor>(serviceProvider =>
            new HostRoutingSnapshotAccessor(
                serviceProvider.GetRequiredService<HostConfigurationSnapshotHolder>()));
        builder.Services.AddSingleton<HostRequestAdmission>();
        builder.Services.AddSingleton<ExtensionRuntimeManager>(_ =>
            new ExtensionRuntimeManager(HostApiVersion.Current));
        builder.Services.AddSingleton<HostConfigurationPublisher>();
        builder.Services.AddSingleton<IRouteFallbackDispatcher, ExtensionRouteFallbackDispatcher>();
        builder.Services.AddMicroserviceProxy();
        builder.Services.AddSingleton<IRouteTargetExecutor>(serviceProvider =>
            new HostRouteTargetExecutor(
                serviceProvider.GetRequiredService<MicroserviceHttpExecutor>(),
                serviceProvider.GetService<IHostServiceLifecycleCoordinator>()));
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
        builder.Services.AddDbContextFactory<NekostickDbContext>(dbContextOptions =>
            dbContextOptions.UseNekostickPostgres(bootstrap.ConnectionString));
        builder.Services.AddSingleton<IMigrationSchemaValidator, PostgresMigrationSchemaValidator>();
        builder.Services.AddSingleton<IStartupDatabaseProbe>(serviceProvider =>
            new PostgresMigrationCoordinator(
                bootstrap.ConnectionString,
                serviceProvider.GetRequiredService<IMigrationSchemaValidator>()));
        builder.Services.AddScoped<IConfigurationRevisionReader, EfConfigurationRevisionReader>();
        builder.Services.AddSingleton<IHostConfigurationSnapshotReader, EfHostConfigurationSnapshotReader>();
        builder.Services.AddScoped<EfHostConfigApi>();
        builder.Services.AddScoped<IHostConfigApi>(serviceProvider =>
            new HostConfigApiReadOnlyDecorator(
                serviceProvider.GetRequiredService<EfHostConfigApi>(),
                serviceProvider.GetRequiredService<HostRuntimeOptions>()));

        if (command.Kind == CliCommandKind.Run)
        {
            builder.Services.AddSingleton<IConfigurationChangeSignal, PostgresConfigurationChangeSignal>();
            builder.Services.AddSingleton<IHostNodeActivityLease>(serviceProvider =>
                new PostgresHostNodeActivityLease(
                    serviceProvider.GetRequiredService<HostRuntimeOptions>()));
            builder.Services.AddHostedService<HostConfigurationRefreshService>();
            if (!command.RunOptions.DisableSupervisor)
            {
                builder.Services.AddSingleton<HostServiceEndpointSnapshotPublisher>(serviceProvider =>
                    new HostServiceEndpointSnapshotPublisher(
                        serviceProvider.GetRequiredService<ExtensionRuntimeManager>()));
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
                        outputSink: serviceProvider.GetRequiredService<IProcessOutputSink>()));
                builder.Services.AddSingleton<IServiceHealthProbe>(serviceProvider =>
                    new ServiceHealthProbe(
                        serviceProvider.GetRequiredService<IProcessExecutor>()));
                builder.Services.AddSingleton<HostPortLeaseStoreAdapter>(serviceProvider =>
                    new HostPortLeaseStoreAdapter(
                        serviceProvider.GetRequiredService<IDbContextFactory<NekostickDbContext>>(),
                        serviceProvider.GetRequiredService<HostRuntimeState>()));
                builder.Services.AddSingleton<HostServiceLifecycleManager>(serviceProvider =>
                    new HostServiceLifecycleManager(
                        serviceProvider.GetRequiredService<IProcessExecutor>(),
                        serviceProvider.GetRequiredService<IServiceHealthProbe>(),
                        serviceProvider.GetRequiredService<IPortLeaseStore>(),
                        serviceProvider.GetRequiredService<HostConfigurationSnapshotHolder>(),
                        serviceProvider.GetRequiredService<HostServiceEndpointSnapshotPublisher>(),
                        serviceProvider.GetRequiredService<HostRuntimeState>(),
                        serviceProvider.GetRequiredService<HostRuntimeOptions>(),
                        serviceProvider.GetRequiredService<ExtensionRuntimeManager>()));
                builder.Services.AddSingleton<IPortLeaseStore>(serviceProvider =>
                    serviceProvider.GetRequiredService<HostPortLeaseStoreAdapter>());
                builder.Services.AddSingleton<IHostServiceLifecycleCoordinator>(serviceProvider =>
                    serviceProvider.GetRequiredService<HostServiceLifecycleManager>());
                builder.Services.AddHostedService(serviceProvider =>
                    serviceProvider.GetRequiredService<HostServiceLifecycleManager>());
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
        BootstrapDefaults.NodeIdOption;

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
                Environment.GetEnvironmentVariable(BootstrapDefaults.NodeIdEnvironmentVariable)
        };

    private sealed record DatabaseInspection(
        StartupDatabaseResult Migration,
        ConfigurationReadResult<ConfigurationRevisionStatus>? Revision);
}
