using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.Inference.Api.Migrations
{
    /// <inheritdoc />
    public partial class PictureCopyOnDelete : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_edit_image_documents_project_files_source_project_file_id",
                table: "edit_image_documents");

            migrationBuilder.AddColumn<long>(
                name: "copy_size_bytes",
                table: "edit_image_layers",
                type: "bigint",
                nullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "source_project_file_id",
                table: "edit_image_documents",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddForeignKey(
                name: "FK_edit_image_documents_project_files_source_project_file_id",
                table: "edit_image_documents",
                column: "source_project_file_id",
                principalTable: "project_files",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_edit_image_documents_project_files_source_project_file_id",
                table: "edit_image_documents");

            migrationBuilder.DropColumn(
                name: "copy_size_bytes",
                table: "edit_image_layers");

            migrationBuilder.AlterColumn<Guid>(
                name: "source_project_file_id",
                table: "edit_image_documents",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_edit_image_documents_project_files_source_project_file_id",
                table: "edit_image_documents",
                column: "source_project_file_id",
                principalTable: "project_files",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
