using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReelBolt.Inference.Api.Controllers.Dto;

/// <summary>
/// One bin in <c>GET /api/v1/projects/{projectId}/media/bins</c>, and the body of a created or
/// patched bin.
///
/// <para>
/// Deliberately flat and without children: the wire shape is the full project bin list, and the
/// client rebuilds the tree from <see cref="ParentBinId"/>. Returning a nested tree instead would
/// make a single malformed parent reference unrepresentable to report and would duplicate every
/// subtree's rows on a move.
/// </para>
/// </summary>
/// <param name="Id">The bin id.</param>
/// <param name="Name">User-facing name, trimmed.</param>
/// <param name="ParentBinId">Containing bin, or null for a root bin.</param>
/// <param name="SortOrder">Display order among siblings, ascending.</param>
/// <param name="ItemCount">
/// How many media references this bin directly holds. Counts the bin's own items only — a subtree
/// total would have to be recomputed on every descendant change and is not what the panel shows.
/// </param>
public sealed record MediaBinSummaryResponse(
    Guid Id,
    string Name,
    Guid? ParentBinId,
    int SortOrder,
    int ItemCount,
    ReelBolt.Shared.Editing.MediaSmartFilter? SmartFilter = null)
{
    /// <summary>P6: true for a smart bin (its items are computed from <see cref="SmartFilter"/>, never pinned).</summary>
    public bool IsSmart => SmartFilter is not null;
}

/// <summary>
/// Body of <c>POST /api/v1/projects/{projectId}/media/bins</c>.
/// </summary>
/// <param name="Name">Required, trimmed, 1..120 characters.</param>
/// <param name="ParentBinId">
/// The containing bin, or null/absent to create a root bin. A bin id that is not in this project,
/// does not exist, or is soft-deleted is a <c>404</c> and nothing is written.
/// </param>
public sealed record CreateMediaBinRequest(
    string? Name,
    Guid? ParentBinId,
    ReelBolt.Shared.Editing.MediaSmartFilter? SmartFilter = null);

/// <summary>
/// Body of <c>PATCH /api/v1/projects/{projectId}/media/bins/{binId}</c> — rename and/or reparent.
///
/// <para>
/// <b>Why <see cref="ParentBinId"/> is a <see cref="JsonElement"/> and not a <c>Guid?</c>.</b> The
/// two no-op-looking JSON shapes are not the same request: <c>{ "name": "X" }</c> (property absent)
/// means "rename, keep the current parent", while <c>{ "parentBinId": null }</c> means "move this
/// bin to the root". A <c>Guid?</c> cannot tell those apart — both arrive as <c>null</c> — so the
/// move-to-root case would be either unreachable or indistinguishable from a rename. A non-nullable
/// <see cref="JsonElement"/> keeps the three states apart: absent leaves it at
/// <see cref="JsonValueKind.Undefined"/>, an explicit JSON <c>null</c> gives
/// <see cref="JsonValueKind.Null"/>, and anything else is the supplied value.
/// </para>
/// </summary>
public sealed record PatchMediaBinRequest
{
    /// <summary>
    /// The new name, or null to leave the name unchanged. A name is required on the entity, so an
    /// explicit JSON <c>null</c> is "no change", not "clear it".
    /// </summary>
    public string? Name { get; init; }

    /// <summary>The raw <c>parentBinId</c> JSON value; see the type's remarks.</summary>
    public JsonElement ParentBinId { get; init; }

    /// <summary>P6: the smart filter to set (an object), clear (<c>null</c>) or keep (absent).</summary>
    public JsonElement SmartFilter { get; init; }

    public bool HasSmartFilter => SmartFilter.ValueKind != JsonValueKind.Undefined;

    /// <summary>True when the caller mentioned <c>parentBinId</c> at all (including as null).</summary>
    public bool HasParentBinId => ParentBinId.ValueKind != JsonValueKind.Undefined;

    /// <summary>
    /// Reads <see cref="ParentBinId"/> as a bin id or as "root". Returns false only for a
    /// well-formed-but-unusable value (a non-guid string, a number, an object), which the caller
    /// answers with <c>400</c> — never a <c>404</c>, since nothing was looked up.
    /// </summary>
    /// <param name="parentBinId">The parsed parent, or null meaning "move to the root".</param>
    public bool TryGetParentBinId(out Guid? parentBinId)
    {
        parentBinId = null;

        switch (ParentBinId.ValueKind)
        {
            case JsonValueKind.Null:
                return true;
            case JsonValueKind.String:
                if (!Guid.TryParse(ParentBinId.GetString(), out Guid parsed))
                    return false;
                parentBinId = parsed;
                return true;
            default:
                return false;
        }
    }
}

