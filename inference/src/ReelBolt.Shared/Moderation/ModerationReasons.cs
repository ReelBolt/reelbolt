namespace ReelBolt.Shared.Moderation;

/// <summary>
/// User-facing reasons for a moderation verdict. Plain sentences that name the surface
/// ("this image", "this content"), never a score, a raw category id or the model name.
/// </summary>
/// <remarks>
/// <b>This is a policy signal, not a CSAM detector.</b> The moderation model is a general
/// harmful-content classifier; it is not a child-safety control. Suspected CSAM must never be sent
/// here at all — handling it is a separate, recognised hash-matching/reporting track. The
/// <c>sexual/minors</c> arm below exists only to fail closed on a category the API returns; it does
/// not turn this client into that control.
/// </remarks>
public static class ModerationReasons
{
    public static string For(string? category, ModerationContentKind kind)
    {
        if (string.IsNullOrEmpty(category))
            return Default;

        return category switch
        {
            "sexual" => kind == ModerationContentKind.Image
                ? "This image may contain sexual content."
                : "This content may contain sexual content.",
            "sexual/minors" => "This content may involve the sexualization of minors.",
            "violence/graphic" => "This content may contain graphic violence.",
            "violence" => "This content may contain violent content.",
            "hate" or "hate/threatening" => "This content may contain hateful content.",
            _ when category.StartsWith("harassment", StringComparison.Ordinal) =>
                "This content may contain harassing content.",
            _ when category.StartsWith("self-harm", StringComparison.Ordinal) =>
                "This content may reference self-harm.",
            _ when category.StartsWith("illicit", StringComparison.Ordinal) =>
                "This content may reference illegal activity.",
            _ => Default
        };
    }

    private const string Default = "This content may violate the content policy.";
}
