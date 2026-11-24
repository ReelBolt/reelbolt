using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.Inference.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddAssistantThreadReadMarker : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "read_through_sequence",
                table: "assistant_threads",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Every existing thread starts fully read, so nothing old shows as unread.
            migrationBuilder.Sql(
                "UPDATE assistant_threads AS t SET read_through_sequence = " +
                "COALESCE((SELECT MAX(m.sequence) FROM assistant_messages m WHERE m.thread_id = t.id), 0);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "read_through_sequence",
                table: "assistant_threads");
        }
    }
}
