using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NpgsqlTypes;

#nullable disable

namespace ReelBolt.Inference.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddRecipeMarketplace : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "marketplace_audit",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    recipe_id = table.Column<Guid>(type: "uuid", nullable: true),
                    version_id = table.Column<Guid>(type: "uuid", nullable: true),
                    action = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    actor_kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    detail_json = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_marketplace_audit", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "marketplace_recipes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    author_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    author_org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    hidden_by_reports = table.Column<bool>(type: "boolean", nullable: false),
                    name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    category = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    language = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    cost_class = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    step_count = table.Column<int>(type: "integer", nullable: false),
                    step_types = table.Column<string[]>(type: "text[]", nullable: false),
                    current_version_id = table.Column<Guid>(type: "uuid", nullable: true),
                    listed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ever_listed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    human_rejected_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    removed_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    consent_version = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    consent_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    author_label = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    copy_orgs = table.Column<int>(type: "integer", nullable: false),
                    run_count = table.Column<long>(type: "bigint", nullable: false),
                    rating_sum = table.Column<int>(type: "integer", nullable: false),
                    rating_count = table.Column<int>(type: "integer", nullable: false),
                    search_vector = table.Column<NpgsqlTsVector>(type: "tsvector", nullable: false)
                        .Annotation("Npgsql:TsVectorConfig", "simple")
                        .Annotation("Npgsql:TsVectorProperties", new[] { "name", "description", "category" }),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_marketplace_recipes", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "marketplace_copies",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    recipe_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    workflow_id = table.Column<Guid>(type: "uuid", nullable: true),
                    first_copied_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_marketplace_copies", x => x.id);
                    table.ForeignKey(
                        name: "fk_marketplace_copies__marketplace_recipes_recipe_id",
                        column: x => x.recipe_id,
                        principalTable: "marketplace_recipes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "marketplace_ratings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    recipe_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    stars = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_marketplace_ratings", x => x.id);
                    table.ForeignKey(
                        name: "fk_marketplace_ratings__marketplace_recipes_recipe_id",
                        column: x => x.recipe_id,
                        principalTable: "marketplace_recipes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "marketplace_recipe_versions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    recipe_id = table.Column<Guid>(type: "uuid", nullable: false),
                    n = table.Column<int>(type: "integer", nullable: false),
                    submitted_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    snapshot_digest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    snapshot_json = table.Column<string>(type: "jsonb", nullable: false),
                    manifest_json = table.Column<string>(type: "jsonb", nullable: false),
                    preview_source_json = table.Column<string>(type: "jsonb", nullable: true),
                    preview_key = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    poster_key = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    preview_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    preview_seconds = table.Column<double>(type: "double precision", nullable: true),
                    preview_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    check_json = table.Column<string>(type: "jsonb", nullable: true),
                    consent_version = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    consent_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_marketplace_recipe_versions", x => x.id);
                    table.ForeignKey(
                        name: "fk_marketplace_recipe_versions_marketplace_recipes_recipe_id",
                        column: x => x.recipe_id,
                        principalTable: "marketplace_recipes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "marketplace_reports",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    recipe_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reporter_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reason = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    handled_by = table.Column<Guid>(type: "uuid", nullable: true),
                    handled_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_marketplace_reports", x => x.id);
                    table.ForeignKey(
                        name: "fk_marketplace_reports_marketplace_recipes_recipe_id",
                        column: x => x.recipe_id,
                        principalTable: "marketplace_recipes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_marketplace_audit_actor_user_id",
                table: "marketplace_audit",
                column: "actor_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_marketplace_audit_recipe_id_created_at",
                table: "marketplace_audit",
                columns: new[] { "recipe_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_marketplace_copies_org_id",
                table: "marketplace_copies",
                column: "org_id");

            migrationBuilder.CreateIndex(
                name: "IX_marketplace_copies_recipe_id_org_id",
                table: "marketplace_copies",
                columns: new[] { "recipe_id", "org_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_marketplace_copies_workflow_id",
                table: "marketplace_copies",
                column: "workflow_id");

            migrationBuilder.CreateIndex(
                name: "IX_marketplace_ratings_recipe_id_user_id",
                table: "marketplace_ratings",
                columns: new[] { "recipe_id", "user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_marketplace_ratings_user_id",
                table: "marketplace_ratings",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_marketplace_recipe_versions_recipe_id_n",
                table: "marketplace_recipe_versions",
                columns: new[] { "recipe_id", "n" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_marketplace_recipe_versions_snapshot_digest",
                table: "marketplace_recipe_versions",
                column: "snapshot_digest");

            migrationBuilder.CreateIndex(
                name: "IX_marketplace_recipe_versions_submitted_by_user_id_created_at",
                table: "marketplace_recipe_versions",
                columns: new[] { "submitted_by_user_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_marketplace_recipes_author_org_id_status",
                table: "marketplace_recipes",
                columns: new[] { "author_org_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_marketplace_recipes_author_user_id",
                table: "marketplace_recipes",
                column: "author_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_marketplace_recipes_category_status",
                table: "marketplace_recipes",
                columns: new[] { "category", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_marketplace_recipes_search_vector",
                table: "marketplace_recipes",
                column: "search_vector")
                .Annotation("Npgsql:IndexMethod", "GIN");

            migrationBuilder.CreateIndex(
                name: "IX_marketplace_recipes_status_listed_at_id",
                table: "marketplace_recipes",
                columns: new[] { "status", "listed_at", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_marketplace_reports_recipe_id_reporter_user_id",
                table: "marketplace_reports",
                columns: new[] { "recipe_id", "reporter_user_id" },
                unique: true,
                filter: "state = 'Open'");

            migrationBuilder.CreateIndex(
                name: "IX_marketplace_reports_reporter_user_id_created_at",
                table: "marketplace_reports",
                columns: new[] { "reporter_user_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "marketplace_audit");

            migrationBuilder.DropTable(
                name: "marketplace_copies");

            migrationBuilder.DropTable(
                name: "marketplace_ratings");

            migrationBuilder.DropTable(
                name: "marketplace_recipe_versions");

            migrationBuilder.DropTable(
                name: "marketplace_reports");

            migrationBuilder.DropTable(
                name: "marketplace_recipes");
        }
    }
}
