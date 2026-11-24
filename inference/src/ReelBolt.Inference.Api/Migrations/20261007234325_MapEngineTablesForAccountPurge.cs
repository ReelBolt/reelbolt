using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.Inference.Api.Migrations
{
    /// <summary>
    /// C9. This migration changes <b>no table</b> — deliberately, and that is the whole point of it
    /// existing. It records the three engine-owned tables <c>InferenceApiDbContext</c> now maps
    /// read-only (<c>external_generation_jobs</c>, <c>workflow_step_cache_entries</c>,
    /// <c>decision_observations</c>, all <c>ExcludeFromMigrations</c>) so the account export can read
    /// them and the account purge can delete them by project and execution id — those tables carry no
    /// foreign key of their own. EF requires the model snapshot to match the model, so a mapping-only
    /// change needs its own (empty) migration; it applies as a single history row.
    /// </summary>
    public partial class MapEngineTablesForAccountPurge : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
