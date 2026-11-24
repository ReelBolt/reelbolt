using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.Inference.Api.Migrations
{
    /// <summary>
    /// The model catalog (docs/design/model-catalog.md §2, §4.1): <c>inference_providers</c> becomes the provider
    /// ACCOUNT; each of its rows is split, 1:1 and losslessly, into one <c>inference_models</c> row and one
    /// <c>model_capabilities</c> row that keeps the provider row's id, so every reference stays valid. The three
    /// hard FKs are re-pointed at <c>model_capabilities</c>. Generated with dotnet-ef, then reordered by hand so the
    /// data moves before the old columns are dropped (the generator also proposed renaming <c>is_default</c> to
    /// <c>is_system</c>, which would have lost the defaults). Touches only API-owned tables.
    /// </summary>
    public partial class AddModelCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. The three FKs that name a provider row; re-pointed at model_capabilities in step 5.
            migrationBuilder.DropForeignKey(
                name: "fk_agent_definitions__inference_providers_inference_provider_id",
                table: "agent_definitions");

            migrationBuilder.DropForeignKey(
                name: "fk_organization_agent_provider_overrides_inference_providers_i~",
                table: "organization_agent_provider_overrides");

            migrationBuilder.DropForeignKey(
                name: "FK_project_voices_inference_providers_inference_provider_id",
                table: "project_voices");

            // 2. The new tables and columns.
            migrationBuilder.AddColumn<bool>(
                name: "is_system",
                table: "inference_providers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // IF NOT EXISTS: Down keeps these two columns (design §4.3: usage recorded in between keeps its price_id), so
            // an Up after a Down finds them already there.
            migrationBuilder.Sql(ModelCatalogMigrationSql.UpUsageEventColumns);

            migrationBuilder.CreateTable(
                name: "inference_models",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: true),
                    model_code = table.Column<string>(type: "text", nullable: false),
                    display_name = table.Column<string>(type: "text", nullable: false),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_inference_models", x => x.id);
                    table.ForeignKey(
                        name: "FK_inference_models_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_inference_models__inference_providers_provider_id",
                        column: x => x.provider_id,
                        principalTable: "inference_providers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "pricing_settings",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    markup = table.Column<decimal>(type: "numeric(12,6)", precision: 12, scale: 6, nullable: false, defaultValue: 1.2m),
                    credit_usd_value = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false, defaultValue: 0.006m),
                    catalog_upgraded_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pricing_settings", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "model_capabilities",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    model_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: true),
                    capability = table.Column<string>(type: "text", nullable: false, defaultValue: "Chat"),
                    is_default = table.Column<bool>(type: "boolean", nullable: false),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    price_source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, defaultValue: "Table"),
                    last_test_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_test_ok = table.Column<bool>(type: "boolean", nullable: true),
                    last_test_error = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_model_capabilities", x => x.id);
                    table.ForeignKey(
                        name: "FK_model_capabilities_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_model_capabilities_inference_models_model_id",
                        column: x => x.model_id,
                        principalTable: "inference_models",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "model_capability_prices",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    model_capability_id = table.Column<Guid>(type: "uuid", nullable: false),
                    unit = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    variant = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    cost_usd_per_unit = table.Column<decimal>(type: "numeric(28,12)", precision: 28, scale: 12, nullable: false),
                    credits_millis_per_unit_override = table.Column<decimal>(type: "numeric(28,10)", precision: 28, scale: 10, nullable: true),
                    effective_from = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    effective_to = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    notes = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_model_capability_prices", x => x.id);
                    table.ForeignKey(
                        name: "fk_model_capability_prices_model_capabilities_model_capability~",
                        column: x => x.model_capability_id,
                        principalTable: "model_capabilities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_inference_models_organization_id_display_name",
                table: "inference_models",
                columns: new[] { "organization_id", "display_name" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "ix_inference_models_provider_id",
                table: "inference_models",
                column: "provider_id");

            migrationBuilder.CreateIndex(
                name: "IX_model_capabilities_model_id_capability",
                table: "model_capabilities",
                columns: new[] { "model_id", "capability" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_model_capabilities_organization_id_capability",
                table: "model_capabilities",
                columns: new[] { "organization_id", "capability" },
                unique: true,
                filter: "is_default")
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_model_capability_prices_model_capability_id_unit_variant_ef~",
                table: "model_capability_prices",
                columns: new[] { "model_capability_id", "unit", "variant", "effective_from" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            // 3. Every provider row -> one model + one capability with the SAME id (before any column is dropped).
            foreach (string statement in ModelCatalogMigrationSql.UpSplit)
            {
                migrationBuilder.Sql(statement);
            }

            // 4. The default rule and the moved columns now live on model_capabilities / inference_models.
            migrationBuilder.DropIndex(
                name: "IX_inference_providers_organization_id_capability",
                table: "inference_providers");

            migrationBuilder.DropColumn(
                name: "capability",
                table: "inference_providers");

            migrationBuilder.DropColumn(
                name: "model_name",
                table: "inference_providers");

            migrationBuilder.DropColumn(
                name: "is_default",
                table: "inference_providers");

            migrationBuilder.DropColumn(
                name: "last_test_at",
                table: "inference_providers");

            migrationBuilder.DropColumn(
                name: "last_test_ok",
                table: "inference_providers");

            migrationBuilder.DropColumn(
                name: "last_test_error",
                table: "inference_providers");

            // 5. The FK columns keep their values; they now name model capabilities (ON DELETE SET NULL).
            migrationBuilder.AddForeignKey(
                name: "fk_agent_definitions__model_capabilities_inference_provider_id",
                table: "agent_definitions",
                column: "inference_provider_id",
                principalTable: "model_capabilities",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "fk_organization_agent_provider_overrides_model_capabilities_in~",
                table: "organization_agent_provider_overrides",
                column: "inference_provider_id",
                principalTable: "model_capabilities",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_project_voices_model_capabilities_inference_provider_id",
                table: "project_voices",
                column: "inference_provider_id",
                principalTable: "model_capabilities",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            // 6. The one pricing settings row.
            migrationBuilder.Sql(ModelCatalogMigrationSql.UpPricingSettings);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_agent_definitions__model_capabilities_inference_provider_id",
                table: "agent_definitions");

            migrationBuilder.DropForeignKey(
                name: "fk_organization_agent_provider_overrides_model_capabilities_in~",
                table: "organization_agent_provider_overrides");

            migrationBuilder.DropForeignKey(
                name: "FK_project_voices_model_capabilities_inference_provider_id",
                table: "project_voices");

            migrationBuilder.AddColumn<string>(
                name: "capability",
                table: "inference_providers",
                type: "text",
                nullable: false,
                defaultValue: "Chat");

            migrationBuilder.AddColumn<string>(
                name: "model_name",
                table: "inference_providers",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "is_default",
                table: "inference_providers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "last_test_at",
                table: "inference_providers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "last_test_ok",
                table: "inference_providers",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "last_test_error",
                table: "inference_providers",
                type: "text",
                nullable: true);

            // Every capability back to one provider row with its id (design §4.1 rollback; prices are lost).
            foreach (string statement in ModelCatalogMigrationSql.DownRestore)
            {
                migrationBuilder.Sql(statement);
            }

            migrationBuilder.CreateIndex(
                name: "IX_inference_providers_organization_id_capability",
                table: "inference_providers",
                columns: new[] { "organization_id", "capability" },
                unique: true,
                filter: "is_default")
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.DropTable(
                name: "model_capability_prices");

            migrationBuilder.DropTable(
                name: "pricing_settings");

            migrationBuilder.DropTable(
                name: "model_capabilities");

            migrationBuilder.DropTable(
                name: "inference_models");

            migrationBuilder.DropColumn(
                name: "is_system",
                table: "inference_providers");

            // usage_events.price_id and variant stay (design §4.3): usage recorded while the catalog ran keeps its price
            // id, the previous code ignores both columns, and Up adds them only IF NOT EXISTS.

            migrationBuilder.AddForeignKey(
                name: "fk_agent_definitions__inference_providers_inference_provider_id",
                table: "agent_definitions",
                column: "inference_provider_id",
                principalTable: "inference_providers",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "fk_organization_agent_provider_overrides_inference_providers_i~",
                table: "organization_agent_provider_overrides",
                column: "inference_provider_id",
                principalTable: "inference_providers",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_project_voices_inference_providers_inference_provider_id",
                table: "project_voices",
                column: "inference_provider_id",
                principalTable: "inference_providers",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }
    }
}
