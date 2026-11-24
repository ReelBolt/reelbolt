using System.Text.RegularExpressions;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.Shared.Acquisition;

/// <summary>
/// The closed sets of the new-user funnel (docs/design/new-user-funnel.md section 4), in one place: who a
/// person says they are (persona), what they start from (material), the starter recipe that follows from
/// it, and the vocabulary of a tracked link (channel, medium, code). The site, the app, the Go API and BI
/// use the same tokens; here they are what the Inference API validates against and what the database
/// CHECK constraints are built from, so the two cannot drift.
/// <para>
/// Every token is lower-case ASCII. A value is compared exactly (ordinal): nothing here trims, folds case
/// or guesses, so a caller that accepts looser input normalises it first.
/// </para>
/// </summary>
public static partial class AcquisitionVocabulary
{
    /// <summary>The persona a person chose only inside the app; it has no landing page.</summary>
    public const string OtherPersona = "other";

    /// <summary>Who a person says they are. Order is the order the product shows them in.</summary>
    public static readonly IReadOnlyList<string> Personas =
    [
        "creator",
        "business",
        "educator",
        "nonprofit",
        "software",
        "agency",
        OtherPersona,
    ];

    /// <summary>The person has clips to edit.</summary>
    public const string FootageMaterial = "footage";

    /// <summary>The person has the source code of a product.</summary>
    public const string CodeMaterial = "code";

    /// <summary>The person has an idea and nothing filmed yet.</summary>
    public const string IdeaMaterial = "idea";

    /// <summary>What a person starts from.</summary>
    public static readonly IReadOnlyList<string> Materials = [FootageMaterial, CodeMaterial, IdeaMaterial];

    /// <summary>The recipe a person who starts from clips, or from an idea they will film, gets first.</summary>
    public const string FootageStarterTemplateKey = "edit-my-video";

    /// <summary>The recipe a person who starts from code gets first (the automatic code-promo starter).</summary>
    public const string CodeStarterTemplateKey = WorkflowTemplateCatalog.StarterTemplateKey;

    /// <summary>Where a tracked link is posted. <c>utm_source</c> of the link.</summary>
    public static readonly IReadOnlyList<string> LinkChannels =
    [
        "hackernews",
        "reddit",
        "linkedin",
        "x",
        "producthunt",
        "youtube",
        "tiktok",
        "instagram",
        "facebook",
        "github",
        "discord",
        "newsletter",
        "email",
        "blog",
        "directory",
        "press",
        "partner",
        "ads",
        "other",
    ];

    /// <summary>The kind of placement. <c>utm_medium</c> of the link.</summary>
    public static readonly IReadOnlyList<string> LinkMedia =
    [
        "social",
        "community",
        "video",
        "email",
        "referral",
        "paid",
        "owned",
        "press",
    ];

    /// <summary>The consented first-touch cookie supplied the attribution.</summary>
    public const string CookieCapture = "cookie";

    /// <summary>The parameters carried in the signup request supplied the attribution (no cookie).</summary>
    public const string UrlCapture = "url";

    /// <summary>How a <c>user_attribution</c> row was learned (<c>user_attribution.capture</c>).</summary>
    public static readonly IReadOnlyList<string> AttributionCaptures = [CookieCapture, UrlCapture];

    /// <summary>A link code: 2 to 40 characters, always a valid UTM value (it is the link's <c>utm_content</c>).</summary>
    public const string LinkCodePattern = "^[a-z0-9][a-z0-9-]{1,39}$";

    /// <summary>The longest link code.</summary>
    public const int LinkCodeMaxLength = 40;

    /// <summary>A UTM value, as every service sanitizes it: 1 to 64 characters. A link's campaign token is one.</summary>
    public const string UtmValuePattern = "^[a-z0-9][a-z0-9._~+-]{0,63}$";

    /// <summary>The longest UTM value.</summary>
    public const int UtmValueMaxLength = 64;

    [GeneratedRegex(LinkCodePattern, RegexOptions.CultureInvariant)]
    private static partial Regex LinkCodeRegex();

    [GeneratedRegex(UtmValuePattern, RegexOptions.CultureInvariant)]
    private static partial Regex UtmValueRegex();

    /// <summary>Whether <paramref name="value"/> is one of <see cref="Personas"/>.</summary>
    public static bool IsPersona(string? value) => Contains(Personas, value);

    /// <summary>Whether <paramref name="value"/> is one of <see cref="Materials"/>.</summary>
    public static bool IsMaterial(string? value) => Contains(Materials, value);

    /// <summary>Whether <paramref name="value"/> is one of <see cref="LinkChannels"/>.</summary>
    public static bool IsLinkChannel(string? value) => Contains(LinkChannels, value);

    /// <summary>Whether <paramref name="value"/> is one of <see cref="LinkMedia"/>.</summary>
    public static bool IsLinkMedium(string? value) => Contains(LinkMedia, value);

    /// <summary>Whether <paramref name="value"/> is a link code (<see cref="LinkCodePattern"/>, no trailing newline).</summary>
    public static bool IsLinkCode(string? value) =>
        value is { Length: >= 2 and <= LinkCodeMaxLength } && !value.Contains('\n') && LinkCodeRegex().IsMatch(value);

    /// <summary>Whether <paramref name="value"/> is a UTM value (<see cref="UtmValuePattern"/>, no trailing newline).</summary>
    public static bool IsUtmValue(string? value) =>
        value is { Length: >= 1 and <= UtmValueMaxLength } && !value.Contains('\n') && UtmValueRegex().IsMatch(value);

    /// <summary>
    /// The starter recipe for a material: clips and an idea start from <see cref="FootageStarterTemplateKey"/>,
    /// code from <see cref="CodeStarterTemplateKey"/>. Null for anything that is not a material.
    /// </summary>
    public static string? StarterTemplateKeyFor(string? material) => material switch
    {
        FootageMaterial or IdeaMaterial => FootageStarterTemplateKey,
        CodeMaterial => CodeStarterTemplateKey,
        _ => null,
    };

    /// <summary>
    /// <paramref name="values"/> as the body of a SQL <c>IN (...)</c> list: <c>'a', 'b'</c>. For the CHECK
    /// constraints of the closed sets; the tokens are lower-case ASCII, so no quoting is needed.
    /// </summary>
    public static string SqlInList(IReadOnlyList<string> values) => string.Join(", ", values.Select(v => "'" + v + "'"));

    private static bool Contains(IReadOnlyList<string> set, string? value)
    {
        if (value is null) return false;
        for (int i = 0; i < set.Count; i++)
        {
            if (string.Equals(set[i], value, StringComparison.Ordinal)) return true;
        }

        return false;
    }
}
