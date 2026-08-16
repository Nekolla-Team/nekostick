using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nekolla.Nekostick.Domain;
using Nekolla.Nekostick.Persistence.Entities;

namespace Nekolla.Nekostick.Persistence;

/// <summary>EF Core context for the Nekostick PostgreSQL schema.</summary>
public sealed class NekostickDbContext : DbContext
{
    /// <summary>Creates a context from explicitly supplied options.</summary>
    /// <param name="options">The provider options configured by the host or design-time factory.</param>
    public NekostickDbContext(DbContextOptions<NekostickDbContext> options)
        : base(options)
    {
    }

    /// <summary>Gets the singleton configuration revision set.</summary>
    public DbSet<ConfigurationRevision> ConfigurationRevisions => Set<ConfigurationRevision>();

    /// <summary>Gets the route set.</summary>
    public DbSet<Route> Routes => Set<Route>();

    /// <summary>Gets the service set.</summary>
    public DbSet<Service> Services => Set<Service>();

    /// <summary>Gets the singleton global settings set.</summary>
    public DbSet<GlobalSettings> GlobalSettings => Set<GlobalSettings>();

    /// <summary>Gets the extension record set.</summary>
    public DbSet<ExtensionRecord> ExtensionRecords => Set<ExtensionRecord>();

    /// <summary>Gets the extension settings set.</summary>
    public DbSet<ExtensionSetting> ExtensionSettings => Set<ExtensionSetting>();

    /// <summary>Gets the node registration set.</summary>
    public DbSet<Node> Nodes => Set<Node>();

