using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReelBolt.Shared.DesignSystems;

/// <summary>
/// One pinned design-system member (SPEC 3.5): the version the run must resolve, and the digest that
/// version had when the run was queued. <see cref="LibraryVersionId"/> and <see cref="Source"/> are
/// Phase 4 fields; in Phase 2 they are always null and the codec omits them.
/// </summary>
public sealed record DesignSystemPinMember(
    Guid? VersionId, int VersionNumber, string Digest,
    Guid? LibraryVersionId = null, string? Source = null);

/// <summary>
/// The design-system pin frozen on <c>workflow_executions.design_system_pin_json</c> when a run is
/// queued (SPEC 3.5). Either member may be absent, and the whole pin is null when the project had no
/// Approved version of either kind. <see cref="V"/> is always 1 in Phase 2.
/// </summary>
public sealed record DesignSystemPin(int V, DesignSystemPinMember? Design, DesignSystemPinMember? ProductMap);

/// <summary>
/// The pin's JSON codec: camelCase, compact, and null members/fields omitted, so the serialized shape
/// is exactly <c>{"v":1,"design":{...},"productMap":{...}}</c>.
/// </summary>
public static class DesignSystemPinJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize(DesignSystemPin pin) => JsonSerializer.Serialize(pin, Options);

    public static DesignSystemPin? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<DesignSystemPin>(json, Options); }
        catch (JsonException) { return null; }
    }
}
