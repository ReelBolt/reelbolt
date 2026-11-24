using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.Inference.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddOrganizations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_inference_providers_capability_is_default",
                table: "inference_providers");

            migrationBuilder.DropIndex(
                name: "IX_inference_providers_name",
                table: "inference_providers");

            migrationBuilder.AddColumn<Guid>(
                name: "organization_id",
                table: "projects",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "organization_id",
                table: "inference_providers",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "organization_id",
                table: "assistant_threads",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "last_active_organization_id",
                table: "application_users",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "organization_id",
                table: "agent_definitions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "organizations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    slug = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    kind = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    compute_preference = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false, defaultValue: "Auto"),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_organizations", x => x.id);
                    table.ForeignKey(
                        name: "FK_organizations_application_users_created_by_user_id",
                        column: x => x.created_by_user_id,
                        principalTable: "application_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "organization_agent_provider_overrides",
                columns: table => new
                {
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    agent_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    inference_provider_id = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_organization_agent_provider_overrides", x => new { x.organization_id, x.agent_type });
                    table.ForeignKey(
                        name: "fk_organization_agent_provider_overrides_inference_providers_i~",
                        column: x => x.inference_provider_id,
                        principalTable: "inference_providers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_organization_agent_provider_overrides_organizations_organiz~",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "organization_invites",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    role = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    token_hash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    invited_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    accepted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    revoked_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_organization_invites", x => x.id);
                    table.ForeignKey(
                        name: "FK_organization_invites_application_users_invited_by_user_id",
                        column: x => x.invited_by_user_id,
                        principalTable: "application_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_organization_invites_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "organization_memberships",
                columns: table => new
                {
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_organization_memberships", x => new { x.organization_id, x.user_id });
                    table.ForeignKey(
                        name: "fk_organization_memberships_application_users_user_id",
                        column: x => x.user_id,
                        principalTable: "application_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_organization_memberships_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            // Backfill (D4), then tighten: projects and threads must always belong to an org.
            foreach (string statement in OrganizationBackfill.Statements)
            {
                migrationBuilder.Sql(statement);
            }

            migrationBuilder.AlterColumn<Guid>(
                name: "organization_id",
                table: "projects",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "organization_id",
                table: "assistant_threads",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_projects_organization_id",
                table: "projects",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "IX_inference_providers_organization_id",
                table: "inference_providers",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "IX_inference_providers_organization_id_capability",
                table: "inference_providers",
                columns: new[] { "organization_id", "capability" },
                unique: true,
                filter: "is_default")
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_inference_providers_organization_id_name",
                table: "inference_providers",
                columns: new[] { "organization_id", "name" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_assistant_threads_organization_id",
                table: "assistant_threads",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "IX_agent_definitions_organization_id",
                table: "agent_definitions",
                column: "organization_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_agent_definitions_builtin_or_org",
                table: "agent_definitions",
                sql: "is_built_in OR organization_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_organization_agent_provider_overrides_inference_provider_id",
                table: "organization_agent_provider_overrides",
                column: "inference_provider_id");

            migrationBuilder.CreateIndex(
                name: "IX_organization_invites_invited_by_user_id",
                table: "organization_invites",
                column: "invited_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_organization_invites_organization_id",
                table: "organization_invites",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "IX_organization_invites_token_hash",
                table: "organization_invites",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_organization_memberships_user_id",
                table: "organization_memberships",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_organizations_created_by_user_id_personal",
                table: "organizations",
                column: "created_by_user_id",
                unique: true,
                filter: "kind = 'Personal'");

            migrationBuilder.CreateIndex(
                name: "IX_organizations_slug",
                table: "organizations",
                column: "slug",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_agent_definitions_organizations_organization_id",
                table: "agent_definitions",
                column: "organization_id",
                principalTable: "organizations",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_assistant_threads_organizations_organization_id",
                table: "assistant_threads",
                column: "organization_id",
                principalTable: "organizations",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_inference_providers_organizations_organization_id",
                table: "inference_providers",
                column: "organization_id",
                principalTable: "organizations",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_projects_organizations_organization_id",
                table: "projects",
                column: "organization_id",
                principalTable: "organizations",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_agent_definitions_organizations_organization_id",
                table: "agent_definitions");

            migrationBuilder.DropForeignKey(
                name: "FK_assistant_threads_organizations_organization_id",
                table: "assistant_threads");

            migrationBuilder.DropForeignKey(
                name: "FK_inference_providers_organizations_organization_id",
                table: "inference_providers");

            migrationBuilder.DropForeignKey(
                name: "FK_projects_organizations_organization_id",
                table: "projects");

            migrationBuilder.DropTable(
                name: "organization_agent_provider_overrides");

            migrationBuilder.DropTable(
                name: "organization_invites");

            migrationBuilder.DropTable(
                name: "organization_memberships");

            migrationBuilder.DropTable(
                name: "organizations");

            migrationBuilder.DropIndex(
                name: "IX_projects_organization_id",
                table: "projects");

            migrationBuilder.DropIndex(
                name: "IX_inference_providers_organization_id",
                table: "inference_providers");

            migrationBuilder.DropIndex(
                name: "IX_inference_providers_organization_id_capability",
                table: "inference_providers");

            migrationBuilder.DropIndex(
                name: "IX_inference_providers_organization_id_name",
                table: "inference_providers");

            migrationBuilder.DropIndex(
                name: "IX_assistant_threads_organization_id",
                table: "assistant_threads");

            migrationBuilder.DropIndex(
                name: "IX_agent_definitions_organization_id",
                table: "agent_definitions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_agent_definitions_builtin_or_org",
                table: "agent_definitions");

            migrationBuilder.DropColumn(
                name: "organization_id",
                table: "projects");

            migrationBuilder.DropColumn(
                name: "organization_id",
                table: "inference_providers");

            migrationBuilder.DropColumn(
                name: "organization_id",
                table: "assistant_threads");

            migrationBuilder.DropColumn(
                name: "last_active_organization_id",
                table: "application_users");

            migrationBuilder.DropColumn(
                name: "organization_id",
                table: "agent_definitions");

            migrationBuilder.CreateIndex(
                name: "IX_inference_providers_capability_is_default",
                table: "inference_providers",
                columns: new[] { "capability", "is_default" },
                unique: true,
                filter: "is_default");

            migrationBuilder.CreateIndex(
                name: "IX_inference_providers_name",
                table: "inference_providers",
                column: "name",
                unique: true);
        }
    }
}
