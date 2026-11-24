namespace ReelBolt.Shared.Avatars;

/// <summary>
/// The one source of the OBJECT statement (docs/design/avatar-objects.md "The object statement and the picture
/// consent version", decision AO-1): what a member ticks when they make an avatar of an object, a product or a place.
/// It is not a person's consent (an object has none to give); it is the member's own attestation that the files show
/// no identifiable person and that they may use them. Same shape as <see cref="AvatarConsentStatement"/>: one version
/// id, two language twins, compared ordinally and stored exactly as accepted on the avatar's consent columns.
/// </summary>
/// <remarks>
/// <b>Never edit a string in place:</b> a change is a NEW version (a new constant pair), because rows already store
/// the old wording under the old id. <c>ProjectAvatarObjectsControllerTests</c> pins the SHA-256 of both strings.
/// </remarks>
public static class AvatarObjectStatement
{
    /// <summary>The statement version both language twins are registered under.</summary>
    public const string Version = "avatar-object-2026-11";

    /// <summary>The English statement (<c>en</c>), verbatim.</summary>
    public const string English =
        "I confirm that these files show an object, a product or a place and no identifiable person, and that I " +
        "have the right to use them to generate AI videos and pictures in this project and to send them for that " +
        "purpose to the AI providers this project uses.";

    /// <summary>The Italian statement (<c>it</c>), verbatim.</summary>
    public const string Italian =
        "Confermo che questi file mostrano un oggetto, un prodotto o un luogo e nessuna persona identificabile, e " +
        "che ho il diritto di usarli per generare video e immagini con l'intelligenza artificiale in questo progetto " +
        "e di inviarli, a tale scopo, ai provider di intelligenza artificiale usati da questo progetto.";

    /// <summary>The locales the statement exists in, in display order.</summary>
    public static readonly IReadOnlyList<string> Locales = ["en", "it"];

    /// <summary>The statement for <paramref name="locale"/> (<c>en</c> or <c>it</c>, exact), or null for any other value.</summary>
    public static string? For(string? locale) => locale switch
    {
        "en" => English,
        "it" => Italian,
        _ => null
    };

    /// <summary>
    /// Whether <paramref name="statement"/> is exactly the statement of <paramref name="locale"/>. Ordinal and
    /// untrimmed: the bytes the member accepted are the bytes stored.
    /// </summary>
    public static bool Matches(string? locale, string? statement) =>
        For(locale) is { } required && string.Equals(statement, required, StringComparison.Ordinal);
}
