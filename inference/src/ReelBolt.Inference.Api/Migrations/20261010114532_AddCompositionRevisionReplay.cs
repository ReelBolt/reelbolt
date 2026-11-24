using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.Inference.Api.Migrations
{
    /// <summary>
    /// A revision's replay of the steps that depend on the film (docs/design/composition-revisions.md
    /// "Replaying what depends on the film"): the owner's cut choice and the persisted replay plan. Both
    /// nullable — every existing request (and every revision with nothing downstream) has neither. The
    /// snapshot also picks up <c>workflow_step_results.reused_from_step_result_id</c>, which the engine's
    /// <c>AddStepResultReusedFrom</c> creates (the table is mapped here with ExcludeFromMigrations).
    /// </summary>
    public partial class AddCompositionRevisionReplay : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "cut_policy",
                table: "composition_revisions",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "replay_plan_json",
                table: "composition_revisions",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "cut_policy",
                table: "composition_revisions");

            migrationBuilder.DropColumn(
                name: "replay_plan_json",
                table: "composition_revisions");
        }
    }
}
