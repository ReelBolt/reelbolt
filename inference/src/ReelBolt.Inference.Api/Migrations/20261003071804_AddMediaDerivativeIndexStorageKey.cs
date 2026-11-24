using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.Inference.Api.Migrations
{
    /// <summary>
    /// Adds the companion pointer that lets a filmstrip SHEET row name the JSON index sidecar
    /// describing the filmstrip it belongs to (which thumbnail index each sheet starts at, and at
    /// what source time), so a client can resolve a hover position to a sprite cell. One density
    /// cannot be one row — a filmstrip is N sheets plus one sidecar — so the sheet rows are the
    /// addressable units and the sidecar has nowhere else to live.
    ///
    /// <para>
    /// <b>Exactly one column, nullable, no default, no index</b>: null is the normal value for every
    /// kind other than <c>Filmstrip</c> and for a filmstrip whose sidecar object is not written yet,
    /// so a default would be a value the domain can never produce. It is deliberately not part of
    /// <c>ux_media_derivatives_cache_key</c> (and not indexed at all): several sheet rows of one
    /// density legitimately share the one sidecar, and it names an index <i>about</i> a derivative
    /// rather than the derivative's content identity. The scaffolder emitted exactly this DDL — no
    /// hand edit was needed — and the model snapshot was regenerated with it.
    /// </para>
    /// </summary>
    public partial class AddMediaDerivativeIndexStorageKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "index_storage_key",
                table: "media_derivatives",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "index_storage_key",
                table: "media_derivatives");
        }
    }
}
