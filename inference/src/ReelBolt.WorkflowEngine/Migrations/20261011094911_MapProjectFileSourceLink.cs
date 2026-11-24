using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.WorkflowEngine.Migrations
{
    /// <summary>
    /// Snapshot-only: maps <c>project_files.source_link_id</c> (added by the Inference API's
    /// <c>AddLocalFolderSync</c>; the table is excluded from engine migrations), so the engine's model of
    /// a project file matches the column. Nothing to create here.
    /// </summary>
    public partial class MapProjectFileSourceLink : Migration
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
