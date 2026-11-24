using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReelBolt.Shared.Avatars;

/// <summary>
/// The structured identity facts of an OBJECT avatar (docs/design/avatar-objects.md "Object fidelity"): what a person
/// would say to describe the object so that no one draws another one. Stored as JSON in
/// <c>project_avatars.identity_facts</c>, derived once by the Vision provider at creation and editable by the owner.
/// Every field is plain words (<see cref="Clean"/>), at most <see cref="MaxFieldChars"/> characters; a record with no
/// field is never stored (null).
/// </summary>
/// <param name="Colour">The body colour as it is in the pictures ("silver", "matte black with a white roof").</param>
/// <param name="MakeModel">Make and model, or what the object is ("Citroen C3", "espresso machine").</param>
/// <param name="Marks">Distinguishing marks (decals, damage, a roof rack, a logo position).</param>
/// <param name="Scale">Size or class ("compact hatchback", "about the size of a cup").</param>
public sealed record AvatarIdentityFacts(
    [property: JsonPropertyName("colour")] string? Colour,
    [property: JsonPropertyName("makeModel")] string? MakeModel,
    [property: JsonPropertyName("marks")] string? Marks,
    [property: JsonPropertyName("scale")] string? Scale)
{
    /// <summary>The longest any one field is.</summary>
    public const int MaxFieldChars = 120;

    /// <summary>The longest stored JSON (the column length): four fields plus keys, with headroom for escapes.</summary>
    public const int MaxStoredChars = 1200;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Non-ASCII stays as written (one character, not a six-character \uXXXX escape): four full Cyrillic, Greek or CJK
        // fields would otherwise overflow the column. Safe because Clean already drops quotes, brackets, angle brackets
        // and backslashes, and the stored JSON is never rendered raw into HTML.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>True when no field says anything.</summary>
    [JsonIgnore]
    public bool IsEmpty => Colour is null && MakeModel is null && Marks is null && Scale is null;

    /// <summary>
    /// The facts with every field cleaned, or null when nothing is left. The one way facts are made from untrusted text
    /// (a model's answer, the owner's typing, a stored value read back).
    /// </summary>
    public static AvatarIdentityFacts? Normalize(string? colour, string? makeModel, string? marks, string? scale)
    {
        var facts = new AvatarIdentityFacts(Clean(colour), Clean(makeModel), Clean(marks), Clean(scale));
        return facts.IsEmpty ? null : facts;
    }

    /// <summary>The same facts with every field cleaned again; null when nothing is left.</summary>
    public AvatarIdentityFacts? Cleaned() => Normalize(Colour, MakeModel, Marks, Scale);

    /// <summary>
    /// One line of plain words: control, format and private-use characters dropped, quotes, brackets, angle brackets,
    /// semicolons and backslashes dropped, whitespace collapsed, cut at <see cref="MaxFieldChars"/>. Null when nothing is left.
    /// </summary>
    public static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var sb = new StringBuilder(Math.Min(value.Length, MaxFieldChars));
        bool space = false;
        foreach (char c in value)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                space = sb.Length > 0;
                continue;
            }

            if (c is '"' or '\'' or '`' or '<' or '>' or '{' or '}' or '[' or ']' or '\\' or ';' or '“' or '”' or '‘' or '’'
                || char.GetUnicodeCategory(c) is UnicodeCategory.Format or UnicodeCategory.Surrogate
                    or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned)
            {
                continue;
            }

            if (space)
            {
                sb.Append(' ');
                space = false;
            }

            sb.Append(c);
            if (sb.Length >= MaxFieldChars) break;
        }

        string text = sb.ToString().Trim();
        return text.Length == 0 ? null : text;
    }

    /// <summary>The stored JSON, or null for empty facts.</summary>
    public string? ToJson()
    {
        AvatarIdentityFacts? cleaned = Cleaned();
        if (cleaned is null) return null;
        string json = JsonSerializer.Serialize(cleaned, Json);

        // A hard bound on the SERIALISED length (the column): should any character still be escaped, cut the longest
        // field one character at a time (deterministic) until it fits, so no input can overflow the column.
        while (json.Length > MaxStoredChars)
        {
            string?[] f = [cleaned.Colour, cleaned.MakeModel, cleaned.Marks, cleaned.Scale];
            int longest = 0;
            for (int i = 1; i < f.Length; i++)
            {
                if ((f[i]?.Length ?? 0) > (f[longest]?.Length ?? 0)) longest = i;
            }

            if ((f[longest]?.Length ?? 0) == 0) break;
            f[longest] = Clean(f[longest]![..^1]);
            cleaned = new AvatarIdentityFacts(f[0], f[1], f[2], f[3]);
            if (cleaned.IsEmpty) return null;
            json = JsonSerializer.Serialize(cleaned, Json);
        }

        return json;
    }

    /// <summary>Reads stored JSON tolerantly (cleaning every field again); null for blank, unreadable or empty facts.</summary>
    public static AvatarIdentityFacts? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxStoredChars) return null;
        try
        {
            return JsonSerializer.Deserialize<AvatarIdentityFacts>(json, Json)?.Cleaned();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The facts as the planner's subject line and the scene instruction read them:
    /// <c>colour silver; make and model Citroen C3; marks roof rack; scale compact hatchback</c>. Null when empty.
    /// </summary>
    public string? Summary()
    {
        var parts = new List<string>(4);
        if (Colour is not null) parts.Add("colour " + Colour);
        if (MakeModel is not null) parts.Add("make and model " + MakeModel);
        if (Marks is not null) parts.Add("marks " + Marks);
        if (Scale is not null) parts.Add("scale " + Scale);
        return parts.Count == 0 ? null : string.Join("; ", parts);
    }

    /// <summary>
    /// The first-party clause appended to a video prompt: states the facts as a constraint the pictures also show.
    /// Built only from cleaned facts; carries no id and no name. Null when no subject has facts.
    /// </summary>
    public static string? Clause(IEnumerable<AvatarIdentityFacts?> facts)
    {
        string[] summaries = [.. facts.Select(f => f?.Summary()).Where(s => s is not null).Select(s => s!)];
        if (summaries.Length == 0) return null;
        return "The subject must match the reference pictures exactly (" + string.Join(" | ", summaries) + "); keep this colour and these details in every frame.";
    }
}
