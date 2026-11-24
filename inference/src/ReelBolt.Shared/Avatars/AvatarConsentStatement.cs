namespace ReelBolt.Shared.Avatars;

/// <summary>
/// The one source of the avatar consent statement (docs/design/avatars.md "Consent and safety", decision AV-6): one
/// version id, two language twins. The attesting user must send the statement for the locale the form showed,
/// verbatim; the server compares ordinally and stores exactly what was accepted, with the version and the locale.
/// </summary>
/// <remarks>
/// Copied byte-for-byte from the counsel drafts (docs/legal/drafts/avatars-privacy-terms.md section 3). <b>Never edit
/// a string in place:</b> any change after counsel review is a NEW version (a new constant pair), because rows already
/// store the old wording under the old id. Two versions exist (docs/design/avatar-objects.md, decision AO-2):
/// <list type="bullet">
/// <item><see cref="Version2026_11"/> (<see cref="English2026_11"/> / <see cref="Italian2026_11"/>): the first text,
/// which covers AI videos only. Rows attested under it keep working for video.</item>
/// <item><see cref="Version"/> (<see cref="English"/> / <see cref="Italian"/>): the CURRENT text, which covers AI
/// videos and pictures. Every new attestation is made under it.</item>
/// </list>
/// <c>ProjectAvatarConsentStatementTests</c> pins the SHA-256 of all four strings.
/// </remarks>
public static class AvatarConsentStatement
{
    /// <summary>The first statement version (videos only). Rows keep it until they are re-attested.</summary>
    public const string Version2026_11 = "avatar-consent-2026-11";

    /// <summary>The English statement of <see cref="Version2026_11"/>, verbatim. Never edited.</summary>
    public const string English2026_11 =
        "I confirm that I am the person shown and heard in these files, or that I have that person's written " +
        "permission; that the person is 18 or older; that they have given informed consent to their face, body and " +
        "voice being used to generate AI videos in this project, and to these files being sent for that purpose to " +
        "the AI providers this project uses; that I will keep a record of that consent; and that I will revoke or " +
        "delete this avatar if they withdraw it.";

    /// <summary>The Italian statement of <see cref="Version2026_11"/>, verbatim (drafted for counsel, question Q2). Never edited.</summary>
    public const string Italian2026_11 =
        "Confermo di essere la persona che si vede e si sente in questi file, oppure di avere il permesso scritto " +
        "di quella persona; che la persona ha almeno 18 anni; che ha dato il proprio consenso informato all'uso del " +
        "suo volto, del suo corpo e della sua voce per generare video con l'intelligenza artificiale in questo " +
        "progetto, e all'invio di questi file, a tale scopo, ai provider di intelligenza artificiale usati da questo " +
        "progetto; che conserverò una prova di tale consenso; e che revocherò o eliminerò questo avatar se la " +
        "persona ritira il consenso.";

    /// <summary>The CURRENT statement version both language twins are registered under (videos and pictures, AO-2).</summary>
    public const string Version = "avatar-consent-2026-12";

    /// <summary>The current English statement (<c>en</c>), verbatim.</summary>
    public const string English =
        "I confirm that I am the person shown and heard in these files, or that I have that person's written " +
        "permission; that the person is 18 or older; that they have given informed consent to their face, body and " +
        "voice being used to generate AI videos and pictures in this project, and to these files being sent for that " +
        "purpose to the AI providers this project uses; that I will keep a record of that consent; and that I will " +
        "revoke or delete this avatar if they withdraw it.";

    /// <summary>The current Italian statement (<c>it</c>), verbatim (drafted for counsel).</summary>
    public const string Italian =
        "Confermo di essere la persona che si vede e si sente in questi file, oppure di avere il permesso scritto " +
        "di quella persona; che la persona ha almeno 18 anni; che ha dato il proprio consenso informato all'uso del " +
        "suo volto, del suo corpo e della sua voce per generare video e immagini con l'intelligenza artificiale in " +
        "questo progetto, e all'invio di questi file, a tale scopo, ai provider di intelligenza artificiale usati da " +
        "questo progetto; che conserverò una prova di tale consenso; e che revocherò o eliminerò questo avatar se la " +
        "persona ritira il consenso.";

    /// <summary>The locales the statement exists in, in display order.</summary>
    public static readonly IReadOnlyList<string> Locales = ["en", "it"];

    /// <summary>
    /// The CURRENT statement for <paramref name="locale"/> (<c>en</c> or <c>it</c>, exact), or null for any other value.
    /// A new attestation is always made under the current text, so this never answers an older version.
    /// </summary>
    public static string? For(string? locale) => locale switch
    {
        "en" => English,
        "it" => Italian,
        _ => null
    };

    /// <summary>
    /// Whether <paramref name="statement"/> is exactly the CURRENT statement of <paramref name="locale"/>. Ordinal and
    /// untrimmed: the bytes the user accepted are the bytes stored, so a statement with extra whitespace is not it.
    /// </summary>
    public static bool Matches(string? locale, string? statement) =>
        For(locale) is { } required && string.Equals(statement, required, StringComparison.Ordinal);

    /// <summary>
    /// Whether an attestation made under <paramref name="version"/> covers generating PICTURES of the person, not only
    /// videos: true for <see cref="Version"/> and for the workspace library's text
    /// (<see cref="AvatarWorkspaceConsentStatement.Version"/>, which names videos and pictures in any project of the
    /// workspace). An avatar attested under <see cref="Version2026_11"/> must be confirmed again before it is used in a
    /// picture.
    /// </summary>
    public static bool CoversPictures(string? version) =>
        string.Equals(version, Version, StringComparison.Ordinal)
        || string.Equals(version, AvatarWorkspaceConsentStatement.Version, StringComparison.Ordinal);
}
