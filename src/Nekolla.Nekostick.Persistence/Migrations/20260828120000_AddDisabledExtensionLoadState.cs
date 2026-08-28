using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nekolla.Nekostick.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDisabledExtensionLoadState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_extension_records_load_state",
                schema: "nekostick",
                table: "extension_records");

            migrationBuilder.AddCheckConstraint(
                name: "ck_extension_records_load_state",
                schema: "nekostick",
                table: "extension_records",
                sql: "load_state IN ('Discovered', 'Loaded', 'Stopped', 'Failed', 'Unloading', 'Disabled')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_extension_records_load_state",
                schema: "nekostick",
                table: "extension_records");

            migrationBuilder.AddCheckConstraint(
                name: "ck_extension_records_load_state",
                schema: "nekostick",
                table: "extension_records",
                sql: "load_state IN ('Discovered', 'Loaded', 'Stopped', 'Failed', 'Unloading')");
        }
    }
}