    /// <summary>Gets the port lease set.</summary>
    public DbSet<PortLease> PortLeases => Set<PortLease>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        BuildModel(modelBuilder);
    }

    internal static void BuildModel(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(PersistenceDatabaseDefaults.Schema);

        ConfigureConfigurationRevision(modelBuilder.Entity<ConfigurationRevision>());
        ConfigureGlobalSettings(modelBuilder.Entity<GlobalSettings>());
        ConfigureRoute(modelBuilder.Entity<Route>());
        ConfigureService(modelBuilder.Entity<Service>());
        ConfigureExtensionRecord(modelBuilder.Entity<ExtensionRecord>());
        ConfigureExtensionSetting(modelBuilder.Entity<ExtensionSetting>());
        ConfigureNode(modelBuilder.Entity<Node>());
        ConfigurePortLease(modelBuilder.Entity<PortLease>());
    }

    private static void ConfigureConfigurationRevision(EntityTypeBuilder<ConfigurationRevision> builder)
    {
        builder.ToTable("configuration_revisions", PersistenceDatabaseDefaults.Schema, table =>
        {
            table.HasCheckConstraint(
                "ck_configuration_revisions_id_uuid_v7",
                PersistenceDatabaseDefaults.UuidV7CheckConstraintSql);
            table.HasCheckConstraint(
                "ck_configuration_revisions_singleton",
                "id = '018f0f00-0000-7000-8000-000000000001'::uuid AND revision_key = 'global'");
        });

        builder.HasKey(value => value.Id).HasName("pk_configuration_revisions");
        builder.HasIndex(value => value.RevisionKey)
            .IsUnique()
            .HasDatabaseName("ux_configuration_revisions_revision_key");

        builder.Property(value => value.Id).HasColumnName("id").HasColumnType("uuid");
        builder.Property(value => value.RevisionKey)
            .HasColumnName("revision_key")
            .HasMaxLength(16)
            .IsRequired();
        builder.Property(value => value.CommittedAt)
            .HasColumnName("committed_at");
        ConfigureUtcTimestamp(builder.Property(value => value.CommittedAt));
        builder.Property(value => value.CommittedBy)
            .HasColumnName("committed_by")
            .HasMaxLength(128);
        ConfigureUtcTimestamp(builder.Property(value => value.CreatedAt).HasColumnName("created_at"));
        ConfigureUtcTimestamp(builder.Property(value => value.UpdatedAt).HasColumnName("updated_at"));
        ConfigureVersion(builder.Property(value => value.Version));

        builder.HasData(new ConfigurationRevision
        {
            Id = Guid.Parse(PersistenceDatabaseDefaults.SeedConfigurationRevisionId),
            RevisionKey = PersistenceDatabaseDefaults.GlobalRevisionKey,
            CommittedAt = SeedTimestamp,
            CommittedBy = "system",
            CreatedAt = SeedTimestamp,
            UpdatedAt = SeedTimestamp,
            Version = 1
        });
    }

    private static void ConfigureGlobalSettings(EntityTypeBuilder<GlobalSettings> builder)
    {
        builder.ToTable("global_settings", PersistenceDatabaseDefaults.Schema, table =>
        {
            table.HasCheckConstraint(
                "ck_global_settings_id_uuid_v7",
                PersistenceDatabaseDefaults.UuidV7CheckConstraintSql);
            table.HasCheckConstraint(
                "ck_global_settings_singleton",
                "id = '018f0f00-0000-7000-8000-000000000002'::uuid");
            table.HasCheckConstraint(
                "ck_global_settings_port_range",
                "auto_port_range_start BETWEEN 1 AND 65535 AND auto_port_range_end BETWEEN 1 AND 65535 AND auto_port_range_start <= auto_port_range_end");
            table.HasCheckConstraint(
                "ck_global_settings_limits",
                "max_request_body_bytes > 0 AND max_concurrent_requests > 0 AND configuration_poll_interval_seconds > 0");
            table.HasCheckConstraint(
                "ck_global_settings_proxy_timeouts",
                ProxyTimeoutPersistenceDefaults.CheckConstraintSql);
            table.HasCheckConstraint(
                "ck_global_settings_trusted_proxy_cidrs_json",
                "jsonb_typeof(trusted_proxy_cidrs_json) = 'array' AND octet_length(trusted_proxy_cidrs_json::text) <= 262144");
        });

        builder.HasKey(value => value.Id).HasName("pk_global_settings");
        builder.Property(value => value.Id).HasColumnName("id").HasColumnType("uuid");
        builder.Property(value => value.AutoPortRangeStart)
            .HasColumnName("auto_port_range_start")
            .HasColumnType("integer")
            .IsRequired();
        builder.Property(value => value.AutoPortRangeEnd)
            .HasColumnName("auto_port_range_end")
            .HasColumnType("integer")
            .IsRequired();
        builder.Property(value => value.MaxRequestBodyBytes)
            .HasColumnName("max_request_body_bytes")
            .HasColumnType("bigint")
            .IsRequired();
        builder.Property(value => value.MaxConcurrentRequests)
            .HasColumnName("max_concurrent_requests")
            .HasColumnType("integer")
            .IsRequired();
        builder.Property(value => value.ConfigurationPollIntervalSeconds)
            .HasColumnName("configuration_poll_interval_seconds")
            .HasColumnType("integer")
            .IsRequired();
        builder.Property(value => value.TrustedProxyCidrsJson)
            .HasColumnName("trusted_proxy_cidrs_json")
            .HasColumnType("jsonb")
            .IsRequired();
        ConfigureUtcTimestamp(builder.Property(value => value.CreatedAt).HasColumnName("created_at"));
        ConfigureUtcTimestamp(builder.Property(value => value.UpdatedAt).HasColumnName("updated_at"));
        ConfigureVersion(builder.Property(value => value.Version));
        builder.Property(value => value.ConnectTimeoutMilliseconds)
            .HasColumnName("connect_timeout_milliseconds")
            .HasColumnType("integer")
            .HasDefaultValue(ProxyTimeoutPersistenceDefaults.DefaultConnectTimeoutMilliseconds)
            .IsRequired();
        builder.Property(value => value.HttpActivityTimeoutMilliseconds)
            .HasColumnName("http_activity_timeout_milliseconds")
            .HasColumnType("integer")
            .HasDefaultValue(ProxyTimeoutPersistenceDefaults.DefaultHttpActivityTimeoutMilliseconds)
            .IsRequired();
        builder.Property(value => value.HttpTotalTimeoutMilliseconds)
            .HasColumnName("http_total_timeout_milliseconds")
            .HasColumnType("integer")
            .HasDefaultValue(ProxyTimeoutPersistenceDefaults.DefaultHttpTotalTimeoutMilliseconds)
            .IsRequired();
        builder.Property(value => value.WebSocketIdleTimeoutMilliseconds)
            .HasColumnName("websocket_idle_timeout_milliseconds")
            .HasColumnType("integer")
            .HasDefaultValue(ProxyTimeoutPersistenceDefaults.DefaultWebSocketIdleTimeoutMilliseconds)
            .IsRequired();

        builder.HasData(new GlobalSettings
        {
            Id = Guid.Parse(PersistenceDatabaseDefaults.SeedGlobalSettingsId),
            AutoPortRangeStart = 20000,
            AutoPortRangeEnd = 29999,
            MaxRequestBodyBytes = 30 * 1024 * 1024,
            MaxConcurrentRequests = 1024,
            ConfigurationPollIntervalSeconds = 30,
            TrustedProxyCidrsJson = "[]",
            CreatedAt = SeedTimestamp,
            UpdatedAt = SeedTimestamp,
            Version = 1,
            ConnectTimeoutMilliseconds = ProxyTimeoutPersistenceDefaults.DefaultConnectTimeoutMilliseconds,
            HttpActivityTimeoutMilliseconds = ProxyTimeoutPersistenceDefaults.DefaultHttpActivityTimeoutMilliseconds,
            HttpTotalTimeoutMilliseconds = ProxyTimeoutPersistenceDefaults.DefaultHttpTotalTimeoutMilliseconds,
            WebSocketIdleTimeoutMilliseconds = ProxyTimeoutPersistenceDefaults.DefaultWebSocketIdleTimeoutMilliseconds
        });
    }

    private static void ConfigureRoute(EntityTypeBuilder<Route> builder)
    {
        builder.ToTable("routes", PersistenceDatabaseDefaults.Schema, table =>
        {
            table.HasCheckConstraint(
                "ck_routes_id_uuid_v7",
                PersistenceDatabaseDefaults.UuidV7CheckConstraintSql);
            table.HasCheckConstraint(
                "ck_routes_pattern_length",
                "length(pattern) BETWEEN 1 AND 4096");
            table.HasCheckConstraint(
                "ck_routes_matcher_json",
                "jsonb_typeof(host_patterns_json) = 'array' AND jsonb_typeof(methods_json) = 'array' AND octet_length(host_patterns_json::text) <= 262144 AND octet_length(methods_json::text) <= 262144");
            table.HasCheckConstraint(
                "ck_routes_enum_values",
                "matcher_type IN ('Exact', 'ExactCaseInsensitive', 'Prefix', 'PrefixCaseInsensitive', 'Regex') AND target_type IN ('Microservice', 'StaticFile', 'ExtensionHandler') AND forwarding_mode IN ('Preserve', 'Strip', 'Replace')");
            table.HasCheckConstraint(
                "ck_routes_target_reference",
                "(target_type = 'Microservice' AND service_id IS NOT NULL AND target_id = service_id::text AND static_root_path IS NULL AND extension_handler_id IS NULL) OR (target_type = 'StaticFile' AND service_id IS NULL AND target_id = static_root_path AND static_root_path IS NOT NULL AND extension_handler_id IS NULL) OR (target_type = 'ExtensionHandler' AND service_id IS NULL AND target_id = extension_handler_id AND static_root_path IS NULL AND extension_handler_id IS NOT NULL)");
            table.HasCheckConstraint(
                "ck_routes_forwarding_template",
                "(forwarding_mode = 'Replace' AND replace_template IS NOT NULL AND length(replace_template) <= 4096) OR (forwarding_mode <> 'Replace' AND replace_template IS NULL)");
            table.HasCheckConstraint(
                "ck_routes_rewrite_metadata_json",
                "jsonb_typeof(request_header_rewrites_json) = 'array' AND jsonb_typeof(response_header_rewrites_json) = 'array' AND jsonb_typeof(metadata_json) = 'object' AND octet_length(request_header_rewrites_json::text) <= 1048576 AND octet_length(response_header_rewrites_json::text) <= 1048576 AND octet_length(metadata_json::text) <= 1048576");
        });

        builder.HasKey(value => value.Id).HasName("pk_routes");
        builder.HasIndex(value => new { value.Enabled, value.MatcherType, value.Priority })
            .HasDatabaseName("ix_routes_enabled_matcher_type_priority");
        builder.HasIndex(value => value.ServiceId).HasDatabaseName("ix_routes_service_id");
        builder.Property(value => value.Id).HasColumnName("id").HasColumnType("uuid");
        builder.Property(value => value.Enabled).HasColumnName("enabled").HasColumnType("boolean").IsRequired();
        ConfigureEnum(builder.Property(value => value.MatcherType).HasColumnName("matcher_type"), 32);
        builder.Property(value => value.Pattern).HasColumnName("pattern").HasMaxLength(4096).IsRequired();
        builder.Property(value => value.HostPatternsJson).HasColumnName("host_patterns_json").HasColumnType("jsonb").IsRequired();
        builder.Property(value => value.MethodsJson).HasColumnName("methods_json").HasColumnType("jsonb").IsRequired();
        ConfigureEnum(builder.Property(value => value.TargetType).HasColumnName("target_type"), 32);
        builder.Property(value => value.TargetId).HasColumnName("target_id").HasMaxLength(4096).IsRequired();
        builder.Property(value => value.ServiceId).HasColumnName("service_id").HasColumnType("uuid");
        builder.Property(value => value.StaticRootPath).HasColumnName("static_root_path").HasMaxLength(4096);
        builder.Property(value => value.ExtensionHandlerId).HasColumnName("extension_handler_id").HasMaxLength(256);
        builder.Property(value => value.Priority).HasColumnName("priority").HasColumnType("integer").IsRequired();
        ConfigureEnum(builder.Property(value => value.ForwardingMode).HasColumnName("forwarding_mode"), 16);
        builder.Property(value => value.ReplaceTemplate).HasColumnName("replace_template").HasMaxLength(4096);
        builder.Property(value => value.RequestHeaderRewritesJson).HasColumnName("request_header_rewrites_json").HasColumnType("jsonb").IsRequired();
        builder.Property(value => value.ResponseHeaderRewritesJson).HasColumnName("response_header_rewrites_json").HasColumnType("jsonb").IsRequired();
        builder.Property(value => value.MetadataJson).HasColumnName("metadata_json").HasColumnType("jsonb").IsRequired();
        ConfigureUtcTimestamp(builder.Property(value => value.CreatedAt).HasColumnName("created_at"));
        ConfigureUtcTimestamp(builder.Property(value => value.UpdatedAt).HasColumnName("updated_at"));
        ConfigureVersion(builder.Property(value => value.Version));

        builder.HasOne(value => value.Service)
            .WithMany(value => value.Routes)
            .HasForeignKey(value => value.ServiceId)
            .HasConstraintName("fk_routes_services_service_id")
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureService(EntityTypeBuilder<Service> builder)
    {
        builder.ToTable("services", PersistenceDatabaseDefaults.Schema, table =>
        {
            table.HasCheckConstraint(
                "ck_services_id_uuid_v7",
                PersistenceDatabaseDefaults.UuidV7CheckConstraintSql);
            table.HasCheckConstraint(
                "ck_services_paths",
                "length(file_name) BETWEEN 1 AND 4096 AND length(working_directory) BETWEEN 1 AND 4096");
            table.HasCheckConstraint(
                "ck_services_process_json",
                "jsonb_typeof(argument_list_json) = 'array' AND jsonb_typeof(environment_json) = 'object' AND octet_length(argument_list_json::text) <= 1048576 AND octet_length(environment_json::text) <= 1048576");
            table.HasCheckConstraint(
                "ck_services_enum_values",
                "start_mode IN ('Eager', 'Lazy') AND restart_policy IN ('Never', 'OnFailure', 'Always') AND health_check_type IN ('Process', 'Tcp', 'Http')");
            table.HasCheckConstraint(
                "ck_services_health",
                "health_check_timeout_milliseconds > 0 AND ((health_check_type = 'Http' AND health_check_http_path IS NOT NULL) OR (health_check_type <> 'Http' AND health_check_http_path IS NULL))");
        });

        builder.HasKey(value => value.Id).HasName("pk_services");
        builder.HasIndex(value => value.Enabled).HasDatabaseName("ix_services_enabled");
        builder.Property(value => value.Id).HasColumnName("id").HasColumnType("uuid");
        builder.Property(value => value.Enabled).HasColumnName("enabled").HasColumnType("boolean").IsRequired();
        builder.Property(value => value.FileName).HasColumnName("file_name").HasMaxLength(4096).IsRequired();
        builder.Property(value => value.ArgumentListJson).HasColumnName("argument_list_json").HasColumnType("jsonb").IsRequired();
        builder.Property(value => value.WorkingDirectory).HasColumnName("working_directory").HasMaxLength(4096).IsRequired();
        builder.Property(value => value.EnvironmentJson).HasColumnName("environment_json").HasColumnType("jsonb").IsRequired();
        ConfigureEnum(builder.Property(value => value.StartMode).HasColumnName("start_mode"), 16);
        ConfigureEnum(builder.Property(value => value.RestartPolicy).HasColumnName("restart_policy"), 16);
        ConfigureEnum(builder.Property(value => value.HealthCheckType).HasColumnName("health_check_type"), 16);
        builder.Property(value => value.HealthCheckHttpPath).HasColumnName("health_check_http_path").HasMaxLength(2048);
        builder.Property(value => value.HealthCheckTimeoutMilliseconds).HasColumnName("health_check_timeout_milliseconds").HasColumnType("integer").IsRequired();
        ConfigureUtcTimestamp(builder.Property(value => value.CreatedAt).HasColumnName("created_at"));
        ConfigureUtcTimestamp(builder.Property(value => value.UpdatedAt).HasColumnName("updated_at"));
        ConfigureVersion(builder.Property(value => value.Version));
    }

    private static void ConfigureExtensionRecord(EntityTypeBuilder<ExtensionRecord> builder)
    {
        builder.ToTable("extension_records", PersistenceDatabaseDefaults.Schema, table =>
        {
            table.HasCheckConstraint(
                "ck_extension_records_id_uuid_v7",
                PersistenceDatabaseDefaults.UuidV7CheckConstraintSql);
            table.HasCheckConstraint("ck_extension_records_text", "length(extension_id) BETWEEN 1 AND 128 AND length(installed_version) BETWEEN 1 AND 128");
            table.HasCheckConstraint("ck_extension_records_load_state", "load_state IN ('Discovered', 'Loaded', 'Stopped', 'Failed', 'Unloading')");
        });

        builder.HasKey(value => value.Id).HasName("pk_extension_records");
        builder.HasIndex(value => value.ExtensionId).IsUnique().HasDatabaseName("ux_extension_records_extension_id");
        builder.Property(value => value.Id).HasColumnName("id").HasColumnType("uuid");
        builder.Property(value => value.ExtensionId).HasColumnName("extension_id").HasMaxLength(128).IsRequired();
        builder.Property(value => value.InstalledVersion).HasColumnName("installed_version").HasMaxLength(128).IsRequired();
        ConfigureEnum(builder.Property(value => value.LoadState).HasColumnName("load_state"), 32);
        ConfigureUtcTimestamp(builder.Property(value => value.CreatedAt).HasColumnName("created_at"));
        ConfigureUtcTimestamp(builder.Property(value => value.UpdatedAt).HasColumnName("updated_at"));
        ConfigureVersion(builder.Property(value => value.Version));
    }

    private static void ConfigureExtensionSetting(EntityTypeBuilder<ExtensionSetting> builder)
    {
        builder.ToTable("extension_settings", PersistenceDatabaseDefaults.Schema, table =>
        {
            table.HasCheckConstraint(
                "ck_extension_settings_id_uuid_v7",
                PersistenceDatabaseDefaults.UuidV7CheckConstraintSql);
            table.HasCheckConstraint("ck_extension_settings_schema_version", "schema_version >= 0");
            table.HasCheckConstraint("ck_extension_settings_json", "jsonb_typeof(settings_json) IS NOT NULL AND octet_length(settings_json::text) <= 1048576");
        });

        builder.HasKey(value => value.Id).HasName("pk_extension_settings");
        builder.HasIndex(value => value.ExtensionRecordId).IsUnique().HasDatabaseName("ux_extension_settings_extension_record_id");
        builder.Property(value => value.Id).HasColumnName("id").HasColumnType("uuid");
        builder.Property(value => value.ExtensionRecordId).HasColumnName("extension_record_id").HasColumnType("uuid").IsRequired();
        builder.Property(value => value.SchemaVersion).HasColumnName("schema_version").HasColumnType("integer").IsRequired();
        builder.Property(value => value.SettingsJson).HasColumnName("settings_json").HasColumnType("jsonb").IsRequired();
        ConfigureUtcTimestamp(builder.Property(value => value.CreatedAt).HasColumnName("created_at"));
        ConfigureUtcTimestamp(builder.Property(value => value.UpdatedAt).HasColumnName("updated_at"));
        ConfigureVersion(builder.Property(value => value.Version));

        builder.HasOne(value => value.ExtensionRecord)
            .WithMany(value => value.Settings)
            .HasForeignKey(value => value.ExtensionRecordId)
            .HasConstraintName("fk_extension_settings_extension_records_extension_record_id")
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureNode(EntityTypeBuilder<Node> builder)
    {
        builder.ToTable("nodes", PersistenceDatabaseDefaults.Schema, table =>
        {
            table.HasCheckConstraint(
                "ck_nodes_id_uuid_v7",
                PersistenceDatabaseDefaults.UuidV7CheckConstraintSql);
            table.HasCheckConstraint("ck_nodes_node_id", "length(node_id) BETWEEN 1 AND 128");
            table.HasCheckConstraint("ck_nodes_versions", "last_configuration_version >= 0 AND length(runtime_state) BETWEEN 1 AND 32");
        });

        builder.HasKey(value => value.Id).HasName("pk_nodes");
        builder.HasAlternateKey(value => value.NodeId).HasName("ak_nodes_node_id");
        builder.HasIndex(value => value.NodeId)
            .IsUnique()
            .HasDatabaseName("ux_nodes_default_node_id_active")
            .HasFilter("node_id = '0' AND is_active");
        builder.Property(value => value.Id).HasColumnName("id").HasColumnType("uuid");
        builder.Property(value => value.NodeId).HasColumnName("node_id").HasMaxLength(128).IsRequired();
        ConfigureUtcTimestamp(builder.Property(value => value.LastHeartbeatAt).HasColumnName("last_heartbeat_at"));
        builder.Property(value => value.LastConfigurationVersion).HasColumnName("last_configuration_version").HasColumnType("bigint").IsRequired();
        builder.Property(value => value.RuntimeState).HasColumnName("runtime_state").HasMaxLength(32).IsRequired();
        builder.Property(value => value.IsActive).HasColumnName("is_active").HasColumnType("boolean").IsRequired();
        ConfigureUtcTimestamp(builder.Property(value => value.CreatedAt).HasColumnName("created_at"));
        ConfigureUtcTimestamp(builder.Property(value => value.UpdatedAt).HasColumnName("updated_at"));
        ConfigureVersion(builder.Property(value => value.Version));
    }

    private static void ConfigurePortLease(EntityTypeBuilder<PortLease> builder)
    {
        builder.ToTable("port_leases", PersistenceDatabaseDefaults.Schema, table =>
        {
            table.HasCheckConstraint(
                "ck_port_leases_id_uuid_v7",
                PersistenceDatabaseDefaults.UuidV7CheckConstraintSql);
            table.HasCheckConstraint("ck_port_leases_port", "port BETWEEN 1 AND 65535");
        });

        builder.HasKey(value => value.Id).HasName("pk_port_leases");
        builder.HasIndex(value => new { value.NodeId, value.Port })
            .IsUnique()
            .HasDatabaseName("ux_port_leases_node_id_port");
        builder.HasIndex(value => value.ServiceId).HasDatabaseName("ix_port_leases_service_id");
        builder.Property(value => value.Id).HasColumnName("id").HasColumnType("uuid");
        builder.Property(value => value.NodeId).HasColumnName("node_id").HasMaxLength(128).IsRequired();
        builder.Property(value => value.Port).HasColumnName("port").HasColumnType("integer").IsRequired();
        builder.Property(value => value.ServiceId).HasColumnName("service_id").HasColumnType("uuid").IsRequired();
        ConfigureUtcTimestamp(builder.Property(value => value.LeaseExpiresAt).HasColumnName("lease_expires_at"));
        ConfigureUtcTimestamp(builder.Property(value => value.RenewedAt).HasColumnName("renewed_at"));
        ConfigureVersion(builder.Property(value => value.Version));
        ConfigureUtcTimestamp(builder.Property(value => value.CreatedAt).HasColumnName("created_at"));
        ConfigureUtcTimestamp(builder.Property(value => value.UpdatedAt).HasColumnName("updated_at"));

        builder.HasOne(value => value.Node)
            .WithMany(value => value.PortLeases)
            .HasForeignKey(value => value.NodeId)
            .HasPrincipalKey(value => value.NodeId)
            .HasConstraintName("fk_port_leases_nodes_node_id")
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(value => value.Service)
            .WithMany(value => value.PortLeases)
            .HasForeignKey(value => value.ServiceId)
            .HasConstraintName("fk_port_leases_services_service_id")
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureVersion(PropertyBuilder<long> property)
    {
        property.HasColumnName("version")
            .HasColumnType("bigint")
            .HasDefaultValue(1L)
            .IsConcurrencyToken()
            .IsRequired();
    }

    private static void ConfigureUtcTimestamp(PropertyBuilder<DateTimeOffset> property)
    {
        property.HasColumnType("timestamptz")
            .IsRequired();
    }

    private static void ConfigureEnum<TEnum>(PropertyBuilder<TEnum> property, int maxLength)
        where TEnum : struct, Enum
    {
        property.HasConversion<string>().HasMaxLength(maxLength).IsRequired();
    }

    private static readonly DateTimeOffset SeedTimestamp =
        new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
}
