using System.Text.RegularExpressions;
using ReelBolt.Shared.Acquisition;
using ReelBolt.Shared.Data.Models.Marketing;

namespace ReelBolt.Inference.Api.Services.Marketing.Links;

/// <summary>
/// The shape rules of a tracked link (docs/design/new-user-funnel.md sections 4 to 6) and the two
/// addresses derived from it. Pure: the admin API validates with these before a row is written, and the
/// redirect checks the stored destination with the same rule before it answers, so a row that reached
/// the table some other way still cannot become an open redirect.
/// </summary>
public static partial class TrackedLinkRules
{
    /// <summary>The widest placement note.</summary>
    public const int PlacementMaxLength = 160;

    /// <summary>The widest destination path.</summary>
    public const int DestinationPathMaxLength = 200;

    /// <summary>Where the redirect sends anything that is not an active link: the site's home page.</summary>
    public const string FallbackPath = "/";

    /// <summary>The public prefix of a tracked link on the site host (<c>/l/{code}</c>).</summary>
    public const string ShortPathPrefix = "/l/";

    /// <summary>
    /// The API's own path space. A destination is a page of the site, never an API route: the tracked
    /// link's own address lives under here (<c>/api/v1/l/{code}</c>), so a destination inside it could
    /// point a link back at a link.
    /// </summary>
    public const string ApiPathRoot = "/api";

    // Both shapes end with \z, never $: in .NET a $ also matches just before a final line feed.
    [GeneratedRegex(@"^/[A-Za-z0-9/_.~-]{0,199}\z", RegexOptions.CultureInvariant)]
    private static partial Regex DestinationShape();

    [GeneratedRegex(@"^[A-Z]{3}\z", RegexOptions.CultureInvariant)]
    private static partial Regex CurrencyShape();

    /// <summary>
    /// A link code as it is looked up: lowered with the invariant culture, null stays null. A stored code
    /// is always lower-case (<see cref="AcquisitionVocabulary.LinkCodePattern"/>), but a posted link gets
    /// re-cased by people and by the places it is posted in, and the two edges disagree about who lowers
    /// it (the Pages Function does, nginx forwards the path as typed). Lowering here makes
    /// <c>/l/HN-Launch</c> the same link on every edge. Nothing else is repaired: a code that is still not
    /// the pattern after lowering is malformed.
    /// </summary>
    public static string? NormalizeCode(string? code) => code?.ToLowerInvariant();

    /// <summary>The short address an operator posts: <c>/l/{code}</c>.</summary>
    public static string ShortPath(string code) => ShortPathPrefix + code;

    /// <summary>
    /// The address the short one redirects to: the destination path plus the link's UTM parameters
    /// (<c>utm_source</c> = channel, <c>utm_medium</c> = medium, <c>utm_campaign</c> = campaign,
    /// <c>utm_content</c> = code) and <c>for</c> = persona when the link has one. Always relative. Values
    /// are escaped, so a <c>+</c> in a campaign token is not read back as a space.
    /// </summary>
    public static string LongPath(MarketingLink link)
    {
        string path = TargetPath(link)
            + "?utm_source=" + Uri.EscapeDataString(link.Channel)
            + "&utm_medium=" + Uri.EscapeDataString(link.Medium)
            + "&utm_campaign=" + Uri.EscapeDataString(link.Campaign)
            + "&utm_content=" + Uri.EscapeDataString(link.Code);
        return link.Persona is { Length: > 0 } persona ? path + "&for=" + Uri.EscapeDataString(persona) : path;
    }

    // Mirrors PERSONA_LANDING_SLUGS in site/lib/attribution-persona.ts: keep the two in step.
    private static readonly Dictionary<string, string> PersonaLandingPaths = new(StringComparer.Ordinal)
    {
        ["creator"] = "/for/creators",
        ["business"] = "/for/small-business",
        ["educator"] = "/for/educators",
        ["nonprofit"] = "/for/nonprofits",
        ["software"] = "/for/software-teams",
        ["agency"] = "/for/agencies",
    };

