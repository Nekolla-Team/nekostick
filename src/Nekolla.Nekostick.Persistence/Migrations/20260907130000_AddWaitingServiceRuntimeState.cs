using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nekolla.Nekostick.Persistence.Migrations;

/// <inheritdoc />
public partial class AddWaitingServiceRuntimeState : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(
            name: "ck_service_runtimes_state",
            schema: "nekostick",
            table: "service_runtimes");

        migrationBuilder.AddCheckConstraint(
            name: "ck_service_runtimes_state",
            schema: "nekostick",
            table: "service_runtimes",
            sql: "lifecycle IN ('Disabled', 'Starting', 'Running', 'Stopping', 'Failed', 'Waiting') AND health IN ('Unknown', 'Healthy', 'Unhealthy') AND restart_count >= 0");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(
            name: "ck_service_runtimes_state",
            schema: "nekostick",
            table: "service_runtimes");

        migrationBuilder.AddCheckConstraint(
            name: "ck_service_runtimes_state",
            schema: "nekostick",
            table: "service_runtimes",
            sql: "lifecycle IN ('Disabled', 'Starting', 'Running', 'Stopping', 'Failed') AND health IN ('Unknown', 'Healthy', 'Unhealthy') AND restart_count >= 0");
    }
}
