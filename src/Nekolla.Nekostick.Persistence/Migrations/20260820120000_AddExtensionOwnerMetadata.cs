using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nekolla.Nekostick.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddExtensionOwnerMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "owner_extension_id",
                schema: "nekostick",
                table: "services",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "owner_extension_id",
                schema: "nekostick",
                table: "routes",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);
            migrationBuilder.CreateIndex(
                name: "ix_routes_owner_extension_id",
                schema: "nekostick",
                table: "routes",
                column: "owner_extension_id");

            migrationBuilder.CreateIndex(
                name: "ix_services_owner_extension_id",
                schema: "nekostick",
                table: "services",
                column: "owner_extension_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_routes_owner_extension_id",
                schema: "nekostick",
                table: "routes");

            migrationBuilder.DropIndex(
                name: "ix_services_owner_extension_id",
                schema: "nekostick",
                table: "services");
            migrationBuilder.DropColumn(
                name: "owner_extension_id",
                schema: "nekostick",
                table: "services");

            migrationBuilder.DropColumn(
                name: "owner_extension_id",
                schema: "nekostick",
                table: "routes");
        }
    }
}
