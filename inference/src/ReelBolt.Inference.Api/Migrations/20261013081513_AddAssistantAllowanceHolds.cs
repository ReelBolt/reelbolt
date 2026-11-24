using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.Inference.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddAssistantAllowanceHolds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "assistant_allowance_holds",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    held_millis = table.Column<long>(type: "bigint", nullable: false),
                    consumed_millis = table.Column<long>(type: "bigint", nullable: false),
                    credit_reservation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    credit_held_millis = table.Column<long>(type: "bigint", nullable: false),
                    charged_millis = table.Column<long>(type: "bigint", nullable: false),
                    uncharged_millis = table.Column<long>(type: "bigint", nullable: false),
                    unsettled_metered_millis = table.Column<long>(type: "bigint", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    closed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_assistant_allowance_holds", x => x.id);
                    table.ForeignKey(
                        name: "FK_assistant_allowance_holds_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_assistant_allowance_holds_idempotency_key",
                table: "assistant_allowance_holds",
                column: "idempotency_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_assistant_allowance_holds_organization_id_created_at",
                table: "assistant_allowance_holds",
                columns: new[] { "organization_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "assistant_allowance_holds");
        }
    }
}
