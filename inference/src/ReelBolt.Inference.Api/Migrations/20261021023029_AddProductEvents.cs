using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ReelBolt.Inference.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddProductEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "product_events",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: true),
                    @event = table.Column<string>(name: "event", type: "character varying(64)", maxLength: 64, nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_product_events", x => x.id);
                    table.CheckConstraint("ck_product_events_source", "source IN ('server', 'client', 'consent')");
                    table.CheckConstraint("ck_product_events_subject", "user_id IS NOT NULL OR organization_id IS NOT NULL");
                });

            migrationBuilder.CreateIndex(
                name: "ix_product_events_event_occurred_at",
                table: "product_events",
                columns: new[] { "event", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_product_events_organization_id",
                table: "product_events",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "ix_product_events_user_id_occurred_at",
                table: "product_events",
                columns: new[] { "user_id", "occurred_at" });

            migrationBuilder.Sql(ProductEventsMigrationSql.PowerBiGrant);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "product_events");
        }
    }
}
