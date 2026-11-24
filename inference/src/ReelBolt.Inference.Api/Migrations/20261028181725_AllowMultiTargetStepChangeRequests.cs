using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.Inference.Api.Migrations
{
    /// <inheritdoc />
    public partial class AllowMultiTargetStepChangeRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_step_change_requests_execution_id",
                table: "step_change_requests");

            migrationBuilder.AlterColumn<string>(
                name: "instruction",
                table: "step_change_requests",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(2000)",
                oldMaxLength: 2000);

            migrationBuilder.CreateIndex(
                name: "IX_step_change_requests_execution_id",
                table: "step_change_requests",
                column: "execution_id");

            migrationBuilder.CreateIndex(
                name: "IX_step_change_requests_execution_id_target_step_order",
                table: "step_change_requests",
                columns: new[] { "execution_id", "target_step_order" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Rolling back needs a data cleanup first: once a request has several targets (rows sharing execution_id),
            // recreating the unique execution_id index fails, and instructions over 2000 characters would not fit.
            migrationBuilder.DropIndex(
                name: "IX_step_change_requests_execution_id",
                table: "step_change_requests");

            migrationBuilder.DropIndex(
                name: "IX_step_change_requests_execution_id_target_step_order",
                table: "step_change_requests");

            migrationBuilder.AlterColumn<string>(
                name: "instruction",
                table: "step_change_requests",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(4000)",
                oldMaxLength: 4000);

            migrationBuilder.CreateIndex(
                name: "IX_step_change_requests_execution_id",
                table: "step_change_requests",
                column: "execution_id",
                unique: true);
        }
    }
}
