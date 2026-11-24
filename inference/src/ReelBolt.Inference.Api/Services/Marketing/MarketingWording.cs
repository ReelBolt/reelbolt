using System.Text.Json;

namespace ReelBolt.Inference.Api.Services.Marketing;

/// <summary>
/// The marketing-consent wording registry (<c>Marketing/marketing-consent.wording.json</c>, copied
/// next to the assembly; the site vendors it byte for byte). Version → text. A published version's
/// text is evidence of what a person agreed to, so it is never edited or deleted: new text gets a new
/// version and <c>current</c> moves to it. <c>MarketingWordingRegistryTests</c> pins the shipped text.
/// </summary>
public sealed class MarketingWording
{
    public const string FileName = "marketing-consent.wording.json";

    private readonly IReadOnlyDictionary<string, string> _versions;

    private MarketingWording(string current, IReadOnlyDictionary<string, string> versions)
    {
        Current = current;
        _versions = versions;
    }

    /// <summary>The registry next to the running assembly.</summary>
    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "Marketing", FileName);

    /// <summary>The version every new grant must name.</summary>
    public string Current { get; }

    /// <summary>The text of <see cref="Current"/>.</summary>
    public string CurrentText => _versions[Current];

    /// <summary>The published text of a version, or null when the version was never published.</summary>
    public string? TextOf(string? version) =>
        version is not null && _versions.TryGetValue(version, out string? text) ? text : null;

    /// <summary>True when <paramref name="version"/> is exactly the current version.</summary>
    public bool IsCurrent(string? version) => string.Equals(version, Current, StringComparison.Ordinal);

    public static MarketingWording LoadFile(string path) => Load(File.ReadAllText(path));

    /// <summary>Parses and validates a registry; throws <see cref="InvalidDataException"/> when unusable.</summary>
    public static MarketingWording Load(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        if (!root.TryGetProperty("current", out JsonElement currentElement) || currentElement.ValueKind != JsonValueKind.String
            || !root.TryGetProperty("versions", out JsonElement versionsElement) || versionsElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"{FileName} needs a string 'current' and an object 'versions'.");
        }

        Dictionary<string, string> versions = new(StringComparer.Ordinal);
        foreach (JsonProperty version in versionsElement.EnumerateObject())
        {
            if (version.Value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(version.Value.GetString())
                || version.Name.Length is 0 or > 64)
            {
                throw new InvalidDataException($"{FileName}: version '{version.Name}' has no text or an unusable name.");
            }

            versions[version.Name] = version.Value.GetString()!;
        }

        string current = currentElement.GetString()!;
        if (!versions.ContainsKey(current))
        {
            throw new InvalidDataException($"{FileName}: the current version '{current}' is not published.");
        }

        return new MarketingWording(current, versions);
    }
}
