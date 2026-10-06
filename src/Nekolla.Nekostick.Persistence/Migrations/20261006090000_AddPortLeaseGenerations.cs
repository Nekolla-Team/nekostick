using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nekolla.Nekostick.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPortLeaseGenerations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "generation_id",
                schema: "nekostick",
                table: "port_leases",
                type: "uuid",
                nullable: true);

            migrationBuilder.Sql("UPDATE \"nekostick\".port_leases SET generation_id = id;");

            migrationBuilder.AlterColumn<Guid>(
                name: "generation_id",
                schema: "nekostick",
                table: "port_leases",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_port_leases_generation_id_uuid_v7",
                schema: "nekostick",
                table: "port_leases",
                sql: "substring(generation_id::text, 15, 1) = '7' AND substring(generation_id::text, 20, 1) IN ('8', '9', 'a', 'b')");

            migrationBuilder.CreateIndex(
                name: "ux_port_leases_node_id_service_id_generation_id",
                schema: "nekostick",
                table: "port_leases",
                columns: new[] { "node_id", "service_id", "generation_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("LOCK TABLE \"nekostick\".port_leases IN ACCESS EXCLUSIVE MODE;");

            migrationBuilder.Sql(
                """
                DO $EF$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM "nekostick".port_leases
                        GROUP BY node_id, service_id
                        HAVING COUNT(*) > 1
                    ) THEN
                        RAISE EXCEPTION 'Cannot drop generation_id while a node/service owns multiple port leases.'
                            USING ERRCODE = '23514';
                    END IF;
                END $EF$;
                """);

            migrationBuilder.DropIndex(
                name: "ux_port_leases_node_id_service_id_generation_id",
                schema: "nekostick",
                table: "port_leases");

            migrationBuilder.DropCheckConstraint(
                name: "ck_port_leases_generation_id_uuid_v7",
                schema: "nekostick",
                table: "port_leases");

            migrationBuilder.DropColumn(
                name: "generation_id",
                schema: "nekostick",
                table: "port_leases");
        }
    }
}
