DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM pg_namespace WHERE nspname = 'nekostick') THEN
        CREATE SCHEMA nekostick;
    END IF;
END $EF$;
CREATE TABLE IF NOT EXISTS nekostick."__EFMigrationsHistory" (
    "MigrationId" character varying(150) NOT NULL,
    "ProductVersion" character varying(32) NOT NULL,
    CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
);

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nekostick."__EFMigrationsHistory" WHERE "MigrationId" = '20260816000611_InitialPersistence') THEN
        IF NOT EXISTS(SELECT 1 FROM pg_namespace WHERE nspname = 'nekostick') THEN
            CREATE SCHEMA nekostick;
        END IF;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nekostick."__EFMigrationsHistory" WHERE "MigrationId" = '20260816000611_InitialPersistence') THEN
    CREATE TABLE nekostick.configuration_revisions (
        id uuid NOT NULL,
        revision_key character varying(16) NOT NULL,
        committed_at timestamptz NOT NULL,
        committed_by character varying(128),
        created_at timestamptz NOT NULL,
        updated_at timestamptz NOT NULL,
        version bigint NOT NULL DEFAULT 1,
        CONSTRAINT pk_configuration_revisions PRIMARY KEY (id),
        CONSTRAINT ck_configuration_revisions_id_uuid_v7 CHECK (substring(id::text, 15, 1) = '7' AND substring(id::text, 20, 1) IN ('8', '9', 'a', 'b')),
        CONSTRAINT ck_configuration_revisions_singleton CHECK (id = '018f0f00-0000-7000-8000-000000000001'::uuid AND revision_key = 'global')
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nekostick."__EFMigrationsHistory" WHERE "MigrationId" = '20260816000611_InitialPersistence') THEN
    CREATE TABLE nekostick.extension_records (
        id uuid NOT NULL,
        extension_id character varying(128) NOT NULL,
        installed_version character varying(128) NOT NULL,
        load_state character varying(32) NOT NULL,
        created_at timestamptz NOT NULL,
        updated_at timestamptz NOT NULL,
        version bigint NOT NULL DEFAULT 1,
        CONSTRAINT pk_extension_records PRIMARY KEY (id),
        CONSTRAINT ck_extension_records_id_uuid_v7 CHECK (substring(id::text, 15, 1) = '7' AND substring(id::text, 20, 1) IN ('8', '9', 'a', 'b')),
        CONSTRAINT ck_extension_records_load_state CHECK (load_state IN ('Discovered', 'Loaded', 'Stopped', 'Failed', 'Unloading')),
        CONSTRAINT ck_extension_records_text CHECK (length(extension_id) BETWEEN 1 AND 128 AND length(installed_version) BETWEEN 1 AND 128)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nekostick."__EFMigrationsHistory" WHERE "MigrationId" = '20260816000611_InitialPersistence') THEN
    CREATE TABLE nekostick.global_settings (
        id uuid NOT NULL,
        auto_port_range_start integer NOT NULL,
        auto_port_range_end integer NOT NULL,
        max_request_body_bytes bigint NOT NULL,
        max_concurrent_requests integer NOT NULL,
        configuration_poll_interval_seconds integer NOT NULL,
        trusted_proxy_cidrs_json jsonb NOT NULL,
        created_at timestamptz NOT NULL,
        updated_at timestamptz NOT NULL,
        version bigint NOT NULL DEFAULT 1,
        CONSTRAINT pk_global_settings PRIMARY KEY (id),
        CONSTRAINT ck_global_settings_id_uuid_v7 CHECK (substring(id::text, 15, 1) = '7' AND substring(id::text, 20, 1) IN ('8', '9', 'a', 'b')),
        CONSTRAINT ck_global_settings_limits CHECK (max_request_body_bytes > 0 AND max_concurrent_requests > 0 AND configuration_poll_interval_seconds > 0),
        CONSTRAINT ck_global_settings_port_range CHECK (auto_port_range_start BETWEEN 1 AND 65535 AND auto_port_range_end BETWEEN 1 AND 65535 AND auto_port_range_start <= auto_port_range_end),
        CONSTRAINT ck_global_settings_singleton CHECK (id = '018f0f00-0000-7000-8000-000000000002'::uuid),
        CONSTRAINT ck_global_settings_trusted_proxy_cidrs_json CHECK (jsonb_typeof(trusted_proxy_cidrs_json) = 'array' AND octet_length(trusted_proxy_cidrs_json::text) <= 262144)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nekostick."__EFMigrationsHistory" WHERE "MigrationId" = '20260816000611_InitialPersistence') THEN
    CREATE TABLE nekostick.nodes (
        id uuid NOT NULL,
        node_id character varying(128) NOT NULL,
        last_heartbeat_at timestamptz NOT NULL,
        last_configuration_version bigint NOT NULL,
        runtime_state character varying(32) NOT NULL,
        is_active boolean NOT NULL,
        created_at timestamptz NOT NULL,
        updated_at timestamptz NOT NULL,
        version bigint NOT NULL DEFAULT 1,
        CONSTRAINT pk_nodes PRIMARY KEY (id),
        CONSTRAINT ak_nodes_node_id UNIQUE (node_id),
        CONSTRAINT ck_nodes_id_uuid_v7 CHECK (substring(id::text, 15, 1) = '7' AND substring(id::text, 20, 1) IN ('8', '9', 'a', 'b')),
        CONSTRAINT ck_nodes_node_id CHECK (length(node_id) BETWEEN 1 AND 128),
        CONSTRAINT ck_nodes_versions CHECK (last_configuration_version >= 0 AND length(runtime_state) BETWEEN 1 AND 32)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nekostick."__EFMigrationsHistory" WHERE "MigrationId" = '20260816000611_InitialPersistence') THEN
    CREATE TABLE nekostick.services (
        id uuid NOT NULL,
        enabled boolean NOT NULL,
        file_name character varying(4096) NOT NULL,
        argument_list_json jsonb NOT NULL,
        working_directory character varying(4096) NOT NULL,
        environment_json jsonb NOT NULL,
        start_mode character varying(16) NOT NULL,
        restart_policy character varying(16) NOT NULL,
        health_check_type character varying(16) NOT NULL,
        health_check_http_path character varying(2048),
        health_check_timeout_milliseconds integer NOT NULL,
        created_at timestamptz NOT NULL,
        updated_at timestamptz NOT NULL,
        version bigint NOT NULL DEFAULT 1,
        CONSTRAINT pk_services PRIMARY KEY (id),
        CONSTRAINT ck_services_enum_values CHECK (start_mode IN ('Eager', 'Lazy') AND restart_policy IN ('Never', 'OnFailure', 'Always') AND health_check_type IN ('Process', 'Tcp', 'Http')),
        CONSTRAINT ck_services_health CHECK (health_check_timeout_milliseconds > 0 AND ((health_check_type = 'Http' AND health_check_http_path IS NOT NULL) OR (health_check_type <> 'Http' AND health_check_http_path IS NULL))),
        CONSTRAINT ck_services_id_uuid_v7 CHECK (substring(id::text, 15, 1) = '7' AND substring(id::text, 20, 1) IN ('8', '9', 'a', 'b')),
        CONSTRAINT ck_services_paths CHECK (length(file_name) BETWEEN 1 AND 4096 AND length(working_directory) BETWEEN 1 AND 4096),
        CONSTRAINT ck_services_process_json CHECK (jsonb_typeof(argument_list_json) = 'array' AND jsonb_typeof(environment_json) = 'object' AND octet_length(argument_list_json::text) <= 1048576 AND octet_length(environment_json::text) <= 1048576)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nekostick."__EFMigrationsHistory" WHERE "MigrationId" = '20260816000611_InitialPersistence') THEN
    CREATE TABLE nekostick.extension_settings (
        id uuid NOT NULL,
        extension_record_id uuid NOT NULL,
        schema_version integer NOT NULL,
        settings_json jsonb NOT NULL,
        created_at timestamptz NOT NULL,
        updated_at timestamptz NOT NULL,
        version bigint NOT NULL DEFAULT 1,
        CONSTRAINT pk_extension_settings PRIMARY KEY (id),
        CONSTRAINT ck_extension_settings_id_uuid_v7 CHECK (substring(id::text, 15, 1) = '7' AND substring(id::text, 20, 1) IN ('8', '9', 'a', 'b')),
        CONSTRAINT ck_extension_settings_json CHECK (jsonb_typeof(settings_json) IS NOT NULL AND octet_length(settings_json::text) <= 1048576),
        CONSTRAINT ck_extension_settings_schema_version CHECK (schema_version >= 0),
        CONSTRAINT fk_extension_settings_extension_records_extension_record_id FOREIGN KEY (extension_record_id) REFERENCES nekostick.extension_records (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nekostick."__EFMigrationsHistory" WHERE "MigrationId" = '20260816000611_InitialPersistence') THEN
    CREATE TABLE nekostick.port_leases (
        id uuid NOT NULL,
        node_id character varying(128) NOT NULL,
        port integer NOT NULL,
        service_id uuid NOT NULL,
        lease_expires_at timestamptz NOT NULL,
        renewed_at timestamptz NOT NULL,
        version bigint NOT NULL DEFAULT 1,
        created_at timestamptz NOT NULL,
        updated_at timestamptz NOT NULL,
        CONSTRAINT pk_port_leases PRIMARY KEY (id),
        CONSTRAINT ck_port_leases_id_uuid_v7 CHECK (substring(id::text, 15, 1) = '7' AND substring(id::text, 20, 1) IN ('8', '9', 'a', 'b')),
        CONSTRAINT ck_port_leases_port CHECK (port BETWEEN 1 AND 65535),
        CONSTRAINT fk_port_leases_nodes_node_id FOREIGN KEY (node_id) REFERENCES nekostick.nodes (node_id) ON DELETE RESTRICT,
        CONSTRAINT fk_port_leases_services_service_id FOREIGN KEY (service_id) REFERENCES nekostick.services (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nekostick."__EFMigrationsHistory" WHERE "MigrationId" = '20260816000611_InitialPersistence') THEN
    CREATE TABLE nekostick.routes (
        id uuid NOT NULL,
        enabled boolean NOT NULL,
        matcher_type character varying(32) NOT NULL,
        pattern character varying(4096) NOT NULL,
        host_patterns_json jsonb NOT NULL,
        methods_json jsonb NOT NULL,
        target_type character varying(32) NOT NULL,
        target_id character varying(4096) NOT NULL,
        service_id uuid,
        static_root_path character varying(4096),
        extension_handler_id character varying(256),
        priority integer NOT NULL,
        forwarding_mode character varying(16) NOT NULL,
        replace_template character varying(4096),
        request_header_rewrites_json jsonb NOT NULL,
        response_header_rewrites_json jsonb NOT NULL,
        metadata_json jsonb NOT NULL,
        created_at timestamptz NOT NULL,
        updated_at timestamptz NOT NULL,
        version bigint NOT NULL DEFAULT 1,
        CONSTRAINT pk_routes PRIMARY KEY (id),
        CONSTRAINT ck_routes_enum_values CHECK (matcher_type IN ('Exact', 'ExactCaseInsensitive', 'Prefix', 'PrefixCaseInsensitive', 'Regex') AND target_type IN ('Microservice', 'StaticFile', 'ExtensionHandler') AND forwarding_mode IN ('Preserve', 'Strip', 'Replace')),
        CONSTRAINT ck_routes_forwarding_template CHECK ((forwarding_mode = 'Replace' AND replace_template IS NOT NULL AND length(replace_template) <= 4096) OR (forwarding_mode <> 'Replace' AND replace_template IS NULL)),
        CONSTRAINT ck_routes_id_uuid_v7 CHECK (substring(id::text, 15, 1) = '7' AND substring(id::text, 20, 1) IN ('8', '9', 'a', 'b')),
        CONSTRAINT ck_routes_matcher_json CHECK (jsonb_typeof(host_patterns_json) = 'array' AND jsonb_typeof(methods_json) = 'array' AND octet_length(host_patterns_json::text) <= 262144 AND octet_length(methods_json::text) <= 262144),
        CONSTRAINT ck_routes_pattern_length CHECK (length(pattern) BETWEEN 1 AND 4096),
        CONSTRAINT ck_routes_rewrite_metadata_json CHECK (jsonb_typeof(request_header_rewrites_json) = 'array' AND jsonb_typeof(response_header_rewrites_json) = 'array' AND jsonb_typeof(metadata_json) = 'object' AND octet_length(request_header_rewrites_json::text) <= 1048576 AND octet_length(response_header_rewrites_json::text) <= 1048576 AND octet_length(metadata_json::text) <= 1048576),
        CONSTRAINT ck_routes_target_reference CHECK ((target_type = 'Microservice' AND service_id IS NOT NULL AND target_id = service_id::text AND static_root_path IS NULL AND extension_handler_id IS NULL) OR (target_type = 'StaticFile' AND service_id IS NULL AND target_id = static_root_path AND static_root_path IS NOT NULL AND extension_handler_id IS NULL) OR (target_type = 'ExtensionHandler' AND service_id IS NULL AND target_id = extension_handler_id AND static_root_path IS NULL AND extension_handler_id IS NOT NULL)),
        CONSTRAINT fk_routes_services_service_id FOREIGN KEY (service_id) REFERENCES nekostick.services (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nekostick."__EFMigrationsHistory" WHERE "MigrationId" = '20260816000611_InitialPersistence') THEN
    INSERT INTO nekostick.configuration_revisions (id, committed_at, committed_by, created_at, revision_key, updated_at, version)
    VALUES ('018f0f00-0000-7000-8000-000000000001', TIMESTAMPTZ '2025-01-01T00:00:00+00:00', 'system', TIMESTAMPTZ '2025-01-01T00:00:00+00:00', 'global', TIMESTAMPTZ '2025-01-01T00:00:00+00:00', 1);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nekostick."__EFMigrationsHistory" WHERE "MigrationId" = '20260816000611_InitialPersistence') THEN
    INSERT INTO nekostick.global_settings (id, auto_port_range_end, auto_port_range_start, configuration_poll_interval_seconds, created_at, max_concurrent_requests, max_request_body_bytes, trusted_proxy_cidrs_json, updated_at, version)
    VALUES ('018f0f00-0000-7000-8000-000000000002', 29999, 20000, 30, TIMESTAMPTZ '2025-01-01T00:00:00+00:00', 1024, 31457280, '[]', TIMESTAMPTZ '2025-01-01T00:00:00+00:00', 1);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nekostick."__EFMigrationsHistory" WHERE "MigrationId" = '20260816000611_InitialPersistence') THEN
    CREATE UNIQUE INDEX ux_configuration_revisions_revision_key ON nekostick.configuration_revisions (revision_key);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nekostick."__EFMigrationsHistory" WHERE "MigrationId" = '20260816000611_InitialPersistence') THEN
    CREATE UNIQUE INDEX ux_extension_records_extension_id ON nekostick.extension_records (extension_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nekostick."__EFMigrationsHistory" WHERE "MigrationId" = '20260816000611_InitialPersistence') THEN
    CREATE UNIQUE INDEX ux_extension_settings_extension_record_id ON nekostick.extension_settings (extension_record_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nekostick."__EFMigrationsHistory" WHERE "MigrationId" = '20260816000611_InitialPersistence') THEN
    CREATE UNIQUE INDEX ux_nodes_default_node_id_active ON nekostick.nodes (node_id) WHERE node_id = '0' AND is_active;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nekostick."__EFMigrationsHistory" WHERE "MigrationId" = '20260816000611_InitialPersistence') THEN
    CREATE INDEX ix_port_leases_service_id ON nekostick.port_leases (service_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nekostick."__EFMigrationsHistory" WHERE "MigrationId" = '20260816000611_InitialPersistence') THEN
    CREATE UNIQUE INDEX ux_port_leases_node_id_port ON nekostick.port_leases (node_id, port);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nekostick."__EFMigrationsHistory" WHERE "MigrationId" = '20260816000611_InitialPersistence') THEN
    CREATE INDEX ix_routes_enabled_matcher_type_priority ON nekostick.routes (enabled, matcher_type, priority);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nekostick."__EFMigrationsHistory" WHERE "MigrationId" = '20260816000611_InitialPersistence') THEN
    CREATE INDEX ix_routes_service_id ON nekostick.routes (service_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nekostick."__EFMigrationsHistory" WHERE "MigrationId" = '20260816000611_InitialPersistence') THEN
    CREATE INDEX ix_services_enabled ON nekostick.services (enabled);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nekostick."__EFMigrationsHistory" WHERE "MigrationId" = '20260816000611_InitialPersistence') THEN
    INSERT INTO nekostick."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260816000611_InitialPersistence', '10.0.11');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nekostick."__EFMigrationsHistory" WHERE "MigrationId" = '20260816170010_AddGlobalProxyTimeouts') THEN
    ALTER TABLE nekostick.global_settings ADD connect_timeout_milliseconds integer NOT NULL DEFAULT 10000;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nekostick."__EFMigrationsHistory" WHERE "MigrationId" = '20260816170010_AddGlobalProxyTimeouts') THEN
    ALTER TABLE nekostick.global_settings ADD http_activity_timeout_milliseconds integer NOT NULL DEFAULT 30000;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nekostick."__EFMigrationsHistory" WHERE "MigrationId" = '20260816170010_AddGlobalProxyTimeouts') THEN
    ALTER TABLE nekostick.global_settings ADD http_total_timeout_milliseconds integer NOT NULL DEFAULT 100000;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nekostick."__EFMigrationsHistory" WHERE "MigrationId" = '20260816170010_AddGlobalProxyTimeouts') THEN
    ALTER TABLE nekostick.global_settings ADD websocket_idle_timeout_milliseconds integer NOT NULL DEFAULT 120000;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nekostick."__EFMigrationsHistory" WHERE "MigrationId" = '20260816170010_AddGlobalProxyTimeouts') THEN
    UPDATE nekostick.global_settings SET connect_timeout_milliseconds = 10000, http_activity_timeout_milliseconds = 30000, http_total_timeout_milliseconds = 100000, websocket_idle_timeout_milliseconds = 120000
    WHERE id = '018f0f00-0000-7000-8000-000000000002';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nekostick."__EFMigrationsHistory" WHERE "MigrationId" = '20260816170010_AddGlobalProxyTimeouts') THEN
    ALTER TABLE nekostick.global_settings ADD CONSTRAINT ck_global_settings_proxy_timeouts CHECK (connect_timeout_milliseconds BETWEEN 1 AND 86400000 AND http_activity_timeout_milliseconds BETWEEN 1 AND 86400000 AND http_total_timeout_milliseconds BETWEEN 1 AND 86400000 AND websocket_idle_timeout_milliseconds BETWEEN 1 AND 86400000);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nekostick."__EFMigrationsHistory" WHERE "MigrationId" = '20260816170010_AddGlobalProxyTimeouts') THEN
    INSERT INTO nekostick."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260816170010_AddGlobalProxyTimeouts', '10.0.11');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nekostick."__EFMigrationsHistory" WHERE "MigrationId" = '20260816203250_AddServiceRuntime') THEN
    CREATE TABLE nekostick.service_runtimes (
        service_id uuid NOT NULL,
        node_id character varying(128) NOT NULL,
        lifecycle character varying(16) NOT NULL,
        health character varying(16) NOT NULL,
        restart_count integer NOT NULL,
        created_at timestamptz NOT NULL,
        updated_at timestamptz NOT NULL,
        version bigint NOT NULL DEFAULT 1,
        CONSTRAINT pk_service_runtimes PRIMARY KEY (node_id, service_id),
        CONSTRAINT ck_service_runtimes_state CHECK (lifecycle IN ('Disabled', 'Starting', 'Running', 'Stopping', 'Failed') AND health IN ('Unknown', 'Healthy', 'Unhealthy') AND restart_count >= 0),
        CONSTRAINT fk_service_runtimes_services_service_id FOREIGN KEY (service_id) REFERENCES nekostick.services (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nekostick."__EFMigrationsHistory" WHERE "MigrationId" = '20260816203250_AddServiceRuntime') THEN
    CREATE INDEX ix_service_runtimes_service_id ON nekostick.service_runtimes (service_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nekostick."__EFMigrationsHistory" WHERE "MigrationId" = '20260816203250_AddServiceRuntime') THEN
    INSERT INTO nekostick."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260816203250_AddServiceRuntime', '10.0.11');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nekostick."__EFMigrationsHistory" WHERE "MigrationId" = '20260818230804_AddRequestLimitsAndRatePolicies') THEN
    ALTER TABLE nekostick.global_settings DROP CONSTRAINT ck_global_settings_limits;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nekostick."__EFMigrationsHistory" WHERE "MigrationId" = '20260818230804_AddRequestLimitsAndRatePolicies') THEN
    ALTER TABLE nekostick.routes ADD client_ip_rate_queue_limit integer;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nekostick."__EFMigrationsHistory" WHERE "MigrationId" = '20260818230804_AddRequestLimitsAndRatePolicies') THEN
    ALTER TABLE nekostick.routes ADD client_ip_rate_rejection_behavior character varying(16);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nekostick."__EFMigrationsHistory" WHERE "MigrationId" = '20260818230804_AddRequestLimitsAndRatePolicies') THEN
    ALTER TABLE nekostick.routes ADD client_ip_rate_replenishment_period_milliseconds integer;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nekostick."__EFMigrationsHistory" WHERE "MigrationId" = '20260818230804_AddRequestLimitsAndRatePolicies') THEN
    ALTER TABLE nekostick.routes ADD client_ip_rate_retry_after_behavior character varying(32);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nekostick."__EFMigrationsHistory" WHERE "MigrationId" = '20260818230804_AddRequestLimitsAndRatePolicies') THEN
    ALTER TABLE nekostick.routes ADD client_ip_rate_token_limit bigint;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nekostick."__EFMigrationsHistory" WHERE "MigrationId" = '20260818230804_AddRequestLimitsAndRatePolicies') THEN
    ALTER TABLE nekostick.routes ADD client_ip_rate_tokens_per_period bigint;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nekostick."__EFMigrationsHistory" WHERE "MigrationId" = '20260818230804_AddRequestLimitsAndRatePolicies') THEN
    ALTER TABLE nekostick.global_settings ADD client_ip_rate_queue_limit integer;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nekostick."__EFMigrationsHistory" WHERE "MigrationId" = '20260818230804_AddRequestLimitsAndRatePolicies') THEN
    ALTER TABLE nekostick.global_settings ADD client_ip_rate_rejection_behavior character varying(16);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nekostick."__EFMigrationsHistory" WHERE "MigrationId" = '20260818230804_AddRequestLimitsAndRatePolicies') THEN
    ALTER TABLE nekostick.global_settings ADD client_ip_rate_replenishment_period_milliseconds integer;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nekostick."__EFMigrationsHistory" WHERE "MigrationId" = '20260818230804_AddRequestLimitsAndRatePolicies') THEN
    ALTER TABLE nekostick.global_settings ADD client_ip_rate_retry_after_behavior character varying(32);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nekostick."__EFMigrationsHistory" WHERE "MigrationId" = '20260818230804_AddRequestLimitsAndRatePolicies') THEN
    ALTER TABLE nekostick.global_settings ADD client_ip_rate_token_limit bigint;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nekostick."__EFMigrationsHistory" WHERE "MigrationId" = '20260818230804_AddRequestLimitsAndRatePolicies') THEN
    ALTER TABLE nekostick.global_settings ADD client_ip_rate_tokens_per_period bigint;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nekostick."__EFMigrationsHistory" WHERE "MigrationId" = '20260818230804_AddRequestLimitsAndRatePolicies') THEN
    ALTER TABLE nekostick.global_settings ADD max_request_header_bytes bigint NOT NULL DEFAULT 0;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nekostick."__EFMigrationsHistory" WHERE "MigrationId" = '20260818230804_AddRequestLimitsAndRatePolicies') THEN
    ALTER TABLE nekostick.global_settings ADD request_read_timeout_milliseconds integer NOT NULL DEFAULT 0;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nekostick."__EFMigrationsHistory" WHERE "MigrationId" = '20260818230804_AddRequestLimitsAndRatePolicies') THEN
    UPDATE nekostick.global_settings SET client_ip_rate_queue_limit = NULL, client_ip_rate_rejection_behavior = NULL, client_ip_rate_replenishment_period_milliseconds = NULL, client_ip_rate_retry_after_behavior = NULL, client_ip_rate_token_limit = NULL, client_ip_rate_tokens_per_period = NULL, max_request_header_bytes = 32768, request_read_timeout_milliseconds = 30000
    WHERE id = '018f0f00-0000-7000-8000-000000000002';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nekostick."__EFMigrationsHistory" WHERE "MigrationId" = '20260818230804_AddRequestLimitsAndRatePolicies') THEN
    ALTER TABLE nekostick.routes ADD CONSTRAINT ck_routes_client_ip_rate_policy CHECK ((client_ip_rate_token_limit IS NULL AND client_ip_rate_tokens_per_period IS NULL AND client_ip_rate_replenishment_period_milliseconds IS NULL AND client_ip_rate_queue_limit IS NULL AND client_ip_rate_rejection_behavior IS NULL AND client_ip_rate_retry_after_behavior IS NULL) OR (client_ip_rate_token_limit > 0 AND client_ip_rate_tokens_per_period > 0 AND client_ip_rate_tokens_per_period <= client_ip_rate_token_limit AND client_ip_rate_replenishment_period_milliseconds BETWEEN 1 AND 86400000 AND client_ip_rate_queue_limit >= 0 AND client_ip_rate_rejection_behavior IN ('Reject', 'Queue') AND client_ip_rate_retry_after_behavior IN ('None', 'FromReplenishmentPeriod')));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nekostick."__EFMigrationsHistory" WHERE "MigrationId" = '20260818230804_AddRequestLimitsAndRatePolicies') THEN
    ALTER TABLE nekostick.global_settings ADD CONSTRAINT ck_global_settings_client_ip_rate_policy CHECK ((client_ip_rate_token_limit IS NULL AND client_ip_rate_tokens_per_period IS NULL AND client_ip_rate_replenishment_period_milliseconds IS NULL AND client_ip_rate_queue_limit IS NULL AND client_ip_rate_rejection_behavior IS NULL AND client_ip_rate_retry_after_behavior IS NULL) OR (client_ip_rate_token_limit > 0 AND client_ip_rate_tokens_per_period > 0 AND client_ip_rate_tokens_per_period <= client_ip_rate_token_limit AND client_ip_rate_replenishment_period_milliseconds BETWEEN 1 AND 86400000 AND client_ip_rate_queue_limit >= 0 AND client_ip_rate_rejection_behavior IN ('Reject', 'Queue') AND client_ip_rate_retry_after_behavior IN ('None', 'FromReplenishmentPeriod')));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nekostick."__EFMigrationsHistory" WHERE "MigrationId" = '20260818230804_AddRequestLimitsAndRatePolicies') THEN
    ALTER TABLE nekostick.global_settings ADD CONSTRAINT ck_global_settings_limits CHECK (max_request_body_bytes > 0 AND max_request_header_bytes > 0 AND max_concurrent_requests > 0 AND configuration_poll_interval_seconds > 0 AND request_read_timeout_milliseconds BETWEEN 1 AND 86400000);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nekostick."__EFMigrationsHistory" WHERE "MigrationId" = '20260818230804_AddRequestLimitsAndRatePolicies') THEN
    ALTER TABLE nekostick.global_settings ADD CONSTRAINT ck_global_settings_max_request_header_bytes CHECK (max_request_header_bytes <= 32768);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nekostick."__EFMigrationsHistory" WHERE "MigrationId" = '20260818230804_AddRequestLimitsAndRatePolicies') THEN
    INSERT INTO nekostick."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260818230804_AddRequestLimitsAndRatePolicies', '10.0.11');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nekostick."__EFMigrationsHistory" WHERE "MigrationId" = '20260818234332_HardenRatePolicyChecks') THEN
    ALTER TABLE nekostick.routes DROP CONSTRAINT ck_routes_client_ip_rate_policy;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nekostick."__EFMigrationsHistory" WHERE "MigrationId" = '20260818234332_HardenRatePolicyChecks') THEN
    ALTER TABLE nekostick.global_settings DROP CONSTRAINT ck_global_settings_client_ip_rate_policy;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nekostick."__EFMigrationsHistory" WHERE "MigrationId" = '20260818234332_HardenRatePolicyChecks') THEN
    ALTER TABLE nekostick.routes ADD CONSTRAINT ck_routes_client_ip_rate_policy CHECK ((client_ip_rate_token_limit IS NULL AND client_ip_rate_tokens_per_period IS NULL AND client_ip_rate_replenishment_period_milliseconds IS NULL AND client_ip_rate_queue_limit IS NULL AND client_ip_rate_rejection_behavior IS NULL AND client_ip_rate_retry_after_behavior IS NULL) OR (client_ip_rate_token_limit IS NOT NULL AND client_ip_rate_tokens_per_period IS NOT NULL AND client_ip_rate_replenishment_period_milliseconds IS NOT NULL AND client_ip_rate_queue_limit IS NOT NULL AND client_ip_rate_rejection_behavior IS NOT NULL AND client_ip_rate_retry_after_behavior IS NOT NULL AND client_ip_rate_token_limit > 0 AND client_ip_rate_tokens_per_period > 0 AND client_ip_rate_tokens_per_period <= client_ip_rate_token_limit AND client_ip_rate_replenishment_period_milliseconds BETWEEN 1 AND 86400000 AND client_ip_rate_queue_limit >= 0 AND client_ip_rate_rejection_behavior IN ('Reject', 'Queue') AND client_ip_rate_retry_after_behavior IN ('None', 'FromReplenishmentPeriod')));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nekostick."__EFMigrationsHistory" WHERE "MigrationId" = '20260818234332_HardenRatePolicyChecks') THEN
    ALTER TABLE nekostick.global_settings ADD CONSTRAINT ck_global_settings_client_ip_rate_policy CHECK ((client_ip_rate_token_limit IS NULL AND client_ip_rate_tokens_per_period IS NULL AND client_ip_rate_replenishment_period_milliseconds IS NULL AND client_ip_rate_queue_limit IS NULL AND client_ip_rate_rejection_behavior IS NULL AND client_ip_rate_retry_after_behavior IS NULL) OR (client_ip_rate_token_limit IS NOT NULL AND client_ip_rate_tokens_per_period IS NOT NULL AND client_ip_rate_replenishment_period_milliseconds IS NOT NULL AND client_ip_rate_queue_limit IS NOT NULL AND client_ip_rate_rejection_behavior IS NOT NULL AND client_ip_rate_retry_after_behavior IS NOT NULL AND client_ip_rate_token_limit > 0 AND client_ip_rate_tokens_per_period > 0 AND client_ip_rate_tokens_per_period <= client_ip_rate_token_limit AND client_ip_rate_replenishment_period_milliseconds BETWEEN 1 AND 86400000 AND client_ip_rate_queue_limit >= 0 AND client_ip_rate_rejection_behavior IN ('Reject', 'Queue') AND client_ip_rate_retry_after_behavior IN ('None', 'FromReplenishmentPeriod')));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nekostick."__EFMigrationsHistory" WHERE "MigrationId" = '20260818234332_HardenRatePolicyChecks') THEN
    INSERT INTO nekostick."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260818234332_HardenRatePolicyChecks', '10.0.11');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nekostick."__EFMigrationsHistory" WHERE "MigrationId" = '20260819015557_AddRouteResourceOverrides') THEN
    ALTER TABLE nekostick.routes ADD max_concurrent_requests integer;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nekostick."__EFMigrationsHistory" WHERE "MigrationId" = '20260819015557_AddRouteResourceOverrides') THEN
    ALTER TABLE nekostick.routes ADD max_request_body_bytes bigint;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nekostick."__EFMigrationsHistory" WHERE "MigrationId" = '20260819015557_AddRouteResourceOverrides') THEN
    ALTER TABLE nekostick.routes ADD max_request_header_bytes bigint;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nekostick."__EFMigrationsHistory" WHERE "MigrationId" = '20260819015557_AddRouteResourceOverrides') THEN
    ALTER TABLE nekostick.routes ADD request_read_timeout_milliseconds integer;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nekostick."__EFMigrationsHistory" WHERE "MigrationId" = '20260819015557_AddRouteResourceOverrides') THEN
    ALTER TABLE nekostick.routes ADD CONSTRAINT ck_routes_resource_limits CHECK ((max_request_body_bytes IS NULL OR max_request_body_bytes BETWEEN 1 AND 31457280) AND (max_request_header_bytes IS NULL OR max_request_header_bytes BETWEEN 1 AND 32768) AND (max_concurrent_requests IS NULL OR max_concurrent_requests > 0) AND (request_read_timeout_milliseconds IS NULL OR request_read_timeout_milliseconds BETWEEN 1 AND 86400000));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nekostick."__EFMigrationsHistory" WHERE "MigrationId" = '20260819015557_AddRouteResourceOverrides') THEN
    UPDATE "nekostick"."global_settings"
    SET "max_request_body_bytes" = 31457280
    WHERE "max_request_body_bytes" > 31457280;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nekostick."__EFMigrationsHistory" WHERE "MigrationId" = '20260819015557_AddRouteResourceOverrides') THEN
    ALTER TABLE nekostick.global_settings ADD CONSTRAINT ck_global_settings_max_request_body_bytes CHECK (max_request_body_bytes <= 31457280);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nekostick."__EFMigrationsHistory" WHERE "MigrationId" = '20260819015557_AddRouteResourceOverrides') THEN
    INSERT INTO nekostick."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260819015557_AddRouteResourceOverrides', '10.0.11');
    END IF;
