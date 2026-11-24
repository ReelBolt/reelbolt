using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReelBolt.Shared.Data.Models;

/// <summary>
/// The edit-v2 contract's enum encoding, applied to the five contract enums
/// (<see cref="TrackKind"/>, <see cref="AspectPolicy"/>, <see cref="InterpKind"/>,
/// <see cref="ProvenanceOrigin"/>, <see cref="AssetRefKind"/>) through a type-level
/// <see cref="JsonConverterAttribute"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>camelCase strings, matching <c>EditTimeline.schema.json</c>, the canonical contract.</b> The
/// schema's enum literals are camelCase strings (<c>fit|fill|stretch</c>, <c>video|audio</c>,
/// <c>linear|…|step</c>, <c>manual|generated|imported|analyzed</c>, <c>projectFile|stepResult</c>),
/// so anything that writes these enums — MVC model binding on the way in, and
/// <c>document_json</c> on the way out — has to write that form and no other.
/// </para>
/// <para>
/// <b>Integers are REJECTED, deliberately.</b> <c>allowIntegerValues: false</c> turns the old
/// integer form into a binding failure, which <c>[ApiController]</c> renders as the endpoint's
/// ordinary <c>400</c> problem response — not a <c>500</c>, and not a silent accept. The wire
/// format is the schema; an ambiguous <c>0</c> that has to be resolved against a C# declaration is
/// not that format. Reading is still case-insensitive (<see cref="JsonStringEnumConverter"/>'s own
/// behaviour), so a legacy PascalCase document keeps parsing — but nothing ever writes one again.
/// </para>
/// <para>
/// This is intentionally <b>not</b> registered in any <see cref="JsonSerializerOptions"/>: the
/// encoding is declared once, on the enum types, so every reader and writer of the contract agrees
/// without an options object that a caller could forget to pass. Note that an options-level
/// converter still takes precedence over this attribute, which is why
/// <c>EditTimelinesController.DocumentJsonOptions</c> carries no converter of its own — leaving a
/// naming-policy-less <see cref="JsonStringEnumConverter"/> there would silently override this
/// attribute and keep persisting PascalCase.
/// </para>
/// </remarks>
public sealed class EditTimelineEnumJsonConverter : JsonStringEnumConverter
{
    /// <summary>
    /// Configures the encoding: camelCase names, integer values rejected.
    /// </summary>
    public EditTimelineEnumJsonConverter()
        : base(JsonNamingPolicy.CamelCase, allowIntegerValues: false)
    {
    }
}
