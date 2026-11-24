using System;
using Microsoft.EntityFrameworkCore.Migrations;

// contract-migration(UniqueIndex): expand only. The one unique index is partial, filtered to purpose = 'EmailChange' AND used_at IS NULL; no released writer creates EmailChange tokens (the purpose was unused until this release), so no existing row matches the filter and the old release cannot violate it.

#nullable disable

namespace ReelBolt.Inference.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddEmailChangeTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "pending_email",
                table: "user_tokens",
                type: "character varying(320)",
                maxLength: 320,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "requested_by_user_id",
                table: "user_tokens",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "sent_to_email",
                table: "user_tokens",
                type: "character varying(320)",
                maxLength: 320,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_user_tokens_one_open_email_change",
                table: "user_tokens",
                column: "user_id",
                unique: true,
                filter: "purpose = 'EmailChange' AND used_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_user_tokens_one_open_email_change",
                table: "user_tokens");

            migrationBuilder.DropColumn(
                name: "pending_email",
                table: "user_tokens");

            migrationBuilder.DropColumn(
                name: "requested_by_user_id",
                table: "user_tokens");

            migrationBuilder.DropColumn(
                name: "sent_to_email",
                table: "user_tokens");
        }
    }
}