    /// <summary>
    /// The page the link lands on. Home with a landing persona lands on that audience's page (the site only
    /// serves audiences at <c>/for/{slug}</c>); decided at redirect time, so stored rows stay as written.
    /// </summary>
    private static string TargetPath(MarketingLink link) =>
        link.DestinationPath == "/" && link.Persona is { } persona && PersonaLandingPaths.TryGetValue(persona, out string? landing)
            ? landing
            : link.DestinationPath;

    /// <summary>
    /// Whether <paramref name="path"/> is an own-origin destination: starts with one <c>/</c>, never
    /// <c>//</c>, no scheme, host, query, fragment, backslash, white space or control character (only
    /// ASCII letters, digits and <c>/ _ . ~ -</c>), no <c>.</c> or <c>..</c> segment, and never an address
    /// that could be a tracked link again, which would loop and count one click per hop:
    /// <list type="bullet">
    /// <item><description><c>/l</c> and <c>/l/…</c> in ANY letter case. The public edge matches <c>/l/</c>
    /// without regard to case and the redirect lowers the code, so <c>/L/hn-launch</c> is the same link as
    /// <c>/l/hn-launch</c>.</description></item>
    /// <item><description><c>/api</c> and everything under it, in any letter case
    /// (<see cref="ApiPathRoot"/>): <c>/api/v1/l/{code}</c> is the redirect itself, and no API route is a
    /// landing page.</description></item>
    /// </list>
    /// </summary>
    public static bool IsDestinationPath(string? path)
    {
        if (path is null || path.Length > DestinationPathMaxLength) return false;
        if (!DestinationShape().IsMatch(path) || path.Contains("//", StringComparison.Ordinal)) return false;
        foreach (string segment in path.Split('/'))
        {
            if (segment is "." or "..") return false;
        }

        return !IsAtOrUnder(path, "/l") && !IsAtOrUnder(path, ApiPathRoot);
    }

    /// <summary>Whether <paramref name="path"/> is <paramref name="root"/> or a path below it, ignoring letter case.</summary>
    private static bool IsAtOrUnder(string path, string root) =>
        path.Equals(root, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="placement"/> is a usable note: 1 to 160 characters, none of them a control character.</summary>
    public static bool IsPlacement(string? placement)
    {
        if (string.IsNullOrWhiteSpace(placement) || placement.Length > PlacementMaxLength) return false;
        foreach (char c in placement)
        {
            if (char.IsControl(c)) return false;
        }

        return true;
    }

    /// <summary>A cost is both parts or neither: a non-negative amount in minor units and an upper-case three-letter currency code.</summary>
    public static bool IsCost(long? costMinor, string? costCurrency)
    {
        if (costMinor is null && costCurrency is null) return true;
        return costMinor is >= 0 && costCurrency is not null && CurrencyShape().IsMatch(costCurrency);
    }

    /// <summary>
    /// The first problem with a link's fields, as the stable error code the admin API answers, or null
    /// when every closed set and shape holds. Checked in the order of the request's fields.
    /// </summary>
    public static string? FirstProblem(MarketingLink link)
    {
        if (!AcquisitionVocabulary.IsLinkCode(link.Code)) return "code_invalid";
        if (!AcquisitionVocabulary.IsLinkChannel(link.Channel)) return "channel_invalid";
        if (!AcquisitionVocabulary.IsLinkMedium(link.Medium)) return "medium_invalid";
        if (!AcquisitionVocabulary.IsUtmValue(link.Campaign)) return "campaign_invalid";
        if (!IsPlacement(link.Placement)) return "placement_invalid";
        if (!IsDestinationPath(link.DestinationPath)) return "destination_path_invalid";
        if (link.Persona is not null && !AcquisitionVocabulary.IsPersona(link.Persona)) return "persona_invalid";
        if (!IsCost(link.CostMinor, link.CostCurrency)) return "cost_invalid";
        return null;
    }
}
