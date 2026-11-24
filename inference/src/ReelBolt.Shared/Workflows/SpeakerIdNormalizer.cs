namespace ReelBolt.Shared.Workflows;

/// <summary>
/// Maps provider-opaque speaker ids to <c>S1</c>, <c>S2</c>... in order of first appearance. Ids are
/// scoped per source (a provider's ids are only consistent inside one response), so the same raw id
/// from two sources yields two different normalized ids: no false joins, and never a name.
/// </summary>
public sealed class SpeakerIdNormalizer
{
    private readonly Dictionary<(int Source, string Raw), string> _map = new();

    /// <summary>Number of distinct normalized speakers so far.</summary>
    public int Count => _map.Count;

    /// <summary>Normalized id for a raw label, or null when unlabeled.</summary>
    public string? Normalize(int sourceIndex, string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var key = (sourceIndex, raw);
        if (!_map.TryGetValue(key, out string? id))
        {
            id = "S" + (_map.Count + 1);
            _map[key] = id;
        }
        return id;
    }
}
