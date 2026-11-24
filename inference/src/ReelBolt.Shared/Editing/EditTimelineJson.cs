using System.Text.Json;

namespace ReelBolt.Shared.Editing;

/// <summary>
/// The one set of JSON options every stored or received edit timeline document is read with.
/// </summary>
/// <remarks>
/// Items are polymorphic, discriminated by <c>type</c>, and System.Text.Json by default refuses a
/// discriminator that is not the object's FIRST property. Two writers never put it first:
/// PostgreSQL <c>jsonb</c>, which stores object keys ordered by length (so <c>id</c> and
/// <c>name</c> always come back ahead of <c>type</c>), and the browser, whose object literals keep
/// whatever key order the code wrote. Without <see cref="JsonSerializerOptions.AllowOutOfOrderMetadataProperties"/>
/// every timeline holding a clip failed to load ("must specify a type discriminator", a 500) the
/// moment it had been saved once.
/// </remarks>
public static class EditTimelineJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        AllowOutOfOrderMetadataProperties = true,
    };
}
