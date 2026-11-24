namespace ReelBolt.Shared.Avatars;

/// <summary>
/// The one source of the WORKSPACE consent statement of a person (docs/design/avatar-objects.md "The workspace avatar
/// library (global avatars)"): what an Owner or Admin attests when they save a person's avatar to the workspace avatar
/// library, or confirm a library item again. It covers AI videos and pictures in ANY project of the workspace, which a
/// project-scoped consent (<see cref="AvatarConsentStatement"/>) never does: publishing a person to the library is a new
/// attestation under this text. Same shape as the other statements: one version id, two language twins, compared
/// ordinally and stored exactly as accepted on the library item and on every import of it.
/// </summary>
/// <remarks>
/// Wording for counsel, like every other statement. <b>Never edit a string in place:</b> a change is a NEW version (a
/// new constant pair), because rows already store the old wording under the old id.
/// <c>AvatarLibraryStatementTests</c> pins the SHA-256 of both strings.
/// </remarks>
public static class AvatarWorkspaceConsentStatement
{
    /// <summary>The statement version both language twins are registered under.</summary>
    public const string Version = "avatar-consent-workspace-2026-11";

    /// <summary>The English statement (<c>en</c>), verbatim.</summary>
    public const string English =
        "I confirm that I am the person shown and heard in these files, or that I have that person's written " +
        "permission; that the person is 18 or older; that they have given informed consent to their face, body and " +
        "voice being used to generate AI videos and pictures in any project of this workspace, and to these files " +
        "being sent for that purpose to the AI providers those projects use; that I will keep a record of that " +
        "consent; and that I will revoke or delete this avatar if they withdraw it.";

    /// <summary>The Italian statement (<c>it</c>), verbatim.</summary>
    public const string Italian =
        "Confermo di essere la persona che si vede e si sente in questi file, oppure di avere il permesso scritto " +
        "di quella persona; che la persona ha almeno 18 anni; che ha dato il proprio consenso informato all'uso del " +
        "suo volto, del suo corpo e della sua voce per generare video e immagini con l'intelligenza artificiale in " +
        "qualsiasi progetto di questo spazio di lavoro, e all'invio di questi file, a tale scopo, ai provider di " +
        "intelligenza artificiale usati da quei progetti; che conserverò una prova di tale consenso; e che revocherò " +
        "o eliminerò questo avatar se la persona ritira il consenso.";

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
