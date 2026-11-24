using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.Inference.Api.Migrations
{
    /// <summary>
    /// Replaces the three-column derivative identity T-100 introduced with the single canonical
    /// cache key the media department's builders already compute, and adds the builder version that
    /// key is derived from.
    ///
    /// <para>
    /// <b>No backfill, and none is possible or needed: <c>media_derivatives</c> has no writer and no
    /// rows.</b> T-100 created the table and the columns; nothing has ever inserted into it, in any
    /// environment, so every row the table will ever contain is written after this migration by the
    /// persistence path that computes <c>MediaDerivativeIdentity.Compute</c> itself. That is also
    /// what makes narrowing two columns and adding a NOT NULL identity column safe here, and why the
    /// new columns carry no default value: a default exists to make a required column addable to a
    /// table that already holds rows, and on an empty table it would instead be a value the domain
    /// can never legitimately produce — a blank identity, or a builder version no builder emits.
    /// </para>
    /// <para>
    /// <b>Why the identity changes at all.</b> <c>ux_media_derivatives_identity</c> was over
    /// <c>(source_storage_key, source_etag, params_hash)</c>, which is not an identity: the parameter
    /// hash covers a spec's knobs and its builder version but neither the derivative kind nor the
    /// variant, so two different kinds of one source that happened to share parameter JSON produced
    /// the same triple — and the unique index then made the second derivative <i>unpersistable</i>
    /// rather than merely redundant. One <c>cache_key</c> over all six components
    /// (<c>source_storage_key</c>, <c>source_etag</c>, kind slug, variant, <c>params_hash</c>,
    /// <c>builder_version</c>) constrains exactly the thing that must be unique, and is byte-identical
    /// to the value the consumer's <c>MediaDerivativeCacheKey.ComputeCacheKey</c> returns.
    /// </para>
    /// <para>
    /// <b>The DDL below was reviewed before committing, not merely generated</b>: EF scaffolded
    /// <c>defaultValue: ""</c> on <c>cache_key</c> and <c>defaultValue: 0</c> on
    /// <c>builder_version</c>, and both were removed by hand for the reason in the first paragraph.
    /// </para>
    /// </summary>
    public partial class AddMediaDerivativeCacheKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Drop before narrowing: the old index is over two of the columns altered below, so
            // rewriting it in place would only be undone a statement later.
            migrationBuilder.DropIndex(
                name: "ux_media_derivatives_identity",
                table: "media_derivatives");

            // 200 characters: a quoted S3 ETag is 34, and the column has to fit a longer hex content
            // hash when the store reports no ETag at all. Narrowing is safe only because the table is
            // empty — a value of 201-256 characters could not survive this statement.
            migrationBuilder.AlterColumn<string>(
                name: "source_etag",
                table: "media_derivatives",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(256)",
                oldMaxLength: 256);

            // 64 characters is exactly a lowercase hex SHA-256; the extra 64 T-100 allowed were for
            // a future algorithm prefix that does not exist and would change the key's meaning.
            migrationBuilder.AlterColumn<string>(
                name: "params_hash",
                table: "media_derivatives",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(128)",
                oldMaxLength: 128);

            // NOT NULL, with no default: see the class remark. Every builder version in
            // MediaDerivativeSpec is 1, so 0 — which EF would have scaffolded — is a value no row may
            // ever hold, and silently recording it would be worse than refusing the insert.
            migrationBuilder.AddColumn<int>(
                name: "builder_version",
                table: "media_derivatives",
                type: "integer",
                nullable: false);

            // NOT NULL, with no default, for the same reason and one more: this column carries the
            // partial unique index below, so a defaulted blank identity would let the FIRST
            // malformed row in and only surface at the second, as a unique-constraint violation
            // naming an index rather than the value that was never supplied.
            migrationBuilder.AddColumn<string>(
                name: "cache_key",
                table: "media_derivatives",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false);

            // Filtered (partial), like inference_providers' unique index: a soft-deleted row never
            // blocks re-producing its derivative. Named explicitly so a violation is greppable.
            migrationBuilder.CreateIndex(
                name: "ux_media_derivatives_cache_key",
                table: "media_derivatives",
                column: "cache_key",
                unique: true,
                filter: "deleted_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Exact inverse: drop the new index, drop the two added columns, restore both lengths,
            // then recreate the old three-column index under its original name.
            migrationBuilder.DropIndex(
                name: "ux_media_derivatives_cache_key",
                table: "media_derivatives");

            migrationBuilder.DropColumn(
                name: "builder_version",
                table: "media_derivatives");

            migrationBuilder.DropColumn(
                name: "cache_key",
                table: "media_derivatives");

            migrationBuilder.AlterColumn<string>(
                name: "source_etag",
                table: "media_derivatives",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200);

            migrationBuilder.AlterColumn<string>(
                name: "params_hash",
                table: "media_derivatives",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(64)",
                oldMaxLength: 64);

            migrationBuilder.CreateIndex(
                name: "ux_media_derivatives_identity",
                table: "media_derivatives",
                columns: new[] { "source_storage_key", "source_etag", "params_hash" },
                unique: true,
                filter: "deleted_at IS NULL");
        }
    }
}
