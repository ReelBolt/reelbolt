namespace ReelBolt.Shared.Avatars;

/// <summary>
/// The one source of the WORKSPACE object statement (docs/design/avatar-objects.md "The workspace avatar library
/// (global avatars)"): what an Owner or Admin ticks when they save an object avatar to the workspace avatar library, or
/// confirm a library item again. It is the object statement (<see cref="AvatarObjectStatement"/>) for ANY project of
/// the workspace. Same shape as the other statements: one version id, two language twins, compared ordinally and
/// stored exactly as accepted on the library item and on every import of it.
/// </summary>
/// <remarks>
/// <b>Never edit a string in place:</b> a change is a NEW version (a new constant pair), because rows already store
/// the old wording under the old id. <c>AvatarLibraryStatementTests</c> pins the SHA-256 of both strings.
/// </remarks>
public static class AvatarWorkspaceObjectStatement
{
    /// <summary>The statement version both language twins are registered under.</summary>
    public const string Version = "avatar-object-workspace-2026-11";

    /// <summary>The English statement (<c>en</c>), verbatim.</summary>
    public const string English =
        "I confirm that these files show an object, a product or a place and no identifiable person, and that I " +
        "have the right to use them to generate AI videos and pictures in any project of this workspace and to send " +
        "them for that purpose to the AI providers those projects use.";

    /// <summary>The Italian statement (<c>it</c>), verbatim.</summary>
    public const string Italian =
        "Confermo che questi file mostrano un oggetto, un prodotto o un luogo e nessuna persona identificabile, e " +
        "che ho il diritto di usarli per generare video e immagini con l'intelligenza artificiale in qualsiasi " +
        "progetto di questo spazio di lavoro e di inviarli, a tale scopo, ai provider di intelligenza artificiale " +
        "usati da quei progetti.";

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
