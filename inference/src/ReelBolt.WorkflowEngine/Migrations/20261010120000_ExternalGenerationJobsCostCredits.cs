using Microsoft.EntityFrameworkCore.Migrations;
using ReelBolt.Shared.Data.Models;

#nullable disable

namespace ReelBolt.WorkflowEngine.Migrations
{
    /// <summary>
    /// The video-generation spend ledger moves from dollars to CREDITS (D1/D2). Three changes to
    /// <c>external_generation_jobs</c>, in one migration because they are one meaning:
    /// <list type="bullet">
    /// <item><description><c>cost_estimate_usd</c> → <c>cost_estimate</c> and <c>actual_cost_usd</c> →
    /// <c>actual_cost</c>: the column carries credits for a priced vendor and GPU-seconds for a
    /// self-hosted ComfyUI provider, so a unit in its name can only be one of the two wrong answers.
    /// <c>cost_unit</c> is what disambiguates, exactly as it did before.</description></item>
    /// <item><description>every row that was denominated in dollars becomes the equivalent credits at
    /// the platform's credit price — 1 credit is <c>$0.006</c> of vendor cost, so an old <c>$0.96</c>
    /// row reads <c>160</c>. The arithmetic is <c>× 1000 / 6</c>, which is <c>/ 0.006</c> written so
    /// that it stays exact in both Postgres <c>numeric</c> and a test's floating-point engine.</description></item>
    /// <item><description><c>cost_unit</c> becomes <c>"credits"</c>, column default included, so a row
    /// inserted without one is a credit row rather than a dollar row.</description></item>
    /// </list>
    /// <para>
    /// The value conversion is guarded on <c>cost_unit = 'usd'</c> and rewrites the unit in the same
    /// statement, so it touches neither the GPU-seconds rows (which were never dollars) nor anything
    /// written since, and running it a second time is a no-op rather than a second multiplication.
    /// </para>
    /// </summary>
    public partial class ExternalGenerationJobsCostCredits : Migration
    {
        /// <summary>The persisted <c>cost_unit</c> every row held before this migration.</summary>
        public const string LegacyUsdUnit = "usd";

        /// <summary>
        /// The first half of the rename. Plain SQL, exposed as a constant because the migration's own
        /// statements are what the idempotence test runs against a real relational engine.
        /// </summary>
        public const string RenameCostEstimateColumnSql =
            "ALTER TABLE external_generation_jobs RENAME COLUMN cost_estimate_usd TO cost_estimate;";

        /// <summary>The second half of the rename; see <see cref="RenameCostEstimateColumnSql"/>.</summary>
        public const string RenameActualCostColumnSql =
            "ALTER TABLE external_generation_jobs RENAME COLUMN actual_cost_usd TO actual_cost;";

        /// <summary>
        /// The money conversion: one statement, guarded on the old unit tag, which it also rewrites.
        /// A NULL <c>actual_cost</c> stays NULL (an unconfirmed row has no actual figure to convert).
        /// </summary>
        public const string ConvertLegacyUsdRowsSql =
            "UPDATE external_generation_jobs SET cost_estimate = cost_estimate * 1000 / 6, "
            + "actual_cost = actual_cost * 1000 / 6, cost_unit = '" + ExternalGenerationJob.CreditsUnit + "' "
            + "WHERE cost_unit = '" + LegacyUsdUnit + "';";

        /// <summary>
        /// The inverse of <see cref="ConvertLegacyUsdRowsSql"/>, for <see cref="Down"/>. Lossy by
        /// nature: it cannot tell a row this migration converted from one written in credits
        /// afterwards, so it converts every credit row back.
        /// </summary>
        public const string ReverseCreditsToUsdSql =
            "UPDATE external_generation_jobs SET cost_estimate = cost_estimate * 6 / 1000, "
            + "actual_cost = actual_cost * 6 / 1000, cost_unit = '" + LegacyUsdUnit + "' "
            + "WHERE cost_unit = '" + ExternalGenerationJob.CreditsUnit + "';";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(RenameCostEstimateColumnSql);
            migrationBuilder.Sql(RenameActualCostColumnSql);
            migrationBuilder.Sql(ConvertLegacyUsdRowsSql);

            migrationBuilder.AlterColumn<string>(
                name: "cost_unit",
                table: "external_generation_jobs",
                type: "text",
                nullable: false,
                defaultValue: ExternalGenerationJob.CreditsUnit,
                oldClrType: typeof(string),
                oldType: "text",
                oldDefaultValue: LegacyUsdUnit);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "cost_unit",
                table: "external_generation_jobs",
                type: "text",
                nullable: false,
                defaultValue: LegacyUsdUnit,
                oldClrType: typeof(string),
                oldType: "text",
                oldDefaultValue: ExternalGenerationJob.CreditsUnit);

            migrationBuilder.Sql(ReverseCreditsToUsdSql);

            migrationBuilder.Sql("ALTER TABLE external_generation_jobs RENAME COLUMN cost_estimate TO cost_estimate_usd;");
            migrationBuilder.Sql("ALTER TABLE external_generation_jobs RENAME COLUMN actual_cost TO actual_cost_usd;");
        }
    }
}