END $EF$;
COMMIT;


START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nekostick."__EFMigrationsHistory" WHERE "MigrationId" = '20260820090000_AddProxyRetries') THEN
    ALTER TABLE nekostick.global_settings
        ADD proxy_max_retries integer NOT NULL DEFAULT 0,
        ADD proxy_initial_retry_backoff_milliseconds integer NOT NULL DEFAULT 200,
        ADD proxy_maximum_retry_backoff_milliseconds integer NOT NULL DEFAULT 2000,
        ADD proxy_retry_on_connection_failure boolean NOT NULL DEFAULT TRUE,
        ADD proxy_retry_on_upstream_disconnect boolean NOT NULL DEFAULT TRUE,
        ADD CONSTRAINT ck_global_settings_proxy_retries CHECK (proxy_max_retries BETWEEN 0 AND 10 AND proxy_initial_retry_backoff_milliseconds BETWEEN 1 AND 2000 AND proxy_maximum_retry_backoff_milliseconds BETWEEN 1 AND 2000 AND proxy_initial_retry_backoff_milliseconds <= proxy_maximum_retry_backoff_milliseconds);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nekostick."__EFMigrationsHistory" WHERE "MigrationId" = '20260820090000_AddProxyRetries') THEN
    INSERT INTO nekostick."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260820090000_AddProxyRetries', '10.0.11');
    END IF;
