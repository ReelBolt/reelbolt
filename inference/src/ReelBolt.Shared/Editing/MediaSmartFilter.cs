using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReelBolt.Shared.Editing;

/// <summary>
/// A smart bin's saved filter (P6): the pool sources that match it ARE the bin's items, computed at
/// read time and never pinned. Every clause is optional and the clauses AND together; an empty filter
/// matches everything. Pure and closed: a keyword list, a kind list from the media contract's three
/// words, one colour label, the favourite/flag bits and a case-insensitive name fragment.
/// </summary>
public sealed record MediaSmartFilter(
    IReadOnlyList<string>? Keywords = null,
    IReadOnlyList<string>? Kinds = null,
    string? ColorLabel = null,
    bool? Favourite = null,
    bool? Flag = null,
    string? NameContains = null)
{
    public const int MaxKeywords = 32;
    public const int MaxKeywordLength = 64;
    public const int MaxNameFragmentLength = 120;

    public static readonly IReadOnlySet<string> KindWords = new HashSet<string>(StringComparer.Ordinal) { "video", "audio", "image" };

    public static readonly IReadOnlySet<string> ColorLabels = new HashSet<string>(StringComparer.Ordinal)
    {
        "red", "orange", "yellow", "green", "blue", "violet", "grey"
    };

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>The first violation, or null when the filter is well-formed.</summary>
    public string? Validate()
    {
        if (Keywords is { } keywords)
        {
            if (keywords.Count > MaxKeywords) return $"at most {MaxKeywords} keywords";
            foreach (string keyword in keywords)
            {
                if (string.IsNullOrWhiteSpace(keyword)) return "a keyword must not be blank";
                if (keyword.Length > MaxKeywordLength) return $"a keyword is at most {MaxKeywordLength} characters";
            }
        }

        if (Kinds is { } kinds)
        {
            foreach (string kind in kinds)
            {
                if (!KindWords.Contains(kind)) return $"kind '{kind}' is not one of video, audio, image";
            }
        }

        if (ColorLabel is { } label && !ColorLabels.Contains(label))
        {
            return $"colorLabel '{label}' is not one of {string.Join(", ", ColorLabels)}";
        }

        if (NameContains is { Length: > MaxNameFragmentLength })
        {
            return $"nameContains is at most {MaxNameFragmentLength} characters";
        }

        return null;
    }

    /// <summary>Normalises keywords (trimmed, lower-cased, distinct) and drops empty clauses.</summary>
    public MediaSmartFilter Normalized() => new(
        Keywords is { Count: > 0 } ? NormalizeKeywords(Keywords) : null,
        Kinds is { Count: > 0 } ? Kinds.Distinct(StringComparer.Ordinal).ToList() : null,
        string.IsNullOrWhiteSpace(ColorLabel) ? null : ColorLabel,
        Favourite,
        Flag,
        string.IsNullOrWhiteSpace(NameContains) ? null : NameContains.Trim());

    /// <summary>True when a source of <paramref name="kind"/> named <paramref name="fileName"/> with <paramref name="annotation"/> (null = none) matches.</summary>
    public bool Matches(string? kind, string? fileName, MediaAnnotationView? annotation)
    {
        if (Kinds is { Count: > 0 } && (kind is null || !Kinds.Contains(kind))) return false;
        if (NameContains is { Length: > 0 } fragment && (fileName is null || !fileName.Contains(fragment, StringComparison.OrdinalIgnoreCase))) return false;

        if (Keywords is { Count: > 0 } wanted)
        {
            if (annotation is null) return false;
            foreach (string keyword in wanted)
            {
                if (!annotation.Keywords.Contains(keyword, StringComparer.Ordinal)) return false;
            }
        }

        if (ColorLabel is { } label && !string.Equals(annotation?.ColorLabel, label, StringComparison.Ordinal)) return false;
        if (Favourite is { } favourite && (annotation?.Favourite ?? false) != favourite) return false;
        if (Flag is { } flag && (annotation?.Flag ?? false) != flag) return false;
        return true;
    }

    public static IReadOnlyList<string> NormalizeKeywords(IEnumerable<string> keywords) =>
        keywords
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => k.Trim().ToLowerInvariant())
            .Where(k => k.Length <= MaxKeywordLength)
            .Distinct(StringComparer.Ordinal)
            .Take(MaxKeywords)
            .ToList();

    public static MediaSmartFilter? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<MediaSmartFilter>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public string ToJson() => JsonSerializer.Serialize(Normalized(), JsonOptions);
}

/// <summary>What a smart filter reads of an annotation: the normalised keywords and the three flags.</summary>
public sealed record MediaAnnotationView(IReadOnlyList<string> Keywords, string? ColorLabel, bool Favourite, bool Flag);
