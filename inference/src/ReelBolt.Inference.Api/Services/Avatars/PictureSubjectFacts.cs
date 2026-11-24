using System.Text.Json;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Services.Avatars;

/// <summary>One avatar or object a picture layer was made with, as stored in the layer's content.</summary>
/// <param name="AvatarId">The avatar (kept after the avatar is deleted: the layer stays, as a generated clip does).</param>
/// <param name="Name">Its display name when the picture was made.</param>
/// <param name="Kind"><c>Person</c> or <c>Object</c>.</param>
public sealed record PictureSubjectFact(Guid AvatarId, string Name, string Kind);

/// <summary>
/// What a picture layer made with avatars or objects records about them (docs/design/avatar-objects.md "Avatars and
/// objects in the picture editor"): <c>{ subjects: [{ avatarId, name, kind }], aiGenerated: true, disclosure }</c>,
/// kept as the layer's CONTENT (<c>edit_image_layers.content_json</c>, served as the layer's <c>content</c>). A text
/// layer's content is its text, and every other bitmap layer has none, so every older layer is unchanged. The editor
/// reads it to say who a layer shows and to add the "AI-generated" label at export when the disclosure is
/// <c>Visible</c>; duplicating a picture copies it with the layer.
/// </summary>
/// <param name="Subjects">The avatars and objects shown, in the order they were named.</param>
/// <param name="Disclosure"><c>Visible</c> or <c>MetadataOnly</c>: the person's own label, else <c>MetadataOnly</c>.</param>
public sealed record PictureSubjectFacts(IReadOnlyList<PictureSubjectFact> Subjects, string Disclosure)
{
    /// <summary>The content key of the list.</summary>
    public const string SubjectsKey = "subjects";

    /// <summary>The content key of the flag.</summary>
    public const string AiGeneratedKey = "aiGenerated";

    /// <summary>The content key of the disclosure word.</summary>
    public const string DisclosureKey = "disclosure";

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>The facts of a result made with <paramref name="read"/>.</summary>
    public static PictureSubjectFacts From(ProjectSubjectRead read)
    {
        ArgumentNullException.ThrowIfNull(read);
        return new PictureSubjectFacts(
            [.. read.Subjects.Select(s => new PictureSubjectFact(s.AvatarId, s.Name, s.Kind.ToString()))],
            read.Disclosure.ToString());
    }

    /// <summary>The layer content that records these facts: exactly the three keys, in this order.</summary>
    public string ToContentJson() => JsonSerializer.Serialize(new
    {
        subjects = Subjects.Select(s => new { avatarId = s.AvatarId, name = s.Name, kind = s.Kind }),
        aiGenerated = true,
        disclosure = Disclosure
    }, Web);

    /// <summary>
    /// The facts a bitmap layer's content records, or null when it records none (a layer made without subjects, a
    /// layer with no content, a text layer's text, an unreadable blob).
    /// </summary>
    public static PictureSubjectFacts? Read(string? contentJson)
    {
        // Cheap first: almost every layer has no subjects, and its content is not parsed for them.
        if (string.IsNullOrWhiteSpace(contentJson) || !contentJson.Contains(SubjectsKey, StringComparison.Ordinal))
            return null;

        try
        {
            using JsonDocument document = JsonDocument.Parse(contentJson);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty(SubjectsKey, out JsonElement list) || list.ValueKind != JsonValueKind.Array)
                return null;

            List<PictureSubjectFact> subjects = [];
            foreach (JsonElement item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("avatarId", out JsonElement id) || !id.TryGetGuid(out Guid avatarId))
                    continue;
                string name = item.TryGetProperty("name", out JsonElement n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? string.Empty : string.Empty;
                string kind = item.TryGetProperty("kind", out JsonElement k) && k.ValueKind == JsonValueKind.String
                              && Enum.TryParse(k.GetString(), ignoreCase: false, out AvatarSubjectKind parsed)
                    ? parsed.ToString()
                    : AvatarSubjectKind.Person.ToString(); // an unreadable kind is treated as the stricter one
                subjects.Add(new PictureSubjectFact(avatarId, name, kind));
            }

            if (subjects.Count == 0) return null;

            // An unreadable label is the visible one: a person may be shown, and the stricter label never under-discloses.
            string disclosure = root.TryGetProperty(DisclosureKey, out JsonElement d) && d.ValueKind == JsonValueKind.String
                                && Enum.TryParse(d.GetString(), ignoreCase: false, out AvatarDisclosure label)
                ? label.ToString()
                : AvatarDisclosure.Visible.ToString();
            return new PictureSubjectFacts(subjects, disclosure);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Whether <paramref name="contentJson"/> names <paramref name="avatarId"/> among its subjects.</summary>
    public static bool Names(string? contentJson, Guid avatarId) =>
        Read(contentJson) is { } facts && facts.Subjects.Any(s => s.AvatarId == avatarId);
}