END $EF$;
COMMIT;
START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nekostick."__EFMigrationsHistory" WHERE "MigrationId" = '20260820100000_AddRouteProxyRetries') THEN
    ALTER TABLE nekostick.routes
        ADD proxy_max_retries integer,
        ADD proxy_initial_retry_backoff_milliseconds integer,
        ADD proxy_maximum_retry_backoff_milliseconds integer,
        ADD proxy_retry_on_connection_failure boolean,
        ADD proxy_retry_on_upstream_disconnect boolean,
        ADD CONSTRAINT ck_routes_proxy_retries CHECK ((proxy_max_retries IS NULL AND proxy_initial_retry_backoff_milliseconds IS NULL AND proxy_maximum_retry_backoff_milliseconds IS NULL AND proxy_retry_on_connection_failure IS NULL AND proxy_retry_on_upstream_disconnect IS NULL) OR (proxy_max_retries IS NOT NULL AND proxy_initial_retry_backoff_milliseconds IS NOT NULL AND proxy_maximum_retry_backoff_milliseconds IS NOT NULL AND proxy_retry_on_connection_failure IS NOT NULL AND proxy_retry_on_upstream_disconnect IS NOT NULL AND proxy_max_retries BETWEEN 0 AND 10 AND proxy_initial_retry_backoff_milliseconds BETWEEN 1 AND 2000 AND proxy_maximum_retry_backoff_milliseconds BETWEEN 1 AND 2000 AND proxy_initial_retry_backoff_milliseconds <= proxy_maximum_retry_backoff_milliseconds));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nekostick."__EFMigrationsHistory" WHERE "MigrationId" = '20260820100000_AddRouteProxyRetries') THEN
    INSERT INTO nekostick."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260820100000_AddRouteProxyRetries', '10.0.11');
    END IF;
END $EF$;
COMMIT;
