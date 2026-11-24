using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.Inference.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddSubscriptionScheduledChange : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "cancel_effective_at",
                table: "subscriptions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "scheduled_change_at",
                table: "subscriptions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "scheduled_plan_id",
                table: "subscriptions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "scheduled_seats",
                table: "subscriptions",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_subscriptions_scheduled_plan_id",
                table: "subscriptions",
                column: "scheduled_plan_id");

            migrationBuilder.AddForeignKey(
                name: "FK_subscriptions_plans_scheduled_plan_id",
                table: "subscriptions",
                column: "scheduled_plan_id",
                principalTable: "plans",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_subscriptions_plans_scheduled_plan_id",
                table: "subscriptions");

            migrationBuilder.DropIndex(
                name: "IX_subscriptions_scheduled_plan_id",
                table: "subscriptions");

            migrationBuilder.DropColumn(
                name: "cancel_effective_at",
                table: "subscriptions");

            migrationBuilder.DropColumn(
                name: "scheduled_change_at",
                table: "subscriptions");

            migrationBuilder.DropColumn(
                name: "scheduled_plan_id",
                table: "subscriptions");

            migrationBuilder.DropColumn(
                name: "scheduled_seats",
                table: "subscriptions");
        }
    }
}
