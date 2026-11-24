using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.WorkflowEngine.Migrations
{
    /// <summary>
    /// Snapshot-only: maps <c>edit_image_graphic_requests</c> (owned and created by the Inference API's
    /// <c>AddEditImageGraphicRequests</c>) with <c>ExcludeFromMigrations()</c>, so the engine can read a
    /// designed-graphic request and write its result. Nothing to create here.
    /// </summary>
    public partial class MapEditImageGraphicRequests : Migration
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