/// <summary>
/// Body of <c>POST /api/v1/projects/{projectId}/media/bins/{binId}/items</c>.
/// </summary>
/// <param name="MediaRef">
/// The opaque pool token of the media to pin: <c>file:&lt;guid&gt;</c> (a <c>ProjectFile</c>) or
/// <c>step_result:&lt;guid&gt;</c> (a <c>WorkflowStepResult</c>). Never a bare Guid and never a raw
/// storage key — see <c>MediaBinsController.TryParseMediaRef</c>.
/// </param>
public sealed record AddMediaBinItemRequest(string? MediaRef);

/// <summary>
/// Body of <c>PATCH /api/v1/projects/{projectId}/media/bins/{binId}/items/{itemId}</c> — the move.
/// </summary>
/// <param name="BinId">
/// The bin to move the item INTO. Nullable so a body that omits it is a named <c>400</c> rather
/// than a lookup against <see cref="Guid.Empty"/>.
/// </param>
public sealed record MoveMediaBinItemRequest(Guid? BinId);

/// <summary>
/// One bin item, as returned by the add/move routes.
/// </summary>
/// <param name="Id">The item id.</param>
/// <param name="BinId">The bin holding it.</param>
/// <param name="MediaRef">The opaque pool token, normalized to lower-case hyphenated Guid form.</param>
/// <param name="SourceKind">
/// <c>"ProjectFile"</c> or <c>"StepResult"</c> — the string form of the shared
/// <c>MediaSourceKind</c> discriminator, matching how the other controllers serialize enums.
/// </param>
/// <param name="SortOrder">Display order inside the bin, ascending.</param>
/// <param name="CreatedAt">When the pin was created.</param>
public sealed record MediaBinItemResponse(
    Guid Id,
    Guid BinId,
    string MediaRef,
    string SourceKind,
    int SortOrder,
    DateTime CreatedAt);

/// <summary>
/// One bin item as returned by <c>GET /api/v1/projects/{projectId}/media/bins/{binId}/items</c> —
/// the add/move shape plus the four fields of the source the pin names.
/// </summary>
/// <remarks>
/// <para>
/// <b>The first six fields are <see cref="MediaBinItemResponse"/>'s, in its order and with its
/// meaning</b>, so a client that already parses the add/move response reads this one unchanged.
/// They are not a separate nested object because the point of this route is one row the panel can
/// render directly: a <c>source</c> sub-record would only add a level for the client to unwrap.
/// </para>
/// <para>
/// <b>The four resolution fields are all <c>null</c> for a DANGLING pin</b> — its source row was
/// deleted, belongs to another project, or (for a step result) no longer sits under this project's
/// <c>outputFiles/</c> prefix. The row itself is still returned, with its id and its normalized
/// <c>mediaRef</c>, because a pin the client cannot see is a pin the client cannot remove; nothing
/// is defaulted, emptied or guessed in place of a value the server does not have. Each field is
/// independently nullable for the same reason: a render records no object size, and a file uploaded
/// without a MIME type has no kind — both are honestly <c>null</c> rather than a stand-in.
/// </para>
/// <para>
/// There is deliberately no <c>probe</c> field: probe metadata is produced by the media pool and
/// joined by <c>mediaRef</c>, and a second producer of the same value here could disagree with it.
/// </para>
/// <para>
/// <b>No field can carry a storage key, a bucket name or an object URL.</b> <see cref="FileName"/>
/// is a base name (and, when a file row has no original name, the base name of its key — never the
/// key), and the media itself is reachable only through this API's own routes.
/// </para>
/// </remarks>
/// <param name="Id">The item id — the value the move and remove routes need.</param>
/// <param name="BinId">The bin holding it.</param>
/// <param name="MediaRef">The opaque pool token, normalized to lower-case hyphenated Guid form.</param>
/// <param name="SourceKind">
/// <c>"ProjectFile"</c> or <c>"StepResult"</c> — unchanged from <see cref="MediaBinItemResponse"/>.
/// </param>
/// <param name="SortOrder">Display order inside the bin, ascending.</param>
/// <param name="CreatedAt">When the pin was created.</param>
/// <param name="Kind">The source's media kind, or null when it is not known.</param>
/// <param name="FileName">The source's display name, or null when it has none to report.</param>
/// <param name="MimeType">The source's MIME type, or null when it is not known.</param>
/// <param name="SizeBytes">The source's size in bytes, or null when it is not recorded.</param>
public sealed record MediaBinItemDetailResponse(
    Guid Id,
    Guid BinId,
    string MediaRef,
    string SourceKind,
    int SortOrder,
    DateTime CreatedAt,
    [property: JsonConverter(typeof(MediaWireEnumJsonConverter))] MediaKind? Kind,
    string? FileName,
    string? MimeType,
    long? SizeBytes);
