using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nekolla.Nekostick.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HardenRatePolicyChecks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_routes_client_ip_rate_policy",
                schema: "nekostick",
                table: "routes");

            migrationBuilder.DropCheckConstraint(
                name: "ck_global_settings_client_ip_rate_policy",
                schema: "nekostick",
                table: "global_settings");

            migrationBuilder.AddCheckConstraint(
                name: "ck_routes_client_ip_rate_policy",
                schema: "nekostick",
                table: "routes",
                sql: "(client_ip_rate_token_limit IS NULL AND client_ip_rate_tokens_per_period IS NULL AND client_ip_rate_replenishment_period_milliseconds IS NULL AND client_ip_rate_queue_limit IS NULL AND client_ip_rate_rejection_behavior IS NULL AND client_ip_rate_retry_after_behavior IS NULL) OR (client_ip_rate_token_limit IS NOT NULL AND client_ip_rate_tokens_per_period IS NOT NULL AND client_ip_rate_replenishment_period_milliseconds IS NOT NULL AND client_ip_rate_queue_limit IS NOT NULL AND client_ip_rate_rejection_behavior IS NOT NULL AND client_ip_rate_retry_after_behavior IS NOT NULL AND client_ip_rate_token_limit > 0 AND client_ip_rate_tokens_per_period > 0 AND client_ip_rate_tokens_per_period <= client_ip_rate_token_limit AND client_ip_rate_replenishment_period_milliseconds BETWEEN 1 AND 86400000 AND client_ip_rate_queue_limit >= 0 AND client_ip_rate_rejection_behavior IN ('Reject', 'Queue') AND client_ip_rate_retry_after_behavior IN ('None', 'FromReplenishmentPeriod'))");

            migrationBuilder.AddCheckConstraint(
                name: "ck_global_settings_client_ip_rate_policy",
                schema: "nekostick",
                table: "global_settings",
                sql: "(client_ip_rate_token_limit IS NULL AND client_ip_rate_tokens_per_period IS NULL AND client_ip_rate_replenishment_period_milliseconds IS NULL AND client_ip_rate_queue_limit IS NULL AND client_ip_rate_rejection_behavior IS NULL AND client_ip_rate_retry_after_behavior IS NULL) OR (client_ip_rate_token_limit IS NOT NULL AND client_ip_rate_tokens_per_period IS NOT NULL AND client_ip_rate_replenishment_period_milliseconds IS NOT NULL AND client_ip_rate_queue_limit IS NOT NULL AND client_ip_rate_rejection_behavior IS NOT NULL AND client_ip_rate_retry_after_behavior IS NOT NULL AND client_ip_rate_token_limit > 0 AND client_ip_rate_tokens_per_period > 0 AND client_ip_rate_tokens_per_period <= client_ip_rate_token_limit AND client_ip_rate_replenishment_period_milliseconds BETWEEN 1 AND 86400000 AND client_ip_rate_queue_limit >= 0 AND client_ip_rate_rejection_behavior IN ('Reject', 'Queue') AND client_ip_rate_retry_after_behavior IN ('None', 'FromReplenishmentPeriod'))");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_routes_client_ip_rate_policy",
                schema: "nekostick",
                table: "routes");

            migrationBuilder.DropCheckConstraint(
                name: "ck_global_settings_client_ip_rate_policy",
                schema: "nekostick",
                table: "global_settings");

            migrationBuilder.AddCheckConstraint(
                name: "ck_routes_client_ip_rate_policy",
                schema: "nekostick",
                table: "routes",
                sql: "(client_ip_rate_token_limit IS NULL AND client_ip_rate_tokens_per_period IS NULL AND client_ip_rate_replenishment_period_milliseconds IS NULL AND client_ip_rate_queue_limit IS NULL AND client_ip_rate_rejection_behavior IS NULL AND client_ip_rate_retry_after_behavior IS NULL) OR (client_ip_rate_token_limit > 0 AND client_ip_rate_tokens_per_period > 0 AND client_ip_rate_tokens_per_period <= client_ip_rate_token_limit AND client_ip_rate_replenishment_period_milliseconds BETWEEN 1 AND 86400000 AND client_ip_rate_queue_limit >= 0 AND client_ip_rate_rejection_behavior IN ('Reject', 'Queue') AND client_ip_rate_retry_after_behavior IN ('None', 'FromReplenishmentPeriod'))");

            migrationBuilder.AddCheckConstraint(
                name: "ck_global_settings_client_ip_rate_policy",
                schema: "nekostick",
                table: "global_settings",
                sql: "(client_ip_rate_token_limit IS NULL AND client_ip_rate_tokens_per_period IS NULL AND client_ip_rate_replenishment_period_milliseconds IS NULL AND client_ip_rate_queue_limit IS NULL AND client_ip_rate_rejection_behavior IS NULL AND client_ip_rate_retry_after_behavior IS NULL) OR (client_ip_rate_token_limit > 0 AND client_ip_rate_tokens_per_period > 0 AND client_ip_rate_tokens_per_period <= client_ip_rate_token_limit AND client_ip_rate_replenishment_period_milliseconds BETWEEN 1 AND 86400000 AND client_ip_rate_queue_limit >= 0 AND client_ip_rate_rejection_behavior IN ('Reject', 'Queue') AND client_ip_rate_retry_after_behavior IN ('None', 'FromReplenishmentPeriod'))");
        }
    }
}
